using DevToolbox.Mcp.Core;
using DevToolbox.Services.Services;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// A whole server environment on disk: a config folder holding the same YAML shapes the real one
/// does, a log folder holding files in the real Elliott web-log format, and a scratch database.
/// <para>
/// The log lines are copied from the real format rather than invented, because the point of these
/// tests is that the server handles what is actually in <c>C:\inetpub\LogFiles</c> — nine
/// pipe-delimited fields from a fixed prefix, then per-call fields, then a trailing delimiter that
/// produces one more empty column than a reader expects.
/// </para>
/// </summary>
internal sealed class LogEnvironment : IDisposable
{
    private readonly TempDirectory _config = new("mcp-config");
    private readonly TempDirectory _logs = new("mcp-logs");
    private readonly TempDirectory _db = new("mcp-db");

    internal LogViewerService Service { get; }
    internal string LogFolder => _logs.Path;
    internal string ConfigFolder => _config.Path;

    /// <param name="extraLocation">
    /// An extra (name, path) appended to log_paths.yaml. Used to add a deliberately broken entry —
    /// the only kind of location the policy still refuses.
    /// </param>
    internal LogEnvironment((string Name, string Path)? extraLocation = null)
    {
        File.WriteAllText(Path.Combine(_config.Path, "log_templates_index.yaml"), """
            templates:
              - name: "WebsiteBase"
                file: "WebsiteBase.yaml"
              - name: "Checkout"
                file: "Checkout.yaml"
            """);

        // Eight declared columns — the real WebsiteBase does not name the Action field, so it lands
        // in the overflow columns. Reproduced rather than corrected: the tests have to describe the
        // configuration that exists.
        File.WriteAllText(Path.Combine(_config.Path, "WebsiteBase.yaml"), """
            name: "WebsiteBase"
            extension: ".txt"
            delimiter: "|"
            columns:
              - DateTime
              - Guid
              - IP
              - Account Number
              - User ID
              - Type
              - JobSeq
              - StoreNumber
            sort:
              - column: DateTime
                direction: asc
            """);

        File.WriteAllText(Path.Combine(_config.Path, "Checkout.yaml"), """
            name: "Checkout"
            inherits: "WebsiteBase"
            columns:
              - Action
              - Detail
            """);

        // The second location mirrors the real config's network shares. It is READABLE now — locality
        // stopped being a refusal on 2026-09-03 — and it stays in the fixture for a different reason:
        // every test that walks files names "Local Logs" explicitly, so a test that forgot the argument,
        // or a Resolve() that quietly widened, would reach for a UNC path that is not there and fail
        // loudly instead of passing on a one-location config where selection cannot be observed.
        var paths = $"""
            logLocations:
            - name: Local Logs
              path: {_logs.Path}
            - name: Archived Logs
              path: '\\fileserver01\LogFiles\WebServers\ElliottLogs'
            """;

        if (extraLocation is not null)
            paths += $"\n- name: {extraLocation.Value.Name}\n  path: '{extraLocation.Value.Path}'";

        File.WriteAllText(Path.Combine(_config.Path, "log_paths.yaml"), paths);

        var yaml = new McpYamlStorage(_config.Path);
        var dbPath = Path.Combine(_db.Path, "logs.test.db");

        Service = new LogViewerService(
            yaml,
            new SqliteLogStorageService(dbPath),
            new SqliteLogStorageService(dbPath, readOnly: true),
            new SavedQueryService(yaml),
            new PreparedTables(),
            new ColumnProfiler(dbPath));
    }

    /// <summary>
    /// The location every file-walking test names. Selection is required with no default, so this is
    /// not boilerplate — it is the argument under test everywhere else.
    /// </summary>
    internal static readonly string[] LocalOnly = ["Local Logs"];

    /// <summary>One log line in the real shape, trailing delimiter included.</summary>
    internal static string Line(string stamp, string type, string action, string detail) =>
        $"{stamp}|7638f364-dafd-4a1b-a441-36a9b9a50eec|127.0.0.1|9999999|280354|{type}|1|1|{action}|{detail}|";

    internal void WriteLog(string name, DateTime written, params string[] lines)
    {
        var path = Path.Combine(_logs.Path, name);
        File.WriteAllLines(path, lines);
        File.SetLastWriteTime(path, written);
    }

