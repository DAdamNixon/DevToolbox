using System.CommandLine;
using System.Globalization;
using DevToolbox.Mcp.Core;
using DevToolbox.Services.Services;

namespace DevToolbox.Cli;

/// <summary>
/// <c>logs</c>: the Log Viewer from a terminal, on the MCP server's <see cref="LogViewerService"/>.
/// <para>
/// So a search here reads exactly what an agent's would: only configured locations, only the ones
/// named, file names that cannot walk out of a location, a read-only connection for queries, a
/// capped page and a time limit. Each run loads into its own scratch database, deleted on the way
/// out — the Log Viewer's logs.db is never touched, so this is safe beside the running app.
/// </para>
/// </summary>
internal static class LogsCommands
{
    public static Command Build()
    {
        var logs = new Command("logs", "Search logs with the Log Viewer's templates and locations.");

        var json = Cli.JsonOption();
        var locations = new Command("locations", "The configured log locations.") { json };
        locations.SetAction((r, _) => WithServiceAsync(s => LocationsAsync(s, r.GetValue(json))));
        logs.Subcommands.Add(locations);

        var templatesJson = Cli.JsonOption();
        var templates = new Command("templates", "The configured log templates.") { templatesJson };
        templates.SetAction((r, _) => WithServiceAsync(s => TemplatesAsync(s, r.GetValue(templatesJson))));
        logs.Subcommands.Add(templates);

        logs.Subcommands.Add(BuildFiles());
        logs.Subcommands.Add(BuildSearch());
        return logs;
    }

    private static Option<string?> TemplateOption() =>
        new("--template", "-t") { Description = "Template name. Default: the locations' own default template, when they agree on one." };

    private static Option<string[]> LocationOption() =>
        new("--location", "-l") { Description = "Location to read, by name; repeat for more. Default: the only one, if only one is configured.", AllowMultipleArgumentsPerToken = true };

    private static Command BuildFiles()
    {
        var template = TemplateOption();
        var location = LocationOption();
        var json = Cli.JsonOption();

        var files = new Command("files", "The log names found in the named locations, most files first.") { template, location, json };
        files.SetAction((r, ct) => WithServiceAsync(async s =>
        {
            var (chosenLocations, chosenTemplate, error) = await ResolveAsync(s, r.GetValue(location), r.GetValue(template));
            if (error is not null) return Output.Fail(error);

            var result = await s.ListLogFilesAsync(chosenTemplate!, chosenLocations, ct);
            if (r.GetValue(json)) { Output.WriteJson(result); return 0; }

            Output.WriteTable(["Log", "Files"], result.Files.Select(f => (IReadOnlyList<string>)[f.Name, f.FileCount.ToString(CultureInfo.InvariantCulture)]));
            return 0;
        }));

        return files;
    }

    private static Command BuildSearch()
    {
        var logFile = new Argument<string>("log") { Description = "Log file name or prefix, as `logs files` lists it." };
        var template = TemplateOption();
        var location = LocationOption();
        var from = new Option<DateOnly?>("--from") { Description = "First day, yyyy-MM-dd, by the file's last-modified date. Default: today." };
        var to = new Option<DateOnly?>("--to") { Description = "Last day, yyyy-MM-dd. Default: the --from day." };
        var terms = new Option<string[]>("--terms") { Description = "Keep rows where any term appears in any column.", AllowMultipleArgumentsPerToken = true };
        var sql = new Option<string?>("--sql") { Description = "A SQLite SELECT instead of --terms. The table is named in the output as {table}; write {table} and it is filled in." };
        var limit = new Option<int>("--limit", "-n") { Description = "Rows per page, at most 200.", DefaultValueFactory = _ => 50 };
        var page = new Option<int>("--page") { Description = "Zero-based page.", DefaultValueFactory = _ => 0 };
        var json = Cli.JsonOption();

        var search = new Command("search", "Load one log over a date range and show the matching rows.")
        {
            logFile, template, location, from, to, terms, sql, limit, page, json,
        };

        search.SetAction((r, ct) => WithServiceAsync(async s =>
        {
            var (chosenLocations, chosenTemplate, error) = await ResolveAsync(s, r.GetValue(location), r.GetValue(template));
            if (error is not null) return Output.Fail(error);

            var start = r.GetValue(from) ?? DateOnly.FromDateTime(DateTime.Today);
            var end = r.GetValue(to) ?? start;

            var prepared = await s.PrepareAsync(
                r.GetValue(logFile)!, chosenTemplate!, start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), chosenLocations, ct);

            if (!prepared.Complete)
            {
                foreach (var skipped in prepared.NotIngested)
                {
                    Console.Error.WriteLine($"Not read: {skipped.FileName} ({skipped.LocationName}) — {skipped.Reason}");
                }
            }

            var query = r.GetValue(sql)?.Replace("{table}", prepared.Handle);
            var wanted = r.GetValue(terms) is { Length: > 0 } t ? t : null;

            var result = await s.QueryAsync(prepared.Handle, query, query is null ? wanted : null, r.GetValue(page), r.GetValue(limit), ct);

            if (r.GetValue(json))
            {
                Output.WriteJson(new { prepared, result });
                return 0;
            }

            var columns = result.Rows.Count > 0
                ? result.Rows[0].Keys.Where(k => k != "SourcePath").ToList()
                : prepared.Columns.ToList();

            Output.WriteTable(columns, result.Rows.Select(row => (IReadOnlyList<string>)columns.Select(c => row.TryGetValue(c, out var v) ? v : "").ToList()));

            var more = (result.Page + 1) * result.PageSize < result.MatchedTotal;
            Console.Error.WriteLine(
                $"{result.Returned} of {result.MatchedTotal} matching rows (page {result.Page}), from {prepared.Rows} loaded" +
                (more ? $". Next page: --page {result.Page + 1}" : "."));
            return 0;
        }));

