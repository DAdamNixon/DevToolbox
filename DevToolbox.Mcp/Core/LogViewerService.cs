using System.Globalization;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.Mcp.Core;

/// <summary>
/// Everything the tools do, in one place. The tool classes are deliberately thin wrappers over
/// this — if a tool body grows past a few lines, something is missing here.
/// <para>
/// The composition is the guardrail, so it is worth reading as one:
/// </para>
/// <list type="bullet">
/// <item><b>Two storage instances, not one.</b> <c>_writeStorage</c> exists solely so an ingest can
/// create its table; <c>_readStorage</c> is opened <c>Mode=ReadOnly</c> and its writing members
/// throw. Every query goes through the read-only one, so the path an agent's arguments travel has
/// no write capability in it at all — not "unused", absent.</item>
/// <item><b>A new <see cref="DbLogService"/> per prepare.</b> Each is constructed with its own
/// table name, which is what stops a second prepare destroying the first one's rows.</item>
/// <item><b>No <c>LogConfigService</c> anywhere.</b> It owns the template and location <em>writes</em>.
/// Not registering it means there is no code path from this server to editing how logs are parsed,
/// which is guardrail #1 rather than an oversight.</item>
/// </list>
/// </summary>
public sealed class LogViewerService
{
    private readonly IYamlStorageService _yaml;
    private readonly ILogStorageService _writeStorage;
    private readonly ILogStorageService _readStorage;
    private readonly ISavedQueryService _savedQueries;
    private readonly PreparedTables _prepared;
    private readonly ColumnProfiler _profiler;
    private readonly DbLogService _reader;

    public LogViewerService(
        IYamlStorageService yaml,
        ILogStorageService writeStorage,
        ILogStorageService readStorage,
        ISavedQueryService savedQueries,
        PreparedTables prepared,
        ColumnProfiler profiler)
    {
        _yaml = yaml;
        _writeStorage = writeStorage;
        _readStorage = readStorage;
        _savedQueries = savedQueries;
        _prepared = prepared;
        _profiler = profiler;

        // Queries only. Its own table name is never used — every query member takes the table
        // explicitly — but it is bound to the read-only storage so it could not write if asked.
        _reader = new DbLogService(_yaml, _readStorage);
    }

    private const string PolicyDescription =
        "Every location configured in log_paths.yaml is readable, local and network alike. A location is " +
        "listed under 'refused' only when its configured path is unusable — blank, or not fully qualified. " +
        "Reading is NOT implicit: list_log_files and prepare_table both require a 'locations' argument " +
        "naming which of these to walk, because reading them all can mean an SMB walk across production " +
        "log shares. Pass the names exactly as they appear here.";

    // ---------------------------------------------------------------- locations

    /// <summary>Configured locations, split into what this server may read and what it refused.</summary>
    internal async Task<LocationsResult> GetLocationsAsync()
    {
        var all = await _reader.GetLogLocationsAsync();

        var admitted = new List<LocationInfo>();
        var refused = new List<RefusedLocationInfo>();

        foreach (var location in all)
        {
            var reason = LocationPolicy.Refuse(location);
            if (reason is null)
                admitted.Add(new LocationInfo(location.Name, location.Path, !string.IsNullOrWhiteSpace(location.NamePattern)));
            else
                refused.Add(new RefusedLocationInfo(location.Name, location.Path, reason));
        }

        return new LocationsResult(admitted, refused, PolicyDescription);
    }

    /// <summary>
    /// The locations this call may walk — the ones the caller named, and only those.
    /// <para>
    /// Resolution takes the full configured list rather than a filtered one so that a name which
    /// exists but is unusable is answered as such. See <see cref="LocationSelection"/> for why the
    /// argument is required and why an unknown name is refused instead of skipped.
    /// </para>
    /// </summary>
    private async Task<List<LogLocation>> SelectedLocationsAsync(IReadOnlyList<string>? requested)
    {
        var all = await _reader.GetLogLocationsAsync();
        return LocationSelection.Resolve(requested, all);
    }

    // ---------------------------------------------------------------- templates

    internal async Task<IReadOnlyList<TemplateSummary>> GetTemplatesAsync()
    {
        var entries = await _reader.GetAvailableLogFileTemplatesAsync();
        return entries.Select(e => new TemplateSummary(e.Name, e.File)).ToList();
    }