    public void Dispose()
    {
        _config.Dispose();
        _logs.Dispose();
        _db.Dispose();
    }
}

public sealed class LogViewerServiceTests
{
    private static readonly DateTime Day = new(2026, 8, 21, 12, 0, 0);
    private const string From = "2026-08-21";
    private const string To = "2026-08-21";

    private static LogEnvironment WithCheckoutLog()
    {
        var env = new LogEnvironment();
        env.WriteLog("Checkout.20260821.txt", Day,
            LogEnvironment.Line("20260821122849", "Customer", "isOnCreditHold", "False"),
            LogEnvironment.Line("20260821122850", "Customer", "BillHow Change", "OnAccount"),
            LogEnvironment.Line("20260821122851", "Employee", "isOnCreditHold", "True"));
        return env;
    }

    // ------------------------------------------------------------------ catalogue

    [Fact]
    public async Task Both_local_and_network_locations_are_listed_as_readable()
    {
        // Until 2026-09-03 this asserted the opposite: one admitted, the UNC one refused. The
        // restriction was scope control for the build phase and was lifted deliberately, so the test
        // was inverted rather than deleted — the pair of them is the record that it was on purpose.
        using var env = new LogEnvironment();

        var result = await env.Service.GetLocationsAsync();

        Assert.Equal(new[] { "Local Logs", "Archived Logs" }, result.Locations.Select(l => l.Name));
        Assert.Empty(result.Refused);
    }

    [Fact]
    public async Task The_policy_text_says_locations_must_be_named()
    {
        // This description is the only place a caller learns the argument has no default, and
        // list_locations is the first tool it calls. If the text stops saying so, the first
        // prepare_table is a guess.
        using var env = new LogEnvironment();

        var result = await env.Service.GetLocationsAsync();

        Assert.Contains("require", result.Policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("locations", result.Policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_misconfigured_location_is_still_refused_and_named()
    {
        // What is left of the location policy. A blank path is a broken config entry, and the dev has
        // to be able to tell that from a decision — the reason refusals are reported at all.
        using var env = new LogEnvironment(extraLocation: ("Broken", ""));

        var result = await env.Service.GetLocationsAsync();

        var refused = Assert.Single(result.Refused);
        Assert.Equal("Broken", refused.Name);
        Assert.Equal(LocationPolicy.ReasonBlank, refused.Reason);
    }

    // ------------------------------------------------------------------ location selection

    [Fact]
    public async Task Preparing_without_locations_is_refused_and_says_there_is_no_default()
    {
        // The point of the change. An omitted argument must not fall back to "everything" — the SMB
        // walk across production this argument exists to prevent — nor to "local", which would
        // silently reinstate the guardrail that was just retired.
        using var env = WithCheckoutLog();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => env.Service.PrepareAsync("Checkout", "Checkout", From, To, Array.Empty<string>()));

        Assert.Contains("no default", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("list_locations", ex.Message);
    }

    [Fact]
    public async Task An_unknown_location_is_refused_with_the_known_names()
    {
        // Refused, never skipped. Skipping returns real rows from a smaller population with no error
        // — the failure this module already rejected a shared table over.
        using var env = WithCheckoutLog();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => env.Service.PrepareAsync("Checkout", "Checkout", From, To, new[] { "Local Logs", "Live Web09" }));

        Assert.Contains("Live Web09", ex.Message);
        Assert.Contains("Local Logs", ex.Message);
        Assert.Contains("Archived Logs", ex.Message);
    }

    [Fact]
    public async Task A_configured_but_unusable_location_is_refused_distinctly_from_an_unknown_one()
    {
        // Two different problems: the caller mistyped, or the config needs fixing. One message each.
        using var env = new LogEnvironment(extraLocation: ("Broken", ""));

        var unusable = await Assert.ThrowsAsync<ArgumentException>(
            () => env.Service.PrepareAsync("Checkout", "Checkout", From, To, new[] { "Broken" }));
        var unknown = await Assert.ThrowsAsync<ArgumentException>(
            () => env.Service.PrepareAsync("Checkout", "Checkout", From, To, new[] { "Nope" }));

        Assert.Contains("is configured, but its path cannot be used", unusable.Message);
        Assert.Contains("Unknown location", unknown.Message);
        Assert.NotEqual(unusable.Message, unknown.Message);
    }

    [Fact]
    public async Task A_prepared_table_reports_which_locations_it_actually_read()
    {
        // The caller chooses the scope now, so a result whose population it cannot see is exactly what
        // the argument was added to prevent.
        using var env = WithCheckoutLog();

        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        Assert.Equal(new[] { "Local Logs" }, prepared.Locations);
    }

    [Fact]
    public async Task Naming_a_location_twice_reads_it_once()
    {
        // A harmless mistake, not an ambiguous one — but it must not double-count the files.
        using var env = WithCheckoutLog();

        var once = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, new[] { "Local Logs" });
        var twice = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, new[] { "Local Logs", "local logs" });

        Assert.Equal(once.Rows, twice.Rows);
        Assert.Equal(new[] { "Local Logs" }, twice.Locations);
    }

