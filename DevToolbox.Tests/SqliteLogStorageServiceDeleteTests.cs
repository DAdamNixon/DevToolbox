using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>The row-leak guard's purge (D4): a skipped file's already-committed rows come back out.</summary>
public sealed class SqliteLogStorageServiceDeleteTests : IDisposable
{
    private readonly TempDirectory _db = new("purge-db");

    public void Dispose() => _db.Dispose();

    private SqliteLogStorageService BuildStorage() => new(Path.Combine(_db.Path, "logs.test.db"));

    [Fact]
    public async Task Deleting_by_file_removes_only_that_files_rows()
    {
        var storage = BuildStorage();
        await storage.EnsureTableAsync("logs", new[] { "Message", "SourcePath" });

        await storage.InsertLogLinesAsync("logs", new[]
        {
            new Dictionary<string, string> { ["Message"] = "a1", ["SourcePath"] = @"C:\logs\A.txt" },
            new Dictionary<string, string> { ["Message"] = "a2", ["SourcePath"] = @"C:\logs\A.txt" },
            new Dictionary<string, string> { ["Message"] = "b1", ["SourcePath"] = @"C:\logs\B.txt" },
        });

        await storage.DeleteRowsForFileAsync("logs", "SourcePath", @"C:\logs\A.txt");

        var (results, total) = await storage.SearchLogsAsync("logs", new DevToolbox.Services.Models.LogQuery());
        Assert.Equal(1, total);
        Assert.All(results, r => Assert.Equal(@"C:\logs\B.txt", r["SourcePath"]));
    }

    [Fact]
    public async Task Deleting_a_file_with_no_rows_is_a_no_op()
    {
        var storage = BuildStorage();
        await storage.EnsureTableAsync("logs", new[] { "Message", "SourcePath" });

        await storage.DeleteRowsForFileAsync("logs", "SourcePath", @"C:\logs\Nothing.txt");

        var (_, total) = await storage.SearchLogsAsync("logs", new DevToolbox.Services.Models.LogQuery());
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task A_read_only_instance_refuses_to_delete()
    {
        var path = Path.Combine(_db.Path, "logs.readonly.db");
        var writable = new SqliteLogStorageService(path);
        await writable.EnsureTableAsync("logs", new[] { "Message" });

        var readOnly = new SqliteLogStorageService(path, readOnly: true);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => readOnly.DeleteRowsForFileAsync("logs", "Message", "x"));
    }
}
