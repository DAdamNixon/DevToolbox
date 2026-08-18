namespace DevToolbox.Services.Models.Hosts;

/// <summary>What terminated a group's scope.</summary>
public enum HostsScopeEnd
{
    /// <summary>Another group directive followed.</summary>
    NextGroup,

    /// <summary>An explicit scope-reset directive followed.</summary>
    Clear,

    /// <summary>
    /// Nothing followed. The group owns the rest of the file, which is where entries get
    /// swallowed — see <c>HostsScopeRiskAnalyzer</c>.
    /// </summary>
    EndOfFile,
}

/// <summary>
/// One address-to-hostnames line, whether or not it is currently enabled.
/// </summary>
/// <param name="Address">The parsed address, or <c>null</c> when the line is not a valid entry.</param>
/// <param name="Hostnames">Names mapped to <paramref name="Address"/>, trailing commentary excluded.</param>
/// <param name="TrailingText">
/// Text after the hostnames that is not introduced by a comment marker — e.g. the
/// <c>(all traffic)</c> in <c>203.0.113.9  www.example.com (all traffic)</c>. The hosts format
/// has no such notion, so those words become additional hostnames the moment the line is
/// enabled. Surfaced rather than silently dropped.
/// </param>
public sealed record HostsEntry(
    int Line,
    string? Address,
    IReadOnlyList<string> Hostnames,
    string? TrailingText,
    bool IsActive,
    string? Group,
    string? Option,
    bool IsSuspect)
{
    public bool IsValid => Address is not null && Hostnames.Count > 0;
}

/// <summary>
/// One switchable choice within a group — the block of entries a developer turns on.
/// <para>
/// "On" is a count, not a flag, because a hosts file can be left half-edited. An option with
/// some lines enabled and some commented is reported as partial rather than rounded to either
/// answer.
/// </para>
/// </summary>
public sealed class HostsOption
{
    public required string Group { get; init; }
    public required string Name { get; init; }
    public HostsSeverityLevel Severity { get; init; }

    /// <summary>
    /// Every line carrying a scope-opening directive for this option, in file order. Usually one;
    /// a name repeated later in the file adds another, and renaming has to rewrite all of them.
    /// Empty when the option only ever appears as a per-line tag.
    /// </summary>
    public required IReadOnlyList<int> DirectiveLines { get; init; }

    /// <summary>The first directive line, or 0 when the option only ever appears inline.</summary>
    public int DirectiveLine => DirectiveLines.Count > 0 ? DirectiveLines[0] : 0;

    /// <summary>Body lines the option unambiguously owns.</summary>
    public required IReadOnlyList<int> OwnedLines { get; init; }

    /// <summary>Lines tagged individually by a trailing directive. Always owned — there is no scope to misread.</summary>
    public required IReadOnlyList<int> InlineLines { get; init; }

    /// <summary>
    /// Lines inside the option's scope that probably were never meant to be, quarantined out
    /// of the counts and excluded from a switch unless explicitly confirmed.
    /// </summary>
    public required IReadOnlyList<int> SuspectLines { get; init; }

    /// <summary>Lines parked by a marker. Never toggled in either direction, and not counted.</summary>
    public required IReadOnlyList<int> ParkedLines { get; init; }

    /// <summary>Enabled lines among <see cref="OwnedLines"/> and <see cref="InlineLines"/>.</summary>
    public int ActiveCount { get; init; }

    /// <summary>Toggleable lines among <see cref="OwnedLines"/> and <see cref="InlineLines"/>.</summary>
    public int TotalCount { get; init; }

    /// <summary>Enabled lines among <see cref="SuspectLines"/> — the reason an option can look active when it is not.</summary>
    public int SuspectActiveCount { get; init; }

    public bool IsOn => ActiveCount > 0;

    public bool IsPartiallyOn => ActiveCount > 0 && ActiveCount < TotalCount;

    public bool HasSuspectContent => SuspectLines.Count > 0;

    /// <summary>Every line this option would toggle, in file order.</summary>
    public IEnumerable<int> ToggleableLines(bool includeSuspect) =>
        includeSuspect
            ? OwnedLines.Concat(InlineLines).Concat(SuspectLines).OrderBy(n => n)
            : OwnedLines.Concat(InlineLines).OrderBy(n => n);

    /// <summary>"3 of 11" while partial, otherwise null — the sublabel the legacy tray showed.</summary>
    public string? PartialLabel => IsPartiallyOn ? $"{ActiveCount} of {TotalCount}" : null;
}

/// <summary>A set of mutually exclusive options, e.g. which database server to point at.</summary>
public sealed class HostsGroup
{
    public required string Name { get; init; }

    /// <inheritdoc cref="HostsOption.DirectiveLines"/>
    public required IReadOnlyList<int> DirectiveLines { get; init; }

    /// <summary>The first directive line.</summary>
    public int DirectiveLine => DirectiveLines.Count > 0 ? DirectiveLines[0] : 0;

    /// <summary>Last line of the group's scope, inclusive.</summary>
    public int EndLine { get; init; }

    public HostsScopeEnd EndKind { get; init; }
    public required IReadOnlyList<HostsOption> Options { get; init; }

    public IReadOnlyList<HostsOption> ActiveOptions => Options.Where(o => o.IsOn).ToArray();

    public HostsSeverityLevel ActiveSeverity =>
        Options.Where(o => o.IsOn).Aggregate(HostsSeverityLevel.Normal, (max, o) => o.Severity > max ? o.Severity : max);

    public bool HasSuspectContent => Options.Any(o => o.HasSuspectContent);

    public HostsOption? Find(string option) =>
        Options.FirstOrDefault(o => string.Equals(o.Name, option, StringComparison.Ordinal));

    /// <summary>"Test (db02)" / "Test, Live" / "off" — one line of the tray tooltip.</summary>
    public string Describe()
    {
        var active = ActiveOptions;
        return active.Count == 0 ? "off" : string.Join(", ", active.Select(o => o.Name));
    }
}

/// <summary>
/// Everything the annotations in a hosts file describe: its groups, their options, the
/// entries they control, and anything about the file that looks wrong.
/// </summary>
public sealed class HostsMap
{
    public required IReadOnlyList<HostsGroup> Groups { get; init; }
    public required IReadOnlyList<HostsEntry> Entries { get; init; }
    public required IReadOnlyList<HostsAnomaly> Anomalies { get; init; }

    /// <summary>The dialect this was parsed with, so callers need not thread it separately.</summary>
    public required HostsDialect Dialect { get; init; }

    /// <summary>Highest severity across every option currently switched on, suspect lines excluded.</summary>
    public HostsSeverityLevel ActiveSeverity =>
        Groups.Aggregate(HostsSeverityLevel.Normal, (max, g) => g.ActiveSeverity > max ? g.ActiveSeverity : max);

    /// <summary>Anomalies serious enough that a switch should not proceed unconfirmed.</summary>
    public IReadOnlyList<HostsAnomaly> BlockingAnomalies => Anomalies.Where(a => a.BlocksApply).ToArray();

    public bool HasAnnotations => Groups.Count > 0;

    public HostsGroup? Find(string group) =>
        Groups.FirstOrDefault(g => string.Equals(g.Name, group, StringComparison.Ordinal));

    public HostsOption? Find(string group, string option) => Find(group)?.Find(option);

    /// <summary>Active entries only — what the machine is actually resolving through this file.</summary>
    public IEnumerable<HostsEntry> ActiveEntries => Entries.Where(e => e.IsActive && e.IsValid);
}