    [Fact]
    public async Task Listing_log_files_requires_locations_too()
    {
        // The other walking tool. Easy to miss precisely because it looks like a catalogue call, and
        // it is the one an agent reaches for first.
        using var env = WithCheckoutLog();

        await Assert.ThrowsAsync<ArgumentException>(
            () => env.Service.ListLogFilesAsync("Checkout", null));
    }

    [Fact]
    public async Task Get_template_resolves_inherited_columns()
    {
        // The reason this tool exists. Checkout.yaml names two columns; a caller that wrote SQL
        // from the file alone would miss the eight it inherits, in a query that parses fine.
        using var env = new LogEnvironment();

        var detail = await env.Service.GetTemplateAsync("Checkout");

        Assert.Equal("WebsiteBase", detail.Inherits);
        Assert.Equal(
            new[] { "DateTime", "Guid", "IP", "Account Number", "User ID", "Type", "JobSeq", "StoreNumber", "Action", "Detail" },
            detail.Columns);
    }

    [Fact]
    public async Task An_unknown_template_is_refused_by_name()
    {
        using var env = new LogEnvironment();

        var ex = await Assert.ThrowsAsync<UnknownTemplateException>(() => env.Service.GetTemplateAsync("Nope"));
        Assert.Contains("Nope", ex.Message);
        Assert.Contains("list_templates", ex.Message);
    }

