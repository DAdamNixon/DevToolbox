namespace DevToolbox.Mcp.Core;

/// <summary>
/// Finds the log names a proposed new name would collide with.
/// <para>
/// This exists because <c>prepare_table</c>'s <c>logFile</c> is a <b>prefix</b> match — it becomes
/// <c>EnumerateFiles($"{logFile}*{extension}")</c> — and the response reports only the prefix the
/// caller asked for plus a total row count. It never names the files it actually read. So one
/// handle can silently hold two flows, and a bare <c>COUNT(*)</c> over it sums both. That is not a
/// hypothetical: on 2026-09-02 Checkout order completes came back as 93 when the truth was 75,
/// because <c>Checkout.WithAccount</c> ingested <c>Checkout.WithAccount.Modern</c> as well.
/// </para>
/// <para>
/// <b>This type reports; it does not refuse.</b> A collision is a design smell, not an error —
/// grouping rules differ per log, and a name that overlaps another may still be the right call once
/// the designer knows it overlaps. Ruled 2026-09-03: taught, not enforced. Nothing here throws, and
/// no caller should treat a collision as a failure.
/// </para>
/// </summary>
public static class LogNameCollision
{
    /// <summary>The proposed name is a prefix of an existing one: querying the <i>proposed</i> name will pull the existing file's rows in too.</summary>
    public const string ProposedIsPrefix = "proposed-is-prefix-of-existing";

    /// <summary>An existing name is a prefix of the proposed one: creating this file silently widens every query already written against the <i>existing</i> name. This is the WithAccount case, and the more damaging direction — it breaks queries that already work.</summary>
    public const string ExistingIsPrefix = "existing-is-prefix-of-proposed";

    /// <summary>The name already exists. Not necessarily wrong — appending to an existing log is normal — but it is never accidental, so it is surfaced rather than ignored.</summary>
    public const string ExactMatch = "exact-match";

    /// <summary>
    /// Compares <paramref name="proposedName"/> against every existing name, in both directions.
    /// Case-insensitive, because these are Windows filenames and a casing difference is not a
    /// different log. Blank input returns no collisions rather than throwing — validating the name
    /// itself is <see cref="LogFileNamePolicy"/>'s job, and duplicating it here would put the same
    /// rule in two places.
    /// </summary>
    public static IReadOnlyList<NameCollision> Find(string? proposedName, IEnumerable<DiscoveredName>? existing)
    {
        if (string.IsNullOrWhiteSpace(proposedName) || existing is null)
        {
            return [];
        }

        var proposed = proposedName.Trim();
        var found = new List<NameCollision>();

        foreach (var candidate in existing)
        {
            if (string.IsNullOrWhiteSpace(candidate?.Name))
            {
                continue;
            }

            var name = candidate.Name;

            if (name.Equals(proposed, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new NameCollision(name, candidate.FileCount, ExactMatch, true));
                continue;
            }

            if (name.StartsWith(proposed, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new NameCollision(name, candidate.FileCount, ProposedIsPrefix, IsAtSeparator(name, proposed.Length)));
            }
            else if (proposed.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new NameCollision(name, candidate.FileCount, ExistingIsPrefix, IsAtSeparator(proposed, name.Length)));
            }
        }

        // Worst direction first: an existing prefix breaks queries that already work, which is
        // strictly worse than a proposed prefix that only misleads queries nobody has written yet.
        return found
            .OrderBy(c => c.Direction switch { ExistingIsPrefix => 0, ExactMatch => 1, _ => 2 })
            .ThenByDescending(c => c.FileCount)
            .ThenBy(c => c.ExistingName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Whether the longer name continues with a separator at the point the shorter one ends.
    /// <para>
    /// <c>Checkout</c> vs <c>Checkout.Pickup</c> breaks at a dot, which reads as a family and is
    /// the collision people expect. <c>Checkout</c> vs <c>CheckoutLegacy</c> has no separator at
    /// all, so it does not look like a relative — and the prefix match catches it anyway, because
    /// the pattern is a raw string prefix and knows nothing about dots. The second kind is the one
    /// that gets missed, so it is reported distinctly rather than folded in.
    /// </para>
    /// </summary>
    private static bool IsAtSeparator(string longer, int shorterLength)
        => shorterLength < longer.Length && (longer[shorterLength] is '.' or '-' or '_');
}

/// <summary>One existing name that overlaps a proposed name, and how.</summary>
/// <param name="ExistingName">The name already present in the searched locations.</param>
/// <param name="FileCount">How many files carry it — a rough measure of how much history a mistake here would affect.</param>
/// <param name="Direction">One of the direction constants on <see cref="LogNameCollision"/>.</param>
/// <param name="AtSeparator">
/// <see langword="true"/> when the names diverge at a <c>.</c>, <c>-</c> or <c>_</c>, so they read
/// as related. <see langword="false"/> means the overlap is mid-token and easy to miss by eye,
/// though the prefix match treats both identically.
/// </param>
public sealed record NameCollision(string ExistingName, int FileCount, string Direction, bool AtSeparator);
