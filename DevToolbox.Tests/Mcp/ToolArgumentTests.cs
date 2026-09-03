using DevToolbox.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// The refusals as a CALLER actually receives them — through the tool method and
/// <c>ToolErrors</c>, not by calling the service directly.
/// <para>
/// This file exists because of a defect the service-level tests could not see. The
/// <c>locations</c> argument was first written non-nullable, which the SDK translates into a
/// required schema property, and the SDK then rejects a call that omits it before the body runs.
/// The service test still passed — it calls the service — while a real client over stdio got
/// <c>"An error occurred invoking 'prepare_table'."</c> and no reason whatsoever. Found by driving
/// the exe on 2026-09-03, not by the suite.
/// </para>
/// <para>
/// So the parameter is optional in the schema and required in the body, and these tests assert the
/// half that broke: that the authored words survive all the way out. It is the same lesson the
/// module already recorded when a BCL <c>Path.Combine</c> message reached a caller as if it had
/// been written here — where a refusal is decided matters less than whether it can explain itself.
/// </para>
/// </summary>
public sealed class ToolArgumentTests
{
    private static LogQueryTools Query(LogEnvironment env) =>
        new(env.Service, NullLogger<LogQueryTools>.Instance);

    private static LogCatalogTools Catalog(LogEnvironment env) =>
        new(env.Service, NullLogger<LogCatalogTools>.Instance);

    [Fact]
    public async Task Prepare_table_without_locations_explains_itself_to_the_caller()
    {
        using var env = new LogEnvironment();

        var ex = await Assert.ThrowsAsync<McpException>(
            () => Query(env).PrepareTable("Checkout", "Checkout", "2026-08-21", "2026-08-21"));

        // The three things the caller needs: that it is required, that guessing a default is not on
        // offer, and where the names come from.
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no default", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("list_locations", ex.Message);
    }

    [Fact]
    public async Task List_log_files_without_locations_explains_itself_to_the_caller()
    {
        using var env = new LogEnvironment();

        var ex = await Assert.ThrowsAsync<McpException>(
            () => Catalog(env).ListLogFiles("Checkout"));

        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("list_locations", ex.Message);
    }

    [Fact]
    public async Task An_unknown_location_names_itself_and_the_alternatives_to_the_caller()
    {
        using var env = new LogEnvironment();

        var ex = await Assert.ThrowsAsync<McpException>(
            () => Query(env).PrepareTable("Checkout", "Checkout", "2026-08-21", "2026-08-21",
                new[] { "Live Web09" }));

        Assert.Contains("Live Web09", ex.Message);
        Assert.Contains("Local Logs", ex.Message);
    }

    [Fact]
    public async Task A_refusal_never_arrives_as_the_generic_invocation_failure()
    {
        // The exact string a caller got before the fix. If any refusal ever reads like this again,
        // the guardrail is still enforced and has stopped being teachable — which is how this was
        // missed the first time.
        using var env = new LogEnvironment();

        var omitted = await Assert.ThrowsAsync<McpException>(
            () => Query(env).PrepareTable("Checkout", "Checkout", "2026-08-21", "2026-08-21"));
        var unknown = await Assert.ThrowsAsync<McpException>(
            () => Query(env).PrepareTable("Checkout", "Checkout", "2026-08-21", "2026-08-21", new[] { "Nope" }));

        foreach (var message in new[] { omitted.Message, unknown.Message })
        {
            Assert.DoesNotContain("An error occurred invoking", message);
            Assert.True(message.Length > 60, $"Refusal is too short to teach anything: '{message}'");
        }
    }
}