    [Fact]
    public async Task Log_files_are_found_in_a_location_with_no_name_pattern()
    {
        // The real local location has no namePattern. DiscoverLogFileNamesAsync would return
        // nothing here, and an agent would read that as "there are no logs".
        using var env = WithCheckoutLog();

        var result = await env.Service.ListLogFilesAsync("Checkout", LogEnvironment.LocalOnly);

        Assert.Equal(LogNameDiscovery.MethodHeuristic, result.Method);
        Assert.Equal("Checkout", Assert.Single(result.Files).Name);
        Assert.Contains("prefix", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ prepare

    [Fact]
    public async Task Preparing_ingests_the_rows_and_reports_the_real_columns()
    {
        using var env = WithCheckoutLog();

        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        Assert.Equal(3, prepared.Rows);
        Assert.StartsWith("logs_", prepared.Handle);

        // Declared columns, then provenance. The authoritative list for writing SQL.
        Assert.Contains("Action", prepared.Columns);
        Assert.Contains("SourceFile", prepared.Columns);
        Assert.Contains("SourcePath", prepared.Columns);
    }

    [Fact]
    public async Task A_date_range_that_excludes_the_file_yields_no_rows_and_explains_why()
    {
        // Files are matched on LastWriteTime, not on the date in the name. That trips people up, so
        // an empty result says so rather than leaving the caller to guess.
        using var env = WithCheckoutLog();

        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", "2026-01-01", "2026-01-02", LogEnvironment.LocalOnly);

        Assert.Equal(0, prepared.Rows);
        Assert.Contains("LastWriteTime", prepared.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_malformed_date_is_refused_before_anything_is_read()
    {
        using var env = WithCheckoutLog();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => env.Service.PrepareAsync("Checkout", "Checkout", "21/08/2026", To, LogEnvironment.LocalOnly));

        Assert.Contains("yyyy-MM-dd", ex.Message);
    }

    [Fact]
    public async Task An_end_date_before_the_start_date_is_refused()
    {
        using var env = WithCheckoutLog();

        await Assert.ThrowsAsync<ArgumentException>(
            () => env.Service.PrepareAsync("Checkout", "Checkout", "2026-08-21", "2026-08-01", LogEnvironment.LocalOnly));
    }

    [Fact]
    public async Task A_second_prepare_does_not_destroy_the_first_ones_rows()
    {
        // THE correctness test, and the reason DbLogService's table name stopped being a constant.
        // Against one shared table the first handle's rows are silently replaced, and the query that
        // follows returns real rows from the wrong log with no error to notice.
        using var env = WithCheckoutLog();
        env.WriteLog("AccountUI.20260821.txt", Day,
            LogEnvironment.Line("20260821130000", "Customer", "ProfileSaved", "ok"));

        var checkout = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);
        var account = await env.Service.PrepareAsync("AccountUI", "Checkout", From, To, LogEnvironment.LocalOnly);

        Assert.NotEqual(checkout.Handle, account.Handle);
        Assert.Equal(3, checkout.Rows);
        Assert.Equal(1, account.Rows);

        // Going back to the first handle after preparing the second is the case that used to break.
        var back = await env.Service.QueryAsync(checkout.Handle, sql: null, terms: null, page: 0, pageSize: 50);
        Assert.Equal(3, back.MatchedTotal);

        var second = await env.Service.QueryAsync(account.Handle, sql: null, terms: null, page: 0, pageSize: 50);
        Assert.Equal(1, second.MatchedTotal);
    }

    // ------------------------------------------------------------------ query

    [Fact]
    public async Task Terms_match_any_column()
    {
        using var env = WithCheckoutLog();
        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        var result = await env.Service.QueryAsync(prepared.Handle, sql: null, terms: new[] { "isOnCreditHold" }, page: 0, pageSize: 50);

        Assert.Equal(2, result.MatchedTotal);
        Assert.Equal("terms", result.Mode);
    }

    [Fact]
    public async Task Raw_sql_runs_against_the_handles_table()
    {
        using var env = WithCheckoutLog();
        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        var result = await env.Service.QueryAsync(
            prepared.Handle,
            sql: $"SELECT * FROM {prepared.Handle} WHERE [Type] = 'Employee'",
            terms: null, page: 0, pageSize: 50);

        Assert.Equal(1, result.MatchedTotal);
        Assert.Equal("sql", result.Mode);
        Assert.Equal("Employee", Assert.Single(result.Rows)["Type"]);
    }

    [Fact]
    public async Task Broken_sql_comes_back_as_the_sqlite_error_because_the_query_was_the_callers()
    {
        // The parse error is the single most useful thing to return here — the caller wrote the SQL
        // and is the only one who can fix it.
        using var env = WithCheckoutLog();
        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => env.Service.QueryAsync(
            prepared.Handle, sql: $"SELECT [NoSuchColumn] FROM {prepared.Handle}", terms: null, page: 0, pageSize: 10));

        Assert.Contains("NoSuchColumn", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passing_both_sql_and_terms_is_refused_rather_than_resolved()
    {
        // Silently ignoring one would make the caller responsible for knowing which — worse than an
        // error, because the wrong answer looks like a right one.
        using var env = WithCheckoutLog();
        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        await Assert.ThrowsAsync<ArgumentException>(() => env.Service.QueryAsync(
            prepared.Handle, sql: "SELECT 1", terms: new[] { "x" }, page: 0, pageSize: 10));
    }

    [Fact]
    public async Task A_page_size_over_the_ceiling_is_clamped_and_the_caller_is_told()
    {
        using var env = WithCheckoutLog();
        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        var result = await env.Service.QueryAsync(prepared.Handle, null, null, 0, RowCap.Max + 500);

        Assert.True(result.Capped);
        Assert.Equal(RowCap.Max, result.PageSize);
    }

    [Fact]
    public async Task Paging_returns_different_rows_without_changing_the_total()
    {
        using var env = WithCheckoutLog();
        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        var first = await env.Service.QueryAsync(prepared.Handle, null, null, 0, 2);
        var second = await env.Service.QueryAsync(prepared.Handle, null, null, 1, 2);

        Assert.Equal(3, first.MatchedTotal);
        Assert.Equal(3, second.MatchedTotal);
        Assert.Equal(2, first.Returned);
        Assert.Equal(1, second.Returned);
    }

    [Fact]
    public async Task Every_result_carrying_log_content_says_the_content_is_untrusted()
    {
        // Log rows hold text typed by website users. This is the only place an agent can be told.
        using var env = WithCheckoutLog();
        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        var query = await env.Service.QueryAsync(prepared.Handle, null, null, 0, 10);
        var describe = await env.Service.DescribeColumnsAsync(prepared.Handle, 5);

        Assert.Contains("instructions", query.UntrustedContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("instructions", describe.UntrustedContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_handle_from_nowhere_is_refused()
    {
        using var env = WithCheckoutLog();

        await Assert.ThrowsAsync<UnknownHandleException>(
            () => env.Service.QueryAsync("logs_notarealhandle", null, null, 0, 10));
    }

    // ------------------------------------------------------------------ describe / split

    [Fact]
    public async Task Describe_columns_reports_the_real_distribution()
    {
        // What makes the difference between a filter that is correct and one that merely runs.
        using var env = WithCheckoutLog();
        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        var result = await env.Service.DescribeColumnsAsync(prepared.Handle, topValues: 5);

        Assert.Equal(3, result.Rows);

        var type = result.Columns.Single(c => c.Column == "Type");
        Assert.Equal(2, type.DistinctValues);
        Assert.Equal("Customer", type.TopValues[0].Value);
        Assert.Equal(2, type.TopValues[0].Count);

        // SourcePath is deliberately not profiled: its distribution is SourceFile's, at length.
        Assert.DoesNotContain(result.Columns, c => c.Column == "SourcePath");
    }

    [Fact]
    public async Task Split_groups_count_rows_per_source_file()
    {
        using var env = WithCheckoutLog();
        env.WriteLog("Checkout.20260820.txt", Day,
            LogEnvironment.Line("20260820090000", "Customer", "isOnCreditHold", "False"));

        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);
        var groups = await env.Service.SplitGroupsAsync(prepared.Handle, "File");

        Assert.Equal(2, groups.Groups.Count);
        Assert.Equal(4, groups.Groups.Sum(g => g.Count));
    }

    [Fact]
    public async Task Split_groups_refuses_a_mode_that_is_not_a_split_column()
    {
        using var env = WithCheckoutLog();
        var prepared = await env.Service.PrepareAsync("Checkout", "Checkout", From, To, LogEnvironment.LocalOnly);

        await Assert.ThrowsAsync<ArgumentException>(() => env.Service.SplitGroupsAsync(prepared.Handle, "Type"));
    }

    // ------------------------------------------------------------------ saved queries

    [Fact]
    public async Task A_saved_query_round_trips_and_is_stamped_as_agent_written()
    {
        // These land in a file the dev reads and reuses, beside queries they wrote themselves.
        // Being able to tell the two apart at a glance costs one line.
        using var env = new LogEnvironment();

        var saved = await env.Service.SaveQueryAsync(
            "Credit hold checks", "SELECT * FROM logs WHERE [Action] = 'isOnCreditHold'", "Checkout", "Finds credit hold checks", "Checkout");

        Assert.Equal("Credit hold checks", saved.Name);
        Assert.Contains("agent", saved.Description!, StringComparison.OrdinalIgnoreCase);

        var all = await env.Service.ListSavedQueriesAsync();
        Assert.Equal("Credit hold checks", Assert.Single(all.Queries).Name);
        Assert.Equal("Checkout", Assert.Single(all.Groups));
    }

    [Fact]
    public async Task A_duplicate_name_in_a_group_is_refused_rather_than_overwriting()
    {
        using var env = new LogEnvironment();
        await env.Service.SaveQueryAsync("Same", "SELECT 1", "G", null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.Service.SaveQueryAsync("Same", "SELECT 2", "G", null, null));
    }

    [Fact]
    public async Task A_query_with_no_name_or_no_sql_is_refused()
    {
        using var env = new LogEnvironment();

        await Assert.ThrowsAsync<ArgumentException>(() => env.Service.SaveQueryAsync("", "SELECT 1", null, null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Service.SaveQueryAsync("n", "  ", null, null, null));
    }
}
