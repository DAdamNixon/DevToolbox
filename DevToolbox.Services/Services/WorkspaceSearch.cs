namespace DevToolbox.Services.Services;

/// <summary>
/// What the dashboard search box counts as a hit. One place, because the same question is
/// asked of group names, workspace names and paths, and the three used to disagree.
/// </summary>
public static class WorkspaceSearch
{
    /// <summary>
    /// Shortest query allowed to match the middle of a word in a name.
    /// <para>
    /// Below this, a substring has to start a word. Two and three letters land inside far too
    /// many ordinary English words to be useful otherwise — <c>ai</c> is in Em<b>ai</b>l,
    /// Tr<b>ai</b>ning, M<b>ai</b>nt, W<b>ai</b>vers and Ch<b>ai</b>ning, none of which is what
    /// anyone typing it is looking for. At four characters and up the query is specific enough
    /// that a mid-word hit is nearly always meant, and <c>count</c> should still find Account.
    /// </para>
    /// </summary>
    private const int MinLengthForMidWordMatch = 4;

    /// <summary>
    /// Whether a card called <paramref name="name"/> answers to <paramref name="query"/>:
    /// by substring, by any of its <paramref name="aliases"/>, or as an abbreviation
    /// (<c>persman</c> → <c>PersonnelManagement</c>). Substring first, since it is the
    /// cheapest and the one users expect to be exhaustive.
    /// </summary>
    public static bool MatchesName(string? query, string? name, IEnumerable<string>? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var needle = query.Trim();

        if (!string.IsNullOrEmpty(name))
        {
            var substring = needle.Length >= MinLengthForMidWordMatch
                ? name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                : ContainsAtWordStart(name, needle);

            if (substring || FuzzyMatch.IsMatch(needle, name))
            {
                return true;
            }
        }

        if (aliases is null)
        {
            return false;
        }

        // An alias is a whole word the user chose, so a prefix of it is enough — typing
        // "pers" should reach a card aliased "persman" without finishing the word.
        return aliases.Any(alias => !string.IsNullOrWhiteSpace(alias)
                                    && (alias.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                        || FuzzyMatch.IsMatch(needle, alias)));
    }

    /// <summary>
    /// Whether a path is a hit. Substring, never abbreviation — a path is long, mostly
    /// punctuation, and shares its middle with every sibling, so abbreviating one produces
    /// noise rather than results.
    /// <para>
    /// The substring has to <em>start a word</em>, though, which a bare
    /// <c>path.Contains(query)</c> did not require. Every one of the 47 NuGet packages lives
    /// under a <c>\Main\</c> branch folder, so searching <c>ai</c> matched all of them through
    /// the middle of the word "Main" — and the same goes for any two or three letters that
    /// happen to fall inside a folder name every project shares. Anchoring on a word start
    /// keeps what people actually search paths for (a folder, a branch, a file name, a pasted
    /// path) and drops the accidental middles.
    /// </para>
    /// </summary>
    public static bool MatchesPath(string? query, string? path)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return !string.IsNullOrEmpty(path) && ContainsAtWordStart(path, query.Trim());
    }

    /// <summary>
    /// Whether <paramref name="needle"/> appears in <paramref name="haystack"/> beginning at a
    /// word start — after a separator, or on a camel-case hump.
    /// <para>
    /// Every occurrence is tried, not just the first: "main" sits mid-word in "Domain" and at a
    /// word start in "\Main\", and one boundary hit anywhere is enough. Finding the mid-word one
    /// first must not settle the question.
    /// </para>
    /// </summary>
    private static bool ContainsAtWordStart(string haystack, string needle)
    {
        for (var at = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = haystack.IndexOf(needle, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (FuzzyMatch.IsWordBoundary(haystack, at))
            {
                return true;
            }
        }

        return false;
    }
}
