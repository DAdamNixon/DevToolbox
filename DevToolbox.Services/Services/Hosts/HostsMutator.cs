using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Turns "switch this group to that option" into a new document plus a list of exactly which
/// lines changed.
/// <para>
/// Pure: nothing here reads or writes a file. Every edit is driven by the line numbers the parser
/// recorded, never by searching the file's text. The legacy tool sliced with
/// <c>indexOf('##key:' + name)</c>, which finds the wrong place when one group's name is a prefix
/// of another's and mangles the file outright when the name is absent and the search returns -1.
/// </para>
/// </summary>
public static class HostsMutator
{
    /// <summary>
    /// Switches <paramref name="group"/> to <paramref name="option"/>: that option's lines are
    /// enabled and every sibling's are commented out. Directive lines, blank lines, parked lines
    /// and everything outside the group are returned untouched.
    /// </summary>
    /// <param name="option">The option to switch on, or <c>null</c> to turn the whole group off.</param>
    /// <param name="includeSuspectLines">
    /// Sweep in the lines the risk analyzer quarantined. Off by default, and should only ever be
    /// on because a developer looked at the diff and said so.
    /// </param>
    /// <exception cref="KeyNotFoundException">
    /// The group, or the named option within it, does not exist. Deliberately loud: the legacy tool
    /// silently rewrote the wrong region in this situation.
    /// </exception>
    public static HostsMutation SetOption(
        HostsDocument document,
        HostsMap map,
        string group,
        string? option,
        bool includeSuspectLines = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        var target = map.Find(group)
                     ?? throw new KeyNotFoundException($"No group named '{group}' in {document.Path}.");

        if (option is not null && target.Find(option) is null)
        {
            throw new KeyNotFoundException($"Group '{group}' has no option named '{option}'.");
        }

        var changes = new List<HostsLineChange>();

        foreach (var candidate in target.Options)
        {
            var isTarget = option is not null && string.Equals(candidate.Name, option, StringComparison.Ordinal);

            foreach (var number in candidate.ToggleableLines(includeSuspectLines))
            {
                var before = document.Lines[number - 1].Text;

                // The whole line is commented or uncommented, trailing inline directive included —
                // the marker always sits at the front, so the directive survives either way.
                var after = isTarget ? HostsTokenizer.Uncomment(before) : HostsTokenizer.Comment(before);
                if (string.Equals(before, after, StringComparison.Ordinal)) continue;

                changes.Add(new HostsLineChange(
                    number,
                    before,
                    after,
                    isTarget ? HostsLineChangeKind.Uncommented : HostsLineChangeKind.Commented)
                {
                    // A switch never moves a line, so it ends up where it started.
                    ResultLine = number,
                });
            }
        }

        changes.Sort((a, b) => a.Line.CompareTo(b.Line));

        return new HostsMutation(Apply(document, changes), changes);
    }

    /// <summary>
    /// Inserts a scope-reset directive immediately before <paramref name="beforeLine"/>, closing
    /// whichever group was running on past it.
    /// <para>
    /// The returned document's line numbers shift from the insertion point down, so any
    /// <see cref="HostsMap"/> held against the old document is stale — reparse after applying.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The line is outside the file. Appending past the last line is rejected too: a closing
    /// directive with nothing after it excludes nothing.
    /// </exception>
    public static HostsMutation InsertClear(HostsDocument document, HostsDialect dialect, int beforeLine)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(dialect);

