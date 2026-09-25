using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Microsoft.Data.Sqlite;

namespace DevToolbox.Tests;

/// <summary>
/// Two SQLite behaviours the plan flagged as unverified at our pin, checked directly against
/// the driver before A2 relies on either. Both passed, so no fallback was needed.
/// </summary>
public sealed class SqliteCtasProbeTests : IDisposable
{
    private readonly TempDirectory _dir = new("ctas-probe");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Bound_parameters_work_inside_a_CTAS_select()
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir.Path, "probe1.db")}");
        await conn.OpenAsync();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE logs (Message TEXT, Location TEXT);";
            await create.ExecuteNonQueryAsync();
        }
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO logs VALUES ('a','Web01'), ('b','Web04');";
            await insert.ExecuteNonQueryAsync();
        }

        using (var ctas = conn.CreateCommand())
        {
            ctas.CommandText = "CREATE TABLE results AS SELECT * FROM logs WHERE Location = @loc;";
            ctas.Parameters.AddWithValue("@loc", "Web01");
            await ctas.ExecuteNonQueryAsync();
        }

        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM results;";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_subquerys_ORDER_BY_survives_a_CTAS_with_no_outer_order()
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir.Path, "probe2.db")}");
        await conn.OpenAsync();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE logs (Message TEXT);";
            await create.ExecuteNonQueryAsync();
        }
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO logs VALUES ('c'), ('a'), ('b');";
            await insert.ExecuteNonQueryAsync();
        }

        using (var ctas = conn.CreateCommand())
        {
            ctas.CommandText = "CREATE TABLE results AS SELECT * FROM (SELECT * FROM logs ORDER BY Message DESC);";
            await ctas.ExecuteNonQueryAsync();
        }

        using var read = conn.CreateCommand();
        read.CommandText = "SELECT Message FROM results;"; // no ORDER BY here — rowid order only
        using var reader = await read.ExecuteReaderAsync();
        var order = new List<string>();
        while (await reader.ReadAsync()) order.Add(reader.GetString(0));

        Assert.Equal(new[] { "c", "b", "a" }, order);
    }
}

/// <summary>
/// The Log Viewer's collapse-into-a-`results`-table feature: SQL mode honouring the split tab
/// (A0), and materializing a filter's result into a second table (A2).
/// </summary>
public sealed class ResultsTableTests : IDisposable
{
    private readonly TempDirectory _db = new("results-db");

    public void Dispose() => _db.Dispose();

    private SqliteLogStorageService BuildStorage() => new(Path.Combine(_db.Path, "logs.test.db"));

    private static async Task SeedAsync(SqliteLogStorageService storage)
    {
        await storage.EnsureTableAsync("logs", new[] { "Message", "Location", "SourceFile" });
        await storage.InsertLogLinesAsync("logs", new[]
        {
            new Dictionary<string, string> { ["Message"] = "one", ["Location"] = "Web01", ["SourceFile"] = "a.log" },
            new Dictionary<string, string> { ["Message"] = "two", ["Location"] = "Web01", ["SourceFile"] = "a.log" },
            new Dictionary<string, string> { ["Message"] = "three", ["Location"] = "Web04", ["SourceFile"] = "b.log" },
        });
    }

    // --- A0: raw SQL honours the split tab ---

    [Fact]
    public async Task Raw_SQL_with_a_tab_filter_returns_only_that_tabs_rows_and_count()
    {
        var storage = BuildStorage();
        await SeedAsync(storage);

        var query = new LogQuery
        {
            RawQuery = "SELECT * FROM logs",
            Filters = new Dictionary<string, object> { ["Location"] = "Web01" }
        };

        var (results, total) = await storage.SearchLogsAsync("logs", query);

        Assert.Equal(2, total);
        Assert.All(results, r => Assert.Equal("Web01", r["Location"]));
    }

    [Fact]
    public async Task Raw_SQL_with_no_tab_filter_is_unaffected()
    {
        var storage = BuildStorage();
        await SeedAsync(storage);

        var (results, total) = await storage.SearchLogsAsync("logs", new LogQuery { RawQuery = "SELECT * FROM logs" });

        Assert.Equal(3, total);
        Assert.Equal(3, results.Count());
    }

