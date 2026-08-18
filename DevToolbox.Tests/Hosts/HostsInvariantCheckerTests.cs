using System.Text;
using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// The last gate before a write. Its value is entirely in what it refuses, so these tests hand it
/// mutations that a bug upstream could plausibly produce and require it to reject each one.
/// </summary>
public class HostsInvariantCheckerTests
{
    private static HostsDocument Doc(string text) =>
        HostsDocumentCodec.FromBytes("test", Encoding.UTF8.GetBytes(text), DateTime.UnixEpoch);

    private static void AssertRefuses(HostsDocument before, HostsDocument after, params HostsLineChange[] changes)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => HostsInvariantChecker.Verify(before, after, changes));

        Assert.Contains("Nothing has been changed on disk", error.Message);
    }

    // ── what it accepts ──────────────────────────────────────────────────────

    [Fact]
    public void A_genuine_switch_is_accepted()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var mutation = HostsMutator.SetOption(document, map, "DB Server", "Live");

        HostsInvariantChecker.Verify(document, mutation.Document, mutation.Changes);
    }

    [Fact]
    public void A_genuine_insertion_is_accepted()
    {
        var (document, map) = HostsSamples.Parse(HostsSamples.CrlfBom);
        var mutation = HostsMutator.InsertClear(document, map.Dialect, 84);

        HostsInvariantChecker.Verify(document, mutation.Document, mutation.Changes);
    }

    [Fact]
    public void A_mutation_that_changes_nothing_is_accepted()
    {
        var document = Doc("127.0.0.1 a.example.com\r\n");

        HostsInvariantChecker.Verify(document, document, []);
    }

    // ── the one byte an insertion may change outside its own lines ───────────

    /// <summary>
    /// Appending to a file that does not end with a newline has to give its last line one, or the
    /// first appended line would be welded onto the end of it. This is the only case, and these
    /// three tests exist to keep it that narrow — a wider allowance would let an insertion quietly
    /// rewrite terminators anywhere, which is exactly the CRLF damage the legacy tool did.
    /// </summary>
    [Fact]
    public void A_final_line_may_gain_a_terminator_when_something_is_appended_after_it()
    {
        var before = Doc("a\n127.0.0.1 b.example.com");

        // Built with WithLines rather than from fresh text, because that is what the mutator does:
        // the result keeps the original's encoding, preamble and newline style, and the checker
        // compares against those.
        var after = before.WithLines(
        [
            before.Lines[0],
            before.Lines[1] with { NewLine = before.DefaultNewLine },
            new HostsLine(3, "##clear", before.DefaultNewLine),
        ]);

        HostsInvariantChecker.Verify(before, after,
            [new HostsLineChange(3, string.Empty, "##clear", HostsLineChangeKind.Inserted) { ResultLine = 3 }]);
    }

    [Fact]
    public void A_terminator_may_not_change_on_a_line_that_is_not_the_last()
    {
        var before = Doc("127.0.0.1 a.example.com\nb\n");
        var after = Doc("127.0.0.1 a.example.com\r\nb\n##clear\n");

        AssertRefuses(before, after, new HostsLineChange(3, string.Empty, "##clear", HostsLineChangeKind.Inserted));
    }

    [Fact]
    public void A_final_line_may_not_gain_a_terminator_when_the_insertion_went_somewhere_else()
    {
        var before = Doc("a\nb");
        var after = Doc("##clear\na\nb\n");

        AssertRefuses(before, after, new HostsLineChange(1, string.Empty, "##clear", HostsLineChangeKind.Inserted));
    }

    [Fact]
    public void An_existing_terminator_may_not_be_replaced_by_an_insertion()
    {
        var before = Doc("a\nb\n");

        // The last line, so the "is it the last one" guard passes and the check being exercised is
        // the one that only lets a *missing* terminator be supplied.
        var after = Doc("a\nb\r\n##clear\n");

        AssertRefuses(before, after, new HostsLineChange(3, string.Empty, "##clear", HostsLineChangeKind.Inserted));
    }

    // ── the assertion that matters ───────────────────────────────────────────

    /// <summary>
    /// The decisive check: a switch may enable or disable a line, never alter what it says. This is
    /// the failure mode that would cost a developer an entry, and the reason the checker runs even
    /// on a change set a developer already approved from a diff.
    /// </summary>
    [Fact]
    public void A_change_that_alters_content_rather_than_markers_is_refused()
    {
        var before = Doc("127.0.0.1 a.example.com\r\n");
        var after = Doc("# 127.0.0.1 b.example.com\r\n");

        var error = Assert.Throws<InvalidOperationException>(() => HostsInvariantChecker.Verify(
            before,
            after,
            [new HostsLineChange(1, before.Lines[0].Text, after.Lines[0].Text, HostsLineChangeKind.Commented)]));

        Assert.Contains("had its content altered", error.Message);
    }

    [Fact]
    public void A_change_that_drops_a_hostname_is_refused()
    {
        var before = Doc("127.0.0.1 a.example.com b.example.com\r\n");
        var after = Doc("# 127.0.0.1 a.example.com\r\n");

        AssertRefuses(
            before,
            after,
            new HostsLineChange(1, before.Lines[0].Text, after.Lines[0].Text, HostsLineChangeKind.Commented));
    }

    // ── unlisted and misreported changes ─────────────────────────────────────

    [Fact]
    public void A_line_that_changed_without_being_listed_is_refused()
    {
        var before = Doc("127.0.0.1 a.example.com\r\n127.0.0.1 b.example.com\r\n");
        var after = Doc("127.0.0.1 a.example.com\r\n# 127.0.0.1 b.example.com\r\n");

        AssertRefuses(before, after);
    }

    [Fact]
    public void A_change_whose_recorded_before_does_not_match_the_file_is_refused()
    {
        var before = Doc("127.0.0.1 a.example.com\r\n");
        var after = Doc("# 127.0.0.1 a.example.com\r\n");

        AssertRefuses(
            before,
            after,
            new HostsLineChange(1, "something else entirely", after.Lines[0].Text, HostsLineChangeKind.Commented));
    }

    [Fact]
    public void The_same_line_listed_twice_is_refused()
    {
        var before = Doc("127.0.0.1 a.example.com\r\n");
        var after = Doc("# 127.0.0.1 a.example.com\r\n");
        var change = new HostsLineChange(1, before.Lines[0].Text, after.Lines[0].Text, HostsLineChangeKind.Commented);

        AssertRefuses(before, after, change, change);
    }

    [Fact]
    public void A_change_labelled_with_the_wrong_direction_is_refused()
    {
        var before = Doc("# 127.0.0.1 a.example.com\r\n");
        var after = Doc("127.0.0.1 a.example.com\r\n");

        // The line was enabled, but the change claims it was commented out.
        AssertRefuses(
            before,
            after,
            new HostsLineChange(1, before.Lines[0].Text, after.Lines[0].Text, HostsLineChangeKind.Commented));
    }

    // ── shape of the file ────────────────────────────────────────────────────

    [Fact]
    public void Losing_a_line_without_declaring_an_insertion_is_refused()
    {
        var before = Doc("127.0.0.1 a.example.com\r\n127.0.0.1 b.example.com\r\n");
        var after = Doc("127.0.0.1 a.example.com\r\n");

        AssertRefuses(before, after);
    }

    [Fact]
    public void Losing_the_byte_order_mark_is_refused()
    {
        const string text = "127.0.0.1 a.example.com\r\n";
        var withMark = HostsDocumentCodec.FromBytes(
            "test",
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)],
            DateTime.UnixEpoch);

        var error = Assert.Throws<InvalidOperationException>(
            () => HostsInvariantChecker.Verify(withMark, Doc(text), []));

        Assert.Contains("byte-order mark changed", error.Message);
    }

    [Fact]
    public void Changing_a_lines_terminator_is_refused()
    {
        var before = Doc("127.0.0.1 a.example.com\r\n127.0.0.1 b.example.com\r\n");
        var after = before.WithLines(
            before.Lines.Select((line, index) => index == 0 ? line with { NewLine = "\n" } : line));

        var error = Assert.Throws<InvalidOperationException>(() => HostsInvariantChecker.Verify(before, after, []));

        Assert.Contains("does not end the way the original did", error.Message);
    }

    [Fact]
    public void Changing_the_newline_style_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => HostsInvariantChecker.Verify(
            Doc("127.0.0.1 a.example.com\r\n"),
            Doc("127.0.0.1 a.example.com\n"),
            []));

        Assert.Contains("newline style changed", error.Message);
    }

    // ── insertions ───────────────────────────────────────────────────────────

    /// <summary>
    /// Editing produces change sets that insert, rewrite and delete at once — renaming an option
    /// while correcting one entry and dropping another is a single action and deserves a single
    /// diff. So mixed sets are accepted, and held to the same total check as any other: the result
    /// must be exactly what the script describes.
    /// </summary>
    [Fact]
    public void A_change_set_that_inserts_rewrites_and_deletes_at_once_is_accepted()
    {
        var before = Doc("a\r\nb\r\nc\r\n");
        var after = Doc("a\r\nB\r\nnew\r\n");

        HostsInvariantChecker.Verify(before, after,
        [
            new HostsLineChange(2, "b", "B", HostsLineChangeKind.Edited) { ResultLine = 2 },
            new HostsLineChange(3, "c", string.Empty, HostsLineChangeKind.Deleted),
            new HostsLineChange(4, string.Empty, "new", HostsLineChangeKind.Inserted) { ResultLine = 3 },
        ]);
    }

    [Fact]
    public void A_deletion_that_was_not_listed_is_refused()
    {
        var before = Doc("a\r\nb\r\nc\r\n");

        // Says it removed one line; actually removed two.
        var after = Doc("a\r\n");

        AssertRefuses(before, after, new HostsLineChange(2, "b", string.Empty, HostsLineChangeKind.Deleted));
    }

    [Fact]
    public void An_edit_that_rewrote_a_line_it_did_not_mention_is_refused()
    {
        var before = Doc("a\r\nb\r\n");

        // Renames the first line as claimed, and quietly rewrites the second as well.
        var after = Doc("A\r\nB\r\n");

        AssertRefuses(before, after, new HostsLineChange(1, "a", "A", HostsLineChangeKind.Edited) { ResultLine = 1 });
    }

    [Fact]
    public void An_edit_whose_recorded_before_does_not_match_the_file_is_refused()
    {
        var before = Doc("a\r\n");
        var after = Doc("A\r\n");

        AssertRefuses(before, after, new HostsLineChange(1, "z", "A", HostsLineChangeKind.Edited) { ResultLine = 1 });
    }

    [Fact]
    public void An_edit_that_claims_to_change_a_line_into_itself_is_refused()
    {
        var document = Doc("a\r\n");

        AssertRefuses(document, document, new HostsLineChange(1, "a", "a", HostsLineChangeKind.Edited) { ResultLine = 1 });
    }

    [Fact]
    public void An_insertion_that_also_altered_an_original_line_is_refused()
    {
        var before = Doc("127.0.0.1 a.example.com\r\n127.0.0.1 b.example.com\r\n");

        // Inserts a directive and quietly comments out the line below it.
        var after = Doc("127.0.0.1 a.example.com\r\n##clear\r\n# 127.0.0.1 b.example.com\r\n");

        var error = Assert.Throws<InvalidOperationException>(() => HostsInvariantChecker.Verify(
            before,
            after,
            [new HostsLineChange(2, string.Empty, "##clear", HostsLineChangeKind.Inserted) { ResultLine = 2 }]));

        Assert.Contains("is not what the change list describes", error.Message);
    }

    [Fact]
    public void An_insertion_that_did_not_change_the_line_count_is_refused()
    {
        var document = Doc("127.0.0.1 a.example.com\r\n");

        AssertRefuses(
            document,
            document,
            new HostsLineChange(1, string.Empty, "##clear", HostsLineChangeKind.Inserted) { ResultLine = 1 });
    }

    [Fact]
    public void An_inserted_line_that_does_not_match_the_recorded_change_is_refused()
    {
        var before = Doc("127.0.0.1 a.example.com\r\n");
        var after = Doc("##something-else\r\n127.0.0.1 a.example.com\r\n");

        AssertRefuses(
            before,
            after,
            new HostsLineChange(1, string.Empty, "##clear", HostsLineChangeKind.Inserted) { ResultLine = 1 });
    }

    // ── it holds across every sample ─────────────────────────────────────────

    /// <summary>
    /// Every switch the parser can describe, on every sample, put through the checker. If any
    /// combination of grammar and mutation could violate an invariant, this is what finds it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_switch_on_every_sample_satisfies_the_invariants(string sample)
    {
        var dialect = sample == HostsSamples.AltDialect ? HostsSamples.AlternateDialect : null;
        var (document, map) = HostsSamples.Parse(sample, dialect);

        foreach (var group in map.Groups)
        {
            // Null stands for "turn the whole group off".
            foreach (var option in group.Options.Select(o => o.Name).Append(null))
            {
                foreach (var includeSuspect in new[] { false, true })
                {
                    var mutation = HostsMutator.SetOption(document, map, group.Name, option, includeSuspect);
                    HostsInvariantChecker.Verify(document, mutation.Document, mutation.Changes);
                }
            }
        }
    }

    public static TheoryData<string> Samples => HostsSamples.All;
}
