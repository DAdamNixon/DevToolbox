using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Reads the groups and options a hosts file describes in its comments.
/// <para>
/// Pure and deterministic: it takes a document and gives back a map, touching no file and
/// holding no state. Every literal it recognises comes from the <see cref="HostsDialect"/>, so
/// the grammar lives here but the vocabulary does not.
/// </para>
/// </summary>
public static class HostsAnnotationParser
{
    /// <param name="dialect">Defaults to <see cref="HostsDialect.Default"/>, the legacy Toolbox format.</param>
    /// <exception cref="InvalidOperationException">The dialect is unusable.</exception>
    public static HostsMap Parse(HostsDocument document, HostsDialect? dialect = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var effective = dialect ?? HostsDialect.Default;
        effective.Validate();

        var facts = document.Lines.Select(line => HostsLineFacts.From(line, effective)).ToArray();
        var anomalies = new List<HostsAnomaly>();

        var drafts = RunScopePass(facts, effective, anomalies);
        HostsScopeRiskAnalyzer.Analyze(facts, drafts, effective, anomalies);

        var owners = BuildOwnerIndex(drafts);
        var entries = BuildEntries(facts, owners);

        AddFileAnomalies(document, anomalies);
        AddEntryAnomalies(facts, entries, owners, anomalies);

        return new HostsMap
        {
            Groups = drafts.Select(draft => Freeze(draft, facts)).ToArray(),
            Entries = entries,
            Anomalies = anomalies,
            Dialect = effective,
        };
    }

    // ── the state machine ────────────────────────────────────────────────────

    /// <summary>
    /// Walks the file once, attaching every content line to the option whose scope it falls in.
    /// An option's scope runs until the next option, the next group, a scope reset, or the end of
    /// the file — whichever comes first.
    /// </summary>
    private static List<HostsGroupDraft> RunScopePass(
        IReadOnlyList<HostsLineFacts> facts,
        HostsDialect dialect,
        List<HostsAnomaly> anomalies)
    {
        var groups = new List<HostsGroupDraft>();
        var groupsByName = new Dictionary<string, HostsGroupDraft>(StringComparer.Ordinal);
        var orphanOptionLines = new List<int>();
        var unknownDirectiveLines = new List<int>();

        HostsGroupDraft? currentGroup = null;
        HostsOptionDraft? currentOption = null;

        void CloseGroup(HostsScopeEnd kind, int endLine)
        {
            if (currentGroup is null) return;

            // A group directive repeated later in the file reopens the same group, so these two
            // describe its last scope only. Nothing that mutates the file reads them — the mutator
            // works from each option's own line numbers — they drive the display and the repair
            // suggestion.
            currentGroup.EndKind = kind;
            currentGroup.EndLine = endLine;
            currentGroup = null;
            currentOption = null;
        }

        foreach (var fact in facts)
        {
            if (!fact.HasDirective)
            {
                // Content before any option belongs to nobody, exactly as the legacy tool had it:
                // there is no option to switch it with.
                if (fact.HasContent && currentOption is not null) AttachBody(currentOption, fact);
                continue;
            }

            var directive = ParseDirective(fact, dialect);

            if (directive.Verb is null)
            {
                unknownDirectiveLines.Add(fact.Number);
                continue;
            }

            if (SameVerb(directive.Verb, dialect.ClearVerb))
            {
                CloseGroup(HostsScopeEnd.Clear, fact.Number - 1);
                continue;
            }

            if (SameVerb(directive.Verb, dialect.GroupVerb))
            {
                // A group cannot be a per-line tag: a line owned by a group but by no option has
                // nothing to switch it with.
                if (fact.IsInlineDirective) continue;

                if (string.IsNullOrEmpty(directive.Name))
                {
                    unknownDirectiveLines.Add(fact.Number);
                    continue;
                }

                CloseGroup(HostsScopeEnd.NextGroup, fact.Number - 1);

                if (!groupsByName.TryGetValue(directive.Name, out var group))
                {
                    group = new HostsGroupDraft { Name = directive.Name };
                    groupsByName[directive.Name] = group;
                    groups.Add(group);
                }

                group.DirectiveLines.Add(fact.Number);
                currentGroup = group;
                currentOption = null;
                continue;
            }

            if (SameVerb(directive.Verb, dialect.OptionVerb))
            {
                if (currentGroup is null)
                {
                    orphanOptionLines.Add(fact.Number);
                    continue;
                }

                if (string.IsNullOrEmpty(directive.Name))
                {
                    unknownDirectiveLines.Add(fact.Number);
                    continue;
                }

                var option = GetOrAddOption(currentGroup, directive.Name, anomalies);

                // A severity already claimed by one occurrence of the name is never downgraded by
                // a later one, so tagging any occurrence as dangerous is enough.
                if (directive.Severity > option.Severity) option.Severity = directive.Severity;

                if (fact.IsSectionDirective)
                {
                    option.DirectiveLines.Add(fact.Number);
                    currentOption = option;
                }
                else
                {
                    AttachInline(option, fact);
                }

                continue;
            }

            unknownDirectiveLines.Add(fact.Number);
        }

        CloseGroup(HostsScopeEnd.EndOfFile, facts.Count);

        if (orphanOptionLines.Count > 0)
        {
            anomalies.Add(new HostsAnomaly(
                HostsAnomalyKind.OptionBeforeGroup,
                HostsSeverityLevel.Caution,
                $"{Count(orphanOptionLines, "option directive")} appear before any group directive and were ignored.",
                Lines: orphanOptionLines));
        }

        if (unknownDirectiveLines.Count > 0)
        {
            anomalies.Add(new HostsAnomaly(
                HostsAnomalyKind.UnknownDirective,
                HostsSeverityLevel.Caution,
                $"{Count(unknownDirectiveLines, "directive")} use a verb this dialect does not define and were left alone.",
                Lines: unknownDirectiveLines));
        }

        return groups;
    }

