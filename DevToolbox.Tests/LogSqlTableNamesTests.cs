using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>
/// What SQL mode does to <c>logs</c> and <c>results</c> once a window's tables have names of their
/// own: a reference in table position goes to the real table, aliased back to what was written, and
/// nothing else in the text moves.
/// </summary>
public class LogSqlTableNamesTests
{
    private static readonly Dictionary<string, string> Scope = new()
    {
        ["logs"] = "logs_ab12",
        ["results"] = "results_ab12"
    };

    private static string Resolve(string sql) => LogSqlTableNames.Resolve(sql, Scope);

    [Theory]
    [InlineData("SELECT * FROM logs", "SELECT * FROM [logs_ab12] AS logs")]
    [InlineData("SELECT LEFT(DateTime,4) AS Year, COUNT(*) AS Hits FROM logs GROUP BY Year",
                "SELECT LEFT(DateTime,4) AS Year, COUNT(*) AS Hits FROM [logs_ab12] AS logs GROUP BY Year")]
    [InlineData("select * from LOGS where x = 1", "select * from [logs_ab12] AS LOGS where x = 1")]
    [InlineData("SELECT * FROM [logs]", "SELECT * FROM [logs_ab12] AS [logs]")]
    [InlineData("SELECT * FROM \"logs\"", "SELECT * FROM [logs_ab12] AS \"logs\"")]
    [InlineData("SELECT * FROM `results`", "SELECT * FROM [results_ab12] AS `results`")]
    [InlineData("SELECT * FROM logs;", "SELECT * FROM [logs_ab12] AS logs;")]
    [InlineData("SELECT logs.Message FROM logs WHERE logs.Message <> ''",
                "SELECT logs.Message FROM [logs_ab12] AS logs WHERE logs.Message <> ''")]
    [InlineData("SELECT rowid, Message FROM logs ORDER BY rowid DESC",
                "SELECT rowid, Message FROM [logs_ab12] AS logs ORDER BY rowid DESC")]
    public void A_table_reference_points_at_the_real_table_under_the_name_written(string sql, string expected) =>
        Assert.Equal(expected, Resolve(sql));

    [Theory]
    [InlineData("SELECT * FROM logs l WHERE l.Message = 'x'", "SELECT * FROM [logs_ab12] l WHERE l.Message = 'x'")]
    [InlineData("SELECT * FROM logs AS l", "SELECT * FROM [logs_ab12] AS l")]
    [InlineData("SELECT * FROM logs \"l\"", "SELECT * FROM [logs_ab12] \"l\"")]
    public void A_reference_with_its_own_alias_keeps_it(string sql, string expected) =>
        Assert.Equal(expected, Resolve(sql));

    [Theory]
    [InlineData("SELECT * FROM results JOIN logs ON results.SourcePath = logs.SourcePath",
                "SELECT * FROM [results_ab12] AS results JOIN [logs_ab12] AS logs ON results.SourcePath = logs.SourcePath")]
    [InlineData("SELECT * FROM logs a, results b WHERE a.x = b.x",
                "SELECT * FROM [logs_ab12] a, [results_ab12] b WHERE a.x = b.x")]
    [InlineData("SELECT * FROM logs, results", "SELECT * FROM [logs_ab12] AS logs, [results_ab12] AS results")]
    [InlineData("SELECT * FROM logs LEFT OUTER JOIN results USING (Message)",
                "SELECT * FROM [logs_ab12] AS logs LEFT OUTER JOIN [results_ab12] AS results USING (Message)")]
    [InlineData("SELECT * FROM logs a JOIN results b ON a.x = b.x, logs c",
                "SELECT * FROM [logs_ab12] a JOIN [results_ab12] b ON a.x = b.x, [logs_ab12] c")]
    [InlineData("SELECT * FROM logs NOT INDEXED", "SELECT * FROM [logs_ab12] AS logs NOT INDEXED")]
    public void Every_table_in_a_join_is_pointed_at_its_own(string sql, string expected) =>
        Assert.Equal(expected, Resolve(sql));

    [Theory]
    [InlineData("SELECT * FROM (SELECT * FROM logs) x", "SELECT * FROM (SELECT * FROM [logs_ab12] AS logs) x")]
    [InlineData("SELECT * FROM logs WHERE Message IN (SELECT Message FROM results)",
                "SELECT * FROM [logs_ab12] AS logs WHERE Message IN (SELECT Message FROM [results_ab12] AS results)")]
    [InlineData("SELECT (SELECT COUNT(*) FROM results), Message FROM logs",
                "SELECT (SELECT COUNT(*) FROM [results_ab12] AS results), Message FROM [logs_ab12] AS logs")]
    [InlineData("SELECT Message FROM logs UNION ALL SELECT Message FROM results",
                "SELECT Message FROM [logs_ab12] AS logs UNION ALL SELECT Message FROM [results_ab12] AS results")]
    public void Subqueries_and_compounds_are_pointed_too(string sql, string expected) =>
        Assert.Equal(expected, Resolve(sql));

