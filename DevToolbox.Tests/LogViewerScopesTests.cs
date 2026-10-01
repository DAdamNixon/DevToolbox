using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using DevToolbox.UI;
using DevToolbox.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevToolbox.Tests;

/// <summary>
/// Two Log Viewers on one database file — the window and a browser view of it — each resolved from a
/// DI scope of its own by the app's real registration (<see cref="ServiceRegistration"/>), pointed at a
/// scratch config and database.
/// <para>
/// On one shared <c>logs</c> table a load in either dropped and recreated it under the other: the
/// second showed the first one's rows, or a query failed with "no such table". Collapsing did the same
/// to <c>results</c>.
/// </para>
/// </summary>
[Collection(LoadLockCollection.Name)]
public sealed class LogViewerScopesTests : IDisposable
{
    private readonly TempDirectory _config = new("scopes-config");
    private readonly TempDirectory _logs = new("scopes-logs");
    private readonly TempDirectory _db = new("scopes-db");
    private readonly SingleThreadContext _ui = new();
    private readonly ServiceProvider _app;

    /// <summary>The database every scope shares, for looking at its tables directly.</summary>
    private readonly SqliteLogStorageService _storage;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public LogViewerScopesTests()
    {
        File.WriteAllText(Path.Combine(_config.Path, "log_templates_index.yaml"), """
            templates:
              - name: "Basic"
                file: "Basic.yaml"
            """);
        File.WriteAllText(Path.Combine(_config.Path, "Basic.yaml"), """
            name: "Basic"
            extension: ".txt"
            delimiter: "|"
            columns:
              - Message
            """);

        var dbPath = Path.Combine(_db.Path, "logs.db");
        _storage = new SqliteLogStorageService(dbPath);

        var services = new ServiceCollection().AddDevToolboxApp();
        services.AddSingleton<IYamlStorageService>(new DirectoryYamlStorage(_config.Path));
        services.AddScoped<ILogStorageService>(_ => new SqliteLogStorageService(dbPath));
        _app = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _app.Dispose();
        _ui.Dispose();
        _config.Dispose();
        _logs.Dispose();
        _db.Dispose();
    }

    private sealed record Window(IServiceScope Scope, LogSearchStateService State, DbLogService Service) : IDisposable
    {
        public void Dispose() => Scope.Dispose();
    }

    /// <summary>A Log Viewer in a scope of its own, set up to load <paramref name="logFile"/> from today.</summary>
    private Window Open(string logFile, Func<string, Stream?>? open = null)
    {
        var scope = _app.CreateScope();
        var service = Assert.IsType<DbLogService>(scope.ServiceProvider.GetRequiredService<ILogFileService>());
        if (open is not null) service.FileOpener = path => open(path) ?? File.OpenRead(path);

        var state = scope.ServiceProvider.GetRequiredService<LogSearchStateService>();
        state.AvailableTemplates = new() { new LogTemplateIndexEntry { Name = "Basic", File = "Basic.yaml" } };
        state.SelectedTemplateName = "Basic";
        state.SelectedLocations = new() { new LogLocation { Name = "Local", Path = _logs.Path } };
        state.StartDate = DateTime.Today;
        state.EndDate = DateTime.Today;
        state.LogFile = logFile;
        state.LivePollInterval = TimeSpan.FromMilliseconds(5);
        state.LiveStartDelay = TimeSpan.Zero;
        state.LiveMinInterval = TimeSpan.Zero;
        state.LiveCostFactor = 0;
        state.OnChanged += () => { };
        return new Window(scope, state, service);
    }

    private string WriteLogFile(string name, IEnumerable<string>? lines = null)
    {
        var path = Path.Combine(_logs.Path, $"{name}.txt");
        File.WriteAllLines(path, lines ?? new[] { "placeholder" });
        return path;
    }

    private static IEnumerable<string> Lines(string prefix, int from, int count) =>
        Enumerable.Range(from, count).Select(i => $"{prefix} {i}");

