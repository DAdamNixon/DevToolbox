using System.Net;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Works out which lines inside an option's scope the option was actually written for, and
/// quarantines the rest.
/// <para>
/// The convention is that the last group in a file is closed with a scope-reset directive. When
/// that line is missing, the last option silently owns every line below it to the end of the
/// file — typically a developer's own unrelated entries and whatever Docker Desktop appended.
/// Two things then go wrong: the option reads as switched on because some of those lines are
/// enabled, and switching the group to a sibling comments them out.
/// </para>
/// <para>
/// Blank lines alone cannot be the test. Real option bodies are grouped with single blank lines
/// between related entries, so "separated by a blank line" would quarantine half of a normal
/// file. Instead each candidate line is scored against several independent signals and needs
/// <see cref="RequiredSignals"/> of them to be considered foreign — and once one line is,
/// everything below it in that option is too, because there is no reason to trust the remainder.
/// </para>
/// </summary>
internal static class HostsScopeRiskAnalyzer
{
    /// <summary>
    /// How many independent signals must agree before a line is treated as foreign. Two, so no
    /// single heuristic can quarantine a legitimate body line on its own.
    /// </summary>
    private const int RequiredSignals = 2;

    /// <summary>IPv4 prefix bytes compared when asking whether two addresses are related.</summary>
    private const int Ipv4PrefixBytes = 3;

    /// <summary>IPv6 prefix bytes compared — a /64, the smallest prefix related hosts normally share.</summary>
    private const int Ipv6PrefixBytes = 8;

    internal static void Analyze(
        IReadOnlyList<HostsLineFacts> facts,
        List<HostsGroupDraft> groups,
        HostsDialect dialect,
        List<HostsAnomaly> anomalies)
    {
        var blankRunBefore = BuildBlankRuns(facts);

        foreach (var group in groups)
        {
            var lastOption = group.Options.Count > 0 ? group.Options[^1] : null;

            foreach (var option in group.Options)
            {
                // Only the final option of an unterminated group can run off the end of the file,
                // so only it carries that extra suspicion.
                var runsToEndOfFile = group.EndKind == HostsScopeEnd.EndOfFile && ReferenceEquals(option, lastOption);

                Partition(option, facts, blankRunBefore, dialect, runsToEndOfFile);
            }

            ReportGroup(group, facts, anomalies);
        }
    }

    /// <summary>
    /// Splits one option's body at the first line that trips enough signals.
    /// </summary>
    private static void Partition(
        HostsOptionDraft option,
        IReadOnlyList<HostsLineFacts> facts,
        int[] blankRunBefore,
        HostsDialect dialect,
        bool runsToEndOfFile)
    {
        var body = option.BodyLines.Order().ToArray();
        var owned = new List<HostsLineFacts>();
        var foreignFrom = -1;

        for (var i = 0; i < body.Length; i++)
        {
            var candidate = facts[body[i] - 1];

            if (Score(candidate, owned, blankRunBefore, dialect, runsToEndOfFile) >= RequiredSignals)
            {
                foreignFrom = i;
                break;
            }

            owned.Add(candidate);
        }

        if (foreignFrom < 0)
        {
            option.OwnedLines.AddRange(body);
            return;
        }

        option.OwnedLines.AddRange(body[..foreignFrom]);
        option.SuspectLines.AddRange(body[foreignFrom..]);
    }

    /// <summary>
    /// How many independent reasons there are to think this line is not part of the option.
    /// </summary>
    /// <param name="owned">The option's body so far. Empty for the first candidate, which is why
    /// the comparison signals stand down rather than firing on nothing.</param>
    private static int Score(
        HostsLineFacts candidate,
        List<HostsLineFacts> owned,
        int[] blankRunBefore,
        HostsDialect dialect,
        bool runsToEndOfFile)
    {
        var score = 0;

        // 1. A wide blank gap. Single blanks group related entries and are normal; several in a row
        //    say the author moved on to something else.
        if (blankRunBefore[candidate.Number] >= dialect.UnscopedGapBlankLines) score++;

        // 2. Not an entry at all. Prose like "# Added by Docker Desktop" or "# End of section" is a
        //    foreign block's fingerprint. A commented-out entry is not caught by this — that is
        //    simply what a switched-off option looks like.
        if (!candidate.IsEntry) score++;

        // 3. An unrelated address. The entries of one option almost always point at the same host
        //    or the same subnet, because that is the point of grouping them.
        if (candidate.Address is not null)
        {
            var neighbours = owned.Where(line => line.Address is not null).Select(line => line.Address!).ToArray();
            if (neighbours.Length > 0 && !neighbours.Any(address => AreRelated(candidate.Address, address))) score++;
        }

        // 4. Activity that contradicts the body. An option whose real entries are all commented but
        //    which has an enabled line further down is precisely the "2 of 11" lie that makes a
        //    switched-off option look switched on.
        if (owned.Count > 0)
        {
            var anyActive = owned.Any(line => line.IsActive);
            var allActive = owned.All(line => line.IsActive);
            if ((candidate.IsActive && !anyActive) || (!candidate.IsActive && allActive)) score++;
        }

        // 5. The option runs to the end of the file with nothing closing it, so anything at all
        //    below it is questionable. On its own this is not enough to quarantine a line.
        if (runsToEndOfFile) score++;

        return score;
    }

