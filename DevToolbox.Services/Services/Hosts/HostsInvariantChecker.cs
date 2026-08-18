using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// The last gate before a hosts file is written: proves that a mutation did only what it claimed.
/// <para>
/// The contract has two halves, and which half applies depends on what was asked for.
/// </para>
/// <para>
/// <b>Everything gets the total check.</b> The change list is treated as an edit script: the
/// original document plus that script is rebuilt line by line and required to equal the proposed
/// result exactly, terminators included. So the change list can never be an incomplete or
/// flattering description of the write. Whatever the developer approved in the diff is, byte for
/// byte, what reaches the disk — a bug upstream can produce a wrong edit, but never a hidden one.
/// </para>
/// <para>
/// <b>Switching also gets the strict check.</b> A change that only moves a comment marker must
/// leave the line's content identical once markers are stripped. That assertion is what makes
/// switching an option provably unable to alter or lose an entry, and it still holds — it is
/// applied per change, so it protects the marker changes inside an edit too, not just a change set
/// made entirely of them. Editing deliberately has no such promise: rewriting content is the
/// point. The diff preview is what stands in its place, and the total check is what makes the diff
/// trustworthy.
/// </para>
/// </summary>
public static class HostsInvariantChecker
{
    /// <exception cref="InvalidOperationException">
    /// An invariant is broken. The caller must abandon the write; the message names the line.
    /// </exception>
    public static void Verify(HostsDocument before, HostsDocument after, IReadOnlyList<HostsLineChange> changes)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(changes);

