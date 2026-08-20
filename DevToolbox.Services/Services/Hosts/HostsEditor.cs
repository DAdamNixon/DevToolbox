using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Rewriting and removing what is already in the file — renaming a group, re-flagging an option,
/// correcting or deleting entries.
/// <para>
/// Kept apart from <see cref="HostsMutator"/> because the promise is different. Switching an option
/// provably cannot alter a line's content, and adding provably cannot touch an existing one; both
/// are safe to do from a tray menu without looking. Editing can lose something a developer typed,
/// by design. What stands in for the lost guarantee is that the change set is a complete edit
/// script, replayed and compared by <c>HostsInvariantChecker</c>, and shown as a diff first — so an
/// edit can be wrong, but it can never be larger than what was on screen.
/// </para>
/// <para>
/// Nothing here touches a quarantined line. The risk analyzer's whole finding is that those lines
/// are not the option's, so deleting an option must not take them with it.
/// </para>
/// </summary>
public static class HostsEditor
{
    /// <summary>An edit that only rewrites or removes. Named because a collection expression cannot build one.</summary>
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> NoInserts =
        new Dictionary<int, IReadOnlyList<string>>();

    /// <summary>Carries out whichever edit it was given.</summary>
    /// <exception cref="ArgumentException">A name or an entry is not valid.</exception>
    /// <exception cref="KeyNotFoundException">It names a group or option that does not exist.</exception>
    /// <exception cref="InvalidOperationException">The new name is taken, or the option has no block to edit.</exception>
    public static HostsMutation Apply(HostsDocument document, HostsMap map, HostsEdit edit)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(edit);