    /// <summary>
    /// One template with its columns already resolved through <c>inherits</c>.
    /// <para>
    /// The resolution is the point. A template that inherits another declares only its own extra
    /// columns, so the raw file is not the column list — and a caller that went on to write SQL
    /// against the raw list would name columns that are not there, in a query that parses.
    /// </para>
    /// </summary>
    internal async Task<TemplateDetail> GetTemplateAsync(string templateName)
    {
        var entry = (await _reader.GetAvailableLogFileTemplatesAsync())
            .FirstOrDefault(t => string.Equals(t.Name, templateName, StringComparison.Ordinal));

        if (entry is null)
            throw new UnknownTemplateException(
                $"Unknown template '{templateName}'. Call list_templates for the names this server knows.");

        var template = await _reader.LoadTemplateAsync(entry.File)
            ?? throw new UnknownTemplateException(
                $"Template '{templateName}' is indexed but its file '{entry.File}' could not be loaded.");

        var columns = await _reader.GetEffectiveColumnsAsync(templateName);
        var sort = await _reader.GetEffectiveSortAsync(templateName);

        return new TemplateDetail(
            entry.Name,
            entry.File,
            template.Extension,
            template.Delimiter,
            template.Inherits,
            columns,
            sort.Select(s => $"{s.Column} {s.Direction}").ToList(),
            "These are the template's declared columns, inheritance applied. A prepared table also " +
            $"carries overflow columns ({LogOverflowColumns.Name(1)}, {LogOverflowColumns.Name(2)}, …) for fields a line " +
            $"holds beyond this list, plus provenance columns ({string.Join(", ", LogProvenanceColumns.All)}). " +
            "How many overflow columns exist depends on the files actually read, so the authoritative " +
            "column list for writing SQL is the one prepare_table returns.");
    }

    // ---------------------------------------------------------------- log files

    internal async Task<LogFilesResult> ListLogFilesAsync(
        string templateName,
        IReadOnlyList<string>? locationNames,
        CancellationToken cancellationToken = default)
    {
        var detail = await GetTemplateAsync(templateName);
        var locations = await SelectedLocationsAsync(locationNames);

        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var methods = new HashSet<string>(StringComparer.Ordinal);

        foreach (var location in locations)
        {
            var (names, discoveryMethod) = LogNameDiscovery.Discover(location, detail.Extension, cancellationToken);
            methods.Add(discoveryMethod);

            foreach (var found in names)
            {
                totals.TryGetValue(found.Name, out var running);
                totals[found.Name] = running + found.FileCount;
            }
        }

        var files = totals
            .Select(kv => new DiscoveredName(kv.Key, kv.Value))
            .OrderByDescending(n => n.FileCount)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var method = methods.Count switch
        {
            0 => LogNameDiscovery.MethodPattern,
            1 => methods.First(),
            _ => "mixed"
        };

        var note = method == LogNameDiscovery.MethodPattern
            ? "Names came from each location's configured namePattern."
            : "At least one location has no namePattern in log_paths.yaml, so names there were derived by " +
              "stripping a trailing date stamp from the file name. Treat them as a good guess rather than " +
              "configuration. prepare_table matches on a name PREFIX, so a partial name works.";

        return new LogFilesResult(files, method, locations.Select(l => l.Name).ToList(), note);
    }

    // ---------------------------------------------------------------- prepare

