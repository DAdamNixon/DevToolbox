namespace DevToolbox.Services.Services;

/// <summary>
/// Abbreviation matching for the dashboard search box: <c>persman</c> finds
/// <c>PersonnelManagement</c>, <c>eesws</c> finds <c>EESWebShares</c>.
/// <para>
/// The query's characters have to appear in the candidate in order, but not together —
/// which on its own matches nearly everything, since a three-letter query is a
/// subsequence of most long names. Two rules narrow it to things that read as
/// abbreviations, both about where a <em>run</em> of matched characters may begin:
/// </para>
/// <list type="bullet">
/// <item>The first run must start at a word boundary — the start of the name, after a
/// separator, or a case step. <c>ersonnel</c> is not an abbreviation of anything.</item>
/// <item>A later run may start mid-word, but only if it is at least two characters long.
/// This is what lets <c>persman</c> reach an all-lowercase <c>personnelmanagement</c>,
/// which has no humps to land on, while still refusing <c>abc</c> the single stray
/// <c>c</c> it would need from the middle of <c>AccountInquiryBackend</c>.</item>
/// </list>
/// <para>
/// Plain substring matching is checked first and always wins, so this only ever adds
/// results; it never takes one away.
/// </para>
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Score awarded to a character that continues the previous run.</summary>
    private const int RunBonus = 8;

    /// <summary>Score for a run that opens on a word boundary.</summary>
    private const int BoundaryBonus = 6;

    /// <summary>Score for a run that opens mid-word — allowed, but worth much less.</summary>
    private const int MidWordBonus = 1;

    /// <summary>Score for a run that follows the previous one with nothing skipped between.</summary>
    private const int AdjacentBonus = 2;

    /// <summary>Shortest a mid-word run may be. One stray character is not an abbreviation.</summary>
    private const int MinMidWordRun = 2;

    /// <summary>Longest candidate considered. Paths are matched as substrings, not fuzzily.</summary>
    private const int MaxCandidateLength = 128;

    public static bool IsMatch(string? query, string? candidate) => Score(query, candidate) > 0;

    /// <summary>
    /// How well <paramref name="query"/> abbreviates <paramref name="candidate"/>, or 0 when
    /// it does not. Higher is better; the scale is arbitrary and only useful for comparing
    /// two candidates against the same query.
    /// <para>
    /// Every component of the score is positive, so any match at all scores at least 1 and
    /// 0 unambiguously means "no". A penalty-based score could not promise that.
    /// </para>
    /// </summary>
    public static int Score(string? query, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(candidate))
        {
            return 0;
        }

        // Spaces in the query are separators the user typed for readability, not characters
        // to find: "aim serv" should still reach "AIM Services" through the space in it.
        var needle = Compact(query);
        if (needle.Length == 0 || needle.Length > candidate.Length || candidate.Length > MaxCandidateLength)
        {
            return 0;
        }

        // An exact prefix is the strongest possible answer and by far the most common one,
        // so it is worth not entering the search for it.
        if (candidate.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
        {
            return needle.Length * (RunBonus + BoundaryBonus);
        }

        var memo = new int?[needle.Length, candidate.Length];
        var best = Best(needle, candidate, 0, 0, memo);
        return best < 0 ? 0 : best;
    }

    /// <summary>
    /// Best score for matching <c>needle[qi..]</c> somewhere in <c>haystack[ci..]</c>, or -1
    /// when it cannot be done. Every position the first remaining character could take is
    /// tried, which is what makes this exhaustive where a greedy left-most scan is not:
    /// <c>eesws</c> in <c>EESWebShares</c> needs the second <c>s</c> to skip the one in
    /// <c>Shares</c>' own run and land on the later one.
    /// <para>
    /// <c>qi == 0</c> doubles as "nothing matched yet", which is how the stricter rule for
    /// the first run is applied without threading an extra flag through the memo.
    /// </para>
    /// </summary>
    private static int Best(string needle, string haystack, int qi, int ci, int?[,] memo)
    {
        if (qi == needle.Length)
        {
            return 0;
        }

        if (ci >= haystack.Length)
        {
            return -1;
        }

        if (memo[qi, ci] is { } cached)
        {
            return cached;
        }

        var best = -1;

        for (var at = ci; at < haystack.Length; at++)
        {
            if (!Same(needle[qi], haystack[at]))
            {
                continue;
            }

            var onBoundary = IsBoundary(haystack, at);
            var run = RunFrom(needle, haystack, qi, at);

            // Where a run may open is the whole of what separates "matches abbreviations"
            // from "matches everything", so it is refused outright rather than penalised.
            if (!onBoundary && (qi == 0 || run < MinMidWordRun))
            {
                continue;
            }

            var opening = onBoundary ? BoundaryBonus : MidWordBonus;
            var adjacent = at == ci ? AdjacentBonus : 0;

            // Every prefix of the run is a candidate: the run may be longer than the split
            // that actually leads to a full match further along. A mid-word run still has to
            // clear the minimum after being cut short.
            for (var taken = 1; taken <= run; taken++)
            {
                if (!onBoundary && taken < MinMidWordRun)
                {
                    continue;
                }

                var rest = Best(needle, haystack, qi + taken, at + taken, memo);
                if (rest < 0)
                {
                    continue;
                }

                var score = opening + adjacent + (taken - 1) * RunBonus + rest;

                if (score > best)
                {
                    best = score;
                }
            }
        }

        memo[qi, ci] = best;
        return best;
    }

    /// <summary>How many characters of the needle continue to match from <paramref name="at"/>.</summary>
    private static int RunFrom(string needle, string haystack, int qi, int at)
    {
        var run = 0;
        while (qi + run < needle.Length
               && at + run < haystack.Length
               && Same(needle[qi + run], haystack[at + run]))
        {
            run++;
        }

        return run;
    }

    /// <summary>
    /// Whether a word starts here: the first character, anything after a separator, or the
    /// upper-case character that starts a new hump in <c>PersonnelManagement</c>. A digit
    /// counts too, so <c>app1</c> reaches <c>AspireApp1</c>.
    /// <para>
    /// Public because <see cref="WorkspaceSearch.MatchesPath"/> anchors its substring search on
    /// the same notion of a word start, and two definitions of "word start" in one search box is
    /// one more than there should be.
    /// </para>
    /// </summary>
    public static bool IsWordBoundary(string text, int index) => IsBoundary(text, index);

    private static bool IsBoundary(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = text[index - 1];
        var current = text[index];

        if (!char.IsLetterOrDigit(previous))
        {
            return true;
        }

        if (char.IsUpper(current) && !char.IsUpper(previous))
        {
            return true;
        }

        // The tail of an acronym: the S in "EESWebShares" that starts "Shares".
        if (char.IsUpper(current) && char.IsUpper(previous)
                                 && index + 1 < text.Length && char.IsLower(text[index + 1]))
        {
            return true;
        }

        return char.IsDigit(current) && !char.IsDigit(previous);
    }

    private static bool Same(char a, char b) =>
        char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    /// <summary>Drops the whitespace and separators a user types for readability.</summary>
    private static string Compact(string query)
    {
        var kept = new char[query.Length];
        var length = 0;

        foreach (var c in query)
        {
            if (!char.IsWhiteSpace(c) && c != '-' && c != '_' && c != '.')
            {
                kept[length++] = c;
            }
        }

        return new string(kept, 0, length);
    }
}
