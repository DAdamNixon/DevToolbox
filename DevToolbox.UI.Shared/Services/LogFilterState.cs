using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.UI.Services;

/// <summary>
/// Everything that belongs to one table's view in the Log Viewer — the <c>logs</c> table, or the
/// <c>results</c> table a collapse materializes beside it.
/// <para>
/// Extracted from <see cref="LogSearchStateService"/> so the two views cannot drift: before this,
/// a second filter card would have meant a second copy of every field below, and the first change
/// made to only one copy is how they stop agreeing. <see cref="LogSearchStateService.Active"/>
/// picks whichever of <see cref="LogSearchStateService.Logs"/> /
/// <see cref="LogSearchStateService.Results"/> is the one the user can currently type into, and
/// every interactive member on the service is a forward to it — so there is exactly one code path
/// for "sort this grid" or "add a keyword row," whichever table it happens to be pointed at.
/// </para>
/// </summary>
public sealed class LogFilterState
{
    /// <summary>
    /// The SQLite table this view queries — <c>logs</c> or
    /// <see cref="DevToolbox.Services.Services.DbLogService.ResultsTableName"/>. Settable rather
    /// than <c>init</c>: a prepare reports back the table name it actually used, and
    /// <see cref="LogSearchStateService.Logs"/> has to be updateable after construction to receive it.
    /// </summary>
    public required string TableName { get; set; }

    /// <summary>Which saved-query list this card's picker and Manage dialog read — <see cref="SavedQueryTargets.Logs"/> or <see cref="SavedQueryTargets.Results"/>.</summary>
    public required string SavedQueryTarget { get; set; }

    // --- filter inputs ---
    public List<LogSearchStateService.KeywordRow> KeywordRows { get; set; } = new() { new() };
    public bool AdvancedRawMode { get; set; }
    public string AdvancedExpression { get; set; } = "";

    // --- saved queries ---

    /// <summary>Every saved query for <see cref="SavedQueryTarget"/>, ordered group-then-name.</summary>
    public List<SavedQuery> SavedQueries { get; set; } = new();

    /// <summary>Whichever saved query the SQL box was last loaded from, or null.</summary>
    public SavedQuery? ActiveSavedQuery { get; set; }

    /// <summary>
    /// The SQL as it was when <see cref="ActiveSavedQuery"/> was loaded, so the bar can say the
    /// query has been edited since.
    /// </summary>
    public string ActiveSavedQuerySql { get; set; } = "";

    /// <summary>True when a saved query is loaded and the box no longer matches it.</summary>
    public bool SavedQueryIsModified =>
        ActiveSavedQuery is not null &&
        !string.Equals((AdvancedExpression ?? "").Trim(), ActiveSavedQuerySql, StringComparison.Ordinal);

    /// <summary>"Checkout / Orders by hour", or just the name when it is ungrouped.</summary>
    public string? ActiveSavedQueryLabel => ActiveSavedQuery is not { } q
        ? null
        : string.IsNullOrWhiteSpace(q.Group) ? q.Name : $"{q.Group} / {q.Name}";

    // --- split into tabs ---
    public LogSplitMode SplitMode { get; set; } = LogSplitMode.None;
    public List<LogSearchStateService.LogTab> Tabs { get; set; } = new();
    public int ActiveTabIndex { get; set; }

    public LogSearchStateService.LogTab? ActiveTab =>
        ActiveTabIndex >= 0 && ActiveTabIndex < Tabs.Count ? Tabs[ActiveTabIndex] : null;

    /// <summary>The predicate for the active tab, or null on All.</summary>
    public LogSplitFilter? CurrentSplitFilter => LogSplitFilter.For(SplitMode, ActiveTab?.Value);

    // --- sort / paging ---
    public List<SortColumn> ActiveSorts { get; set; } = new();
    public int CurrentPage { get; set; }
    public bool HasMorePages { get; set; } = true;
    public int PageInput { get; set; } = 1;
    public int TotalPages { get; set; }
    public int TotalRecords { get; set; }

    // --- results of the last query ---
    public List<Dictionary<string, string>> FilteredLogLines { get; set; } = new();

    /// <summary>
    /// The columns to render. On <see cref="LogSearchStateService.Logs"/> this starts as the
    /// template's own list (<see cref="LogSearchStateService.UpdateTableColumnsAsync"/>) and is
    /// then replaced by whatever a query actually returned; on
    /// <see cref="LogSearchStateService.Results"/> it only ever comes from a query — there is no
    /// template driving it, so a template change must never repaint this grid.
    /// </summary>
    public List<string> TableColumns { get; set; } = new();

    /// <summary>Row count from the collapse that created this view, before any query has run against
    /// it. Null on <see cref="LogSearchStateService.Logs"/>, which has no such moment.</summary>
    public int? CollapsedRowCount { get; set; }

    // --- availability (C6) ---

    /// <summary>
    /// The split modes this view's own columns actually support — <see cref="LogSplitMode.None"/>
    /// always, plus <see cref="LogSplitMode.Location"/> / <see cref="LogSplitMode.File"/> only when
    /// their column survived. A <c>GROUP BY</c> collapse commonly has neither, and the Split group
    /// is hidden rather than offered with nothing it can do.
    /// </summary>
    public IReadOnlyList<LogSplitMode> AllowedSplitModes =>
        Enum.GetValues<LogSplitMode>()
            .Where(m => m == LogSplitMode.None || (LogSplitColumns.TryResolve(m, out var column) && TableColumns.Contains(column)))
            .ToList();

    /// <summary>
    /// True when a row can be opened in an editor — the hidden <c>SourcePath</c> column survived
    /// whatever produced this view. A collapse that projects away <c>Location</c> or
    /// <c>SourceFile</c> can still keep this; one that never selected the column at all cannot.
    /// </summary>
    public bool CanOpenSource =>
        FilteredLogLines.Count > 0 && FilteredLogLines[0].ContainsKey(DbLogService.SourcePathColumn);

    public void ResetPagination()
    {
        CurrentPage = 0;
        PageInput = 1;
        TotalPages = 0;
        TotalRecords = 0;
        HasMorePages = true;
    }
}
