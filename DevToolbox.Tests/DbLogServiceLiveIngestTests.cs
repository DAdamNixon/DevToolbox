using System.Threading;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>
/// The ingest behaviour a live view depends on: the table is signalled the moment it exists, a
/// cancelled load keeps what it committed and says which files that covers, a skipped file's rows
/// come back out, overflow columns appear when first needed, and the prepare never returns — or
/// hangs — with its writer still going.
/// </summary>
[Collection(LoadLockCollection.Name)]
public sealed class DbLogServiceLiveIngestTests : IDisposable
{
    private readonly TempDirectory _config = new("live-ingest-config");
    private readonly TempDirectory _logs = new("live-ingest-logs");
    private readonly TempDirectory _db = new("live-ingest-db");

    private const string TemplateName = "Basic";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public DbLogServiceLiveIngestTests()
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
        _logs.Dispose();
        _db.Dispose();
    }

    private string NewDbPath() => Path.Combine(_db.Path, $"logs.{Guid.NewGuid():N}.db");

    private (DbLogService Service, SpyStorage Storage) Build()
    {
        var storage = new SpyStorage(new SqliteLogStorageService(NewDbPath()));
        return (new DbLogService(new DirectoryYamlStorage(_config.Path), storage), storage);
    }

    private List<LogLocation> Locations() => new() { new LogLocation { Name = "Local", Path = _logs.Path } };

    private string WriteLogFile(string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(_logs.Path, $"{name}.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static IEnumerable<string> Lines(int count, string prefix = "line") => Enumerable.Range(1, count).Select(i => $"{prefix} {i}");

    private static byte[] Utf8(IEnumerable<string> lines) => System.Text.Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    // --- the table signal ---

    [Fact]
    public async Task The_control_names_the_table_once_it_exists_and_says_the_old_one_is_gone()
    {
        var path = WriteLogFile("Held", new[] { "first" });
        var stream = new GatedStream(Utf8(Lines(10)));
        var (service, _) = Build();
        service.FileOpener = p => p == path ? stream : File.OpenRead(p);
        var control = new LogIngestControl();

        Assert.Null(control.ReadyTable);
        Assert.False(control.TableDropped);

        try
        {
            var prepare = service.PrepareLogTableAsync("Held", Locations(), DateTime.Today, DateTime.Today, TemplateName, control: control);
            await stream.EnteredBlockingRead.Task.WaitAsync(Patience);

            // Mid-read: the new table exists and is being filled.
            Assert.Equal(DbLogService.DefaultTableName, control.ReadyTable);
            Assert.True(control.TableDropped);

            stream.Release();
            await prepare.WaitAsync(Patience);
        }
        finally
        {
            stream.Release();
        }
    }

    [Fact]
    public async Task A_cancel_before_the_drop_leaves_the_previous_table_standing()
    {
        // Something else holds the process-wide load lock, so this prepare is still waiting to start
        // when the cancel lands — before it could have touched anything.
        var otherPath = WriteLogFile("Other", new[] { "x" });
        var otherStream = new GatedStream(Utf8(Lines(1)));
        var other = new DbLogService(new DirectoryYamlStorage(_config.Path), new SqliteLogStorageService(NewDbPath()));
        other.FileOpener = p => p == otherPath ? otherStream : File.OpenRead(p);

        var (service, storage) = Build();
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", new[] { new Dictionary<string, string> { ["Message"] = "previous" } });

        try
        {
            var holding = other.PrepareLogTableAsync("Other", Locations(), DateTime.Today, DateTime.Today, TemplateName);
            await otherStream.EnteredBlockingRead.Task.WaitAsync(Patience);

            using var cts = new CancellationTokenSource();
            var control = new LogIngestControl();
            var prepare = service.PrepareLogTableAsync("Other", Locations(), DateTime.Today, DateTime.Today, TemplateName, control: control, cancellationToken: cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare.WaitAsync(Patience));
            Assert.False(control.TableDropped);
            Assert.Null(control.ReadyTable);
            Assert.Equal(1, await storage.CountLogsAsync("logs", new LogQuery()));

            otherStream.Release();
            await holding.WaitAsync(Patience);
        }
        finally
        {
            otherStream.Release();
        }
    }

    // --- cancel keeps what it read ---

    [Fact]
    public async Task A_cancelled_load_keeps_its_committed_rows_and_says_the_file_was_cut_short()
    {
        var path = WriteLogFile("Long", new[] { "x" });
        var stream = new GatedStream(Utf8(Lines(1500)));
        var (service, storage) = Build();
        service.FileOpener = p => p == path ? stream : File.OpenRead(p);
        var progress = new LatestProgress();
        var control = new LogIngestControl();
        using var cts = new CancellationTokenSource();

        try
        {
            var prepare = service.PrepareLogTableAsync("Long", Locations(), DateTime.Today, DateTime.Today, TemplateName, progress, control, cts.Token);
            await stream.EnteredBlockingRead.Task.WaitAsync(Patience);
            await WaitUntil(() => progress.Latest?.RowsIngested >= 1000, "the first batch to commit");

            // What CancelSearch does, token first.
            cts.Cancel();
            control.SkipAll();

            var cancelled = await Assert.ThrowsAsync<LogIngestCancelledException>(() => prepare.WaitAsync(Patience));
            Assert.True(cancelled.TableReplaced);

            var file = Assert.Single(cancelled.Result.NotIngested);
            Assert.Equal(NotIngestedFile.CancelledPartwayReason, file.Reason);
            Assert.Equal(1000, file.RowsInTable);

            Assert.Equal(1000, await storage.CountLogsAsync("logs", new LogQuery()));
            Assert.Equal(0, storage.RangeDeletes);
        }
        finally
        {
            stream.Release();
        }
    }

    // --- D4: a skipped file contributes nothing ---

    [Fact]
    public async Task A_file_skipped_after_committing_rows_contributes_none_of_them()
    {
        var healthy = WriteLogFile("Healthy", Lines(20, "ok"));
        var stuck = WriteLogFile("Stuck", new[] { "x" });
        var stream = new GatedStream(Utf8(Lines(1500, "stuck")));
        var (service, storage) = Build();
        service.FileOpener = p => p == stuck ? stream : File.OpenRead(p);
        var progress = new LatestProgress();
        var control = new LogIngestControl();

        try
        {
            var prepare = service.PrepareLogTableAsync("", Locations(), DateTime.Today, DateTime.Today, TemplateName, progress, control);
            await stream.EnteredBlockingRead.Task.WaitAsync(Patience);
            await WaitUntil(() => progress.Latest?.RowsIngested >= 1000, "the stuck file's first batch to commit");

            control.Skip(stuck);
            var result = await prepare.WaitAsync(Patience);

            Assert.False(result.IsComplete);
            Assert.Equal(0, await storage.CountLogsAsync("logs", new LogQuery { Filters = new() { ["SourceFile"] = "Stuck.txt" } }));
            Assert.Equal(20, await storage.CountLogsAsync("logs", new LogQuery()));
            Assert.Equal(1000, progress.Latest!.RowsPurged);
            Assert.Equal(20, progress.Latest.RowsInTable);
        }
        finally
        {
            stream.Release();
        }
    }

    [Fact]
    public async Task A_skip_whose_purge_a_cancel_interrupts_is_still_purged_before_the_load_returns()
    {
        var stuck = WriteLogFile("Stuck", new[] { "x" });
        var stream = new GatedStream(Utf8(Lines(1500, "stuck")));
        var (service, storage) = Build();
        service.FileOpener = p => p == stuck ? stream : File.OpenRead(p);
        var progress = new LatestProgress();
        var control = new LogIngestControl();
        using var cts = new CancellationTokenSource();

        // The writer's prompt purge is held until the Cancel has landed, so it then runs on a
        // cancelled token and fails — the rows can only leave through the sweep at the end.
        var purgeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var purgeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.OnRangeDelete = call =>
        {
            if (call != 1) return Task.CompletedTask;
            purgeEntered.TrySetResult();
            return purgeGate.Task;
        };

        try
        {
            var prepare = service.PrepareLogTableAsync("Stuck", Locations(), DateTime.Today, DateTime.Today, TemplateName, progress, control, cts.Token);
            await stream.EnteredBlockingRead.Task.WaitAsync(Patience);
            await WaitUntil(() => progress.Latest?.RowsIngested >= 1000, "the stuck file's first batch to commit");

            control.Skip(stuck);
            await purgeEntered.Task.WaitAsync(Patience);

            cts.Cancel();
            control.SkipAll();
            purgeGate.SetResult();

            await Assert.ThrowsAsync<LogIngestCancelledException>(() => prepare.WaitAsync(Patience));
            Assert.Equal(0, await storage.CountLogsAsync("logs", new LogQuery()));
            Assert.Equal(2, storage.RangeDeletes);
        }
        finally
        {
            stream.Release();
            purgeGate.TrySetResult();
        }
    }

    [Fact]
    public async Task Skipping_a_file_that_committed_nothing_costs_no_delete()
    {
        var stuck = WriteLogFile("Stuck", new[] { "x" });
        var stream = new GatedStream(Utf8(Lines(3)));
        var (service, storage) = Build();
        service.FileOpener = p => p == stuck ? stream : File.OpenRead(p);
        var control = new LogIngestControl();

        try
        {
            var prepare = service.PrepareLogTableAsync("Stuck", Locations(), DateTime.Today, DateTime.Today, TemplateName, control: control);
            await stream.EnteredBlockingRead.Task.WaitAsync(Patience);

            control.Skip(stuck);
            await prepare.WaitAsync(Patience);

            Assert.Equal(0, storage.RangeDeletes);
            Assert.Equal(0, storage.PathDeletes);
        }
        finally
        {
            stream.Release();
        }
    }

    [Fact]
    public async Task A_skip_that_arrives_after_a_file_finished_is_too_late_to_take_its_rows()
    {
        // The drawer's list trails the ingest: Skip can be clicked on a file that has just finished.
        var finished = WriteLogFile("A", Lines(20, "a"));
        var stuck = WriteLogFile("B", new[] { "x" });
        var stream = new GatedStream(Utf8(Lines(3, "b")));
        var (service, storage) = Build();
        service.FileOpener = p => p == stuck ? stream : File.OpenRead(p);
        var progress = new LatestProgress();
        var control = new LogIngestControl();

        try
        {
            var prepare = service.PrepareLogTableAsync("", Locations(), DateTime.Today, DateTime.Today, TemplateName, progress, control);
            await stream.EnteredBlockingRead.Task.WaitAsync(Patience);
            await WaitUntil(() => progress.Latest is { FilesDone: >= 1, RowsIngested: >= 20 }, "A to finish");

            control.Skip(finished);
            stream.Release();
            var result = await prepare.WaitAsync(Patience);

            Assert.True(result.IsComplete);
            Assert.Equal(20, await storage.CountLogsAsync("logs", new LogQuery { Filters = new() { ["SourceFile"] = "A.txt" } }));
            Assert.Equal(0, storage.RangeDeletes);
        }
        finally
        {
            stream.Release();
        }
    }

    [Fact]
    public async Task A_file_whose_last_batch_a_cancel_kept_from_the_table_is_not_reported_complete()
    {
        // One batch: the parser hands it over and has read to the end — Done — while the writer is
        // still inserting it. The Cancel then rolls that insert back.
        WriteLogFile("Small", Lines(10));
        var (service, storage) = Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.OnInsert = call =>
        {
            if (call != 1) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        var progress = new LatestProgress();
        var control = new LogIngestControl();
        using var cts = new CancellationTokenSource();

        var prepare = service.PrepareLogTableAsync("Small", Locations(), DateTime.Today, DateTime.Today, TemplateName, progress, control, cts.Token);
        await entered.Task.WaitAsync(Patience);
        await WaitUntil(() => progress.Latest?.FilesDone >= 1, "the parser to finish the file");

        cts.Cancel();
        control.SkipAll();
        release.SetResult();

        var cancelled = await Assert.ThrowsAsync<LogIngestCancelledException>(() => prepare.WaitAsync(Patience));
        var file = Assert.Single(cancelled.Result.NotIngested);
        Assert.Equal(NotIngestedFile.CancelledBeforeReadingReason, file.Reason);
        Assert.Equal(0, file.RowsInTable);
        Assert.Equal(0, await storage.CountLogsAsync("logs", new LogQuery()));
    }

    // --- column order ---

    [Fact]
    public async Task A_results_table_collapsed_from_sql_keeps_the_order_its_select_chose()
    {
        WriteLogFile("App", Lines(5));
        var (service, _) = Build();
        await service.PrepareLogTableAsync("App", Locations(), DateTime.Today, DateTime.Today, TemplateName).WaitAsync(Patience);

        var sql = new LogSearchCriteria { UseAdvanced = true, AdvancedExpression = "SELECT Sequence, SourceFile, Message FROM logs" };
        await service.MaterializeResultsAsync("logs", TemplateName, null, sql, null);

        var rows = await service.QueryLogPageAsync(DbLogService.ResultsTableName, TemplateName, 0, 10, null, null);
        Assert.Equal(new[] { "Sequence", "SourceFile", "Message" }, rows[0].Keys);
    }

    [Fact]
    public async Task A_template_column_named_after_the_rowid_is_refused_before_anything_is_dropped()
    {
        File.WriteAllText(Path.Combine(_config.Path, "Basic.yaml"), """
            name: "Basic"
            extension: ".txt"
            delimiter: "|"
            columns:
              - RowId
              - Message
            """);
        WriteLogFile("App", Lines(5));
        var (service, storage) = Build();
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", new[] { new Dictionary<string, string> { ["Message"] = "previous" } });
        var control = new LogIngestControl();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareLogTableAsync("App", Locations(), DateTime.Today, DateTime.Today, TemplateName, control: control).WaitAsync(Patience));

        Assert.Contains("RowId", refused.Message);
        Assert.False(control.TableDropped);
        Assert.Equal(1, await storage.CountLogsAsync("logs", new LogQuery()));
    }

    // --- overflow columns on demand ---

    [Fact]
    public async Task A_line_past_the_first_thousand_with_more_fields_gets_its_columns()
    {
        // The old column scan read 1000 lines; the extra fields here are on line 1001, so the insert
        // named columns the table did not have and the writer failed.
        var lines = Lines(1000).Append("late|extra one|extra two");
        WriteLogFile("Wide", lines);
        var (service, storage) = Build();

        var result = await service.PrepareLogTableAsync("Wide", Locations(), DateTime.Today, DateTime.Today, TemplateName)
            .WaitAsync(Patience);

        Assert.True(result.IsComplete);
        var (rows, total) = await storage.SearchLogsAsync("logs", new LogQuery { InsertionOrder = true, Page = 1000, PageSize = 1 });
        Assert.Equal(1001, total);
        var last = Assert.Single(rows);
        Assert.Equal("extra one", last["Message1"]);
        Assert.Equal("extra two", last["Message2"]);

        // Overflow before provenance, as it always read.
        Assert.Equal(new[] { "Message", "Message1", "Message2", "Location", "SourceFile", "Sequence", "SourcePath" }, last.Keys);
    }

    // --- the writer ---

    [Fact]
    public async Task A_writer_failure_ends_the_load_with_that_failure_instead_of_hanging_it()
    {
        // Twelve batches: more than the channel holds, so parsers would block on it forever once the
        // writer stopped draining.
        WriteLogFile("Big", Lines(12_000));
        var (service, storage) = Build();
        storage.OnInsert = call => call == 2 ? throw new IOException("disk full") : Task.CompletedTask;

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            service.PrepareLogTableAsync("Big", Locations(), DateTime.Today, DateTime.Today, TemplateName).WaitAsync(Patience));

        Assert.Equal("disk full", failure.Message);
    }

    [Fact]
    public async Task A_cancelled_load_does_not_return_until_its_writer_has_stopped()
    {
        WriteLogFile("Big", Lines(3000));
        var (service, storage) = Build();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.OnInsert = call =>
        {
            if (call != 1) return Task.CompletedTask;
            entered.TrySetResult();
            return gate.Task;
        };

        using var cts = new CancellationTokenSource();
        var control = new LogIngestControl();
        var prepare = service.PrepareLogTableAsync("Big", Locations(), DateTime.Today, DateTime.Today, TemplateName, control: control, cancellationToken: cts.Token);
        await entered.Task.WaitAsync(Patience);

        cts.Cancel();
        control.SkipAll();

        // The writer is still inside its insert: the prepare must not be over yet.
        await Task.Delay(300);
        Assert.False(prepare.IsCompleted);

        gate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare.WaitAsync(Patience));

        var callsAtReturn = storage.InsertCalls;
        await Task.Delay(200);
        Assert.Equal(callsAtReturn, storage.InsertCalls);
    }

    // --- counting ---

    [Fact]
    public async Task Counting_entries_never_reads_the_rows()
    {
        var (service, storage) = Build();
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", Enumerable.Range(0, 5).Select(i => new Dictionary<string, string> { ["Message"] = $"m{i}" }));

        Assert.Equal(5, await service.CountLogEntriesAsync("logs", criteria: null));
        Assert.Equal(0, storage.Searches);
    }

    // --- helpers ---

    /// <summary>Keeps the newest snapshot, on the reporting thread — no synchronization context to post to.</summary>
    private sealed class LatestProgress : IProgress<LogIngestProgress>
    {
        private LogIngestProgress? _latest;
        public LogIngestProgress? Latest => Volatile.Read(ref _latest);
        public void Report(LogIngestProgress value) => Volatile.Write(ref _latest, value);
    }

    /// <summary>Real SQLite underneath, with the calls counted and inserts open to delay or fail.</summary>
    private sealed class SpyStorage : ILogStorageService
    {
        private readonly SqliteLogStorageService _inner;
        private int _insertCalls;
        private int _rangeDeletes;
        private int _pathDeletes;
        private int _searches;

        public SpyStorage(SqliteLogStorageService inner) => _inner = inner;

        /// <summary>Runs before each insert with its 1-based call number; can delay it or throw.</summary>
        public Func<int, Task>? OnInsert { get; set; }

        public int InsertCalls => Volatile.Read(ref _insertCalls);
        public int RangeDeletes => Volatile.Read(ref _rangeDeletes);
        public int PathDeletes => Volatile.Read(ref _pathDeletes);
        public int Searches => Volatile.Read(ref _searches);

        public async Task<LogRowRange> InsertLogLinesAsync(string tableName, IEnumerable<Dictionary<string, string>> lines, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _insertCalls);
            if (OnInsert is { } hook) await hook(call);
            return await _inner.InsertLogLinesAsync(tableName, lines, cancellationToken);
        }

        /// <summary>Runs before each range delete with its 1-based call number; can delay it.</summary>
        public Func<int, Task>? OnRangeDelete { get; set; }

        public async Task<int> DeleteRowRangesAsync(string tableName, IReadOnlyList<LogRowRange> ranges, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _rangeDeletes);
            if (OnRangeDelete is { } hook) await hook(call);
            return await _inner.DeleteRowRangesAsync(tableName, ranges, cancellationToken);
        }

        public Task DeleteRowsForFileAsync(string tableName, string column, string value, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _pathDeletes);
            return _inner.DeleteRowsForFileAsync(tableName, column, value, cancellationToken);
        }

        public Task<(IEnumerable<Dictionary<string, string>> Results, int TotalCount)> SearchLogsAsync(string tableName, LogQuery query, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _searches);
            return _inner.SearchLogsAsync(tableName, query, cancellationToken);
        }

        public Task EnsureTableAsync(string tableName, IEnumerable<string> columns) => _inner.EnsureTableAsync(tableName, columns);
        public Task AddColumnsAsync(string tableName, IEnumerable<string> columns) => _inner.AddColumnsAsync(tableName, columns);
        public Task<int> CountLogsAsync(string tableName, LogQuery query, CancellationToken cancellationToken = default) => _inner.CountLogsAsync(tableName, query, cancellationToken);
        public Task<LogLiveSlice> ReadLiveSliceAsync(string tableName, LogLiveRequest request, CancellationToken cancellationToken = default) => _inner.ReadLiveSliceAsync(tableName, request, cancellationToken);
        public Task<List<LogSplitGroup>> GetGroupCountsAsync(string tableName, string column, LogQuery query, CancellationToken cancellationToken = default) => _inner.GetGroupCountsAsync(tableName, column, query, cancellationToken);
        public Task<bool> TableExistsAsync(string tableName) => _inner.TableExistsAsync(tableName);
        public Task DropTableAsync(string tableName) => _inner.DropTableAsync(tableName);
        public Task<(int Rows, List<string> Columns)> CreateTableFromQueryAsync(string source, string target, LogQuery query, CancellationToken cancellationToken = default) => _inner.CreateTableFromQueryAsync(source, target, query, cancellationToken);
    }
}