    /// <summary>
    /// Reads matching files into a fresh table and returns a handle for it.
    /// <para>
    /// This is the one tool that is not purely a read: it writes to this process's own scratch
    /// database. It writes nothing to the logs and nothing to the config, and the database is
    /// deleted when the session ends.
    /// </para>
    /// </summary>
    internal async Task<PrepareResult> PrepareAsync(
        string logFile,
        string templateName,
        string startDate,
        string endDate,
        IReadOnlyList<string>? locationNames,
        CancellationToken cancellationToken = default)
    {
        // Before anything touches the filesystem. The value is interpolated into a search pattern
        // downstream, where a directory separator would move the search out of the admitted
        // location entirely — see LogFileNamePolicy for what that allowed and how it was measured.
        var refusal = LogFileNamePolicy.Refuse(logFile);
        if (refusal is not null)
            throw new ArgumentException(refusal, nameof(logFile));

        var start = ParseDate(startDate, nameof(startDate));
        var end = ParseDate(endDate, nameof(endDate));

        if (end < start)
            throw new ArgumentException($"endDate ({endDate}) is before startDate ({startDate}).", nameof(endDate));

        // Confirms the template exists before any file walking, so a typo fails immediately rather
        // than after a directory scan.
        var detail = await GetTemplateAsync(templateName);

        // Refuses before the ingest rather than after it: an unknown or unusable name must not cost
        // a directory walk, and must not come back as a small-but-plausible result.
        var locations = await SelectedLocationsAsync(locationNames);

        var handle = PreparedTables.NewHandle();

        // Its own table name: this is what stops a second prepare destroying this one's rows.
        var ingest = new DbLogService(_yaml, _writeStorage, handle);

        // progress is null deliberately. The UI passes an IProgress that drives a progress bar;
        // here there is nowhere for it to go, and anything that wrote it to the console would be
        // writing on the JSON-RPC wire.
        var table = await ingest.PrepareLogTableAsync(
            logFile, locations, start, end, templateName, progress: null, cancellationToken: cancellationToken);

        var rows = await _reader.CountLogEntriesAsync(table, criteria: null, split: null, cancellationToken: cancellationToken);
        var columns = await ActualColumnsAsync(table, cancellationToken);

        _prepared.Register(new PreparedTable(handle, logFile, templateName, columns, rows, DateTime.UtcNow));

        var note = rows == 0
            ? "No rows. Either no file matched the name prefix in that date range, or the files matched but were empty. " +
              "Files are matched by LastWriteTime, not by the date in the file name."
            : $"Query with query_entries using handle '{handle}'. In raw SQL, the table is named {handle}. " +
              ResultDocs.UntrustedContentWarning;

        return new PrepareResult(
            handle, logFile, detail.Name,
            start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // Echoed back because the caller now chooses the scope, and a result whose population
            // the caller cannot see is the thing this whole argument exists to prevent.
            locations.Select(l => l.Name).ToList(),
            rows, columns, note);
    }

    /// <summary>The columns the table really has — template, overflow and provenance together.</summary>
    private async Task<List<string>> ActualColumnsAsync(string table, CancellationToken cancellationToken)
    {
        var (_, profiles) = await _profiler.ProfileAsync(table, topValues: 0, cancellationToken);
        var columns = profiles.Select(p => p.Column).ToList();

        // ProfileAsync omits SourcePath by design; the column list must not, because a caller
        // writing SQL is entitled to know every column that exists.
        if (!columns.Contains(LogProvenanceColumns.SourcePath, StringComparer.OrdinalIgnoreCase))
            columns.Add(LogProvenanceColumns.SourcePath);

        return columns;
    }

    private static DateTime ParseDate(string value, string parameterName)
    {
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        throw new ArgumentException($"{parameterName} must be yyyy-MM-dd (got '{value}').", parameterName);
    }

    // ---------------------------------------------------------------- query

    internal async Task<QueryResult> QueryAsync(
        string handle,
        string? sql,
        IReadOnlyList<string>? terms,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var prepared = _prepared.Resolve(handle);

        if (!string.IsNullOrWhiteSpace(sql) && terms is { Count: > 0 })
            throw new ArgumentException(
                "Pass either sql or terms, not both. Combining them silently ignores one, and which one is " +
                "not something a caller should have to know.");

        var size = RowCap.Clamp(pageSize);
        var capped = pageSize.HasValue && pageSize.Value > RowCap.Max;
        var pageNumber = Math.Max(0, page ?? 0);

        var criteria = BuildCriteria(sql, terms);
        var mode = !string.IsNullOrWhiteSpace(sql) ? "sql" : terms is { Count: > 0 } ? "terms" : "all";

        var (rows, total) = await QueryDeadline.RunAsync(async token =>
        {
            var rowsPage = await _reader.QueryLogPageAsync(
                prepared.Handle, prepared.Template, pageNumber, size,
                sortColumns: null, criteria: criteria, split: null, cancellationToken: token);

            var count = await _reader.CountLogEntriesAsync(prepared.Handle, criteria, split: null, cancellationToken: token);
            return (rowsPage, count);
        }, cancellationToken: cancellationToken);

        return new QueryResult(
            rows.Select(r => (IReadOnlyDictionary<string, string>)r).ToList(),
            rows.Count, total, pageNumber, size, capped, mode,
            ResultDocs.UntrustedContentWarning);
    }