        VerifyFileShape(before, after);
        VerifyEachChange(before, changes);
        VerifyResultIsExactlyTheScript(before, after, changes);
    }

    /// <summary>The things that must survive any mutation, whatever it was.</summary>
    private static void VerifyFileShape(HostsDocument before, HostsDocument after)
    {
        if (!after.Preamble.SequenceEqual(before.Preamble))
        {
            Fail("the byte-order mark changed");
        }

        if (!string.Equals(after.DefaultNewLine, before.DefaultNewLine, StringComparison.Ordinal))
        {
            Fail("the newline style changed");
        }

        if (after.Encoding.CodePage != before.Encoding.CodePage)
        {
            Fail($"the encoding changed from {before.Encoding.WebName} to {after.Encoding.WebName}");
        }
    }

    // ── one change at a time ─────────────────────────────────────────────────

    private static void VerifyEachChange(HostsDocument before, IReadOnlyList<HostsLineChange> changes)
    {
        var claimed = new HashSet<int>();

        foreach (var change in changes)
        {
            if (change.Kind == HostsLineChangeKind.Inserted)
            {
                VerifyInsertion(before, change);
                continue;
            }

            if (change.Line < 1 || change.Line > before.Lines.Count)
            {
                Fail($"a change refers to line {change.Line}, which is not in a file of {before.Lines.Count} lines");
            }

            if (!claimed.Add(change.Line)) Fail($"line {change.Line} is listed as changed twice");

            var original = before.Lines[change.Line - 1];

            if (!string.Equals(change.Before, original.Text, StringComparison.Ordinal))
            {
                Fail($"line {change.Line}'s recorded 'before' does not match the file");
            }

            switch (change.Kind)
            {
                case HostsLineChangeKind.Commented:
                case HostsLineChangeKind.Uncommented:
                    VerifyMarkerOnly(change);
                    break;

                case HostsLineChangeKind.Edited:
                    if (string.Equals(change.Before, change.After, StringComparison.Ordinal))
                    {
                        Fail($"line {change.Line} is listed as edited but is unchanged");
                    }

                    break;

                case HostsLineChangeKind.Deleted:
                    if (change.After.Length > 0)
                    {
                        Fail($"line {change.Line} is listed as deleted but records replacement text");
                    }

                    if (change.ResultLine != 0)
                    {
                        Fail($"line {change.Line} is listed as deleted but claims to end up at line {change.ResultLine}");
                    }

                    break;

                default:
                    Fail($"line {change.Line} has an unrecognised change kind {change.Kind}");
                    break;
            }
        }
    }

    private static void VerifyInsertion(HostsDocument before, HostsLineChange change)
    {
        if (change.Before.Length > 0)
        {
            Fail($"an insertion at line {change.ResultLine} records text that was there before it");
        }

        // The anchor is a position in the original, and one past the end means appended.
        if (change.Line < 1 || change.Line > before.Lines.Count + 1)
        {
            Fail($"an insertion is anchored at line {change.Line}, outside a file of {before.Lines.Count} lines");
        }

        if (change.ResultLine < 1) Fail("an insertion does not say where it ends up");
    }

    /// <summary>
    /// The strict half. A switch may enable or disable a line; it may never alter what that line
    /// says. Applied per change, so the marker changes inside a larger edit are held to it too.
    /// </summary>
    private static void VerifyMarkerOnly(HostsLineChange change)
    {
        var contentBefore = HostsTokenizer.StripComment(change.Before);
        var contentAfter = HostsTokenizer.StripComment(change.After);

        if (!string.Equals(contentBefore, contentAfter, StringComparison.Ordinal))
        {
            Fail($"line {change.Line} had its content altered, not just its comment marker: "
                 + $"'{contentBefore}' became '{contentAfter}'");
        }

        var wasActive = HostsTokenizer.IsActive(change.Before);
        var isActive = HostsTokenizer.IsActive(change.After);

        var expected = change.Kind == HostsLineChangeKind.Commented;

        if (wasActive != expected || isActive == expected)
        {
            Fail($"line {change.Line} is recorded as {(expected ? "commented out" : "enabled")} but went from "
                 + $"{Describe(wasActive)} to {Describe(isActive)}");
        }
    }

    // ── the total check ──────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the original with the change list applied and requires the proposed result to match
    /// it exactly. This is what makes the change list a complete description rather than a summary:
    /// a line altered without being listed, a listed change that was not actually made, or a
    /// terminator quietly rewritten all fail here.
    /// </summary>
    private static void VerifyResultIsExactlyTheScript(
        HostsDocument before,
        HostsDocument after,
        IReadOnlyList<HostsLineChange> changes)
    {
        var expected = Rebuild(before, changes);

        if (expected.Count != after.Lines.Count)
        {
            Fail($"the change list describes a file of {expected.Count} lines but the result has {after.Lines.Count}");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var want = expected[index];
            var got = after.Lines[index];

            if (!string.Equals(want.Text, got.Text, StringComparison.Ordinal))
            {
                Fail($"line {index + 1} of the result is not what the change list describes: "
                     + $"expected '{Clip(want.Text)}', found '{Clip(got.Text)}'");
            }

            if (!string.Equals(want.NewLine, got.NewLine, StringComparison.Ordinal))
            {
                Fail($"line {index + 1} of the result does not end the way the original did");
            }
        }
    }

    private static List<HostsLine> Rebuild(HostsDocument before, IReadOnlyList<HostsLineChange> changes)
    {
        var rewritten = new Dictionary<int, HostsLineChange>();
        var deleted = new HashSet<int>();
        var inserted = new Dictionary<int, List<HostsLineChange>>();

        foreach (var change in changes)
        {
            if (change.Kind == HostsLineChangeKind.Inserted)
            {
                if (!inserted.TryGetValue(change.Line, out var atAnchor))
                {
                    atAnchor = [];
                    inserted[change.Line] = atAnchor;
                }

                atAnchor.Add(change);
                continue;
            }

            if (change.Kind == HostsLineChangeKind.Deleted) deleted.Add(change.Line);
            else rewritten[change.Line] = change;
        }

        var result = new List<HostsLine>(before.Lines.Count + changes.Count);

        void EmitInsertionsAnchoredAt(int anchor)
        {
            if (!inserted.TryGetValue(anchor, out var block)) return;

            foreach (var change in block.OrderBy(c => c.ResultLine))
            {
                result.Add(new HostsLine(result.Count + 1, change.After, before.DefaultNewLine));
            }
        }

        foreach (var original in before.Lines)
        {
            EmitInsertionsAnchoredAt(original.Number);

            if (deleted.Contains(original.Number)) continue;

            var text = rewritten.TryGetValue(original.Number, out var change) ? change.After : original.Text;
            result.Add(new HostsLine(result.Count + 1, text, original.NewLine));
        }

        EmitInsertionsAnchoredAt(before.Lines.Count + 1);

        // Only the final line of a file may lack a terminator. Appending after a file that had none
        // therefore has to give its old last line one, or the first appended line would be joined
        // onto the end of it. Normalising here rather than allowing it as a special case means the
        // comparison stays exact: the rule is applied to the expectation, not waived on the result.
        for (var index = 0; index < result.Count - 1; index++)
        {
            if (result[index].NewLine.Length == 0)
            {
                result[index] = result[index] with { NewLine = before.DefaultNewLine };
            }
        }

        return result;
    }

    private static string Describe(bool active) => active ? "enabled" : "commented out";

    private static string Clip(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static void Fail(string reason) =>
        throw new InvalidOperationException(
            $"Refusing to write the hosts file: {reason}. Nothing has been changed on disk.");
}
