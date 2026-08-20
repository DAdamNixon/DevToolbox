namespace DevToolbox.Services.Models.Hosts;

/// <summary>Kinds of thing that can be wrong with an annotated hosts file.</summary>
public enum HostsAnomalyKind
{
    /// <summary>
    /// The last group's scope runs to the end of the file with no scope-reset directive, so it
    /// claims every line below it. The convention is to close the last group; a file that
    /// never got that line silently hands unrelated entries to whichever option came last.
    /// </summary>
    UnterminatedTrailingScope,

    /// <summary>
    /// Lines inside an option's scope that do not look like they belong to it. Switching the
    /// group would comment or uncomment them along with the real body.
    /// </summary>
    ForeignContentInOption,

    /// <summary>An option directive appeared before any group directive, so there was nothing to attach it to.</summary>
    OptionBeforeGroup,

    /// <summary>
    /// The same hostname is enabled on more than one line. Windows honours the first and
    /// ignores the rest, so the later one is dead weight that reads as if it were in effect.
    /// </summary>
    DuplicateActiveHost,

    /// <summary>Two options in one group share a name after case-insensitive comparison.</summary>
    DuplicateOptionName,

    /// <summary>A directive whose verb is not in the dialect. Left alone, never guessed at.</summary>
    UnknownDirective,

    /// <summary>A line inside an option that is neither blank, a comment, nor a usable entry.</summary>
    MalformedEntry,

    /// <summary>
    /// Text after the hostnames with no comment marker — it becomes extra hostnames once the
    /// line is enabled.
    /// </summary>
    TrailingTextAfterHostnames,

    /// <summary>The file mixes CRLF and LF. Harmless here, since terminators are preserved per line, but worth knowing.</summary>
    MixedNewLines,

    /// <summary>The bytes were not valid UTF-8 and were read as Latin-1 to keep them intact.</summary>
    NonUtf8Encoding,
}

/// <summary>
/// Something the parser noticed that a developer should see. Findings, not errors: a file with
/// anomalies still parses, and the ones that could cost you an entry set
/// <see cref="BlocksApply"/> so a switch stops to ask first.
/// </summary>
/// <param name="Lines">Lines the finding refers to, in file order. May be empty for whole-file findings.</param>
/// <param name="SuggestedClearLine">
/// Where inserting a scope-reset directive would fix this, when that is the remedy.
/// The new line goes <em>before</em> this one.
/// </param>
public sealed record HostsAnomaly(
    HostsAnomalyKind Kind,
    HostsSeverityLevel Severity,
    string Message,
    string? Group = null,
    string? Option = null,
    IReadOnlyList<int>? Lines = null,
    int? SuggestedClearLine = null)
{
    public IReadOnlyList<int> Lines { get; init; } = Lines ?? Array.Empty<int>();

    /// <summary>
    /// Whether a switch should refuse to proceed without explicit confirmation.
    /// <para>
    /// Only <see cref="HostsAnomalyKind.ForeignContentInOption"/> qualifies, because only it means
    /// a switch would touch lines outside the option's real body — the failure that silently
    /// disables entries a developer depends on. An
    /// <see cref="HostsAnomalyKind.UnterminatedTrailingScope"/> is worth reporting and worth
    /// repairing, but on its own it costs nothing: plenty of perfectly safe files simply never got
    /// their closing directive, and blocking every switch on those would train people to click
    /// through the warning that matters.
    /// </para>
    /// </summary>
    public bool BlocksApply => Kind is HostsAnomalyKind.ForeignContentInOption;

    /// <summary>"lines 84-89, 97, 99, 100" / "line 97" / "" — for message text.</summary>
    public string DescribeLines() => Describe(Lines);

    /// <summary>
    /// Renders line numbers with consecutive runs collapsed.
    /// <para>
    /// Static as well as instance, because the analyzer composes an anomaly's message before it has
    /// an anomaly to ask. Runs are collapsed because the interesting findings are contiguous blocks —
    /// "lines 84-89, 97, 99, 100" is read at a glance, where nine bare numbers are not.
    /// </para>
    /// </summary>
    /// <param name="lines">Ascending line numbers.</param>
    public static string Describe(IReadOnlyList<int> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0) return string.Empty;

        var runs = new List<string>();
        var start = lines[0];
        var previous = start;

        for (var i = 1; i <= lines.Count; i++)
        {
            if (i < lines.Count && lines[i] == previous + 1)
            {
                previous = lines[i];
                continue;
            }

            runs.Add(start == previous ? start.ToString() : $"{start}-{previous}");

            if (i >= lines.Count) break;

            start = lines[i];
            previous = start;
        }

        var singleLine = runs.Count == 1 && start == previous && lines.Count == 1;
        return (singleLine ? "line " : "lines ") + string.Join(", ", runs);
    }
}