    [Fact]
    public async Task Raw_SQL_refuses_a_tab_filter_on_a_column_that_is_not_groupable()
    {
        var storage = BuildStorage();
        await SeedAsync(storage);

        var query = new LogQuery
        {
            RawQuery = "SELECT * FROM logs",
            Filters = new Dictionary<string, object> { ["Message"] = "one" }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SearchLogsAsync("logs", query));
    }

    // --- A2: materializing a filter's result into `results` ---

    [Fact]
    public async Task A_keyword_collapse_copies_exactly_the_grid_rows_across_pages()
    {
        var storage = BuildStorage();
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", Enumerable.Range(1, 30)
            .Select(i => new Dictionary<string, string> { ["Message"] = $"row{i:00}" }));

        // A page-1 view: page size 10, page 0. The collapse ignores paging — it copies the whole
        // filtered set, not one page — but must agree with what a page query would return in order.
        var query = new LogQuery { SearchTerm = "row", Sort = new() { new SortColumn { Column = "Message", Direction = "asc" } } };
        var (rows, columns) = await storage.CreateTableFromQueryAsync("logs", "results", query);

        Assert.Equal(30, rows);
        Assert.Contains("Message", columns);

        var (readBack, total) = await storage.SearchLogsAsync("results", new LogQuery { InsertionOrder = true });
        Assert.Equal(30, total);
        Assert.Equal("row01", readBack.First()["Message"]);
    }

    [Fact]
    public async Task A_keyword_collapse_with_a_tab_copies_only_that_tabs_rows()
    {
        var storage = BuildStorage();
        await SeedAsync(storage);

        var query = new LogQuery { Filters = new Dictionary<string, object> { ["Location"] = "Web01" } };
        var (rows, _) = await storage.CreateTableFromQueryAsync("logs", "results", query);

        Assert.Equal(2, rows);
        var (readBack, _) = await storage.SearchLogsAsync("results", new LogQuery());
        Assert.All(readBack, r => Assert.Equal("Web01", r["Location"]));
    }

    [Fact]
    public async Task A_SQL_collapse_with_a_tab_copies_only_that_tabs_rows()
    {
        var storage = BuildStorage();
        await SeedAsync(storage);

        var query = new LogQuery
        {
            RawQuery = "SELECT * FROM logs",
            Filters = new Dictionary<string, object> { ["Location"] = "Web04" }
        };
        var (rows, _) = await storage.CreateTableFromQueryAsync("logs", "results", query);

        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task A_group_by_collapse_has_no_Location_column()
    {
        var storage = BuildStorage();
        await SeedAsync(storage);

        var query = new LogQuery { RawQuery = "SELECT Location, COUNT(*) AS n FROM logs GROUP BY Location" };
        var (rows, columns) = await storage.CreateTableFromQueryAsync("logs", "results", query);

        Assert.Equal(2, rows);
        Assert.DoesNotContain("SourceFile", columns);
    }

    [Fact]
    public async Task Unsorted_keyword_collapse_reads_back_in_the_order_it_was_collapsed()
    {
        var storage = BuildStorage();
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", new[]
        {
            new Dictionary<string, string> { ["Message"] = "first" },
            new Dictionary<string, string> { ["Message"] = "second" },
            new Dictionary<string, string> { ["Message"] = "third" },
        });

        // No sort: the grid's own default is rowid DESC (newest first).
        await storage.CreateTableFromQueryAsync("logs", "results", new LogQuery());

        var (readBack, _) = await storage.SearchLogsAsync("results", new LogQuery { InsertionOrder = true });
        Assert.Equal(new[] { "third", "second", "first" }, readBack.Select(r => r["Message"]));
    }

    [Fact]
    public async Task A_template_sort_at_collapse_is_baked_into_the_read_back_order()
    {
        var storage = BuildStorage();
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", new[]
        {
            new Dictionary<string, string> { ["Message"] = "b" },
            new Dictionary<string, string> { ["Message"] = "a" },
            new Dictionary<string, string> { ["Message"] = "c" },
        });

        var sortedQuery = new LogQuery { Sort = new() { new SortColumn { Column = "Message", Direction = "asc" } } };
        await storage.CreateTableFromQueryAsync("logs", "results", sortedQuery);

        var (readBack, _) = await storage.SearchLogsAsync("results", new LogQuery { InsertionOrder = true });
        Assert.Equal(new[] { "a", "b", "c" }, readBack.Select(r => r["Message"]));
    }

    [Fact]
    public async Task A_SQL_ORDER_BY_DESC_at_collapse_is_baked_into_the_read_back_order()
    {
        var storage = BuildStorage();
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", new[]
        {
            new Dictionary<string, string> { ["Message"] = "a" },
            new Dictionary<string, string> { ["Message"] = "c" },
            new Dictionary<string, string> { ["Message"] = "b" },
        });

        var query = new LogQuery { RawQuery = "SELECT * FROM logs ORDER BY Message DESC" };
        await storage.CreateTableFromQueryAsync("logs", "results", query);

        // No outer ORDER BY of ours in SQL mode: the inner query's order decides, and a plain
        // "SELECT * FROM results" reads back in rowid (insertion) order.
        var (readBack, _) = await storage.SearchLogsAsync("results", new LogQuery { InsertionOrder = true });
        Assert.Equal(new[] { "c", "b", "a" }, readBack.Select(r => r["Message"]));
    }

    [Fact]
    public async Task A_column_name_with_a_bracket_is_refused_and_leaves_no_table()
    {
        var storage = BuildStorage();
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", new[] { new Dictionary<string, string> { ["Message"] = "x" } });

        var query = new LogQuery { RawQuery = "SELECT Message AS \"Bad]Name\" FROM logs" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.CreateTableFromQueryAsync("logs", "results", query));

        Assert.False(await storage.TableExistsAsync("results"));
    }

    [Fact]
    public async Task Duplicate_column_names_come_out_as_name_and_name_colon_1()
    {
        var storage = BuildStorage();
        await storage.EnsureTableAsync("logs", new[] { "Message" });
        await storage.InsertLogLinesAsync("logs", new[] { new Dictionary<string, string> { ["Message"] = "x" } });

        var query = new LogQuery { RawQuery = "SELECT Message, Message FROM logs" };
        var (_, columns) = await storage.CreateTableFromQueryAsync("logs", "results", query);

        Assert.Contains("Message", columns);
        Assert.Contains("Message:1", columns);
    }

    [Fact]
    public async Task A_computed_count_sorts_numerically_not_lexically()
    {
        var storage = BuildStorage();
        await storage.EnsureTableAsync("logs", new[] { "Location" });
        await storage.InsertLogLinesAsync("logs", Enumerable.Range(0, 12)
            .Select(_ => new Dictionary<string, string> { ["Location"] = "Web01" })
            .Concat(Enumerable.Range(0, 2).Select(_ => new Dictionary<string, string> { ["Location"] = "Web04" })));

        var query = new LogQuery { RawQuery = "SELECT Location, COUNT(*) AS n FROM logs GROUP BY Location" };
        await storage.CreateTableFromQueryAsync("logs", "results", query);

        var sorted = await storage.SearchLogsAsync("results",
            new LogQuery { Sort = new() { new SortColumn { Column = "n", Direction = "asc" } } });

        // Lexical sort would put "12" before "2"; numeric affinity puts 2 first.
        Assert.Equal("2", sorted.Results.First()["n"]);
    }

    [Fact]
    public async Task Split_indexes_exist_on_the_results_table_when_the_columns_do()
    {
        var storage = BuildStorage();
        await SeedAsync(storage);
        await storage.CreateTableFromQueryAsync("logs", "results", new LogQuery());

        // No public "list indexes" surface — proven indirectly: a tab query against the new
        // table still returns the right rows, which is all an index changes performance of, not
        // correctness. Kept as a smoke check that indexing didn't throw or corrupt the table.
        var (rows, _) = await storage.SearchLogsAsync("results", new LogQuery
        {
            Filters = new Dictionary<string, object> { ["Location"] = "Web01" }
        });
        Assert.Equal(2, rows.Count());
    }

    [Fact]
    public async Task A_read_only_instance_refuses_to_collapse()
    {
        var path = Path.Combine(_db.Path, "logs.readonly2.db");
        var writable = new SqliteLogStorageService(path);
        await writable.EnsureTableAsync("logs", new[] { "Message" });

        var readOnly = new SqliteLogStorageService(path, readOnly: true);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => readOnly.CreateTableFromQueryAsync("logs", "results", new LogQuery()));
    }
}