        return search;
    }

    /// <summary>
    /// Fills in what can be inferred: the only location when there is one, and the template the
    /// named locations all default to. Anything ambiguous is an error that lists the choices.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Locations, string? Template, string? Error)> ResolveAsync(
        LogViewerService service, string[]? requestedLocations, string? requestedTemplate)
    {
        var configured = (await service.GetLocationsAsync()).Locations;

        IReadOnlyList<string> locations = requestedLocations is { Length: > 0 }
            ? requestedLocations
            : configured.Count == 1 ? [configured[0].Name] : [];

        if (locations.Count == 0)
        {
            return ([], null, "Name a location with --location. Configured: " + string.Join(", ", configured.Select(l => l.Name)) + ".");
        }

        if (requestedTemplate is not null) return (locations, requestedTemplate, null);

        var defaults = configured
            .Where(l => locations.Contains(l.Name, StringComparer.OrdinalIgnoreCase))
            .Select(l => l.DefaultTemplate)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (defaults is [{ } only]) return (locations, only, null);

        var names = (await service.GetTemplatesAsync()).Select(t => t.Name);
        return (locations, null, "Name a template with --template. Configured: " + string.Join(", ", names) + ".");
    }

    private static async Task<int> LocationsAsync(LogViewerService service, bool json)
    {
        var result = await service.GetLocationsAsync();
        if (json) { Output.WriteJson(result); return 0; }

        Output.WriteTable(["Location", "Path", "Default template"],
            result.Locations.Select(l => (IReadOnlyList<string>)[l.Name, l.Path, l.DefaultTemplate ?? ""]), widthCap: 0);

        foreach (var refused in result.Refused) Console.Error.WriteLine($"Unusable: {refused.Name} — {refused.Reason}");
        return 0;
    }

    private static async Task<int> TemplatesAsync(LogViewerService service, bool json)
    {
        var templates = await service.GetTemplatesAsync();
        if (json) { Output.WriteJson(templates); return 0; }

        Output.WriteTable(["Template", "File"], templates.Select(t => (IReadOnlyList<string>)[t.Name, t.File]));
        return 0;
    }

    /// <summary>
    /// A LogViewerService over a scratch database of this process's own, the way the MCP server
    /// makes one, and the database deleted afterwards. Refusals from the service — an unknown
    /// location, a name shaped like a path — are its own words, printed as they are.
    /// </summary>
    private static async Task<int> WithServiceAsync(Func<LogViewerService, Task<int>> run)
    {
        var databasePath = Path.Combine(McpLogDatabase.Folder, McpLogDatabase.FileNameFor(Environment.ProcessId, DateTime.UtcNow));
        using var ownership = McpLogDatabase.AcquireOwnership(databasePath);
        McpLogDatabase.Sweep(keep: Path.GetFileName(databasePath));

        try
        {
            var yaml = new McpYamlStorage();
            var service = new LogViewerService(
                yaml,
                new SqliteLogStorageService(databasePath),
                new SqliteLogStorageService(databasePath, readOnly: true),
                new SavedQueryService(yaml),
                new PreparedTables(),
                new ColumnProfiler(databasePath));

            return await run(service);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnknownTemplateException)
        {
            // The service's messages are written for an agent and name its tools; here the same
            // advice is a command.
            return Output.Fail(ex.Message
                .Replace("list_locations", "`logs locations`")
                .Replace("list_log_files", "`logs files`")
                .Replace("list_templates", "`logs templates`")
                .Replace("prepare_table", "`logs search`"));
        }
        finally
        {
            ownership.Dispose();
            McpLogDatabase.Delete(databasePath);
        }
    }
}
