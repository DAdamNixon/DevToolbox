using DevToolbox.Services.Models;

namespace DevToolbox.Mcp.Core;

/// <summary>
/// Which configured locations a single call is allowed to walk. Required, always, with no default.
/// <para>
/// This is the replacement for the half of <see cref="LocationPolicy"/> that was retired on
/// 2026-09-03. While the server read one local directory, "which locations" had an obvious answer
/// and no argument: <c>AdmittedLocationsAsync</c> returned everything admitted and every ingest
/// walked all of it. That is a reasonable shape for one local folder and a bad one for ten
/// locations, four of which are web servers currently serving customers and one of which is an
/// archive share <c>DbLogService</c> measures at 17 seconds across 238,000 files.
/// </para>
/// <para>
/// <b>Required rather than defaulted, and the alternatives are worth recording because both are
/// tempting.</b> Defaulting to the local locations would have preserved, invisibly, the exact
/// guardrail that was just deliberately retired — the dev would ask for the network and keep
/// getting local until they discovered the argument. Defaulting to all of them reintroduces the
/// accident in full: one unqualified call, ten locations, an SMB scan of production nobody asked
/// for. A required argument is the only shape in which the expensive thing cannot happen unless
/// someone names it. It costs a little chatter, which is the cheapest thing here.
/// </para>
/// <para>
/// There is deliberately <b>no <c>"*"</c> or <c>"all"</c> token.</b> It would be one character
/// away from the default that was just rejected, and it would be reached for exactly when the
/// caller has not thought about cost. Ten locations means naming ten, and that friction is the
/// feature.
/// </para>
/// <para>
/// <b>An unknown name is refused, never ignored.</b> Silently dropping one returns rows — real
/// rows, from a real ingest, drawn from a smaller population than the caller asked for, with no
/// error and nothing to notice. That is the same failure this module already rejected a shared
/// table over, and the one the DB2 server's <c>UserTypes</c> disagreement cost a wrong answer to:
/// the hazard is never the exception, it is the plausible result.
/// </para>
/// </summary>
internal static class LocationSelection
{
    internal const string ReasonNoneRequested =
        "The 'locations' argument is required and may not be empty: name the locations this call " +
        "should read. There is no default, because reading every configured location can mean an " +
        "SMB walk across production log shares. Call list_locations for the names.";

    internal const string ReasonBlankName =
        "A location name in 'locations' is blank. Call list_locations for the names.";

    /// <summary>An unknown name, with the names that would have worked. Never silently dropped.</summary>
    internal static string UnknownReason(string requested, IEnumerable<string> known) =>
        $"Unknown location '{requested}'. This server knows: {string.Join(", ", known)}. " +
        "Call list_locations. A name that is not recognised is refused rather than skipped, because " +
        "skipping it would return rows from a smaller population with no sign anything was missing.";

    /// <summary>
    /// A name that IS configured but whose path this server cannot use. A distinct message from
    /// the unknown one on purpose: one means the caller mistyped, the other means the config needs
    /// fixing, and telling them apart is the reason refusals are reported at all.
    /// </summary>
    internal static string UnusableReason(string requested, string refusal) =>
        $"Location '{requested}' is configured, but its path cannot be used. {refusal}";

    /// <summary>
    /// The locations to walk, in the order requested, de-duplicated.
    /// <para>
    /// Takes the FULL configured list rather than a pre-filtered one, so that a name which exists
    /// but is unusable can be answered as such instead of falling through to "unknown".
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// No names, a blank name, an unknown name, or a name whose location is unusable.
    /// </exception>
    internal static List<LogLocation> Resolve(
        IReadOnlyList<string>? requested,
        IReadOnlyList<LogLocation> configured)
    {
        if (requested is null || requested.Count == 0)
            throw new ArgumentException(ReasonNoneRequested, nameof(requested));

        var known = configured.Select(l => l.Name).ToList();
        var selected = new List<LogLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in requested)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException(ReasonBlankName, nameof(requested));

            var trimmed = name.Trim();

            var match = configured.FirstOrDefault(l => string.Equals(l.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(UnknownReason(trimmed, known), nameof(requested));

            var refusal = LocationPolicy.Refuse(match);
            if (refusal is not null)
                throw new ArgumentException(UnusableReason(trimmed, refusal), nameof(requested));

            // Naming the same location twice is a harmless mistake, not an ambiguous one — but it
            // must not double-count the files, so it is collapsed rather than refused.
            if (seen.Add(match.Name))
                selected.Add(match);
        }

        return selected;
    }
}
