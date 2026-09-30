using System.Threading;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using DevToolbox.UI.Services;

namespace DevToolbox.Tests;

/// <summary>
/// The Log Viewer showing rows while a load is still reading them — against a real
/// <see cref="DbLogService"/> and SQLite, with the file held open mid-read by a stream the test
/// releases a chunk at a time, and every scenario run on a single-threaded context like the
/// renderer's (<see cref="SingleThreadContext"/>).
/// </summary>
[Collection(LoadLockCollection.Name)]
public sealed class LogSearchStateServiceLiveTests : IDisposable
{
    private readonly TempDirectory _config = new("state-live-config");
    private readonly TempDirectory _logs = new("state-live-logs");
    private readonly TempDirectory _db = new("state-live-db");
    private readonly SingleThreadContext _ui = new();

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public LogSearchStateServiceLiveTests()
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
        _ui.Dispose();
        _config.Dispose();
        _logs.Dispose();
        _db.Dispose();
    }

    private sealed record Rig(LogSearchStateService State, DbLogService Service, SqliteLogStorageService Storage);

    /// <summary>
    /// A state service over its own database, pointed at the logs folder, with the live pacing
    /// brought down to nothing and something listening — the page, as far as the service can tell,
    /// and a live refresh only runs for a page that is showing.
    /// </summary>
    private Rig Build(Func<string, Stream?>? open = null)
    {
        var storage = new SqliteLogStorageService(Path.Combine(_db.Path, $"logs.{Guid.NewGuid():N}.db"));
        var yaml = new DirectoryYamlStorage(_config.Path);
        var service = new DbLogService(yaml, storage);
        if (open is not null) service.FileOpener = path => open(path) ?? File.OpenRead(path);

        var state = new LogSearchStateService(service, yaml, new SavedQueryService(yaml))
        {
            AvailableTemplates = new() { new LogTemplateIndexEntry { Name = "Basic", File = "Basic.yaml" } },
            SelectedTemplateName = "Basic",
            SelectedLocations = new() { new LogLocation { Name = "Local", Path = _logs.Path } },
            StartDate = DateTime.Today,
            EndDate = DateTime.Today,
            LivePollInterval = TimeSpan.FromMilliseconds(5),
            LiveStartDelay = TimeSpan.Zero,
            LiveMinInterval = TimeSpan.Zero,
            LiveCostFactor = 0
        };
        state.OnChanged += () => { };
        return new Rig(state, service, storage);
    }

    /// <summary><see cref="Build"/>, with the page query open to being held.</summary>
    private (LogSearchStateService State, GatedLogFileService Gated, SqliteLogStorageService Storage) BuildGated(Func<string, Stream?>? open = null)
    {
        var rig = Build(open);
        var gated = new GatedLogFileService(rig.Service);
        var yaml = new DirectoryYamlStorage(_config.Path);
        var state = new LogSearchStateService(gated, yaml, new SavedQueryService(yaml))
        {
            AvailableTemplates = rig.State.AvailableTemplates,
            SelectedTemplateName = "Basic",
            SelectedLocations = rig.State.SelectedLocations,
            StartDate = DateTime.Today,
            EndDate = DateTime.Today,
            LivePollInterval = TimeSpan.FromMilliseconds(5),
            LiveStartDelay = TimeSpan.Zero,
            LiveMinInterval = TimeSpan.Zero,
            LiveCostFactor = 0
        };
        state.OnChanged += () => { };
        return (state, gated, rig.Storage);
    }

    private string WriteLogFile(string name, IEnumerable<string>? lines = null)
    {
        var path = Path.Combine(_logs.Path, $"{name}.txt");
        File.WriteAllLines(path, lines ?? new[] { "placeholder" });
        return path;
    }

    /// <summary>Every fourth line an error, the rest fine — so a filter has something to find, spread through the file.</summary>
    private static IEnumerable<string> Lines(int from, int count) =>
        Enumerable.Range(from, count).Select(i => $"{(i % 4 == 0 ? "error" : "ok")} {i}");

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

    // --- rows appear while the load runs ---

    [Fact]
    public async Task Rows_show_up_while_the_file_is_still_being_read_and_the_load_finishes_as_before()
    {
        // 2,500 lines, then the read blocks: two batches committed, 500 waiting in the parser.
        var path = WriteLogFile("App");
        var stream = new SteppedStream(Utf8(Lines(1, 2500)), Utf8(Lines(2501, 700)));
        var rig = Build(p => p == path ? stream : null);
        var state = rig.State;
        rig.State.LogFile = "App";

        try
        {
            await _ui.RunAsync(async () =>
            {
                var search = state.SearchAsync();

                await WaitUntil(() => state.IsLive && state.TotalRecords == 2000, "the first 2,000 rows on screen");
                Assert.True(state.IsIngesting);
                Assert.False(search.IsCompleted);
                Assert.Equal(500, state.FilteredLogLines.Count);
                Assert.Equal("ok 1", state.FilteredLogLines[0]["Message"]);   // the order they were read in
                Assert.True(state.SourceCollapsed);

                stream.Advance();
                await WaitUntil(() => state.TotalRecords == 3000, "the third batch to show");
                Assert.True(state.IsIngesting);

                stream.Advance(); // end of file
                await search.WaitAsync(Patience);

                Assert.False(state.IsIngesting);
                Assert.False(state.IsLive);
                Assert.Equal(3200, state.TotalRecords);
                Assert.NotNull(state.LastLoad);
                Assert.False(state.LastLoad!.Cancelled);
                Assert.Equal(3200, state.LastLoad.Rows);
                Assert.True(state.LastPrepareResult!.IsComplete);
            });
        }
        finally
        {
            stream.End();
        }
    }

    [Fact]
    public async Task A_filter_typed_mid_load_runs_on_the_rows_so_far_keeps_counting_and_does_not_stop_the_load()
    {
        var path = WriteLogFile("App");
        var stream = new SteppedStream(Utf8(Lines(1, 2500)), Utf8(Lines(2501, 700)));
        var rig = Build(p => p == path ? stream : null);
        var state = rig.State;
        state.LogFile = "App";

        try
        {
            await _ui.RunAsync(async () =>
            {
                var search = state.SearchAsync();
                await WaitUntil(() => state.IsLive && state.TotalRecords == 2000, "the first rows");

                state.Logs.KeywordRows[0].Text = "error";
                await state.RunLiveQueryAsync();
                Assert.Equal(500, state.TotalRecords);            // every fourth of 2,000
                Assert.True(state.IsIngesting);

                // Rows 2,001-3,000 arrive; a refresh counts only them and adds 250 more matches.
                stream.Advance();
                await WaitUntil(() => state.TotalRecords == 750, "the new matches to be counted");

                stream.Advance();
                await search.WaitAsync(Patience);
                Assert.Equal(800, state.TotalRecords);             // every fourth of 3,200
                Assert.All(state.FilteredLogLines, r => Assert.StartsWith("error", r["Message"]));
            });
        }
        finally
        {
            stream.End();
        }
    }

    [Fact]
    public async Task A_refresh_adds_to_a_page_that_is_not_full_without_touching_its_rows_or_the_page_box()
    {
        var path = WriteLogFile("App");
        var stream = new SteppedStream(Utf8(Lines(1, 2500)), Utf8(Lines(2501, 700)));
        var rig = Build(p => p == path ? stream : null);
        var state = rig.State;
        state.LogFile = "App";
        state.PageSize = 3000;

        try
        {
            await _ui.RunAsync(async () =>
            {
                var search = state.SearchAsync();
                await WaitUntil(() => state.IsLive && state.FilteredLogLines.Count == 2000, "a part-filled page");

                var firstRow = state.FilteredLogLines[0];
                state.PageInput = 7; // someone mid-way through typing a page number

                stream.Advance();
                await WaitUntil(() => state.FilteredLogLines.Count == 3000, "the page to fill up");

                Assert.Same(firstRow, state.FilteredLogLines[0]);
                Assert.Equal(7, state.PageInput);

                stream.Advance();
                await search.WaitAsync(Patience);
            });
        }
        finally
        {
            stream.End();
        }
    }

    [Fact]
    public async Task A_sort_mid_load_sorts_what_is_there_and_later_rows_only_move_the_counts()
    {
        var path = WriteLogFile("App");
        var stream = new SteppedStream(Utf8(Lines(1, 2500)), Utf8(Lines(2501, 700)));
        var rig = Build(p => p == path ? stream : null);
        var state = rig.State;
        state.LogFile = "App";

        try
        {
            await _ui.RunAsync(async () =>
            {
                var search = state.SearchAsync();
                await WaitUntil(() => state.IsLive && state.TotalRecords == 2000, "the first rows");

                await state.SortByColumnAsync("Message", append: false);
                await state.SortByColumnAsync("Message", append: false); // descending
                Assert.StartsWith("ok ", state.FilteredLogLines[0]["Message"]);
                Assert.True(state.IsIngesting);

                var sortedPage = state.FilteredLogLines;
                stream.Advance();
                await WaitUntil(() => state.TotalRecords == 3000, "the count to move");
                Assert.Same(sortedPage, state.FilteredLogLines);

                stream.Advance();
                await search.WaitAsync(Patience);
                Assert.Equal(3200, state.TotalRecords);
            });
        }
        finally
        {
            stream.End();
        }
    }

    // --- cancel ---

    [Fact]
    public async Task Cancel_mid_load_keeps_the_rows_says_which_file_was_cut_short_and_unfolds_the_source()
    {
        var path = WriteLogFile("App");
        var stream = new SteppedStream(Utf8(Lines(1, 2500)));
        var rig = Build(p => p == path ? stream : null);
        var state = rig.State;
        state.LogFile = "App";

        try
        {
            await _ui.RunAsync(async () =>
            {
                var search = state.SearchAsync();
                await WaitUntil(() => state.IsLive && state.TotalRecords == 2000, "the first rows");
                Assert.True(state.SourceCollapsed);

                state.CancelSearch();
                await search.WaitAsync(Patience);

                Assert.False(state.IsIngesting);
                Assert.Equal(2000, state.TotalRecords);
                Assert.Equal(500, state.FilteredLogLines.Count);
                Assert.True(state.LastLoad!.Cancelled);
                Assert.True(state.LastLoad.TableReplaced);

                var file = Assert.Single(state.LastPrepareResult!.NotIngested);
                Assert.Equal(NotIngestedFile.CancelledPartwayReason, file.Reason);
                Assert.Equal(2000, file.RowsInTable);

                // Cancelled: the user is about to change what they asked for, so the form comes back.
                Assert.False(state.SourceCollapsed);
                Assert.Equal("", state.ErrorMessage);
            });
        }
        finally
        {
            stream.End();
        }
    }

    [Fact]
    public async Task Cancel_before_the_load_touched_the_table_leaves_the_rows_on_screen_as_they_were()
    {
        // Another load holds the process-wide lock, so this one is cancelled while still queued for it.
        var otherPath = WriteLogFile("Other");
        var otherStream = new GatedStream(Utf8(Lines(1, 3)));
        var other = new DbLogService(new DirectoryYamlStorage(_config.Path),
            new SqliteLogStorageService(Path.Combine(_db.Path, $"other.{Guid.NewGuid():N}.db")));
        other.FileOpener = p => p == otherPath ? otherStream : File.OpenRead(p);

        var rig = Build();
        var state = rig.State;
        await rig.Storage.EnsureTableAsync("logs", new[] { "Message" });
        await rig.Storage.InsertLogLinesAsync("logs", new[] { "alpha", "beta" }.Select(m => new Dictionary<string, string> { ["Message"] = m }));

        try
        {
            var holding = other.PrepareLogTableAsync("Other", state.SelectedLocations, DateTime.Today, DateTime.Today, "Basic");
            await otherStream.EnteredBlockingRead.Task.WaitAsync(Patience);

            await _ui.RunAsync(async () =>
            {
                state.HasSearched = true;
                await state.QueryCurrentPageAsync();
                var before = state.FilteredLogLines;
                Assert.Equal(2, state.TotalRecords);

                state.LogFile = "Other";
                var search = state.SearchAsync();
                await WaitUntil(() => state.IsIngesting, "the load to start");
                state.CancelSearch();
                await search.WaitAsync(Patience);

                Assert.True(state.LastLoad!.Cancelled);
                Assert.False(state.LastLoad.TableReplaced);
                Assert.Same(before, state.FilteredLogLines);
                Assert.Equal(2, state.TotalRecords);
                Assert.True(state.HasSearched);
            });

            otherStream.Release();
            await holding.WaitAsync(Patience);
        }
        finally
        {
            otherStream.Release();
        }
    }

    // --- skip ---

    [Fact]
    public async Task Skipping_a_stalled_file_mid_load_takes_its_rows_back_off_the_screen()
    {
        WriteLogFile("A", Lines(1, 50));
        var stuck = WriteLogFile("B");
        var stream = new SteppedStream(Utf8(Lines(1, 1200)));
        var rig = Build(p => p == stuck ? stream : null);
        var state = rig.State;
        state.LogFile = "";

        try
        {
            await _ui.RunAsync(async () =>
            {
                var search = state.SearchAsync();
                await WaitUntil(() => state.IsLive && state.TotalRecords == 1050, "both files' rows");

                state.SkipFile(stuck);
                await WaitUntil(() => state.TotalRecords == 50, "the skipped file's rows to go");

                await search.WaitAsync(Patience);
                Assert.Equal(50, state.TotalRecords);
                Assert.False(state.LastPrepareResult!.IsComplete);
            });
        }
        finally
        {
            stream.End();
        }
    }

    // --- the end of a load ---

    [Fact]
    public async Task A_filter_queued_behind_the_final_query_is_read_in_the_template_order_not_the_order_read()
    {
        var path = WriteLogFile("App");
        var stream = new SteppedStream(Utf8(Lines(1, 2500)));
        var (state, gated, _) = BuildGated(p => p == path ? stream : null);
        state.LogFile = "App";
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _ui.RunAsync(async () =>
            {
                var search = state.SearchAsync();
                await WaitUntil(() => state.IsLive && state.TotalRecords == 2000, "the first rows");

                // Live reads never use the page query, so this holds only the final one.
                gated.BeforePage = () => { held.TrySetResult(); return release.Task; };
                stream.Advance(); // end of file
                await held.Task.WaitAsync(Patience);
                Assert.True(state.IsBusy);

                state.Logs.KeywordRows[0].Text = "error";
                await state.RunLiveQueryAsync(); // queued behind the final query

                gated.BeforePage = null;
                release.SetResult();
                await search.WaitAsync(Patience);

                Assert.Equal(625, state.TotalRecords);
                // Basic declares no sort, so a finished grid is newest first — never the oldest-first
                // order the rows were shown in while they arrived.
                Assert.Equal("error 2500", state.FilteredLogLines[0]["Message"]);
            });
        }
        finally
        {
            stream.End();
            release.TrySetResult();
        }
    }

    // --- queries against the view ---

    [Fact]
    public async Task A_filter_typed_while_a_page_is_loading_lands_on_page_one_of_the_new_filter()
    {
        var (state, gated, storage) = BuildGated();
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", Lines(1, 1500).Select(m => new Dictionary<string, string> { ["Message"] = m }));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await _ui.RunAsync(async () =>
        {
            state.HasSearched = true;
            await state.QueryCurrentPageAsync();
            Assert.Equal(3, state.TotalPages);

            gated.BeforePage = () => { held.TrySetResult(); return release.Task; };
            var next = state.NextPageAsync();
            await held.Task.WaitAsync(Patience);

            // A filter that still leaves three pages: with fewer, the last-page clamp would hide a wrong
            // page by moving it anyway.
            state.Logs.KeywordRows[0].Text = "ok";
            var filter = state.RunLiveQueryAsync(); // queued behind page 2

            gated.BeforePage = null;
            release.SetResult();
            await Task.WhenAll(next, filter).WaitAsync(Patience);

            // Page 2 landing must not have put its page back over the "page 1" the filter asked for.
            Assert.Equal(0, state.CurrentPage);
            Assert.Equal(1, state.PageInput);
            Assert.Equal(1125, state.TotalRecords);
            Assert.Equal("ok 1499", state.FilteredLogLines[0]["Message"]); // newest first, page 1 (1500 is an error line)
            Assert.All(state.FilteredLogLines, r => Assert.StartsWith("ok", r["Message"]));
        });
    }


    [Fact]
    public async Task A_filter_changed_while_a_split_is_running_is_queued_and_applied_not_dropped()
    {
        var rig = Build();
        var state = rig.State;
        await SeedTwoFilesAsync(rig.Storage);

        await _ui.RunAsync(async () =>
        {
            state.HasSearched = true;
            await state.QueryCurrentPageAsync();

            var split = state.SetSplitModeAsync(LogSplitMode.File);
            Assert.True(state.IsBusy);
            state.Logs.KeywordRows[0].Text = "error";
            var filter = state.RunLiveQueryAsync();
            await Task.WhenAll(split, filter).WaitAsync(Patience);

            Assert.Equal(3, state.TotalRecords);
            Assert.All(state.FilteredLogLines, r => Assert.StartsWith("error", r["Message"]));
            Assert.Equal(3, state.Tabs[0].RowCount);
        });
    }

    [Fact]
    public async Task A_tab_keeps_its_parked_sort_when_the_strip_is_rebuilt()
    {
        var rig = Build();
        var state = rig.State;
        await SeedTwoFilesAsync(rig.Storage);

        await _ui.RunAsync(async () =>
        {
            state.HasSearched = true;
            await state.QueryCurrentPageAsync();
            await state.SetSplitModeAsync(LogSplitMode.File);

            var b = state.Tabs.FindIndex(t => t.Value == "b.log");
            await state.SelectTabAsync(b);
            await state.SortByColumnAsync("Message", append: false);
            await state.SelectTabAsync(state.Tabs.FindIndex(t => t.Value == "a.log"));

            // A filter change rebuilds the strip; b's parked sort must survive it.
            state.Logs.KeywordRows[0].Text = "o";
            await state.RunLiveQueryAsync();

            var rebuilt = state.Tabs.Single(t => t.Value == "b.log");
            Assert.Equal("Message", Assert.Single(rebuilt.Sorts).Column);
        });
    }

    private static async Task SeedTwoFilesAsync(SqliteLogStorageService storage)
    {
        await storage.EnsureTableAsync("logs", new[] { "Message", "Location", "SourceFile", "Sequence", "SourcePath" });
        var rows = new[]
        {
            ("error one", "a.log"), ("ok two", "a.log"), ("error three", "b.log"),
            ("ok four", "b.log"), ("error five", "b.log"), ("ok six", "a.log")
        };
        await storage.InsertLogLinesAsync("logs", rows.Select((r, i) => new Dictionary<string, string>
        {
            ["Message"] = r.Item1, ["Location"] = "Local", ["SourceFile"] = r.Item2,
            ["Sequence"] = (i + 1).ToString(), ["SourcePath"] = $@"C:\logs\{r.Item2}"
        }));
    }

    // --- lifetime ---

    [Fact]
    public async Task Disposing_mid_load_lets_go_of_the_load_lock_for_everyone_else()
    {
        var path = WriteLogFile("Stuck");
        var stream = new GatedStream(Utf8(Lines(1, 5)));
        var rig = Build(p => p == path ? stream : null);
        var state = rig.State;
        state.LogFile = "Stuck";

        try
        {
            await _ui.RunAsync(async () =>
            {
                _ = state.SearchAsync();
                await stream.EnteredBlockingRead.Task.WaitAsync(Patience);
            });

            // The page's circuit closed: the service is disposed with a file still blocked.
            state.Dispose();

            using var empty = new TempDirectory("state-live-empty");
            var next = new DbLogService(new DirectoryYamlStorage(_config.Path),
                new SqliteLogStorageService(Path.Combine(_db.Path, $"next.{Guid.NewGuid():N}.db")));
            var result = await next.PrepareLogTableAsync("Nothing", new List<LogLocation> { new() { Name = "Empty", Path = empty.Path } },
                DateTime.Today, DateTime.Today, "Basic").WaitAsync(Patience);
            Assert.True(result.IsComplete);
        }
        finally
        {
            stream.Release();
        }
    }
}

