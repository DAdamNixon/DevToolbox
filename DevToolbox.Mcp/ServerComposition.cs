using DevToolbox.Mcp.Core;
using DevToolbox.Mcp.Tools;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DevToolbox.Mcp;

/// <summary>
/// The one place the server is composed. <c>Program</c> calls these and adds a transport; the test
/// suite calls the same methods with a different database path and no transport. Wiring inline in
/// <c>Program</c> would mean the tests assert on a configuration nobody ships.
/// </summary>
public static class ServerComposition
{
    /// <summary>
    /// Registers the ten tools and the services behind them.
    /// <para>
    /// Services go in through <c>TryAdd</c>, so a caller that already registered a substitute wins —
    /// that is how the tests point everything at a temporary config folder and database without a
    /// real one anywhere.
    /// </para>
    /// <para>
    /// Tool registration is an EXPLICIT list, deliberately not <c>WithToolsFromAssembly</c>. The
    /// claim this server makes is that its surface is a closed set of ten with exactly one write in
    /// it; an explicit list is a reviewable artifact of that claim, while an assembly scan is
    /// whatever happens to be in the assembly on the day.
    /// </para>
    /// </summary>
    public static IMcpServerBuilder AddLogViewerTools(IServiceCollection services, string? databasePath = null)
    {
        var dbPath = databasePath ?? Path.Combine(
            McpLogDatabase.Folder,
            McpLogDatabase.FileNameFor(Environment.ProcessId, DateTime.UtcNow));

        services.TryAddSingleton<IYamlStorageService>(_ => new McpYamlStorage());
        services.TryAddSingleton<ISavedQueryService>(sp => new SavedQueryService(sp.GetRequiredService<IYamlStorageService>()));
        services.TryAddSingleton(_ => new PreparedTables());
        services.TryAddSingleton(_ => new ColumnProfiler(dbPath));

        services.TryAddSingleton(sp => new LogViewerService(
            sp.GetRequiredService<IYamlStorageService>(),

            // Writable, and used for exactly one thing: letting an ingest create and fill its table.
            new SqliteLogStorageService(dbPath),

            // Read-only, and every query goes through it. Its writing members throw AND its
            // connections are opened Mode=ReadOnly, so the path an agent's arguments travel has no
            // write capability in it — two independent layers, neither one load-bearing alone.
            new SqliteLogStorageService(dbPath, readOnly: true),

            sp.GetRequiredService<ISavedQueryService>(),
            sp.GetRequiredService<PreparedTables>(),
            sp.GetRequiredService<ColumnProfiler>()));

        return services
            .AddMcpServer()
            .WithTools<LogCatalogTools>()
            .WithTools<LogQueryTools>()
            .WithTools<SavedQueryTools>();
    }

    /// <summary>
    /// The logging policy. STDOUT IS THE JSON-RPC WIRE, and a single stray byte on it corrupts the
    /// session — with a symptom (a client that mysteriously fails to enumerate tools) that looks
    /// nothing like the cause.
    /// <para>
    /// HAZARD 1 — the console provider's default sink is stdout. Left alone, the first log line
    /// destroys the session. <c>LogToStandardErrorThreshold = Trace</c> means "at or above the
    /// lowest level", i.e. everything, goes to stderr and nothing to stdout.
    /// </para>
    /// <para>
    /// HAZARD 2 — the SDK ships payload loggers whose names end in "Sensitive". They log WHOLE
    /// JSON-RPC messages, and a <c>query_entries</c> message carries the caller's SQL while its
    /// response carries log rows containing user-entered text. Our own logging discipline cannot
    /// prevent that, because those are the SDK's loggers. The only defence is never letting those
    /// categories reach a level where they fire.
    /// </para>
    /// <para>
    /// The level is NOT read from configuration or an environment variable, on purpose. An override
    /// that switches on payload logging is a guardrail with an off switch.
    /// </para>
    /// </summary>
    public static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();

        logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        logging.SetMinimumLevel(LogLevel.Information);

        // Hazard 2. Belt: the global minimum already excludes Debug and Trace. Braces: this cannot
        // be raised by a category rule from configuration, because no configuration is bound.
        logging.AddFilter("ModelContextProtocol", LogLevel.Information);
    }
}
