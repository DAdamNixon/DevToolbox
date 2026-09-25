using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>
/// A4: <c>results</c> is one level only, and a page query against it never falls back to the
/// template's configured sort — that template may not even describe these columns.
/// </summary>
public sealed class DbLogServiceResultsTests : IDisposable
{
    private readonly TempDirectory _config = new("results-config");
    private readonly TempDirectory _db = new("results-svc-db");

    private const string TemplateName = "Basic";

    public DbLogServiceResultsTests()
    {
        File.WriteAllText(Path.Combine(_config.Path, "log_templates_index.yaml"), """
            templates:
              - name: "Basic"
                file: "Basic.yaml"
            """);

        // The template's own sort is descending by Message — the opposite of insertion order —
        // so a test that reads results back ascending-by-insertion proves the fallback was skipped.
        File.WriteAllText(Path.Combine(_config.Path, "Basic.yaml"), """
            name: "Basic"
            extension: ".txt"
            delimiter: "|"
            columns:
              - Message
            sort:
              - column: Message
                direction: desc
            """);
    }

    public void Dispose()
    {
        _config.Dispose();
        _db.Dispose();
    }

    private (DbLogService Service, SqliteLogStorageService Storage) Build()
    {
        var storage = new SqliteLogStorageService(Path.Combine(_db.Path, $"logs.{Guid.NewGuid():N}.db"));
        return (new DbLogService(new DirectoryYamlStorage(_config.Path), storage), storage);
    }

    [Fact]
    public async Task Collapsing_the_results_table_itself_is_refused()
    {
        var (service, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.MaterializeResultsAsync(
                DbLogService.ResultsTableName, TemplateName, sorts: null, criteria: null, split: null));
    }

    [Fact]
    public async Task A_page_query_on_results_ignores_the_templates_configured_sort()
    {
        var (service, storage) = Build();
        await storage.EnsureTableAsync(DbLogService.ResultsTableName, new[] { "Message" });
        await storage.InsertLogLinesAsync(DbLogService.ResultsTableName, new[]
        {
            new Dictionary<string, string> { ["Message"] = "first" },
            new Dictionary<string, string> { ["Message"] = "second" },
        });

        var page = await service.QueryLogPageAsync(
            DbLogService.ResultsTableName, TemplateName, pageNumber: 0, pageSize: 500,
            sortColumns: null, criteria: null);

        // Insertion order (rowid ASC), not the template's "Message desc" — which would read
        // "second" then "first".
        Assert.Equal(new[] { "first", "second" }, page.Select(r => r["Message"]));
    }

    [Fact]
    public async Task A_header_click_still_sorts_a_results_page()
    {
        var (service, storage) = Build();
        await storage.EnsureTableAsync(DbLogService.ResultsTableName, new[] { "Message" });
        await storage.InsertLogLinesAsync(DbLogService.ResultsTableName, new[]
        {
            new Dictionary<string, string> { ["Message"] = "b" },
            new Dictionary<string, string> { ["Message"] = "a" },
        });

        var page = await service.QueryLogPageAsync(
            DbLogService.ResultsTableName, TemplateName, pageNumber: 0, pageSize: 500,
            sortColumns: new() { new SortColumn { Column = "Message", Direction = "asc" } }, criteria: null);

        Assert.Equal(new[] { "a", "b" }, page.Select(r => r["Message"]));
    }
}
