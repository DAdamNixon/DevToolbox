namespace DevToolbox.Mcp.Core;

/// <summary>
/// The row-cap clamp policy — arithmetic only, and deliberately the same semantics the DB2 server
/// settled on: clamp above the ceiling, reject at or below zero.
/// <para>
/// The numbers differ from that server's because the failure differs. There, fifty rows of a
/// customer table was a lot of production data to put in a context window. Here the data is a log
/// the dev can already open, and the hazard is volume: a single day of web logs is millions of
/// lines, and an uncapped page would flood the agent's context and the bill with it.
/// </para>
/// </summary>
internal static class RowCap
{
    /// <summary>The hard ceiling. Never exceeded, whatever a caller asks for.</summary>
    internal const int Max = 200;

    /// <summary>
    /// What an omitted page size means. Well below <see cref="Max"/>: an agent orienting itself in
    /// a log wants to see the shape of the rows, and asking for more is one argument away.
    /// </summary>
    internal const int Default = 50;

    /// <summary>
    /// null (omitted) -> <see cref="Default"/>, NOT <see cref="Max"/>.
    /// Above <see cref="Max"/> -> clamped (the caller is told through a separate <c>capped</c>
    /// flag; this method returns only the number to use).
    /// Zero or less -> rejected: an empty page is indistinguishable from "the query matched
    /// nothing", so honouring 0 silently would make an ambiguous answer look like a real one.
    /// </summary>
    internal static int Clamp(int? requested)
    {
        if (requested is null) return Default;

        if (requested.Value <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(requested),
                requested.Value,
                $"Page size must be greater than zero (requested {requested.Value}, ceiling {Max}).");

        return requested.Value > Max ? Max : requested.Value;
    }
}
