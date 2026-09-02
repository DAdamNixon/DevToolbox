using System;
using System.Collections.Generic;
using System.Linq;

namespace DevToolbox.Services.Services;

/// <summary>
/// A line-by-line diff of two texts, for showing someone what a restore is about to do to a file
/// they have edited.
/// <para>
/// Hand-written rather than DiffPlex for the same reason the SQL tokeniser is hand-written: every
/// dependency here has to be vendored, pinned and registered in the vault's <c>08-Third-Party</c>,
/// and what is actually needed is a line diff over small YAML config files — not a diff library.
/// </para>
/// <para>
/// The algorithm is a plain LCS, with the shared prefix and suffix trimmed off first so the
/// quadratic part only ever runs over the region that genuinely differs. That is what makes it fine
/// on a 900-line template where one key changed. <see cref="MaxCells"/> is the backstop for the case
/// the trimming does not help — two files that share almost nothing — where the honest answer is
/// "this was replaced wholesale" rather than a matrix with tens of millions of entries in it.
/// </para>
/// </summary>
public static class TextDiff
{
    /// <summary>
    /// The most LCS cells to allocate — a 2000×2000 comparison, which is far larger than any config
    /// file here and still only a few MB. Past it, the two texts are reported as a wholesale
    /// replacement, which is what a diff of two unrelated files amounts to anyway.
    /// </summary>
    private const int MaxCells = 4_000_000;

    /// <summary>Unchanged lines kept either side of a change, so a hunk reads in context.</summary>
    public const int DefaultContext = 3;

    public enum LineKind
    {
        Unchanged,
        Added,
        Removed,
    }

    /// <param name="LeftNumber">1-based line number in the left text, or null for an added line.</param>
    /// <param name="RightNumber">1-based line number in the right text, or null for a removed line.</param>
    public sealed record Line(LineKind Kind, int? LeftNumber, int? RightNumber, string Text);

    /// <summary>
    /// A run of changed lines with its context. Separate hunks rather than one flat list so the view
    /// can show "… 40 unchanged lines …" between them instead of making someone scroll through them.
    /// </summary>
    public sealed record Hunk(int LeftStart, int RightStart, IReadOnlyList<Line> Lines);

    /// <param name="Identical">The two texts are the same. <see cref="Hunks"/> is then empty.</param>
    /// <param name="Truncated">The comparison hit <see cref="MaxCells"/> and is reported as a
    /// wholesale replacement rather than a line-level diff.</param>
    public sealed record Result(
        IReadOnlyList<Hunk> Hunks,
        int Added,
        int Removed,
        bool Identical,
        bool Truncated);

    /// <summary>
    /// Compares two texts by line. Line endings are normalised first — a file saved by a tool that
    /// writes LF against one saved by a tool that writes CRLF is not a change anybody wants to read
    /// about, and would otherwise report every single line as modified.
    /// </summary>
    public static Result Compare(string? left, string? right, int context = DefaultContext)
    {
        var leftLines = SplitLines(left);
        var rightLines = SplitLines(right);

        if (leftLines.SequenceEqual(rightLines, StringComparer.Ordinal))
            return new Result(Array.Empty<Hunk>(), 0, 0, Identical: true, Truncated: false);

        // Trim what matches at each end. Everything trimmed is Unchanged by definition, and the
        // offsets are added back when the line numbers are assigned.
        var prefix = 0;
        while (prefix < leftLines.Count && prefix < rightLines.Count &&
               string.Equals(leftLines[prefix], rightLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < leftLines.Count - prefix && suffix < rightLines.Count - prefix &&
               string.Equals(leftLines[^(suffix + 1)], rightLines[^(suffix + 1)], StringComparison.Ordinal))
        {
            suffix++;
        }

        var leftMiddle = leftLines.GetRange(prefix, leftLines.Count - prefix - suffix);
        var rightMiddle = rightLines.GetRange(prefix, rightLines.Count - prefix - suffix);

        var truncated = (long)(leftMiddle.Count + 1) * (rightMiddle.Count + 1) > MaxCells;

        var script = truncated
            ? WholesaleReplacement(leftMiddle, rightMiddle, prefix)
            : Walk(leftMiddle, rightMiddle, prefix);

        // Put the trimmed prefix and suffix back as Unchanged lines. They cost nothing to carry and
        // they are exactly what the context windows below are cut from.
        var all = new List<Line>(leftLines.Count + rightLines.Count);
        for (var i = 0; i < prefix; i++)
            all.Add(new Line(LineKind.Unchanged, i + 1, i + 1, leftLines[i]));

        all.AddRange(script);

        for (var i = 0; i < suffix; i++)
        {
            var leftNumber = leftLines.Count - suffix + i + 1;
            var rightNumber = rightLines.Count - suffix + i + 1;
            all.Add(new Line(LineKind.Unchanged, leftNumber, rightNumber, leftLines[leftNumber - 1]));
        }

        var added = all.Count(l => l.Kind == LineKind.Added);
        var removed = all.Count(l => l.Kind == LineKind.Removed);

        return new Result(BuildHunks(all, context), added, removed, Identical: false, truncated);
    }

    /// <summary>
    /// Splits on either line ending and drops a single trailing newline, so a file that ends with one
    /// does not diff against one that does not purely because of a phantom final empty line.
    /// </summary>
    private static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();

        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalised.EndsWith('\n')) normalised = normalised[..^1];

