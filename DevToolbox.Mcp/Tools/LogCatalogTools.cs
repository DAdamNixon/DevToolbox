using System.ComponentModel;
using DevToolbox.Mcp.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DevToolbox.Mcp.Tools;

/// <summary>
/// The four catalogue tools: what locations exist, what templates exist, what one template really
/// looks like, and what log files are actually there.
/// <para>
/// An agent walks down that progression, and each step only accepts identifiers the previous step
/// handed it. The bodies are thin on purpose — everything they do lives in
/// <see cref="LogViewerService"/>.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class LogCatalogTools
{
    private readonly LogViewerService _logs;
    private readonly ILogger<LogCatalogTools> _log;

    public LogCatalogTools(LogViewerService logs, ILogger<LogCatalogTools> log)
    {
        _logs = logs;
        _log = log;
    }

    [McpServerTool(Name = "list_locations", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Lists the log locations this server can read, and separately any it refused. Start here: the names " +
        "it returns are the ONLY accepted values for the required 'locations' argument on list_log_files and " +
        "prepare_table, and neither has a default. " +
        "Local and network locations are both readable. A location appears under 'refused' only when its " +
        "configured path is unusable (blank, or not fully qualified) — that is a broken config entry, not a " +
        "policy decision. " +
        "COST: naming a network location means an SMB directory walk on another machine — one configured " +
        "archive share measures 17 seconds across 238,000 files, and some locations are web servers serving " +
        "live traffic. Name the locations the question actually needs, not all of them.")]
    public Task<LocationsResult> ListLocations()
        => ToolErrors.GuardAsync(async () =>
        {
            var result = await _logs.GetLocationsAsync();

            // Per-call log: counts only. Never a path, never a row, never a query.
            _log.LogInformation("list_locations -> {Admitted} admitted, {Refused} refused.",
                result.Locations.Count, result.Refused.Count);

            return result;
        });

    [McpServerTool(Name = "list_templates", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Lists the log parsing templates configured on this machine, by name. A template says how a log line " +
        "is split into columns. Pass one of these names to get_template to see its columns, and to " +
        "prepare_table to parse with it. Choosing the wrong template does not fail — it produces rows whose " +
        "columns are misaligned, so confirm with get_template before relying on a result.")]
    public Task<IReadOnlyList<TemplateSummary>> ListTemplates()
        => ToolErrors.GuardAsync(async () =>
        {
            var result = await _logs.GetTemplatesAsync();
            _log.LogInformation("list_templates -> {Count} templates.", result.Count);
            return result;
        });

    [McpServerTool(Name = "get_template", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Describes one template: its delimiter, file extension, and its COLUMNS WITH INHERITANCE ALREADY " +
        "APPLIED. Read this before writing any SQL. A template that inherits another declares only its own " +
        "extra columns, so its file is not its column list — a query written from the raw file names columns " +
        "that do not exist, and still parses. Note that a prepared table has more columns than a template " +
        "declares: overflow columns for fields beyond the template, plus provenance columns. The definitive " +
        "list for a given ingest is the one prepare_table returns.")]
    public Task<TemplateDetail> GetTemplate(
        [Description("Template name, exactly as list_templates reported it.")] string templateName)
        => ToolErrors.GuardAsync(async () =>
        {
            var result = await _logs.GetTemplateAsync(templateName);
            _log.LogInformation("get_template({Template}) -> {Count} columns.", templateName, result.Columns.Count);
            return result;
        });

    [McpServerTool(Name = "list_log_files", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Lists the distinct log names present in the locations you name, with how many files carry each " +
        "name, most numerous first. Log files are per-day, so one name usually covers many files. " +
        "The result reports HOW the names were derived: 'pattern' means a location's configured namePattern " +
        "produced them; 'heuristic' means the location has no pattern and names were derived by stripping a " +
        "trailing date stamp — a good guess, not configuration. Pass a name to prepare_table, which matches " +
        "on a prefix, so a partial name works.")]
    public Task<LogFilesResult> ListLogFiles(
        [Description("Template name — decides which file extension is searched for.")] string templateName,
        [Description(
            "REQUIRED. Which locations to search, by name, exactly as list_locations reports them. No default: " +
            "name the ones the question needs. An unknown name is refused with the list of known ones, never " +
            "skipped.")]
        // Nullable with a default, which makes the generated schema call it OPTIONAL — deliberately,
        // and against first instinct. A non-nullable parameter is marked required, and the SDK then
        // rejects a call that omits it before the body runs: the caller gets
        // "An error occurred invoking 'list_log_files'." and is told nothing at all. Measured over
        // stdio on 2026-09-03. That is the same failure this server already recorded once, when a
        // BCL Path.Combine message reached a caller as if it were authored here — a refusal that
        // holds by framework incidental is untestable as policy and teaches the caller nothing.
        // Optional in the schema, required in the body, refused in our own words.
        IReadOnlyList<string>? locations = null,
        CancellationToken cancellationToken = default)
        => ToolErrors.GuardAsync(async () =>
        {
            var result = await _logs.ListLogFilesAsync(templateName, locations, cancellationToken);
            _log.LogInformation("list_log_files({Template}, {Locations} locations) -> {Count} names via {Method}.",
                templateName, locations?.Count ?? 0, result.Files.Count, result.Method);
            return result;
        });

    [McpServerTool(Name = "check_log_name", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "ADVISORY. Checks whether a proposed NEW log file name would overlap one that already exists in the " +
        "locations you name, and reports every overlap in both directions. Call this before creating a log " +
        "file. " +
        "WHY IT MATTERS: prepare_table matches logFile on a PREFIX and its response never names the files it " +
        "actually read, so two flows can share one handle and a bare COUNT(*) sums both. That produced a real " +
        "wrong answer (93 reported, 75 true) on 2026-09-02. " +
        "Two directions, and they differ: 'existing-is-prefix-of-proposed' is worse, because creating the " +
        "file silently widens every query already written against the shorter existing name; " +
        "'proposed-is-prefix-of-existing' only misleads queries not yet written. 'atSeparator' false means " +
        "the overlap is mid-token (Checkout vs CheckoutLegacy) and easy to miss by eye — the prefix match " +
        "treats it identically. " +
        "THIS TOOL DOES NOT REFUSE ANYTHING. A collision may still be the right design once you know it is " +
        "there; grouping rules differ per log, so the call is yours. It reports, you decide.")]
    public Task<NameCheckResult> CheckLogName(
        [Description("The log file name you are proposing to create, e.g. 'Checkout.WithAccount.Modern'.")] string proposedName,
        [Description("Template name — decides which file extension is searched for. From list_templates.")] string templateName,
        [Description(
            "REQUIRED. Which locations to check, by name, exactly as list_locations reports them. No default. " +
            "Check the location the new log will actually be written to — that is where a collision matters. " +
            "An unknown name is refused with the list of known ones, never skipped.")]
        // Optional in the schema, required in the body — same reasoning as list_log_files above.
        IReadOnlyList<string>? locations = null,
        CancellationToken cancellationToken = default)
        => ToolErrors.GuardAsync(async () =>
        {
            // Reuses the catalogue read rather than a second discovery path, so this tool can never
            // disagree with what list_log_files reports. Its refusals (blank/unknown locations) are
            // therefore the ones the caller already knows.
            var existing = await _logs.ListLogFilesAsync(templateName, locations, cancellationToken);
            var collisions = LogNameCollision.Find(proposedName, existing.Files);

            var verdict = collisions.Count == 0
                ? "No overlap found in the locations checked."
                : $"{collisions.Count} existing name(s) overlap this one. Read the direction on each before deciding.";

            _log.LogInformation("check_log_name({Template}, {Locations} locations) -> {Count} collisions among {Names} names.",
                templateName, locations?.Count ?? 0, collisions.Count, existing.Files.Count);

            return new NameCheckResult(
                proposedName,
                collisions.Count == 0,
                collisions,
                existing.SearchedLocations,
                existing.Method,
                verdict,
                "Advisory only — nothing here blocks creating the file. A clean result is only as good as the " +
                "locations checked, and names were derived by '" + existing.Method + "'. If you proceed with an " +
                "overlap, every query over either name needs GROUP BY [SourceFile] to stay correct.");
        });
}
