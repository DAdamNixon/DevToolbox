using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>
/// The storage calls a load that is shown while it runs depends on: reading only what arrived since
/// the last refresh, purging a skipped file by rowid range, growing overflow columns on demand, and
/// statements that stop when their token is cancelled.
/// </summary>
public sealed class SqliteLogStorageServiceLiveTests : IDisposable
{
    private readonly TempDirectory _db = new("live-storage-db");

    public void Dispose() => _db.Dispose();

    private SqliteLogStorageService BuildStorage() => new(Path.Combine(_db.Path, $"logs.{Guid.NewGuid():N}.db"));

    private static Dictionary<string, string> Row(string message, string location = "Web01", string file = "a.log", int seq = 0) => new()
    {
        ["Message"] = message,
        [LogProvenanceColumns.Location] = location,
        [LogProvenanceColumns.SourceFile] = file,
        [LogProvenanceColumns.Sequence] = seq.ToString(),
        [LogProvenanceColumns.SourcePath] = $@"C:\logs\{file}"
    };

    private static async Task<SqliteLogStorageService> SeededAsync(SqliteLogStorageService storage, params Dictionary<string, string>[] rows)
    {
        await storage.EnsureTableAsync("logs", new[] { "Message", "Location", "SourceFile", "Sequence", "SourcePath" });
        if (rows.Length > 0) await storage.InsertLogLinesAsync("logs", rows);
        return storage;
    }

    private static LogSearchCriteria Keyword(string term) =>
        new() { Groups = { new KeywordGroup { Gate = "AND", Terms = new() { term } } } };

    // --- live slice ---

    [Fact]
    public async Task A_slice_from_zero_counts_groups_and_pages_the_whole_table_in_arrival_order()
    {
        var storage = await SeededAsync(BuildStorage(),
            Row("one", "Web01"), Row("two", "Web02"), Row("three", "Web01"));

        var slice = await storage.ReadLiveSliceAsync("logs", new LogLiveRequest { GroupColumn = "Location", Take = 10 });

        Assert.Equal(3, slice.Count);
        Assert.Equal(3, slice.MaxRowid);
        Assert.Equal(new[] { "one", "two", "three" }, slice.Rows.Select(r => r["Message"]));
        Assert.Equal(2, slice.Groups.Single(g => g.Value == "Web01").Count);
        Assert.Equal(1, slice.Groups.Single(g => g.Value == "Web02").Count);
    }

    [Fact]
    public async Task A_slice_after_a_rowid_sees_only_what_arrived_since()
    {
        var storage = await SeededAsync(BuildStorage(), Row("one"), Row("two"));
        var first = await storage.ReadLiveSliceAsync("logs", new LogLiveRequest { Take = 10 });

        await storage.InsertLogLinesAsync("logs", new[] { Row("three"), Row("four") });
        var next = await storage.ReadLiveSliceAsync("logs", new LogLiveRequest { AfterRowid = first.MaxRowid, Take = 10 });

        Assert.Equal(2, next.Count);
        Assert.Equal(4, next.MaxRowid);
        Assert.Equal(new[] { "three", "four" }, next.Rows.Select(r => r["Message"]));
    }

    [Fact]
    public async Task The_filter_and_tab_narrow_the_count_and_rows_but_the_groups_ignore_the_tab()
    {
        var storage = await SeededAsync(BuildStorage(),
            Row("error one", "Web01"), Row("fine", "Web01"), Row("error two", "Web02"), Row("error three", "Web02"));

        var slice = await storage.ReadLiveSliceAsync("logs", new LogLiveRequest
        {
            Criteria = Keyword("error"),
            Split = LogSplitFilter.For(LogSplitMode.Location, "Web02"),
            GroupColumn = "Location",
            Take = 10
        });

        Assert.Equal(2, slice.Count);
        Assert.All(slice.Rows, r => Assert.Equal("Web02", r["Location"]));

        // Groups describe every tab under the filter, so the strip's counts add up to All.
        Assert.Equal(1, slice.Groups.Single(g => g.Value == "Web01").Count);
        Assert.Equal(2, slice.Groups.Single(g => g.Value == "Web02").Count);
    }

    [Fact]
    public async Task Skip_and_take_page_through_the_matches_in_the_range()
    {
        var storage = await SeededAsync(BuildStorage(), Enumerable.Range(1, 10).Select(i => Row($"m{i}")).ToArray());

        var slice = await storage.ReadLiveSliceAsync("logs", new LogLiveRequest { AfterRowid = 2, Skip = 3, Take = 2 });

        // Rows 3..10 are in range; skip three of them (3,4,5), take two.
        Assert.Equal(8, slice.Count);
        Assert.Equal(new[] { "m6", "m7" }, slice.Rows.Select(r => r["Message"]));
    }

    [Fact]
    public async Task A_sort_orders_the_rows_and_take_zero_returns_only_counts()
    {
        var storage = await SeededAsync(BuildStorage(), Row("b"), Row("c"), Row("a"));

        var sorted = await storage.ReadLiveSliceAsync("logs", new LogLiveRequest
        {
            Sort = new() { new SortColumn { Column = "Message", Direction = "asc" } },
            Take = 10
        });
        Assert.Equal(new[] { "a", "b", "c" }, sorted.Rows.Select(r => r["Message"]));

        var countsOnly = await storage.ReadLiveSliceAsync("logs", new LogLiveRequest { Take = 0 });
        Assert.Equal(3, countsOnly.Count);
        Assert.Empty(countsOnly.Rows);
    }