    /// <summary>
    /// Whether two addresses look like they belong to the same set of hosts — same /24 for IPv4,
    /// same /64 for IPv6.
    /// </summary>
    private static bool AreRelated(IPAddress a, IPAddress b)
    {
        if (a.AddressFamily != b.AddressFamily) return false;

        var left = a.GetAddressBytes();
        var right = b.GetAddressBytes();
        if (left.Length != right.Length) return false;

        var prefix = left.Length == 4 ? Ipv4PrefixBytes : Ipv6PrefixBytes;
        for (var i = 0; i < prefix && i < left.Length; i++)
        {
            if (left[i] != right[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// For each line, how many blank lines run immediately before it.
    /// Index is the 1-based line number; slot 0 is unused.
    /// </summary>
    private static int[] BuildBlankRuns(IReadOnlyList<HostsLineFacts> facts)
    {
        var runs = new int[facts.Count + 1];

        for (var number = 2; number <= facts.Count; number++)
        {
            runs[number] = facts[number - 2].IsBlank ? runs[number - 1] + 1 : 0;
        }

        return runs;
    }

    private static void ReportGroup(HostsGroupDraft group, IReadOnlyList<HostsLineFacts> facts, List<HostsAnomaly> anomalies)
    {
        foreach (var option in group.Options.Where(o => o.SuspectLines.Count > 0))
        {
            var suspect = option.SuspectLines.Order().ToArray();
            var activeSuspects = suspect.Count(number => facts[number - 1].IsActive);

            anomalies.Add(new HostsAnomaly(
                HostsAnomalyKind.ForeignContentInOption,
                HostsSeverityLevel.Danger,
                $"'{group.Name} / {option.Name}' claims {suspect.Length} lines that look like they belong "
                + $"to something else ({HostsAnomaly.Describe(suspect)}). "
                + (activeSuspects > 0
                    ? $"{activeSuspects} of them are enabled, so this option reads as switched on when it is not. "
                    : string.Empty)
                + "Switching this group would comment or uncomment them too.",
                Group: group.Name,
                Option: option.Name,
                Lines: suspect,
                SuggestedClearLine: SuggestClearLine(option, facts)));
        }

        if (group.EndKind != HostsScopeEnd.EndOfFile) return;

        var lastOption = group.Options.Count > 0 ? group.Options[^1] : null;

        anomalies.Add(new HostsAnomaly(
            HostsAnomalyKind.UnterminatedTrailingScope,
            HostsSeverityLevel.Normal,
            $"Nothing closes group '{group.Name}', so "
            + (lastOption is null
                ? "it extends to the end of the file."
                : $"its last option '{lastOption.Name}' extends to the end of the file. ")
            + "Anything added below becomes part of it.",
            Group: group.Name,
            Option: lastOption?.Name,
            SuggestedClearLine: lastOption is null ? null : SuggestClearLine(lastOption, facts)));
    }

    /// <summary>
    /// Where a scope-reset directive should go: the first blank line after the option's real body,
    /// falling back to the first quarantined line. The directive is inserted <em>before</em> the
    /// returned line.
    /// <para>
    /// Null when there is nowhere useful to put one — in particular when the body already runs to
    /// the last line of the file, since a closing directive with nothing after it excludes nothing.
    /// </para>
    /// </summary>
    private static int? SuggestClearLine(HostsOptionDraft option, IReadOnlyList<HostsLineFacts> facts)
    {
        if (option.OwnedLines.Count == 0 && option.SuspectLines.Count == 0) return null;

        var lastOwned = option.OwnedLines.Count > 0 ? option.OwnedLines.Max() : 0;
        var firstSuspect = option.SuspectLines.Count > 0 ? option.SuspectLines.Min() : facts.Count + 1;

        for (var number = lastOwned + 1; number < firstSuspect && number <= facts.Count; number++)
        {
            if (facts[number - 1].IsBlank) return number;
        }

        var candidate = Math.Min(firstSuspect, lastOwned + 1);
        return candidate <= facts.Count ? candidate : null;
    }

}
