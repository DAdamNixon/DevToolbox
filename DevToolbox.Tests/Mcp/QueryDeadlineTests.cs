using System.Diagnostics;
using DevToolbox.Mcp.Core;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// The query budget — the guardrail that exists <em>because</em> unrestricted SQL was allowed in.
/// <para>
/// It bounds how long the CALLER waits, not how long the statement runs. That distinction is the
/// whole design, and it is asserted below rather than glossed: claiming a hard timeout the storage
/// layer cannot deliver would be a guardrail that is believed and does not hold.
/// </para>
/// </summary>
public sealed class QueryDeadlineTests
{
    [Fact]
    public async Task Work_that_finishes_in_time_returns_its_result()
    {
        var value = await QueryDeadline.RunAsync(_ => Task.FromResult(42), TimeSpan.FromSeconds(5));
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task Work_that_outruns_the_budget_throws_instead_of_hanging_the_session()
    {
        // The failure this prevents: on stdio a server that never returns is an agent session that
        // simply stops, with no error and nothing to act on.
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<QueryTimeoutException>(() => QueryDeadline.RunAsync(
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return 0;
            },
            TimeSpan.FromMilliseconds(150)));

        stopwatch.Stop();

        // The point is that control came back promptly, not merely that it came back.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task The_refusal_tells_the_caller_how_to_narrow_the_query()
    {
        var ex = await Assert.ThrowsAsync<QueryTimeoutException>(() => QueryDeadline.RunAsync(
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return 0;
            },
            TimeSpan.FromMilliseconds(100)));

        // An error an agent cannot act on just becomes a retry of the same query.
        Assert.Contains("WHERE", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prepare_table", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_exception_from_the_work_itself_is_not_disguised_as_a_timeout()
    {
        // A SQLite parse error must reach the caller as itself. Wrapping everything that went wrong
        // inside the budget into "your query was too slow" would be actively misleading.
        await Assert.ThrowsAsync<InvalidOperationException>(() => QueryDeadline.RunAsync<int>(
            _ => throw new InvalidOperationException("no such column: Nope"),
            TimeSpan.FromSeconds(5)));
    }
}