        return edit switch
        {
            HostsEdit.RenameGroup rename => RenameGroup(document, map, rename.InGroup, rename.NewName),
            HostsEdit.DeleteGroup delete => DeleteGroup(document, map, delete.InGroup),
            HostsEdit.UpdateOption update => UpdateOption(document, map, update),
            HostsEdit.DeleteOption delete => DeleteOption(document, map, delete.InGroup, delete.InOption),
            _ => throw new ArgumentOutOfRangeException(nameof(edit), edit, "Unknown edit."),
        };
    }

    // ── groups ───────────────────────────────────────────────────────────────

    private static HostsMutation RenameGroup(HostsDocument document, HostsMap map, string group, string newName)
    {
        var target = Group(document, map, group);

        Require(HostsLineValidator.ValidateName(newName, map.Dialect, "group"));

        var trimmed = newName.Trim();
        if (string.Equals(trimmed, target.Name, StringComparison.Ordinal)) return Nothing(document);

        if (map.Find(trimmed) is not null)
        {
            throw new InvalidOperationException($"There is already a group called '{trimmed}'.");
        }

        // Every occurrence, not just the first: a group directive repeated later in the file reopens
        // the same group, and renaming one of two would split it in half.
        var rewrites = target.DirectiveLines.ToDictionary(
            line => line,
            line => (string?)ReplaceDirective(document.Lines[line - 1].Text, map.Dialect, map.Dialect.GroupDirective(trimmed)));

        return Splice(document, rewrites, NoInserts);
    }

    private static HostsMutation DeleteGroup(HostsDocument document, HostsMap map, string group)
    {
        var target = Group(document, map, group);

        var lines = new HashSet<int>(target.DirectiveLines);

        foreach (var option in target.Options)
        {
            lines.UnionWith(OwnLinesOf(option));
        }

        return Splice(document, lines.ToDictionary(line => line, _ => (string?)null), NoInserts);
    }

    // ── options ──────────────────────────────────────────────────────────────

    private static HostsMutation DeleteOption(HostsDocument document, HostsMap map, string group, string option)
    {
        var target = Option(document, map, group, option);

        return Splice(document, OwnLinesOf(target).ToDictionary(line => line, _ => (string?)null), NoInserts);
    }

    private static HostsMutation UpdateOption(HostsDocument document, HostsMap map, HostsEdit.UpdateOption update)
    {
        var group = Group(document, map, update.InGroup);
        var target = Option(document, map, update.InGroup, update.InOption);

        Require(HostsLineValidator.ValidateName(update.NewName, map.Dialect, "option"));

        var problems = update.Entries
            .SelectMany(entry => HostsLineValidator.ValidateEntry(entry.Value, map.Dialect))
            .ToArray();

        Require(problems);

        var newName = update.NewName.Trim();
        var renaming = !string.Equals(newName, target.Name, StringComparison.Ordinal);

        if (renaming && group.Options.Any(other =>
                !ReferenceEquals(other, target) &&
                string.Equals(other.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Group '{update.InGroup}' already has an option called '{newName}'.");
        }

        if (target.DirectiveLines.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{update.InGroup}/{update.InOption}' is only ever tagged line by line, so there is no block to edit.");
        }

        var rewrites = new Dictionary<int, string?>();

        if (renaming || update.Severity != target.Severity)
        {
            foreach (var line in target.DirectiveLines)
            {
                rewrites[line] = ReplaceDirective(
                    document.Lines[line - 1].Text,
                    map.Dialect,
                    map.Dialect.OptionDirective(newName, update.Severity));
            }
        }

        // Only lines the option unambiguously owns are the editor's to touch. Inline-tagged lines
        // carry their own directive and parked lines are never toggled in either direction, so both
        // are shown read-only and left exactly as they are; quarantined lines are not the option's
        // at all.
        var editable = target.OwnedLines.ToHashSet();
        var kept = new HashSet<int>();

        foreach (var entry in update.Entries.Where(entry => entry.Line > 0))
        {
            if (!editable.Contains(entry.Line))
            {
                throw new InvalidOperationException(
                    $"Line {entry.Line} is not one of the lines '{update.InGroup}/{update.InOption}' owns.");
            }

            if (!kept.Add(entry.Line)) throw new InvalidOperationException($"Line {entry.Line} appears twice.");

            var original = document.Lines[entry.Line - 1].Text;

            // Compared by meaning, not by text. A line read out of the file and handed straight back
            // is unchanged even though recomposing it would tidy its column alignment — and an
            // editor that rewrote every line of an option each time it was opened would turn every
            // diff into noise and every save into a whole-block rewrite.
            if (IsUnchanged(original, entry.Value)) continue;

            // The line keeps whichever state it was in. Editing an entry is not a way to switch it
            // on, and an edit that silently enabled a commented line would change what resolves
            // without ever saying so.
            rewrites[entry.Line] =
                Present(HostsLineValidator.ComposeEntry(entry.Value), HostsTokenizer.IsActive(original));
        }

        foreach (var line in editable.Where(line => !kept.Contains(line)))
        {
            rewrites[line] = null;
        }

        // New rows land after the last line that survives, so an option's entries stay together and
        // in the order the dialog showed them.
        var anchor = (kept.Count > 0 ? kept.Max() : target.DirectiveLines.Max()) + 1;
        anchor = Math.Min(anchor, group.EndLine + 1);

        var additions = update.Entries
            .Where(entry => entry.Line == 0)
            .Select(entry => Present(HostsLineValidator.ComposeEntry(entry.Value), target.IsOn))
            .ToArray();

        var inserts = additions.Length == 0
            ? new Dictionary<int, IReadOnlyList<string>>()
            : new Dictionary<int, IReadOnlyList<string>> { [anchor] = additions };

        return Splice(document, rewrites, inserts);
    }

    // ── shared ───────────────────────────────────────────────────────────────

    /// <summary>Every line an option is responsible for — its directives and its own content.</summary>
    private static IEnumerable<int> OwnLinesOf(HostsOption option) =>
        option.DirectiveLines
            .Concat(option.OwnedLines)
            .Concat(option.InlineLines)
            .Concat(option.ParkedLines);

    private static HostsGroup Group(HostsDocument document, HostsMap map, string group) =>
        map.Find(group) ?? throw new KeyNotFoundException($"No group named '{group}' in {document.Path}.");

    private static HostsOption Option(HostsDocument document, HostsMap map, string group, string option) =>
        Group(document, map, group).Find(option)
        ?? throw new KeyNotFoundException($"Group '{group}' has no option named '{option}'.");

    /// <summary>
    /// Swaps a line's directive for a new one, keeping whatever preceded it — indentation, or a
    /// byte-order mark on the very first line.
    /// </summary>
    private static string ReplaceDirective(string text, HostsDialect dialect, string directive)
    {
        var index = text.IndexOf(dialect.Prefix, StringComparison.Ordinal);

        return index < 0 ? directive : text[..index] + directive;
    }

    /// <summary>
    /// Whether a line already says what the edited entry says, ignoring how it is spaced. Both
    /// sides are put through <see cref="HostsLineValidator.ComposeEntry"/>, which is the one
    /// definition of an entry's canonical form.
    /// </summary>
    private static bool IsUnchanged(string original, NewHostsEntry edited)
    {
        var current = HostsLineValidator.DecomposeEntry(original);
        if (current is null) return false;

        return string.Equals(
            HostsLineValidator.ComposeEntry(current),
            HostsLineValidator.ComposeEntry(edited),
            StringComparison.Ordinal);
    }

    private static string Present(string content, bool enabled) =>
        enabled ? content : HostsTokenizer.Comment(content);

    private static HostsMutation Nothing(HostsDocument document) => new(document, []);

    private static void Require(IReadOnlyList<string> problems)
    {
        if (problems.Count == 0) return;

        throw new ArgumentException(string.Join(" ", problems));
    }

    /// <summary>
    /// Builds the new document and the change list describing it, in one pass.
    /// </summary>
    /// <param name="rewrites">Line number to replacement text; a null value deletes the line.</param>
    /// <param name="inserts">Line number to the block of new lines that goes immediately before it.</param>
    private static HostsMutation Splice(
        HostsDocument document,
        IReadOnlyDictionary<int, string?> rewrites,
        IReadOnlyDictionary<int, IReadOnlyList<string>> inserts)
    {
        if (rewrites.Count == 0 && inserts.Count == 0) return Nothing(document);

        var lines = new List<HostsLine>(document.Lines.Count + inserts.Sum(pair => pair.Value.Count));
        var changes = new List<HostsLineChange>();

        void EmitInsertionsAt(int anchor)
        {
            if (!inserts.TryGetValue(anchor, out var block)) return;

            foreach (var text in block)
            {
                lines.Add(new HostsLine(lines.Count + 1, text, document.DefaultNewLine));
                changes.Add(new HostsLineChange(anchor, string.Empty, text, HostsLineChangeKind.Inserted)
                {
                    ResultLine = lines.Count,
                });
            }
        }

        foreach (var original in document.Lines)
        {
            EmitInsertionsAt(original.Number);

            if (!rewrites.TryGetValue(original.Number, out var replacement))
            {
                lines.Add(original with { Number = lines.Count + 1 });
                continue;
            }

            if (replacement is null)
            {
                changes.Add(new HostsLineChange(
                    original.Number, original.Text, string.Empty, HostsLineChangeKind.Deleted));

                continue;
            }

            lines.Add(new HostsLine(lines.Count + 1, replacement, original.NewLine));
            changes.Add(new HostsLineChange(
                original.Number, original.Text, replacement, HostsLineChangeKind.Edited)
            {
                ResultLine = lines.Count,
            });
        }

        EmitInsertionsAt(document.Lines.Count + 1);

        // Deleting the file's last line leaves the new one carrying that line's terminator, which
        // may be none at all. Nothing else needs saying about it — the checker rebuilds the same way
        // and compares, so a mismatch here would fail rather than reach the disk.
        changes.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.ResultLine.CompareTo(b.ResultLine));

        return new HostsMutation(document.WithLines(lines), changes);
    }
}