    [Theory]
    // Columns that happen to share a table's name.
    [InlineData("SELECT Results, Logs FROM logs", "SELECT Results, Logs FROM [logs_ab12] AS logs")]
    [InlineData("SELECT * FROM logs ORDER BY Message, results", "SELECT * FROM [logs_ab12] AS logs ORDER BY Message, results")]
    [InlineData("SELECT * FROM logs GROUP BY Location, logs", "SELECT * FROM [logs_ab12] AS logs GROUP BY Location, logs")]
    [InlineData("SELECT COUNT(*) AS logs FROM logs", "SELECT COUNT(*) AS logs FROM [logs_ab12] AS logs")]
    [InlineData("SELECT * FROM logs WHERE Message IS NOT DISTINCT FROM Results",
                "SELECT * FROM [logs_ab12] AS logs WHERE Message IS NOT DISTINCT FROM Results")]
    [InlineData("SELECT * FROM logs a JOIN results b ON a.x IS DISTINCT FROM results",
                "SELECT * FROM [logs_ab12] a JOIN [results_ab12] b ON a.x IS DISTINCT FROM results")]
    // Text that only looks like a reference.
    [InlineData("SELECT * FROM logs WHERE Message = 'from logs'", "SELECT * FROM [logs_ab12] AS logs WHERE Message = 'from logs'")]
    [InlineData("SELECT * FROM logs WHERE Message = 'it''s from logs'", "SELECT * FROM [logs_ab12] AS logs WHERE Message = 'it''s from logs'")]
    [InlineData("SELECT * -- from logs\nFROM logs", "SELECT * -- from logs\nFROM [logs_ab12] AS logs")]
    [InlineData("SELECT * /* from logs */ FROM logs", "SELECT * /* from logs */ FROM [logs_ab12] AS logs")]
    [InlineData("SELECT * FROM logs WHERE Message = @logs", "SELECT * FROM [logs_ab12] AS logs WHERE Message = @logs")]
    public void Nothing_but_a_table_reference_is_touched(string sql, string expected) =>
        Assert.Equal(expected, Resolve(sql));

    [Theory]
    [InlineData("SELECT * FROM logs2")]
    [InlineData("SELECT * FROM mylogs")]
    [InlineData("SELECT * FROM logs_old")]
    [InlineData("SELECT * FROM main.logs")]
    [InlineData("SELECT * FROM pragma_table_info('logs')")]
    [InlineData("SELECT Message FROM other WHERE Message = 'logs'")]
    [InlineData("")]
    public void Other_names_are_left_alone(string sql) =>
        Assert.Equal(sql, Resolve(sql));

    [Fact]
    public void A_cte_the_query_calls_logs_is_its_own()
    {
        Assert.Equal("WITH logs AS (SELECT 1 AS n) SELECT * FROM logs",
            Resolve("WITH logs AS (SELECT 1 AS n) SELECT * FROM logs"));

        Assert.Equal("WITH x AS (SELECT * FROM [logs_ab12] AS logs), results AS (SELECT 2) SELECT * FROM x, results",
            Resolve("WITH x AS (SELECT * FROM logs), results AS (SELECT 2) SELECT * FROM x, results"));
    }

    [Fact]
    public void Half_typed_sql_is_rewritten_as_far_as_it_goes_and_never_throws()
    {
        Assert.Equal("SELECT * FROM [logs_ab12] AS logs WHERE x = 'oops", Resolve("SELECT * FROM logs WHERE x = 'oops"));
        Assert.Equal("SELECT * FROM [logs_ab12] AS logs WHERE [unclosed", Resolve("SELECT * FROM logs WHERE [unclosed"));
        Assert.Equal("SELECT * FROM", Resolve("SELECT * FROM"));
    }

    [Fact]
    public void Names_that_already_match_return_the_sql_untouched()
    {
        // The default-named service — the MCP server's reader, every older test — must see no change at all.
        var sql = "SELECT * FROM logs JOIN results ON 1";
        Assert.Same(sql, LogSqlTableNames.Resolve(sql, new Dictionary<string, string> { ["logs"] = "logs", ["results"] = "results" }));
    }
}