    private static byte[] Utf8(IEnumerable<string> lines) =>
        System.Text.Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }

    private static void AssertShows(LogSearchStateService state, string prefix, int rows)
    {
        Assert.Equal("", state.ErrorMessage);
        Assert.Equal(rows, state.TotalRecords);
        Assert.NotEmpty(state.FilteredLogLines);
        Assert.All(state.FilteredLogLines, r => Assert.StartsWith(prefix, r["Message"]));
    }

    // --- loads ---

    [Fact]
    public async Task Two_windows_loading_at_once_each_end_up_showing_their_own_rows()
    {
        WriteLogFile("Alpha", Lines("alpha", 1, 30));
        WriteLogFile("Beta", Lines("beta", 1, 20));
        using var a = Open("Alpha");
        using var b = Open("Beta");

        await _ui.RunAsync(async () =>
        {
            await Task.WhenAll(a.State.SearchAsync(), b.State.SearchAsync()).WaitAsync(Patience);
            AssertShows(a.State, "alpha", 30);
            AssertShows(b.State, "beta", 20);

            // Asked again once both are done: on one shared table, whichever load went first was by
            // now looking at the other's rows.
            await a.State.QueryCurrentPageAsync();
            await b.State.QueryCurrentPageAsync();
            AssertShows(a.State, "alpha", 30);
            AssertShows(b.State, "beta", 20);
        });

        Assert.NotEqual(a.State.Logs.TableName, b.State.Logs.TableName);
    }

    [Fact]
    public async Task A_load_in_one_window_leaves_the_rows_another_is_showing_alone()
    {
        WriteLogFile("Alpha", Lines("alpha", 1, 30));
        var betaPath = WriteLogFile("Beta");

        // 1,500 lines, then the read blocks: one batch committed and on screen, the rest still coming.
        var beta = new SteppedStream(Utf8(Lines("beta", 1, 1500)), Utf8(Lines("beta", 1501, 100)));
        using var a = Open("Alpha");
        using var b = Open("Beta", p => p == betaPath ? beta : null);

        try
        {
            await _ui.RunAsync(async () =>
            {
                await a.State.SearchAsync().WaitAsync(Patience);
                AssertShows(a.State, "alpha", 30);

                var loading = b.State.SearchAsync();
                await WaitUntil(() => b.State.IsLive && b.State.TotalRecords == 1000, "the other window's first rows");

                // Its table was replaced and is being filled; every kind of query here still reads this one's.
                await a.State.QueryCurrentPageAsync();
                AssertShows(a.State, "alpha", 30);

                await a.State.SetSplitModeAsync(LogSplitMode.File);
                Assert.Equal(new[] { "All", "Alpha.txt" }, a.State.Tabs.Select(t => t.Label));

                a.State.AdvancedRawMode = true;
                a.State.AdvancedExpression = "SELECT * FROM logs";
                await a.State.RunLiveQueryAsync();
                AssertShows(a.State, "alpha", 30);

                beta.End();
                await loading.WaitAsync(Patience);
                AssertShows(b.State, "beta", 1600);

                await a.State.QueryCurrentPageAsync();
                AssertShows(a.State, "alpha", 30);
            });
        }
        finally
        {
            beta.End();
        }
    }

    // --- results ---

    [Fact]
    public async Task A_collapse_in_one_window_neither_replaces_nor_drops_another_windows_results()
    {
        WriteLogFile("Alpha", Lines("alpha", 1, 30));
        WriteLogFile("Beta", Lines("beta", 1, 20));
        using var a = Open("Alpha");
        using var b = Open("Beta");

        await _ui.RunAsync(async () =>
        {
            await a.State.SearchAsync();
            await b.State.SearchAsync();

            a.State.Logs.KeywordRows[0].Text = "alpha 1";   // 1 and 10-19
            await a.State.RunLiveQueryAsync();
            await a.State.CollapseToResultsAsync();
            AssertShows(a.State, "alpha 1", 11);

            b.State.Logs.KeywordRows[0].Text = "beta 2";    // 2 and 20
            await b.State.RunLiveQueryAsync();
            await b.State.CollapseToResultsAsync();
            AssertShows(b.State, "beta 2", 2);

            Assert.NotEqual(a.State.Results!.TableName, b.State.Results!.TableName);
            Assert.Equal(DbLogService.ResultsSqlName, a.State.Results.SqlName);

            await a.State.QueryCurrentPageAsync();
            AssertShows(a.State, "alpha 1", 11);

            // Discarding drops that window's results; this one's are still there to query.
            await b.State.DiscardResultsAsync();
            AssertShows(b.State, "beta 2", 2);
            await a.State.QueryCurrentPageAsync();
            AssertShows(a.State, "alpha 1", 11);

            // SQL against the results view still calls it results.
            a.State.AdvancedRawMode = true;
            a.State.AdvancedExpression = "SELECT * FROM results ORDER BY rowid";
            await a.State.RunLiveQueryAsync();
            AssertShows(a.State, "alpha 1", 11);
        });
    }

    // --- SQL mode ---

    [Fact]
    public async Task A_saved_query_that_says_logs_reads_each_windows_own_table()
    {
        WriteLogFile("Alpha", Lines("alpha", 1, 30));
        WriteLogFile("Beta", Lines("beta", 1, 20));
        using var a = Open("Alpha");
        using var b = Open("Beta");

        // As one sits in saved_queries.yaml: rowid, a qualified column, and the table called logs.
        var lastThree = new SavedQuery { Id = "q1", Name = "Last three", Sql = "SELECT rowid, logs.Message FROM logs ORDER BY rowid DESC LIMIT 3" };

        await _ui.RunAsync(async () =>
        {
            await a.State.SearchAsync();
            await b.State.SearchAsync();

            await a.State.ApplySavedQueryAsync(lastThree);
            await b.State.ApplySavedQueryAsync(lastThree);
        });

        Assert.Equal("", a.State.ErrorMessage);
        Assert.Equal(new[] { "alpha 30", "alpha 29", "alpha 28" }, a.State.FilteredLogLines.Select(r => r["Message"]));
        Assert.Equal(new[] { "beta 20", "beta 19", "beta 18" }, b.State.FilteredLogLines.Select(r => r["Message"]));

        // What the page shows as the table's name is what the SQL box takes.
        Assert.Equal(DbLogService.LogsSqlName, a.State.Active.SqlName);
        Assert.StartsWith("logs_", a.State.Logs.TableName);
    }

    [Fact]
    public async Task A_sql_error_names_the_table_the_way_it_was_written()
    {
        WriteLogFile("Alpha", Lines("alpha", 1, 3));
        using var a = Open("Alpha");

        await _ui.RunAsync(async () =>
        {
            await a.State.SearchAsync();
            a.State.AdvancedRawMode = true;
            a.State.AdvancedExpression = "SELECT * FROM results"; // nothing collapsed yet
            await a.State.RunLiveQueryAsync();
        });

        Assert.Contains("no such table: results", a.State.ErrorMessage);
        Assert.DoesNotContain(a.Service.ResultsTableName, a.State.ErrorMessage);
    }

    // --- lifetime ---

    [Fact]
    public async Task Closing_a_window_drops_its_own_tables_and_nobody_elses()
    {
        WriteLogFile("Alpha", Lines("alpha", 1, 30));
        WriteLogFile("Beta", Lines("beta", 1, 20));
        var a = Open("Alpha");
        using var b = Open("Beta");

        await _ui.RunAsync(async () =>
        {
            await a.State.SearchAsync();
            a.State.Logs.KeywordRows[0].Text = "alpha 1";
            await a.State.RunLiveQueryAsync();
            await a.State.CollapseToResultsAsync();
            await b.State.SearchAsync();
        });

        var aTables = new[] { a.State.Logs.TableName, a.State.Results!.TableName };
        foreach (var table in aTables) Assert.True(await _storage.TableExistsAsync(table));

        a.Dispose(); // a browser view closing: its scope ends
        await a.Service.TablesDropped.WaitAsync(Patience);

        foreach (var table in aTables) Assert.False(await _storage.TableExistsAsync(table));
        Assert.True(await _storage.TableExistsAsync(b.State.Logs.TableName));
        await _ui.RunAsync(async () =>
        {
            await b.State.QueryCurrentPageAsync();
            AssertShows(b.State, "beta", 20);
        });
    }

    [Fact]
    public async Task A_service_handed_its_table_name_leaves_the_table_when_disposed()
    {
        // The MCP server names its tables and keeps them past the service that built them.
        await _storage.EnsureTableAsync("logs", new[] { "Message" });
        await _storage.EnsureTableAsync("logs_handle", new[] { "Message" });
        var yaml = new DirectoryYamlStorage(_config.Path);

        foreach (var service in new[] { new DbLogService(yaml, _storage), new DbLogService(yaml, _storage, "logs_handle") })
        {
            service.Dispose();
            await service.TablesDropped;
        }

        Assert.True(await _storage.TableExistsAsync("logs"));
        Assert.True(await _storage.TableExistsAsync("logs_handle"));
    }

    // --- names ---

    [Fact]
    public void Each_scope_has_one_service_with_names_of_its_own()
    {
        using var a = Open("Alpha");
        using var b = Open("Beta");

        Assert.Same(a.Service, a.Scope.ServiceProvider.GetRequiredService<ILogFileService>());
        Assert.Matches("^results_[0-9a-f]{16}$", a.Service.ResultsTableName);
        Assert.NotEqual(a.Service.ResultsTableName, b.Service.ResultsTableName);
    }

    [Theory]
    [InlineData("logs]; DROP TABLE results; --")]
    [InlineData("my logs")]
    [InlineData("1logs")]
    [InlineData("logs-1")]
    [InlineData("logs\n")]
    public void A_table_name_that_would_not_survive_being_bracketed_is_refused(string name)
    {
        var yaml = new DirectoryYamlStorage(_config.Path);
        Assert.Throws<ArgumentException>(() => new DbLogService(yaml, _storage, name));
        Assert.Throws<ArgumentException>(() => new DbLogService(yaml, _storage, "logs_ok", name));
    }

    [Fact]
    public void The_results_table_cannot_be_the_logs_table() =>
        Assert.Throws<ArgumentException>(() => new DbLogService(new DirectoryYamlStorage(_config.Path), _storage, "logs_x", "LOGS_X"));
}