    /// <summary>
    /// Turns the two query shapes into the one type the storage layer understands.
    /// <para>
    /// <c>UseAdvanced</c> routes the string into <c>LogQuery.RawQuery</c>, which the storage layer
    /// runs as a full SELECT wrapped for paging. Terms take the other branch and are bound as
    /// parameters by <c>LogCriteriaTranslator</c> — a keyword group matches when ANY of its terms
    /// appears in ANY column.
    /// </para>
    /// </summary>
    private static LogSearchCriteria? BuildCriteria(string? sql, IReadOnlyList<string>? terms)
    {
        if (!string.IsNullOrWhiteSpace(sql))
            return new LogSearchCriteria { UseAdvanced = true, AdvancedExpression = sql };

        var wanted = terms?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (wanted is not { Count: > 0 }) return null;

        return new LogSearchCriteria
        {
            Groups = new List<KeywordGroup> { new() { Gate = "AND", Terms = wanted } }
        };
    }

    // ---------------------------------------------------------------- describe

    internal async Task<DescribeColumnsResult> DescribeColumnsAsync(
        string handle, int? topValues, CancellationToken cancellationToken = default)
    {
        var prepared = _prepared.Resolve(handle);
        var top = Math.Clamp(topValues ?? 5, 1, 25);

        var (rows, columns) = await QueryDeadline.RunAsync(
            token => _profiler.ProfileAsync(prepared.Handle, top, token),
            cancellationToken: cancellationToken);

        return new DescribeColumnsResult(
            prepared.Handle, rows, columns,
            "distinctValues and nonEmpty count every row in the table; topValues are the most frequent " +
            $"non-empty values, at most {top} per column. SourcePath is omitted — its distribution is " +
            "SourceFile's, at far greater length.",
            ResultDocs.UntrustedContentWarning);
    }

    internal async Task<SplitGroupsResult> SplitGroupsAsync(
        string handle, string mode, CancellationToken cancellationToken = default)
    {
        var prepared = _prepared.Resolve(handle);

        if (!Enum.TryParse<LogSplitMode>(mode, ignoreCase: true, out var parsed) || parsed == LogSplitMode.None)
            throw new ArgumentException(
                $"mode must be 'Location' or 'File' (got '{mode}').", nameof(mode));

        var groups = await QueryDeadline.RunAsync(
            token => _reader.GetSplitGroupsAsync(prepared.Handle, parsed, criteria: null, cancellationToken: token),
            cancellationToken: cancellationToken);

        return new SplitGroupsResult(
            prepared.Handle, parsed.ToString(),
            groups.Select(g => new SplitGroupInfo(g.Value, g.Count)).ToList());
    }

    // ---------------------------------------------------------------- saved queries

    internal async Task<SavedQueriesResult> ListSavedQueriesAsync()
    {
        var all = await _savedQueries.GetAllAsync();
        var groups = await _savedQueries.GetGroupsAsync();

        return new SavedQueriesResult(all.Select(Describe).ToList(), groups);
    }

    /// <summary>
    /// The only write this server performs, and the only one it ever should.
    /// <para>
    /// The saved query is stamped in its description with the fact that an agent wrote it and when.
    /// These land in a file the dev reads and trusts, beside queries they wrote themselves; being
    /// able to tell the two apart at a glance costs one line and is worth it.
    /// </para>
    /// </summary>
    internal async Task<SavedQueryInfo> SaveQueryAsync(
        string name, string sql, string? group, string? description, string? template)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A query name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("Query SQL is required.", nameof(sql));

        var stamp = $"[saved by an agent {DateTime.Now:yyyy-MM-dd}]";
        var note = string.IsNullOrWhiteSpace(description) ? stamp : $"{description.Trim()} {stamp}";

        var saved = await _savedQueries.SaveAsync(new SavedQuery
        {
            Name = name.Trim(),
            Group = group?.Trim() ?? string.Empty,
            Sql = sql.Trim(),
            Description = note,
            Template = string.IsNullOrWhiteSpace(template) ? null : template.Trim()
        });

        return Describe(saved);
    }

    private static SavedQueryInfo Describe(SavedQuery q) => new(
        q.Id, q.Name, q.Group, q.Sql, q.Description, q.Template,
        q.UpdatedUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

/// <summary>
/// A template name the index does not carry. Its own type so the message — which names only the
/// caller's own argument — is returned verbatim rather than described.
/// </summary>
internal sealed class UnknownTemplateException : Exception
{
    internal UnknownTemplateException(string message) : base(message) { }
}