        return normalised.Split('\n').ToList();
    }

    /// <summary>
    /// Classic LCS length table, then a walk back through it. Kept as a table rather than a
    /// Myers/Hirschberg implementation because the input here is a config file with its ends already
    /// trimmed off — the clarity is worth more than the memory.
    /// </summary>
    private static List<Line> Walk(List<string> left, List<string> right, int offset)
    {
        var lengths = new int[left.Count + 1, right.Count + 1];

        for (var i = left.Count - 1; i >= 0; i--)
        {
            for (var j = right.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var script = new List<Line>();
        int x = 0, y = 0;

        while (x < left.Count && y < right.Count)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                script.Add(new Line(LineKind.Unchanged, offset + x + 1, offset + y + 1, left[x]));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                script.Add(new Line(LineKind.Removed, offset + x + 1, null, left[x]));
                x++;
            }
            else
            {
                script.Add(new Line(LineKind.Added, null, offset + y + 1, right[y]));
                y++;
            }
        }

        while (x < left.Count)
        {
            script.Add(new Line(LineKind.Removed, offset + x + 1, null, left[x]));
            x++;
        }

        while (y < right.Count)
        {
            script.Add(new Line(LineKind.Added, null, offset + y + 1, right[y]));
            y++;
        }

        return script;
    }

    /// <summary>Everything on the left removed, everything on the right added.</summary>
    private static List<Line> WholesaleReplacement(List<string> left, List<string> right, int offset)
    {
        var script = new List<Line>(left.Count + right.Count);

        for (var i = 0; i < left.Count; i++)
            script.Add(new Line(LineKind.Removed, offset + i + 1, null, left[i]));

        for (var i = 0; i < right.Count; i++)
            script.Add(new Line(LineKind.Added, null, offset + i + 1, right[i]));

        return script;
    }

    /// <summary>
    /// Cuts the full script down to the changed runs plus <paramref name="context"/> unchanged lines
    /// either side, merging runs whose context windows touch — two changes four lines apart read as
    /// one hunk, not two with a duplicated line between them.
    /// </summary>
    private static List<Hunk> BuildHunks(List<Line> lines, int context)
    {
        if (context < 0) context = 0;

        var keep = new bool[lines.Count];

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == LineKind.Unchanged) continue;

            var from = Math.Max(0, i - context);
            var to = Math.Min(lines.Count - 1, i + context);
            for (var j = from; j <= to; j++) keep[j] = true;
        }

        var hunks = new List<Hunk>();
        var current = new List<Line>();

        void Flush()
        {
            if (current.Count == 0) return;

            var first = current[0];
            hunks.Add(new Hunk(first.LeftNumber ?? 0, first.RightNumber ?? 0, current.ToList()));
            current.Clear();
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (keep[i]) current.Add(lines[i]);
            else Flush();
        }

        Flush();
        return hunks;
    }
}