    /// <summary>The verb, name and severity flag of a directive, or a null verb when it is unusable.</summary>
    private readonly record struct ParsedDirective(string? Verb, string? Name, HostsSeverityLevel Severity);

    /// <summary>
    /// Splits a directive's payload into its verb, name and optional severity flag.
    /// <para>
    /// Only a third-or-later token can be a flag, and only when the dialect defines it. Everything
    /// else folds back into the name, so an option genuinely called <c>A:B</c> keeps its name and
    /// an option called <c>warn</c> is not mistaken for a flag on a nameless one.
    /// </para>
    /// </summary>
    private static ParsedDirective ParseDirective(HostsLineFacts fact, HostsDialect dialect)
    {
        var payload = fact.Line.Text[(fact.DirectiveIndex + dialect.Prefix.Length)..];
        var parts = payload.Split(dialect.FlagSeparator);

        var verb = parts[0].Trim();
        if (verb.Length == 0) return new ParsedDirective(null, null, HostsSeverityLevel.Normal);
        if (parts.Length == 1) return new ParsedDirective(verb, null, HostsSeverityLevel.Normal);

        var severity = parts.Length >= 3 ? dialect.SeverityFor(parts[^1].Trim()) : null;
        var nameParts = severity is null ? parts[1..] : parts[1..^1];
        var name = string.Join(dialect.FlagSeparator, nameParts).Trim();

        return new ParsedDirective(verb, name, severity ?? HostsSeverityLevel.Normal);
    }

