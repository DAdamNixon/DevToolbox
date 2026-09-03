using System.ComponentModel;
using DevToolbox.Mcp.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DevToolbox.Mcp.Tools;

/// <summary>
/// The dev's saved Log Viewer queries — and the one write this server performs.
/// <para>
/// Reading them is the more valuable half, and it is easy to undersell: a saved query is a
/// question someone already worked out how to ask correctly against these logs. Reading the set
/// before composing new SQL is the cheapest way to inherit that.
/// </para>
/// <para>
/// There is deliberately no delete and no rename here, though <c>ISavedQueryService</c> offers
/// both. Guardrail #1 is the absence of a code path: an agent adding a query is recoverable and
/// visible; an agent removing or renaming one is neither.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class SavedQueryTools
{
    private readonly LogViewerService _logs;
    private readonly ILogger<SavedQueryTools> _log;

    public SavedQueryTools(LogViewerService logs, ILogger<SavedQueryTools> log)
    {
        _logs = logs;
        _log = log;
    }

    [McpServerTool(Name = "list_saved_queries", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Lists the SQL queries the developer has saved in the Log Viewer, with their groups. " +
        "Read these before composing your own: each one is a question somebody already worked out how to " +
        "ask correctly against these logs, including which columns actually hold what. " +
        "A query's 'template' field is a hint about what it was written against, not a restriction — the " +
        "SQL will run against any prepared table, it just may name columns that table does not have.")]
    public Task<SavedQueriesResult> ListSavedQueries()
        => ToolErrors.GuardAsync(async () =>
        {
            var result = await _logs.ListSavedQueriesAsync();
            _log.LogInformation("list_saved_queries -> {Count} queries in {Groups} groups.",
                result.Queries.Count, result.Groups.Count);
            return result;
        });

    [McpServerTool(Name = "save_query", ReadOnly = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Saves a SQL query to the developer's Log Viewer so it can be run again from the app. " +
        "THIS IS THE ONLY TOOL ON THIS SERVER THAT WRITES ANYTHING PERSISTENT. It appends to the developer's " +
        "own saved_queries.yaml, which they read and reuse, so save a query only when asked to or when it is " +
        "genuinely worth keeping — not to record intermediate work. The saved description is stamped to show " +
        "an agent wrote it and when. " +
        "Names must be unique within a group; a duplicate is refused rather than overwritten. " +
        "Nothing here can delete or rename an existing query.")]
    public Task<SavedQueryInfo> SaveQuery(
        [Description("Short descriptive name. Must be unique within its group.")] string name,
        [Description("The SQL to save. Write it against a prepared table, then generalise the table name if reusing.")] string sql,
        [Description("Optional group heading to file it under. Omit for ungrouped.")] string? group = null,
        [Description("Optional description of what the query answers.")] string? description = null,
        [Description("Optional template name the query was written against, as a hint for future readers.")] string? template = null)
        => ToolErrors.GuardAsync(async () =>
        {
            var result = await _logs.SaveQueryAsync(name, sql, group, description, template);

            // Name and group only. The SQL is not logged: it can quote values lifted from log rows.
            _log.LogInformation("save_query({Name}) -> saved in group '{Group}'.", result.Name, result.Group);

            return result;
        });
}
