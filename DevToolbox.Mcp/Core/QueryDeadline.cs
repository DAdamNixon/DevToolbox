namespace DevToolbox.Mcp.Core;

/// <summary>
/// A wall-clock budget on a query, and an honest account of what it does and does not do.
/// <para>
/// The hazard is real and follows directly from the decision to accept unrestricted SQL: a
/// cartesian join, an unbounded recursive CTE, or a <c>LIKE '%x%'</c> across millions of rows runs
/// for as long as it takes. On stdio a server that never returns is a session that hangs, with no
/// error and nothing for the caller to act on — the agent simply stops.
/// </para>
/// <para>
/// <b>What this does</b>: bounds how long the CALLER waits. Past the deadline the tool returns an
/// error the agent can read and act on, and the session stays usable.
/// </para>
/// <para>
/// <b>What this does not do</b>: stop the statement. SQLite's execution is synchronous, and
/// Microsoft.Data.Sqlite's <c>CommandTimeout</c> governs retry-on-busy rather than aborting a
/// long computation — so cancelling the wait does not cancel the work. The abandoned statement
/// runs to completion on a thread-pool thread.
/// </para>
/// <para>
/// That is accepted rather than hidden, because the leak is bounded by the thing it is a leak in:
/// this is a single-session process that exits when its client disconnects, so an abandoned query
/// outlives the request but not the session. Claiming a hard timeout here would be claiming a
/// guarantee the storage layer cannot give, and a guardrail that is believed but does not hold is
/// worse than one that is documented and does.
/// </para>
/// </summary>
internal static class QueryDeadline
{
    /// <summary>
    /// Long enough for an honest query over a large ingest, short enough that a runaway is
    /// reported while the agent is still working on the same question.
    /// </summary>
    internal static readonly TimeSpan Default = TimeSpan.FromSeconds(30);

    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan? budget = null,
        CancellationToken cancellationToken = default)
    {
        var limit = budget ?? Default;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Started on the pool so the wait below is a real wait: calling work() inline would run a
        // synchronous SQLite statement on this thread and the deadline would never be observed.
        var task = Task.Run(() => work(linked.Token), linked.Token);
        var finished = await Task.WhenAny(task, Task.Delay(limit, cancellationToken)).ConfigureAwait(false);

        if (finished != task)
        {
            // Signalled for the sake of anything that does observe it. The statement itself very
            // likely will not — see the type-level remarks.
            linked.Cancel();

            throw new QueryTimeoutException(
                $"Query exceeded the {limit.TotalSeconds:0}s budget and was abandoned. " +
                "Narrow it — add a WHERE clause, reduce the date range at prepare_table, or query fewer columns.");
        }

        return await task.ConfigureAwait(false);
    }
}

/// <summary>
/// A query that outran its budget. Its own type so <c>ToolErrors</c> passes the message through
/// verbatim: it describes the caller's own query and suggests the fix, and reveals nothing else.
/// </summary>
internal sealed class QueryTimeoutException : Exception
{
    internal QueryTimeoutException(string message) : base(message) { }
}
