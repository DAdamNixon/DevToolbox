namespace DevToolbox.Services.Models.Hosts;

/// <summary>
/// How serious it is that an option is currently switched on.
/// <para>
/// Ordered, so the highest level across a set of active options is
/// <see cref="Math.Max(int, int)"/> over the underlying values. Deliberately generic:
/// the words that appear in a hosts file (<c>warn</c>, <c>web</c>, …) are one team's
/// vocabulary and are mapped onto these levels by <see cref="HostsDialect.SeverityFlags"/>,
/// so nothing in the code knows them.
/// </para>
/// </summary>
public enum HostsSeverityLevel
{
    Normal = 0,
    Caution = 1,
    Danger = 2,
}

/// <summary>
/// The annotation dialect embedded in a hosts file's comments — which tokens introduce a
/// group, an option and a scope reset, and which flag names map to which severity.
/// <para>
/// <see cref="Default"/> reproduces the legacy Toolbox format exactly. Everything here is
/// declared in <c>host_changer_settings.yaml</c> rather than baked into the parser: the
/// grammar is ours, but the vocabulary belongs to whoever's hosts file it is.
/// </para>
/// </summary>
public sealed class HostsDialect
{
    /// <summary>The legacy Toolbox dialect: <c>##key:</c> / <c>##value:Name:warn</c> / <c>##clear</c>.</summary>
    public static HostsDialect Default { get; } = new();

    /// <summary>Marks the start of a directive anywhere on a line. Never empty.</summary>
    public string Prefix { get; init; } = "##";

    /// <summary>Verb introducing a group, e.g. the <c>key</c> of <c>##key:DB Server</c>.</summary>
    public string GroupVerb { get; init; } = "key";

    /// <summary>Verb introducing an option, e.g. the <c>value</c> of <c>##value:Live:warn</c>.</summary>
    public string OptionVerb { get; init; } = "value";

    /// <summary>Verb closing the current group and option scope.</summary>
    public string ClearVerb { get; init; } = "clear";

    /// <summary>Separates the verb, the name and the optional severity flag.</summary>
    public string FlagSeparator { get; init; } = ":";

    /// <summary>
    /// Flag name to severity level. A trailing directive token is treated as a flag only
    /// when it appears here; anything else folds back into the option name, so a name
    /// containing <see cref="FlagSeparator"/> survives intact.
    /// </summary>
    public IReadOnlyDictionary<string, HostsSeverityLevel> SeverityFlags { get; init; } =
        new Dictionary<string, HostsSeverityLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["warn"] = HostsSeverityLevel.Danger,
            ["web"] = HostsSeverityLevel.Caution,
        };

    /// <summary>
    /// Markers that park a line permanently. A body line whose content begins with one of
    /// these is never enabled and never re-commented, and does not count towards an
    /// option's totals.
    /// <para>
    /// These exist because hosts files in the wild carry lines like
    /// <c># ; 203.0.113.9  db01</c> — an alternate address kept for reference that nobody
    /// wants switched on. The legacy tool would happily uncomment one to
    /// <c>; 203.0.113.9  db01</c>, which Windows does not honour, quietly turning the
    /// group off instead of pointing it somewhere.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ParkedMarkers { get; init; } = new[] { ";" };

    /// <summary>
    /// Consecutive blank lines that count as a gap when deciding whether an option's body
    /// has run past its intended end. See <c>HostsScopeRiskAnalyzer</c>.
    /// </summary>
    public int UnscopedGapBlankLines { get; init; } = 2;

    /// <summary>The prefix plus a verb, e.g. <c>##key</c>. Used when composing new lines.</summary>
    public string Directive(string verb) => Prefix + verb;

    /// <summary>The full text of a scope-reset line, e.g. <c>##clear</c>.</summary>
    public string ClearDirective => Directive(ClearVerb);

    /// <summary>The full text of a group directive, e.g. <c>##key:DB Server</c>.</summary>
    public string GroupDirective(string name) => Directive(GroupVerb) + FlagSeparator + name;

    /// <summary>The full text of an option directive, e.g. <c>##value:Live:warn</c>.</summary>
    public string OptionDirective(string name, HostsSeverityLevel severity)
    {
        var line = Directive(OptionVerb) + FlagSeparator + name;
        var flag = FlagFor(severity);

        return flag is null ? line : line + FlagSeparator + flag;
    }

    /// <summary>
    /// The word this dialect uses for a severity, or <c>null</c> when it declares none — which is
    /// always the case for <see cref="HostsSeverityLevel.Normal"/>, whose absence is the flag.
    /// <para>
    /// A dialect may map several words onto one level; composing picks the first, so a file this
    /// application writes uses one spelling consistently even where it can read more than one.
    /// </para>
    /// </summary>
    public string? FlagFor(HostsSeverityLevel severity)
    {
        if (severity == HostsSeverityLevel.Normal) return null;

        foreach (var pair in SeverityFlags)
        {
            if (pair.Value == severity) return pair.Key;
        }

        return null;
    }

    /// <summary>
    /// The severity a flag token maps to, or <c>null</c> when the token is not a flag at
    /// all and therefore belongs to the name.
    /// </summary>
    public HostsSeverityLevel? SeverityFor(string flag) =>
        SeverityFlags.TryGetValue(flag, out var level) ? level : null;

    /// <summary>Whether <paramref name="content"/> begins with a parked marker.</summary>
    public bool IsParked(string content)
    {
        foreach (var marker in ParkedMarkers)
        {
            if (marker.Length > 0 && content.StartsWith(marker, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// Rejects a dialect that cannot be parsed with, rather than letting it fail obscurely
    /// deep in the state machine. A hand-edited config is the expected source of these.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dialect is unusable.</exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(Prefix)) Fail("prefix must not be empty");
        if (string.IsNullOrEmpty(FlagSeparator)) Fail("flagSeparator must not be empty");
        if (string.IsNullOrWhiteSpace(GroupVerb)) Fail("groupVerb must not be empty");
        if (string.IsNullOrWhiteSpace(OptionVerb)) Fail("optionVerb must not be empty");
        if (string.IsNullOrWhiteSpace(ClearVerb)) Fail("clearVerb must not be empty");

        if (Prefix.Any(char.IsWhiteSpace)) Fail("prefix must not contain whitespace");

        // The verbs are located by splitting on the separator, so a verb containing it
        // could never be matched.
        if (GroupVerb.Contains(FlagSeparator, StringComparison.Ordinal) ||
            OptionVerb.Contains(FlagSeparator, StringComparison.Ordinal) ||
            ClearVerb.Contains(FlagSeparator, StringComparison.Ordinal))
        {
            Fail("verbs must not contain the flagSeparator");
        }

        var verbs = new[] { GroupVerb, OptionVerb, ClearVerb };
        if (verbs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != verbs.Length)
        {
            Fail("groupVerb, optionVerb and clearVerb must differ");
        }

        if (UnscopedGapBlankLines < 1) Fail("unscopedGapBlankLines must be at least 1");

        static void Fail(string reason) =>
            throw new InvalidOperationException($"Invalid hosts annotation dialect: {reason}.");
    }
}