    [Fact]
    public async Task An_empty_table_slices_to_nothing()
    {
        var storage = await SeededAsync(BuildStorage());

        var slice = await storage.ReadLiveSliceAsync("logs", new LogLiveRequest { GroupColumn = "SourceFile", Take = 10 });

        Assert.Equal(0, slice.MaxRowid);
        Assert.Equal(0, slice.Count);
        Assert.Empty(slice.Groups);
        Assert.Empty(slice.Rows);
    }

    // --- rowid ranges and purges ---

    [Fact]
    public async Task Each_batch_reports_the_contiguous_rowids_it_was_given()
    {
        var storage = await SeededAsync(BuildStorage());

        var first = await storage.InsertLogLinesAsync("logs", new[] { Row("a"), Row("b"), Row("c") });
        var second = await storage.InsertLogLinesAsync("logs", new[] { Row("d"), Row("e") });

        Assert.Equal(new LogRowRange(1, 3), first);
        Assert.Equal(new LogRowRange(4, 5), second);
        Assert.Equal(3, first.Count);
    }

    [Fact]
    public async Task Deleting_row_ranges_removes_exactly_those_rows_and_counts_them()
    {
        var storage = await SeededAsync(BuildStorage());
        var keep = await storage.InsertLogLinesAsync("logs", new[] { Row("keep1"), Row("keep2") });
        var drop1 = await storage.InsertLogLinesAsync("logs", new[] { Row("drop1"), Row("drop2") });
        await storage.InsertLogLinesAsync("logs", new[] { Row("keep3") });
        var drop2 = await storage.InsertLogLinesAsync("logs", new[] { Row("drop3") });

        var deleted = await storage.DeleteRowRangesAsync("logs", new[] { drop1, drop2 });

        Assert.Equal(3, deleted);
        var (rows, total) = await storage.SearchLogsAsync("logs", new LogQuery { InsertionOrder = true });
        Assert.Equal(3, total);
        Assert.Equal(new[] { "keep1", "keep2", "keep3" }, rows.Select(r => r["Message"]));
        Assert.Equal(2, keep.Count);
    }

    // --- overflow columns ---

    [Fact]
    public async Task An_added_column_reads_as_empty_on_the_rows_already_there()
    {
        var storage = await SeededAsync(BuildStorage(), Row("before"));

        await storage.AddColumnsAsync("logs", new[] { "Message1" });
        var later = Row("after");
        later["Message1"] = "extra";
        await storage.InsertLogLinesAsync("logs", new[] { later });

        var (rows, _) = await storage.SearchLogsAsync("logs", new LogQuery { InsertionOrder = true });
        var list = rows.ToList();
        Assert.Equal("", list[0]["Message1"]);
        Assert.Equal("extra", list[1]["Message1"]);

        // And the empty string is really there, not a NULL rendered as one: SQL typed against the
        // column compares with '' the way it always could.
        var blanks = await storage.CountLogsAsync("logs", new LogQuery { RawQuery = "SELECT * FROM logs WHERE [Message1] = ''" });
        Assert.Equal(1, blanks);
    }

    [Fact]
    public async Task Keyword_results_keep_overflow_columns_ahead_of_provenance_whatever_the_physical_order()
    {
        var storage = await SeededAsync(BuildStorage(), Row("x"));
        await storage.AddColumnsAsync("logs", new[] { "Message1" });

        var (rows, _) = await storage.SearchLogsAsync("logs", new LogQuery());

        Assert.Equal(
            new[] { "Message", "Message1", "Location", "SourceFile", "Sequence", "SourcePath" },
            rows.Single().Keys);
    }

    [Fact]
    public async Task A_sql_select_star_reads_in_display_order_and_a_projection_keeps_its_own()
    {
        var storage = await SeededAsync(BuildStorage(), Row("x"));
        await storage.AddColumnsAsync("logs", new[] { "Message1" });

        var (all, _) = await storage.SearchLogsAsync("logs", new LogQuery { RawQuery = "SELECT * FROM logs" });
        Assert.Equal(new[] { "Message", "Message1", "Location", "SourceFile", "Sequence", "SourcePath" }, all.Single().Keys);

        var (some, _) = await storage.SearchLogsAsync("logs", new LogQuery { RawQuery = "SELECT SourceFile, Message FROM logs" });
        Assert.Equal(new[] { "SourceFile", "Message" }, some.Single().Keys);
    }

    // --- counting ---

    [Fact]
    public async Task A_count_only_query_counts_without_a_page_and_a_search_can_skip_its_count()
    {
        var storage = await SeededAsync(BuildStorage(), Row("error"), Row("fine"), Row("error again"));

        Assert.Equal(2, await storage.CountLogsAsync("logs", new LogQuery { Criteria = Keyword("error") }));

        var (rows, total) = await storage.SearchLogsAsync("logs", new LogQuery { PageSize = 1, IncludeCount = false });
        Assert.Single(rows);
        Assert.Equal(0, total);
    }

    // --- cancellation ---

    [Fact]
    public async Task Cancelling_a_running_statement_interrupts_it()
    {
        var storage = await SeededAsync(BuildStorage());
        await storage.InsertLogLinesAsync("logs", Enumerable.Range(0, 400).Select(i => Row($"m{i}")));

        // A 64-million-row cross join: many seconds of work once it has started, so finishing well
        // inside the bound below can only mean it was stopped.
        var slow = new LogQuery { RawQuery = "SELECT a.Message FROM logs a, logs b, logs c WHERE a.Message || b.Message || c.Message LIKE '%zz%'" };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var count = storage.CountLogsAsync("logs", slow, cts.Token);

        // Bounded, so an interrupt that does not work fails here rather than hanging the run.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => count.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"took {started.Elapsed}");
    }
}
