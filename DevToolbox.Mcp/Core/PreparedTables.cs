using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DevToolbox.Mcp.Core;

/// <summary>One ingest, and what a caller is allowed to know about it.</summary>
internal sealed record PreparedTable(
    string Handle,
    string LogFile,
    string Template,
    IReadOnlyList<string> Columns,
    int Rows,
    DateTime PreparedUtc);

/// <summary>
/// The handles this process has issued.
/// <para>
/// Two jobs, and the second is the one that matters. The obvious job is remembering what a handle
/// refers to. The load-bearing job is that <b>a handle is the only string from a caller that ever
/// reaches SQL as an identifier</b> — it lands in <c>FROM [handle]</c>, which is interpolated, not
/// parameterized, because a table name cannot be a parameter. So a handle is never taken on trust:
/// <see cref="Resolve"/> refuses anything this registry did not itself issue, which makes the set
/// of reachable identifiers a closed one that no caller can add to.
/// </para>
/// <para>
/// <see cref="NewHandle"/> generates the names, so even the issued set is drawn from a shape that
/// cannot contain a quote, a bracket or a space. Validation and generation agree on
/// <see cref="HandleShape"/> deliberately: the guard would still hold if the generator changed,
/// and the generator would still be safe if the guard were removed.
/// </para>
/// </summary>
public sealed class PreparedTables
{
    /// <summary>Letters, digits and underscore only — nothing that can end an identifier.</summary>
    private static readonly Regex HandleShape = new(@"^logs_[a-z0-9]{8,32}$", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, PreparedTable> _tables = new(StringComparer.Ordinal);

    internal static string NewHandle() => $"logs_{Guid.NewGuid():n}"[..21];

    internal void Register(PreparedTable table) => _tables[table.Handle] = table;

    /// <summary>
    /// The prepared table for a handle, or a refusal naming only the caller's own handle.
    /// <para>
    /// The message deliberately does not enumerate valid handles. A caller that lost one should
    /// prepare again; listing what else exists tells it about other work in the session.
    /// </para>
    /// </summary>
    internal PreparedTable Resolve(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle) || !HandleShape.IsMatch(handle))
            throw new UnknownHandleException(
                $"'{handle}' is not a table handle issued by this server. Call prepare_table first and use the handle it returns.");

        if (!_tables.TryGetValue(handle, out var table))
            throw new UnknownHandleException(
                $"Table handle '{handle}' is not known to this server. It may belong to a different session; call prepare_table again.");

        return table;
    }

    internal IReadOnlyCollection<PreparedTable> All => _tables.Values.ToList();
}

/// <summary>
/// A handle the server did not issue. Its own exception type so <c>ToolErrors</c> can pass the
/// message through verbatim — it names the caller's argument and nothing else, which is exactly
/// the class of message that is safe to return.
/// </summary>
internal sealed class UnknownHandleException : Exception
{
    internal UnknownHandleException(string message) : base(message) { }
}
