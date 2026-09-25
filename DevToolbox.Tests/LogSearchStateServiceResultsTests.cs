using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using DevToolbox.UI.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevToolbox.Tests;

/// <summary>
/// The Log Viewer's collapse-into-results state machine, against a real
/// <see cref="DbLogService"/> and <see cref="SqliteLogStorageService"/> on a temp file — the first
/// tests <see cref="LogSearchStateService"/> has ever had.
/// <para>
/// Ingestion itself (<c>PrepareLogTableAsync</c>) is exercised only where the test is specifically
/// about it (Load Logs clearing <c>results</c>); everywhere else the <c>logs</c> table is seeded
/// directly and a page query stands in for "a search already ran" — cheaper, and it is the filter
/// and collapse logic under test, not ingestion.
/// </para>
/// </summary>
public sealed class LogSearchStateServiceResultsTests : IDisposable
{
    private readonly TempDirectory _config = new("state-results-config");
    private readonly TempDirectory _db = new("state-results-db");

    public LogSearchStateServiceResultsTests()
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
    }

    public void Dispose()
    {
        _config.Dispose();
        _db.Dispose();
    }

    /// <summary>A writable fake, round-tripping through the real YAML serializer — SaveAsync on
    /// <see cref="DirectoryYamlStorage"/> throws, and saved queries need a real write.</summary>
    private sealed class WritableFakeYamlStorage : IYamlStorageService
    {
        private static readonly ISerializer Serializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        private static readonly IDeserializer Deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public string StorageDirectory => "in-memory";
        public Task SaveAsync<T>(string fileName, T data) { _files[fileName] = Serializer.Serialize(data); return Task.CompletedTask; }
        public Task<T?> LoadAsync<T>(string fileName) => Task.FromResult(_files.TryGetValue(fileName, out var yaml) ? Deserializer.Deserialize<T>(yaml) : default);
        public Task<bool> DeleteAsync(string fileName) => Task.FromResult(_files.Remove(fileName));
        public Task<List<string>> ListFilesAsync() => Task.FromResult(_files.Keys.ToList());
    }

    private (LogSearchStateService State, SqliteLogStorageService Storage) Build()
    {
        var dbPath = Path.Combine(_db.Path, $"logs.{Guid.NewGuid():N}.db");
        var storage = new SqliteLogStorageService(dbPath);
        var yaml = new DirectoryYamlStorage(_config.Path);
        var dbLogService = new DbLogService(yaml, storage);
        var savedQueries = new SavedQueryService(new WritableFakeYamlStorage());
        var state = new LogSearchStateService(dbLogService, yaml, savedQueries);
        return (state, storage);
    }

    private static async Task SeedLogsAsync(SqliteLogStorageService storage, IEnumerable<string> messages)
    {
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", messages.Select(m => new Dictionary<string, string> { ["Message"] = m }));
    }

    /// <summary>Stands in for a completed search without a real ingest: the state already believes
    /// one ran, and a page query against the pre-seeded table fills in the rest.</summary>
    private static async Task RunASearchAsync(LogSearchStateService state)
    {
        state.HasSearched = true;
        state.AvailableTemplates = new() { new LogTemplateIndexEntry { Name = "Basic", File = "Basic.yaml" } };
        state.SelectedTemplateName = "Basic";
        await state.QueryCurrentPageAsync();
    }

    // --- collapse ---

    [Fact]
    public async Task Collapse_moves_the_grid_to_results_and_reports_the_row_count()
    {
        var (state, storage) = Build();
        await SeedLogsAsync(storage, new[] { "alpha", "beta", "alpha two" });
        await RunASearchAsync(state);

        state.Logs.KeywordRows[0].Text = "alpha";
        await state.RunLiveQueryAsync();
        Assert.True(state.CanCollapseToResults);

        await state.CollapseToResultsAsync();

        Assert.NotNull(state.Results);
        Assert.Equal(DbLogService.ResultsTableName, state.Active.TableName);
        Assert.Equal(2, state.TotalRecords);
        Assert.Equal(2, state.Results!.CollapsedRowCount);
    }

    [Fact]
    public async Task Collapse_is_refused_with_no_filter_content()
    {
        var (state, storage) = Build();
        await SeedLogsAsync(storage, new[] { "alpha" });
        await RunASearchAsync(state);

        Assert.False(state.CanCollapseToResults);
        await state.CollapseToResultsAsync();

        Assert.Null(state.Results);
        Assert.False(await storage.TableExistsAsync("results"));
    }

    [Fact]
    public async Task Collapse_with_zero_actual_matches_refuses_and_leaves_no_table_behind()
    {
        var (state, storage) = Build();
        await SeedLogsAsync(storage, new[] { "alpha" });
        await RunASearchAsync(state);

        // The guard's own row count (>0) is still the *last* query's; the box has since changed
        // to something that matches nothing — the debounce race C2 explicitly accepts.
        state.Logs.KeywordRows[0].Text = "no-such-term";
        Assert.True(state.CanCollapseToResults);

        await state.CollapseToResultsAsync();

        Assert.Null(state.Results);
        Assert.Equal("Nothing matched — nothing was collapsed.", state.ErrorMessage);
        Assert.False(await storage.TableExistsAsync("results"));
    }

    [Fact]
    public async Task Collapse_is_not_offered_once_results_already_exists()
    {
        var (state, storage) = Build();
        await SeedLogsAsync(storage, new[] { "alpha", "alpha two" });
        await RunASearchAsync(state);
        state.Logs.KeywordRows[0].Text = "alpha";
        await state.RunLiveQueryAsync();
        await state.CollapseToResultsAsync();

        Assert.False(state.CanCollapseToResults);
    }

    // --- discard ---

    [Fact]
    public async Task Discard_drops_results_and_restores_the_logs_view_exactly()
    {
        var (state, storage) = Build();
        await SeedLogsAsync(storage, Enumerable.Range(1, 3).Select(i => $"row{i}"));
        await RunASearchAsync(state);
        state.Logs.KeywordRows[0].Text = "row";
        await state.RunLiveQueryAsync();
        var pageBefore = state.Logs.CurrentPage;

        await state.CollapseToResultsAsync();
        Assert.NotNull(state.Results);

        await state.DiscardResultsAsync();

        Assert.Null(state.Results);
        Assert.True(await storage.TableExistsAsync("logs"));
        Assert.False(await storage.TableExistsAsync("results"));
        Assert.Equal(3, state.TotalRecords);
        Assert.Equal(pageBefore, state.Logs.CurrentPage);
        Assert.Equal("row", state.Logs.KeywordRows[0].Text);
    }

    // --- Load Logs ---

    [Fact]
    public async Task Load_Logs_with_results_present_clears_it_before_the_prepare()
    {
        var (state, storage) = Build();
        await SeedLogsAsync(storage, new[] { "alpha" });
        await RunASearchAsync(state);
        state.Logs.KeywordRows[0].Text = "alpha";
        await state.RunLiveQueryAsync();
        await state.CollapseToResultsAsync();
        Assert.NotNull(state.Results);

        using var emptyLogsDir = new TempDirectory("empty-logs");
        state.SelectedLocations = new() { new LogLocation { Name = "Local", Path = emptyLogsDir.Path } };
        state.StartDate = DateTime.Today.AddDays(-1);
        state.EndDate = DateTime.Today;
        state.LogFile = "NothingMatches";

        await state.SearchAsync();

        Assert.Null(state.Results);
        Assert.False(await storage.TableExistsAsync("results"));
    }

    // --- template change ---

    [Fact]
    public async Task A_template_change_leaves_results_columns_alone()
    {
        var (state, storage) = Build();
        await SeedLogsAsync(storage, new[] { "alpha", "alpha 2" });
        await RunASearchAsync(state);
        state.Logs.KeywordRows[0].Text = "alpha";
        await state.RunLiveQueryAsync();
        await state.CollapseToResultsAsync();

        var before = new List<string>(state.Results!.TableColumns);

        await state.OnTemplateChangedAsync();

        Assert.Equal(before, state.Results!.TableColumns);
    }

    // --- availability (C6) ---

    [Fact]
    public async Task A_group_by_collapse_hides_split_and_open_in_editor()
    {
        var (state, storage) = Build();
        await storage.EnsureTableAsync("logs", new[] { "Location", "SourceFile", "Message" });
        await storage.InsertLogLinesAsync("logs", new[]
        {
            new Dictionary<string, string> { ["Location"] = "Web01", ["SourceFile"] = "a.log", ["Message"] = "x" },
            new Dictionary<string, string> { ["Location"] = "Web04", ["SourceFile"] = "b.log", ["Message"] = "y" },
        });
        await RunASearchAsync(state);
        state.Logs.AdvancedRawMode = true;
        state.Logs.AdvancedExpression = "SELECT Message, COUNT(*) AS n FROM logs GROUP BY Message";
        await state.RunLiveQueryAsync();

        await state.CollapseToResultsAsync();

        Assert.NotNull(state.Results);
        Assert.Equal(new[] { LogSplitMode.None }, state.Results!.AllowedSplitModes);
        Assert.False(state.Results!.CanOpenSource);
    }
}