/// <summary>
/// A stream that serves its first chunk, then waits for <see cref="Advance"/> before each of the
/// others, and ends at the <see cref="Advance"/> after the last — a share that delivers a file in
/// bursts, under the test's control. Ignores the read's token, like a real SMB read.
/// </summary>
internal sealed class SteppedStream : Stream
{
    private readonly Queue<byte[]> _pending;
    private readonly SemaphoreSlim _released = new(0);
    private byte[] _current;
    private int _position;
    private long _total;

    internal SteppedStream(params byte[][] chunks)
    {
        _current = chunks[0];
        _pending = new Queue<byte[]>(chunks.Skip(1));
    }

    /// <summary>Lets the next chunk through, or the end of the stream once there are none left.</summary>
    internal void Advance() => _released.Release();

    /// <summary>
    /// Releases everything, end of stream included. Every test calls it before returning: a read left
    /// blocked would hold the ingest's process-wide load lock for every test after it.
    /// </summary>
    internal void End() => _released.Release(_pending.Count + 1);

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (_position >= _current.Length)
        {
            await _released.WaitAsync();
            if (!_pending.TryDequeue(out var next)) return 0;
            _current = next;
            _position = 0;
        }

        var n = Math.Min(count, _current.Length - _position);
        Array.Copy(_current, _position, buffer, offset, n);
        _position += n;
        _total += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    // Bytes served so far, across chunks: the ingest reports progress, and judges a stall, from it.
    public override long Position { get => _total; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
