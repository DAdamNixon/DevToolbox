using System;
using System.Linq;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The line diff behind the Bundled Configuration comparison.
/// <para>
/// What matters here is not that it finds *a* set of differences — almost anything does — but that
/// what it reports is the set a person would accept as the change: unchanged lines stay unchanged,
/// a one-line edit is one line each way rather than a whole-file replacement, and two files that
/// differ only in line endings do not read as entirely rewritten.
/// </para>
/// </summary>
public class TextDiffTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void Identical_texts_report_no_change()
    {
        var result = TextDiff.Compare("a\nb\nc", "a\nb\nc");

        Assert.True(result.Identical);
        Assert.Empty(result.Hunks);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void Two_empty_texts_are_identical()
    {
        Assert.True(TextDiff.Compare("", "").Identical);
        Assert.True(TextDiff.Compare(null, null).Identical);
        Assert.True(TextDiff.Compare(null, "").Identical);
    }

    /// <summary>
    /// The case the whole feature exists for: someone changed one value, and the diff has to say
    /// so rather than colouring the file red and green.
    /// </summary>
    [Fact]
    public void A_single_changed_line_is_one_added_and_one_removed()
    {
        var left = Lines("name: Checkout", "path: C:\\logs\\old", "enabled: true");
        var right = Lines("name: Checkout", "path: C:\\logs\\new", "enabled: true");

        var result = TextDiff.Compare(left, right);

        Assert.False(result.Identical);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);

        var all = result.Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Single(all, l => l.Kind == TextDiff.LineKind.Removed && l.Text.EndsWith("old"));
        Assert.Single(all, l => l.Kind == TextDiff.LineKind.Added && l.Text.EndsWith("new"));
    }

    [Fact]
    public void An_empty_left_side_is_all_additions()
    {
        var result = TextDiff.Compare("", Lines("a", "b", "c"));

        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.All(result.Hunks.SelectMany(h => h.Lines), l => Assert.Equal(TextDiff.LineKind.Added, l.Kind));
    }

    [Fact]
    public void An_empty_right_side_is_all_removals()
    {
        var result = TextDiff.Compare(Lines("a", "b", "c"), "");

        Assert.Equal(0, result.Added);
        Assert.Equal(3, result.Removed);
        Assert.All(result.Hunks.SelectMany(h => h.Lines), l => Assert.Equal(TextDiff.LineKind.Removed, l.Kind));
    }

    /// <summary>
    /// A file written by an editor that uses CRLF against one written by a tool that uses LF is not
    /// a change anybody wants to read about — and without normalising it is *every* line.
    /// </summary>
    [Fact]
    public void Line_endings_alone_are_not_a_difference()
    {
        Assert.True(TextDiff.Compare("a\r\nb\r\nc", "a\nb\nc").Identical);
        Assert.True(TextDiff.Compare("a\rb\rc", "a\nb\nc").Identical);
    }

    /// <summary>Likewise a trailing newline, which most editors add and most serializers do not.</summary>
    [Fact]
    public void A_trailing_newline_alone_is_not_a_difference()
    {
        Assert.True(TextDiff.Compare("a\nb\n", "a\nb").Identical);
    }

    [Fact]
    public void Line_numbers_are_one_based_and_side_specific()
    {
        // Left:  a b c      Right: a x c
        var result = TextDiff.Compare(Lines("a", "b", "c"), Lines("a", "x", "c"));
        var all = result.Hunks.SelectMany(h => h.Lines).ToList();

        var removed = Assert.Single(all, l => l.Kind == TextDiff.LineKind.Removed);
        Assert.Equal(2, removed.LeftNumber);
        Assert.Null(removed.RightNumber);

        var added = Assert.Single(all, l => l.Kind == TextDiff.LineKind.Added);
        Assert.Equal(2, added.RightNumber);
        Assert.Null(added.LeftNumber);

        var first = all.First(l => l.Kind == TextDiff.LineKind.Unchanged);
        Assert.Equal(1, first.LeftNumber);
        Assert.Equal(1, first.RightNumber);
    }

    /// <summary>
    /// Unchanged lines far from any change are not carried, which is what keeps a two-line edit in a
    /// 900-line template readable.
    /// </summary>
    [Fact]
    public void Context_is_limited_to_the_lines_around_a_change()
    {
        var left = Enumerable.Range(1, 40).Select(i => $"line {i}").ToArray();
        var right = left.ToArray();
        right[20] = "changed";

        var result = TextDiff.Compare(Lines(left), Lines(right), context: 2);

        var hunk = Assert.Single(result.Hunks);

        // 2 lines of context either side, plus the removed and the added line.
        Assert.Equal(6, hunk.Lines.Count);
        Assert.Equal(4, hunk.Lines.Count(l => l.Kind == TextDiff.LineKind.Unchanged));
    }

    [Fact]
    public void Changes_far_apart_become_separate_hunks()
    {
        var left = Enumerable.Range(1, 40).Select(i => $"line {i}").ToArray();
        var right = left.ToArray();
        right[2] = "early change";
        right[35] = "late change";

        var result = TextDiff.Compare(Lines(left), Lines(right), context: 2);

        Assert.Equal(2, result.Hunks.Count);
    }

    /// <summary>Two changes whose context windows touch read as one hunk, not two with a shared line.</summary>
    [Fact]
    public void Changes_close_together_merge_into_one_hunk()
    {
        var left = Enumerable.Range(1, 20).Select(i => $"line {i}").ToArray();
        var right = left.ToArray();
        right[8] = "first";
        right[10] = "second";

        var result = TextDiff.Compare(Lines(left), Lines(right), context: 3);

        Assert.Single(result.Hunks);
    }

    [Fact]
    public void Zero_context_keeps_only_the_changed_lines()
    {
        var left = Enumerable.Range(1, 10).Select(i => $"line {i}").ToArray();
        var right = left.ToArray();
        right[5] = "changed";

        var result = TextDiff.Compare(Lines(left), Lines(right), context: 0);

        var hunk = Assert.Single(result.Hunks);
        Assert.Equal(2, hunk.Lines.Count);
        Assert.DoesNotContain(hunk.Lines, l => l.Kind == TextDiff.LineKind.Unchanged);
    }

    /// <summary>
    /// A large file with a small edit must not be reported as a wholesale replacement — the prefix
    /// and suffix trimming is what keeps the quadratic part off it, and this is the test that fails
    /// if that trimming is ever removed.
    /// </summary>
    [Fact]
    public void A_small_edit_in_a_large_file_is_still_a_line_diff()
    {
        var left = Enumerable.Range(1, 5000).Select(i => $"key{i}: value{i}").ToArray();
        var right = left.ToArray();
        right[2500] = "key2501: changed";

        var result = TextDiff.Compare(Lines(left), Lines(right));

        Assert.False(result.Truncated);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
    }

    /// <summary>
    /// Two files sharing nothing, both large enough to blow the cell cap. The honest report is
    /// "replaced wholesale" rather than an enormous matrix.
    /// </summary>
    [Fact]
    public void Two_unrelated_large_files_are_reported_as_a_replacement()
    {
        var left = Lines(Enumerable.Range(1, 2500).Select(i => $"left {i}").ToArray());
        var right = Lines(Enumerable.Range(1, 2500).Select(i => $"right {i}").ToArray());

        var result = TextDiff.Compare(left, right);

        Assert.True(result.Truncated);
        Assert.Equal(2500, result.Added);
        Assert.Equal(2500, result.Removed);
    }

    [Fact]
    public void Every_line_of_both_texts_is_accounted_for_exactly_once()
    {
        var left = Lines("a", "b", "c", "d");
        var right = Lines("a", "x", "c", "y", "z");

        var all = TextDiff.Compare(left, right, context: 10).Hunks.SelectMany(h => h.Lines).ToList();

        var leftSeen = all.Where(l => l.Kind != TextDiff.LineKind.Added).Select(l => l.LeftNumber).ToList();
        var rightSeen = all.Where(l => l.Kind != TextDiff.LineKind.Removed).Select(l => l.RightNumber).ToList();

        Assert.Equal(new int?[] { 1, 2, 3, 4 }, leftSeen);
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5 }, rightSeen);
    }
}