    /// <summary>Verbs are matched case-insensitively, in the parser and the mutator alike.</summary>
    private static bool SameVerb(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void AttachBody(HostsOptionDraft option, HostsLineFacts fact)
    {
        if (fact.IsParked) option.ParkedLines.Add(fact.Number);
        else option.BodyLines.Add(fact.Number);
    }

    private static void AttachInline(HostsOptionDraft option, HostsLineFacts fact)
    {
        if (!fact.HasContent) return;

        // Inline lines are tagged one by one, so there is no scope to misread and nothing for the
        // risk analyzer to quarantine.
        if (fact.IsParked) option.ParkedLines.Add(fact.Number);
        else option.InlineLines.Add(fact.Number);
    }

    private static HostsOptionDraft GetOrAddOption(HostsGroupDraft group, string name, List<HostsAnomaly> anomalies)
    {
        if (group.ByName.TryGetValue(name, out var existing)) return existing;

        // Identity is exact, so "Live" and "live" are two different options. That is almost always
        // a typo rather than intent, and it yields two menu items a developer cannot tell apart.
        var caseClash = group.Options
            .FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

        if (caseClash is not null)
        {
            anomalies.Add(new HostsAnomaly(
                HostsAnomalyKind.DuplicateOptionName,
                HostsSeverityLevel.Caution,
                $"Group '{group.Name}' has both '{caseClash.Name}' and '{name}', which differ only in case.",
                Group: group.Name,
                Option: name));
        }

        var option = new HostsOptionDraft { Group = group.Name, Name = name };
        group.ByName[name] = option;
        group.Options.Add(option);

        return option;
    }

    // ── freezing ─────────────────────────────────────────────────────────────

    private static HostsGroup Freeze(HostsGroupDraft draft, IReadOnlyList<HostsLineFacts> facts) => new()
    {
        Name = draft.Name,
        DirectiveLines = Sorted(draft.DirectiveLines),
        EndLine = draft.EndLine,
        EndKind = draft.EndKind,
        Options = draft.Options.Select(option => Freeze(option, facts)).ToArray(),
    };

    private static HostsOption Freeze(HostsOptionDraft draft, IReadOnlyList<HostsLineFacts> facts)
    {
        var owned = Sorted(draft.OwnedLines);
        var inline = Sorted(draft.InlineLines);
        var suspect = Sorted(draft.SuspectLines);
        var parked = Sorted(draft.ParkedLines);

        var toggleable = owned.Concat(inline).ToArray();

        return new HostsOption
        {
            Group = draft.Group,
            Name = draft.Name,
            Severity = draft.Severity,
            DirectiveLines = Sorted(draft.DirectiveLines),
            OwnedLines = owned,
            InlineLines = inline,
            SuspectLines = suspect,
            ParkedLines = parked,
            TotalCount = toggleable.Length,
            ActiveCount = toggleable.Count(number => facts[number - 1].IsActive),
            SuspectActiveCount = suspect.Count(number => facts[number - 1].IsActive),
        };
    }

    private static int[] Sorted(IEnumerable<int> lines) => lines.Order().ToArray();

    // ── entries ──────────────────────────────────────────────────────────────

    /// <summary>Which option, if any, owns a given line — and whether it owns it only suspiciously.</summary>
    private readonly record struct LineOwner(string? Group, string? Option, bool IsSuspect);

    private static Dictionary<int, LineOwner> BuildOwnerIndex(List<HostsGroupDraft> groups)
    {
        var index = new Dictionary<int, LineOwner>();

        foreach (var group in groups)
        {
            foreach (var option in group.Options)
            {
                foreach (var line in option.OwnedLines.Concat(option.InlineLines).Concat(option.ParkedLines))
                {
                    index[line] = new LineOwner(group.Name, option.Name, false);
                }

                foreach (var line in option.SuspectLines)
                {
                    index[line] = new LineOwner(group.Name, option.Name, true);
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Every content line in the file, owned or not.
    /// <para>
    /// Deliberately not limited to annotated lines: a duplicate hostname is only visible when the
    /// un-annotated remainder of the file is included, and that is exactly where a stray copy of
    /// an entry tends to end up.
    /// </para>
    /// </summary>
    private static List<HostsEntry> BuildEntries(
        IReadOnlyList<HostsLineFacts> facts,
        Dictionary<int, LineOwner> owners)
    {
        var entries = new List<HostsEntry>();

        foreach (var fact in facts)
        {
            if (!fact.HasContent) continue;

            owners.TryGetValue(fact.Number, out var owner);

            entries.Add(new HostsEntry(
                Line: fact.Number,
                Address: fact.Address?.ToString(),
                Hostnames: fact.Hostnames,
                TrailingText: fact.TrailingText,
                IsActive: fact.IsActive,
                Group: owner.Group,
                Option: owner.Option,
                IsSuspect: owner.IsSuspect));
        }

        return entries;
    }

    // ── anomalies ────────────────────────────────────────────────────────────

    private static void AddFileAnomalies(HostsDocument document, List<HostsAnomaly> anomalies)
    {
        if (document.DecodedWithFallbackEncoding)
        {
            anomalies.Add(new HostsAnomaly(
                HostsAnomalyKind.NonUtf8Encoding,
                HostsSeverityLevel.Caution,
                "This file is not valid UTF-8 and was read as Latin-1 so its bytes survive intact. "
                + "Non-ASCII characters may not be what was intended."));
        }

        var terminators = document.Lines
            .Where(line => line.NewLine.Length > 0)
            .Select(line => line.NewLine)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (terminators > 1)
        {
            anomalies.Add(new HostsAnomaly(
                HostsAnomalyKind.MixedNewLines,
                HostsSeverityLevel.Normal,
                "This file mixes CRLF and LF line endings. Each line keeps its own, so saving will "
                + $"not change them; new lines use {Describe(document.DefaultNewLine)}."));
        }
    }

    private static void AddEntryAnomalies(
        IReadOnlyList<HostsLineFacts> facts,
        List<HostsEntry> entries,
        Dictionary<int, LineOwner> owners,
        List<HostsAnomaly> anomalies)
    {
        // Hostnames resolve case-insensitively, so a duplicate that differs only in case is still
        // a duplicate.
        var duplicates = entries
            .Where(entry => entry.IsActive && entry.IsValid)
            .SelectMany(entry => entry.Hostnames.Select(host => (Host: host, entry.Line)))
            .GroupBy(pair => pair.Host, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(pair => pair.Line).Distinct().Count() > 1);

        foreach (var duplicate in duplicates)
        {
            var lines = duplicate.Select(pair => pair.Line).Distinct().Order().ToArray();

            anomalies.Add(new HostsAnomaly(
                HostsAnomalyKind.DuplicateActiveHost,
                HostsSeverityLevel.Caution,
                $"'{duplicate.Key}' is enabled on {lines.Length} lines. Windows uses the first and "
                + "ignores the rest, so the later ones have no effect.",
                Lines: lines));
        }

        var trailing = entries.Where(entry => entry.TrailingText is not null).Select(entry => entry.Line).ToArray();
        if (trailing.Length > 0)
        {
            anomalies.Add(new HostsAnomaly(
                HostsAnomalyKind.TrailingTextAfterHostnames,
                HostsSeverityLevel.Normal,
                $"{Count(trailing, "line")} have text in brackets after the hostnames. The hosts format "
                + "has no comment there, so those words become extra hostnames whenever the line is "
                + "enabled. Move them behind a '#'.",
                Lines: trailing));
        }

        // Only lines the option genuinely owns are worth reporting. A prose comment that drifted
        // into scope is already covered, in far more useful terms, by the risk analyzer.
        var malformed = facts
            .Where(fact => fact.HasContent
                           && !fact.IsParked
                           && !fact.IsEntry
                           && owners.TryGetValue(fact.Number, out var owner)
                           && !owner.IsSuspect)
            .Select(fact => fact.Number)
            .ToArray();

        if (malformed.Length > 0)
        {
            anomalies.Add(new HostsAnomaly(
                HostsAnomalyKind.MalformedEntry,
                HostsSeverityLevel.Normal,
                $"{Count(malformed, "line")} inside a switchable block are not valid host entries. "
                + "They will still be commented and uncommented along with the block.",
                Lines: malformed));
        }
    }

    private static string Count(IReadOnlyCollection<int> lines, string noun) =>
        lines.Count == 1 ? $"1 {noun}" : $"{lines.Count} {noun}s";

    private static string Describe(string newLine) => newLine == "\r\n" ? "CRLF" : "LF";
}