        if (beforeLine < 1 || beforeLine > document.Lines.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beforeLine),
                beforeLine,
                $"Must be an existing line between 1 and {document.Lines.Count}.");
        }

        var inserted = new HostsLine(beforeLine, dialect.ClearDirective, document.DefaultNewLine);

        var lines = new List<HostsLine>(document.Lines.Count + 1);
        lines.AddRange(document.Lines.Take(beforeLine - 1));
        lines.Add(inserted);
        lines.AddRange(document.Lines.Skip(beforeLine - 1));

        var changes = new[]
        {
            new HostsLineChange(beforeLine, string.Empty, inserted.Text, HostsLineChangeKind.Inserted)
            {
                ResultLine = beforeLine,
            },
        };

        return new HostsMutation(document.WithLines(lines), changes);
    }

    // ── authoring ────────────────────────────────────────────────────────────
    //
    // Everything below only ever adds lines. Nothing here renames, reorders or removes anything,
    // which is why it needs no new safety contract: HostsInvariantChecker already proves that an
    // insertion added exactly the listed lines and left every original one byte-identical. An
    // editing feature would need that contract widened; an adding one does not.

    /// <summary>Carries out whichever addition it was given.</summary>
    /// <exception cref="ArgumentException">The addition is not valid — see <see cref="HostsLineValidator"/>.</exception>
    /// <exception cref="KeyNotFoundException">It names a group or option that does not exist.</exception>
    /// <exception cref="InvalidOperationException">The name is taken, or the option has no block to add to.</exception>
    public static HostsMutation Add(HostsDocument document, HostsMap map, HostsAddition addition)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(addition);

        return addition switch
        {
            HostsAddition.Group group => AppendGroup(document, map.Dialect, group.Value),
            HostsAddition.Option option => AddOption(document, map, option.InGroup, option.Value),
            HostsAddition.Entries entries => AddEntries(document, map, entries.InGroup, entries.InOption, entries.Values),
            _ => throw new ArgumentOutOfRangeException(nameof(addition), addition, "Unknown addition."),
        };
    }

    /// <summary>
    /// Adds a new group, with its options, at the end of the file.
    /// <para>
    /// At the end rather than anywhere chosen, because appending is the one position that cannot
    /// change which option owns an existing line. The block is always closed with a scope-reset
    /// directive — the missing one is the single most damaging thing found in real files, and a
    /// tool that writes annotations has no business creating another.
    /// </para>
    /// <para>
    /// Every entry is written commented out, so adding a group never changes what the machine
    /// resolves. Switching it on afterwards is a switch, and goes through the switch's own preview.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">The group is not valid — see <see cref="HostsLineValidator"/>.</exception>
    public static HostsMutation AppendGroup(HostsDocument document, HostsDialect dialect, NewHostsGroup group)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(group);

        Require(HostsLineValidator.ValidateGroup(group, dialect));

        var block = new List<string>();

        // One blank line of separation, unless the file already ends with one.
        if (document.Lines.Count > 0 && HostsTokenizer.HasContent(document.Lines[^1].Text))
        {
            block.Add(string.Empty);
        }

        block.Add(dialect.GroupDirective(group.Name.Trim()));

        foreach (var option in group.Options)
        {
            block.AddRange(ComposeOption(dialect, option, enabled: false));
        }

        block.Add(dialect.ClearDirective);

        return Insert(document, document.Lines.Count + 1, block);
    }

    /// <summary>
    /// Adds an option to an existing group, immediately before whatever closes that group.
    /// <para>
    /// A new option is always added switched off. Turning it on would mean turning its siblings
    /// off, which is a switch and belongs in the path that previews one.
    /// </para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such group.</exception>
    /// <exception cref="InvalidOperationException">The group already has an option with that name.</exception>
    public static HostsMutation AddOption(
        HostsDocument document,
        HostsMap map,
        string group,
        NewHostsOption option)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(option);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        var target = map.Find(group)
                     ?? throw new KeyNotFoundException($"No group named '{group}' in {document.Path}.");

        Require(HostsLineValidator.ValidateOption(option, map.Dialect));

        var name = option.Name.Trim();

        if (target.Options.Any(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Group '{group}' already has an option called '{name}'.");
        }

        // EndLine is the last line of the group's scope, so one past it is immediately before the
        // scope-reset directive, the next group, or the end of the file — whichever ends this group.
        return Insert(document, target.EndLine + 1, ComposeOption(map.Dialect, option, enabled: false));
    }

    /// <summary>
    /// Adds entries to an existing option, after the lines it already owns.
    /// <para>
    /// The new lines are enabled if the option currently is, and commented out if it is not. An
    /// option that gained a switched-off line while switched on would read as half-applied — a
    /// warning, for something a developer deliberately did — and one that gained a live line while
    /// switched off would resolve names its own card says are not in use.
    /// </para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such group, or no such option in it.</exception>
    /// <exception cref="InvalidOperationException">The option exists only as per-line tags, so it has no body to add to.</exception>
    public static HostsMutation AddEntries(
        HostsDocument document,
        HostsMap map,
        string group,
        string option,
        IReadOnlyList<NewHostsEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(option);

        var targetGroup = map.Find(group)
                          ?? throw new KeyNotFoundException($"No group named '{group}' in {document.Path}.");

        var targetOption = targetGroup.Find(option)
                           ?? throw new KeyNotFoundException($"Group '{group}' has no option named '{option}'.");

        if (entries.Count == 0) return new HostsMutation(document, []);

        var problems = entries.SelectMany(entry => HostsLineValidator.ValidateEntry(entry, map.Dialect)).ToArray();
        Require(problems);

        if (targetOption.DirectiveLine == 0)
        {
            throw new InvalidOperationException(
                $"'{group}/{option}' is only ever tagged line by line, so it has no block to add to. "
                + "Add the entry in the file and tag it the same way.");
        }

        // Deliberately not counting suspect lines: they are the content the risk analyzer believes
        // was never meant to be in scope, and a new entry belongs with the option's own body, not
        // after somebody else's stray block.
        var anchor = targetOption.OwnedLines
            .Concat(targetOption.InlineLines)
            .Concat(targetOption.ParkedLines)
            .Append(targetOption.DirectiveLine)
            .Max();

        var texts = entries
            .Select(entry => Present(HostsLineValidator.ComposeEntry(entry), targetOption.IsOn))
            .ToArray();

        return Insert(document, Math.Min(anchor, targetGroup.EndLine) + 1, texts);
    }

    /// <summary>An option's directive line followed by its entries.</summary>
    private static List<string> ComposeOption(HostsDialect dialect, NewHostsOption option, bool enabled)
    {
        var lines = new List<string>(option.EntryList.Count + 1)
        {
            dialect.OptionDirective(option.Name.Trim(), option.Severity),
        };

        foreach (var entry in option.EntryList)
        {
            lines.Add(Present(HostsLineValidator.ComposeEntry(entry), enabled));
        }

        return lines;
    }

    /// <summary>
    /// Content as it should appear. Commenting goes through the same helper every switch uses, so
    /// an authored line is indistinguishable from one this tool has been switching for years.
    /// </summary>
    private static string Present(string content, bool enabled) =>
        enabled ? content : HostsTokenizer.Comment(content);

    /// <exception cref="ArgumentException">Always, when there is anything to report.</exception>
    private static void Require(IReadOnlyList<string> problems)
    {
        if (problems.Count == 0) return;

        throw new ArgumentException(string.Join(" ", problems));
    }

    /// <summary>
    /// Splices new lines in at <paramref name="atLine"/>, pushing everything from there down.
    /// <para>
    /// The returned document's line numbers shift from the insertion point on, so any
    /// <see cref="HostsMap"/> held against the old document is stale — reparse after applying.
    /// </para>
    /// </summary>
    /// <param name="atLine">1-based position the first new line takes; one past the last line appends.</param>
    private static HostsMutation Insert(HostsDocument document, int atLine, IReadOnlyList<string> texts)
    {
        if (atLine < 1 || atLine > document.Lines.Count + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(atLine),
                atLine,
                $"Must be between 1 and {document.Lines.Count + 1}.");
        }

        if (texts.Count == 0) return new HostsMutation(document, []);

        var lines = new List<HostsLine>(document.Lines.Count + texts.Count);
        lines.AddRange(document.Lines.Take(atLine - 1));

        // Only the very last line of a file can lack a terminator, and only when the file did not
        // end with a newline. Appending after it has to give it one, or the first new line would run
        // onto the end of it. This is the one byte an insertion may change outside its own lines,
        // and HostsInvariantChecker allows exactly that and nothing more.
        if (lines.Count == document.Lines.Count && lines.Count > 0 && lines[^1].NewLine.Length == 0)
        {
            lines[^1] = lines[^1] with { NewLine = document.DefaultNewLine };
        }

        var changes = new List<HostsLineChange>(texts.Count);

        for (var index = 0; index < texts.Count; index++)
        {
            var number = atLine + index;
            lines.Add(new HostsLine(number, texts[index], document.DefaultNewLine));

            // Line is the anchor in the original — the same for every line of the block — while
            // ResultLine is where each one lands. Together they make the list an edit script the
            // checker can replay.
            changes.Add(new HostsLineChange(atLine, string.Empty, texts[index], HostsLineChangeKind.Inserted)
            {
                ResultLine = number,
            });
        }

        lines.AddRange(document.Lines.Skip(atLine - 1));

        return new HostsMutation(document.WithLines(lines), changes);
    }

    /// <summary>Rewrites the named lines in place. Every other line keeps its text and its terminator.</summary>
    private static HostsDocument Apply(HostsDocument document, IReadOnlyList<HostsLineChange> changes)
    {
        if (changes.Count == 0) return document;

        var lines = document.Lines.ToArray();
        foreach (var change in changes)
        {
            lines[change.Line - 1] = lines[change.Line - 1].WithText(change.After);
        }

        return document.WithLines(lines);
    }
}
