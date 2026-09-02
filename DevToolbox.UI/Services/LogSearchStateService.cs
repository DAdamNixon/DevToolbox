using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.UI.Services;

/// <summary>
/// Scoped state container for the Log Viewer page.
/// Survives tab navigation so searches continue running in the background
/// and results are preserved when the user returns.
/// </summary>
public sealed class LogSearchStateService : IDisposable
{
    // --- filter inputs ---
    public List<LogLocation> LogLocations { get; set; } = new();
    public List<LogLocation> SelectedLocations { get; set; } = new();
    public DateTime StartDate { get; set; } = DateTime.Today.AddDays(-7);
    public DateTime EndDate { get; set; } = DateTime.Today;
    public string LogFile { get; set; } = string.Empty;

    /// <summary>
    /// Names offered by the Log File box. Either discovered from the selected
    /// locations or, when none of them declare a name pattern, the configured
    /// preset list.
    /// </summary>
    public List<string> AvailableLogFiles { get; set; } = new();

    /// <summary>File count per discovered name, for the hint beside each option. Empty when using presets.</summary>
    public Dictionary<string, int> LogFileCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Discovery is walking the selected locations.</summary>
    public bool IsDiscoveringLogFiles { get; set; }

    /// <summary>True when the list came from disk rather than from presets.</summary>
    public bool LogFilesWereDiscovered { get; set; }
    public List<LogTemplateIndexEntry> AvailableTemplates { get; set; } = new();
    public string SelectedTemplateName { get; set; } = "";
    public LogFilePresetConfig? PresetConfig { get; set; }

    // --- search results ---
    public List<Dictionary<string, string>> FilteredLogLines { get; set; } = new();
    public List<string> TableColumns { get; set; } = new();
    public bool IsLoading { get; set; }

    /// <summary>
    /// The active template's column delimiter, kept alongside <see cref="TableColumns"/>
    /// so plain-text mode can rebuild something close to the original line.
    /// </summary>
    public string TemplateDelimiter { get; set; } = "|";

    // --- presentation ---

    /// <summary>
    /// The source card folds to a one-line summary after a successful search, so
    /// the results get the screen. Lives here rather than in the page so leaving
    /// the tab and coming back does not pop the card open again.
    /// </summary>
    public bool SourceCollapsed { get; set; }

    /// <summary>
    /// The results card fills the window, covering the source and filter cards.
    /// Page size only ever added rows to the same short box; this is what makes the
    /// box itself bigger.
    /// </summary>
    public bool ResultsFullscreen { get; set; }

    /// <summary>
    /// The filter card floats over the full-screen results instead of lying under
    /// them in page flow. Ctrl+F raises it; Escape and the toolbar button put it
    /// back. Only meaningful while <see cref="ResultsFullscreen"/> is set, since
    /// outside full screen the card is on the page anyway.
    /// </summary>
    public bool FilterOverlayVisible { get; set; }

    /// <summary>Results rendered as raw delimited lines instead of the table.</summary>
    public bool PlainTextView { get; set; }

    /// <summary>Plain-text mode wraps long lines instead of scrolling them.</summary>
    public bool PlainTextWrap { get; set; }

    /// <summary>
    /// Show columns that hold nothing on the current page. Off by default: the
    /// WebsiteBase template alone carries fourteen Message columns, and rendering
    /// the empty ones costs a screen's width of nothing.
    /// </summary>
    public bool ShowEmptyColumns { get; set; }

    /// <summary>Latest ingest progress, or null when not ingesting.</summary>
    public LogIngestProgress? Progress { get; set; }

    /// <summary>
    /// Cancel has been pressed but the operation has not unwound yet. Drives the
    /// button's "Cancelling…" state so a Cancel during a long SQLite batch does not
    /// look ignored.
    /// </summary>
    public bool IsCancelling { get; set; }
    public List<SortColumn> ActiveSorts { get; set; } = new();
    public bool HasSearched { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string CurrentTableName { get; set; } = "";

    // --- advanced search ---
    public class KeywordRow { public string Gate { get; set; } = "AND"; public string Text { get; set; } = ""; }
    public List<KeywordRow> KeywordRows { get; set; } = new() { new KeywordRow() };
    public bool AdvancedRawMode { get; set; }
    public string AdvancedExpression { get; set; } = "";

    // --- saved queries ---

    /// <summary>
    /// Every saved advanced-mode query, ordered group-then-name — the order the picker draws.
    /// Loaded on demand rather than at startup: the list only matters once advanced mode is on,
    /// and most sessions never turn it on.
    /// </summary>
    public List<SavedQuery> SavedQueries { get; private set; } = new();

    /// <summary>Whichever saved query the SQL box was last loaded from, or null.</summary>
    public SavedQuery? ActiveSavedQuery { get; private set; }

    /// <summary>
    /// The SQL as it was when <see cref="ActiveSavedQuery"/> was loaded, so the bar can say the
    /// query has been edited since. Kept apart from the saved copy because that one is replaced
    /// wholesale whenever the store is re-read.
    /// </summary>
    private string _activeSavedQuerySql = "";

    /// <summary>True when a saved query is loaded and the box no longer matches it.</summary>
    public bool SavedQueryIsModified =>
        ActiveSavedQuery is not null &&
        !string.Equals((AdvancedExpression ?? "").Trim(), _activeSavedQuerySql, StringComparison.Ordinal);

    /// <summary>"Checkout / Orders by hour", or just the name when it is ungrouped.</summary>
    public string? ActiveSavedQueryLabel => ActiveSavedQuery is not { } q
        ? null
        : string.IsNullOrWhiteSpace(q.Group) ? q.Name : $"{q.Group} / {q.Name}";

    // --- split into tabs ---

    /// <summary>
    /// A tab over the single ingested table. Nothing is re-read or copied: a tab is
    /// a WHERE clause, so splitting costs one grouped query and no extra memory.
    /// </summary>
    public sealed class LogTab
    {
        /// <summary>The split column's value, or null for the All tab.</summary>
        public string? Value { get; init; }

        public string Label { get; init; } = "All";
        public int RowCount { get; init; }

        // Kept per tab so switching away and back lands where you left off rather
        // than resetting to page 1 of the default sort.
        public int CurrentPage { get; set; }
        public List<SortColumn> Sorts { get; set; } = new();
    }

    public LogSplitMode SplitMode { get; set; } = LogSplitMode.None;
    public List<LogTab> Tabs { get; set; } = new();
    public int ActiveTabIndex { get; set; }

    public LogTab? ActiveTab =>
        ActiveTabIndex >= 0 && ActiveTabIndex < Tabs.Count ? Tabs[ActiveTabIndex] : null;

    /// <summary>The predicate for the active tab, or null on All.</summary>
    public LogSplitFilter? CurrentSplitFilter => LogSplitFilter.For(SplitMode, ActiveTab?.Value);

    // --- pagination ---
    public int CurrentPage { get; set; }
    public int PageSize { get; set; } = 500;
    public bool HasMorePages { get; set; } = true;
    public int PageInput { get; set; } = 1;
    public int TotalPages { get; set; }
    public int TotalRecords { get; set; }

    // --- lifecycle ---
    public bool IsInitialized { get; private set; }
    public event Action? OnChanged;

    private CancellationTokenSource? _cts;

    // Discovery gets its own token source: it is triggered by changing a filter,
    // which must neither cancel a running search nor be cancelled by one.
    private CancellationTokenSource _discoveryCts = new();

    private readonly ILogFileService _logFileService;
    private readonly IYamlStorageService _yamlStorage;
    private readonly ISavedQueryService _savedQueries;

    public LogSearchStateService(
        ILogFileService logFileService,
        IYamlStorageService yamlStorage,
        ISavedQueryService savedQueries)
    {
        _logFileService = logFileService;
        _yamlStorage = yamlStorage;
        _savedQueries = savedQueries;
    }

    public void Notify() => OnChanged?.Invoke();

    // --- initialization ---

    public async Task InitializeAsync()
    {
        if (IsInitialized) return;
        IsLoading = true;
        Notify();
        try
        {
            var locationsTask = _logFileService.GetLogLocationsAsync();
            var templatesTask = _logFileService.GetAvailableLogFileTemplatesAsync();
            await Task.WhenAll(locationsTask, templatesTask);

            LogLocations = await locationsTask ?? new();
            AvailableTemplates = await templatesTask ?? new();
            PresetConfig = await _yamlStorage.LoadAsync<LogFilePresetConfig>("log_file_presets");

            SelectedTemplateName = AvailableTemplates.FirstOrDefault()?.Name ?? string.Empty;
            SelectedLocations = LogLocations.Take(1).ToList();

            if (!string.IsNullOrEmpty(SelectedTemplateName))
            {
                ApplyPresetsForTemplate();
                await UpdateTableColumnsAsync();
            }
            IsInitialized = true;

            // Deliberately not awaited: discovery walks directories that may be on
            // a slow share, and the form must be usable while it runs. The dropdown
            // shows a spinner and swaps its options in when the walk finishes.
            _ = RefreshLogFileNamesAsync();
        }
        catch (Exception ex)
        {
            SetError($"Failed to load initial data: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            Notify();
        }
    }

    /// <summary>
    /// Re-reads the templates and locations after they have been edited, keeping the current
    /// selection wherever it still exists.
    /// <para>
    /// This state service lives as long as the host, which is what lets a search survive tab
    /// navigation — and also means it would go on showing a template list from startup for the rest
    /// of the session. Selections are matched by name and path rather than by object: the lists were
    /// rebuilt from YAML, so nothing the page is holding is the same instance any more.
    /// </para>
    /// <para>
    /// Results already on screen are left alone. They came from an ingest under the old template and
    /// still say what they said; re-parsing them would need another search, which is the user's call.
    /// </para>
    /// </summary>
    public async Task ReloadConfigAsync()
    {
        try
        {
            var previousTemplate = SelectedTemplateName;
            var previousLocations = SelectedLocations.Select(l => l.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // ApplyPresetsForTemplate fills the Log File box from the template's default, which is
            // right when the template is picked and wrong here — nobody wants a config edit to
            // overwrite the file name they had already typed.
            var previousLogFile = LogFile;

            LogLocations = await _logFileService.GetLogLocationsAsync() ?? new();
            AvailableTemplates = await _logFileService.GetAvailableLogFileTemplatesAsync() ?? new();
            PresetConfig = await _yamlStorage.LoadAsync<LogFilePresetConfig>("log_file_presets");

            SelectedLocations = LogLocations.Where(l => previousLocations.Contains(l.Path)).ToList();
            if (SelectedLocations.Count == 0) SelectedLocations = LogLocations.Take(1).ToList();

            var stillThere = AvailableTemplates.Any(t => t.Name == previousTemplate);
            SelectedTemplateName = stillThere
                ? previousTemplate
                : AvailableTemplates.FirstOrDefault()?.Name ?? string.Empty;

            if (!string.IsNullOrEmpty(SelectedTemplateName))
            {
                // Not OnTemplateChangedAsync: that resets pagination, which would throw away the page
                // of results the user is looking at over what may have been an edit to a different
                // template entirely.
                ApplyPresetsForTemplate();
                await UpdateTableColumnsAsync();
            }
            else
            {
                TableColumns = new();
            }

            await RefreshLogFileNamesAsync();
            LogFile = previousLogFile;
        }
        catch (Exception ex)
        {
            SetError($"Failed to reload the log configuration: {ex.Message}");
        }
        finally
        {
            Notify();
        }
    }

    // --- location helpers ---

    public string LocationSummary => SelectedLocations.Count switch
    {
        0 => "Select location(s)...",
        1 => SelectedLocations[0].Name,
        _ => $"{SelectedLocations.Count} locations selected"
    };

    /// <summary>
    /// What the collapsed source card shows: enough to know what was searched
    /// without expanding it. Reads as "WebsiteBase · EE IIS · Jul 14 – Aug 20 · Checkout".
    /// </summary>
    public string SourceSummary
    {
        get
        {
            var dates = StartDate.Date == EndDate.Date
                ? StartDate.ToString("MMM d")
                : $"{StartDate:MMM d} – {EndDate:MMM d}";
            var parts = new List<string> { SelectedTemplateName, LocationSummary, dates };
            if (!string.IsNullOrWhiteSpace(LogFile)) parts.Add(LogFile);
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    public bool AllLocationsSelected =>
        LogLocations.Count > 0 && SelectedLocations.Count == LogLocations.Count;

    public bool IsLocationSelected(LogLocation location) =>
        SelectedLocations.Any(l => l.Path == location.Path);

    public async Task ToggleLocationAsync(LogLocation location)
    {
        var existing = SelectedLocations.FirstOrDefault(l => l.Path == location.Path);
        if (existing != null) SelectedLocations.Remove(existing);
        else SelectedLocations.Add(location);
        ResetPagination();
        await RefreshLogFileNamesAsync();
    }

    public async Task ToggleAllLocationsAsync()
    {
        SelectedLocations = AllLocationsSelected ? new() : new(LogLocations);
        ResetPagination();
        await RefreshLogFileNamesAsync();
    }

    // --- template ---

    public async Task OnTemplateChangedAsync()
    {
        ApplyPresetsForTemplate();
        await UpdateTableColumnsAsync();
        ResetPagination();

        // The template decides the extension, so the set of discoverable names
        // changes with it.
        await RefreshLogFileNamesAsync();
    }

    public void ApplyPresetsForTemplate()
    {
        var group = PresetConfig?.Presets?
            .FirstOrDefault(p => string.Equals(p.Template, SelectedTemplateName, StringComparison.OrdinalIgnoreCase));
        AvailableLogFiles = group?.Files ?? new();
        LogFileCounts = new(StringComparer.OrdinalIgnoreCase);
        LogFilesWereDiscovered = false;
        if (!string.IsNullOrWhiteSpace(group?.DefaultFile))
            LogFile = group!.DefaultFile!;
    }

    /// <summary>
    /// Replaces the Log File options with the names actually present in the
    /// selected locations. Falls back to the preset list — leaving what
    /// <see cref="ApplyPresetsForTemplate"/> set — when nothing is discoverable,
    /// so an offline share or a location without a name pattern costs nothing.
    /// </summary>
    public async Task RefreshLogFileNamesAsync()
    {
        if (SelectedLocations.Count == 0 || string.IsNullOrWhiteSpace(SelectedTemplateName))
        {
            ApplyPresetsForTemplate();
            Notify();
            return;
        }

        // Supersede any walk still in flight — clicking through four locations
        // should not leave four directory scans racing to set the same list.
        _discoveryCts.Cancel();
        _discoveryCts.Dispose();
        _discoveryCts = new CancellationTokenSource();

        IsDiscoveringLogFiles = true;
        Notify();
        try
        {
            var locations = SelectedLocations.ToList();
            var templateName = SelectedTemplateName;

            // Off the UI thread: this walks directories that may be on a slow share.
            var discovered = await Task.Run(
                () => _logFileService.DiscoverLogFileNamesAsync(locations, templateName, _discoveryCts.Token),
                _discoveryCts.Token);

            if (discovered.Count > 0)
            {
                AvailableLogFiles = discovered.Select(d => d.Name).ToList();
                LogFileCounts = discovered.ToDictionary(d => d.Name, d => d.FileCount, StringComparer.OrdinalIgnoreCase);
                LogFilesWereDiscovered = true;
            }
            else
            {
                ApplyPresetsForTemplate();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException ex)
        {
            // Discovery is a convenience. Say what happened, but leave the box usable.
            SetError($"Could not list log file names: {ex.Message}");
            ApplyPresetsForTemplate();
        }
        finally
        {
            IsDiscoveringLogFiles = false;
            Notify();
        }
    }

    public async Task UpdateTableColumnsAsync()
    {
        try
        {
            var templateEntry = AvailableTemplates.FirstOrDefault(t => t.Name == SelectedTemplateName);
            if (templateEntry != null)
            {
                var template = await _logFileService.LoadTemplateAsync(templateEntry.File);
                TableColumns = template?.Columns ?? new();
                TemplateDelimiter = template?.Delimiter ?? "|";
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to update table columns: {ex.Message}");
        }
    }

    // --- pagination ---

    public void ResetPagination()
    {
        CurrentPage = 0;
        PageInput = 1;
        TotalPages = 0;
        TotalRecords = 0;
        HasMorePages = true;
    }

    public async Task OnPageSizeChangedAsync()
    {
        CurrentPage = 0;
        PageInput = 1;
        if (HasSearched) await QueryCurrentPageAsync();
    }

    public async Task JumpToPageAsync()
    {
        if (PageInput < 1) PageInput = 1;
        if (PageInput > TotalPages) PageInput = TotalPages;
        var newPage = PageInput - 1;
        if (newPage != CurrentPage)
        {
            CurrentPage = newPage;
            await QueryCurrentPageAsync();
        }
    }

    public async Task NextPageAsync()
    {
        if (HasMorePages && CurrentPage < TotalPages - 1)
        {
            CurrentPage++;
            await QueryCurrentPageAsync();
        }
    }

    public async Task PrevPageAsync()
    {
        if (CurrentPage > 0)
        {
            CurrentPage--;
            await QueryCurrentPageAsync();
        }
    }

    // --- search ---

    /// <summary>
    /// Cancels whatever is running and leaves it cancelled. This is the Cancel
    /// button.
    /// <para>
    /// Kept separate from <see cref="BeginOperation"/> because the old single
    /// method cancelled *and* immediately replaced the token source, and was
    /// called at the start of every search — so there was no way to express "stop"
    /// without also arming the next run.
    /// </para>
    /// </summary>
    public void CancelSearch()
    {
        _cts?.Cancel();
        IsCancelling = IsLoading;
        Notify();
    }

    /// <summary>Retires the previous token source and issues a fresh one for a new operation.</summary>
    private CancellationToken BeginOperation()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsCancelling = false;
        return _cts.Token;
    }

    public void SetError(string message) { ErrorMessage = message; Notify(); }
    public void ClearError() { ErrorMessage = ""; Notify(); }

    public async Task SearchAsync()
    {
        ClearError();
        if (string.IsNullOrWhiteSpace(SelectedTemplateName)) { SetError("Please select a template."); return; }
        if (SelectedLocations.Count == 0) { SetError("Please select at least one location."); return; }
        if (StartDate > EndDate) { SetError("Start date cannot be after end date."); return; }

        ResetPagination();
        HasSearched = true;
        ActiveSorts.Clear();
        await PrepareAndQueryAsync();

        // Fold the source card once a search lands, so the results get the
        // screen. Only on success: an error or a cancel means the user is about
        // to adjust the very fields the fold would hide.
        if (string.IsNullOrEmpty(ErrorMessage) && FilteredLogLines.Count > 0)
        {
            SourceCollapsed = true;
            Notify();
        }
    }

    public async Task PrepareAndQueryAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Progress = null;
        Notify();
        try
        {
            var token = BeginOperation();
            var templateEntry = AvailableTemplates.FirstOrDefault(t => t.Name == SelectedTemplateName);
            if (templateEntry == null) { SetError($"Template '{SelectedTemplateName}' not found."); return; }

            // Snapshot inputs before entering Task.Run to avoid capturing mutable state
            var logFile = LogFile;
            var locations = SelectedLocations.ToList();
            var start = StartDate;
            var end = EndDate;
            var templateName = templateEntry.Name;

            // Progress arrives from parser threads. Assign and notify only — the
            // reporter already throttles to ~4/sec, so this is a render rate the UI
            // can keep up with.
            var progress = new Progress<LogIngestProgress>(p =>
            {
                Progress = p;
                Notify();
            });

            // Task.Run keeps heavy file I/O off the UI thread (Blazor Hybrid sync context)
            CurrentTableName = await Task.Run(
                () => _logFileService.PrepareLogTableAsync(logFile, locations, start, end, templateName, progress, token),
                token);

            if (token.IsCancellationRequested) return;

            // Counts first: the All tab's total comes from the page query, so the
            // strip is rebuilt again afterwards to pick it up.
            await RebuildTabsAsync(token);
            await QueryPageCoreAsync(token);
            await RebuildTabsAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError($"Search failed: {ex.Message}"); }
        finally { IsLoading = false; IsCancelling = false; Progress = null; Notify(); }
    }

    public async Task QueryCurrentPageAsync()
    {
        if (IsLoading || string.IsNullOrEmpty(CurrentTableName)) return;
        IsLoading = true;
        Notify();
        try
        {
            await QueryPageCoreAsync(BeginOperation());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError($"Search failed: {ex.Message}"); }
        finally { IsLoading = false; IsCancelling = false; Notify(); }
    }

    private async Task QueryPageCoreAsync(CancellationToken token)
    {
        var templateEntry = AvailableTemplates.FirstOrDefault(t => t.Name == SelectedTemplateName);
        if (templateEntry == null) { SetError($"Template '{SelectedTemplateName}' not found."); return; }

        var criteria = BuildCriteria();
        var criteriaArg = criteria.HasContent ? criteria : null;
        var sorts = AdvancedRawMode ? null : (ActiveSorts.Count > 0 ? ActiveSorts : null);

        // Snapshot locals for Task.Run closures
        var tableName = CurrentTableName;
        var templateName = templateEntry.Name;
        var page = CurrentPage;
        var pageSize = PageSize;

        var split = CurrentSplitFilter;

        // Task.Run keeps SQLite queries off the UI thread
        var countTask = Task.Run(() => _logFileService.CountLogEntriesAsync(tableName, criteriaArg, split, token), token);
        var dataTask = Task.Run(() => _logFileService.QueryLogPageAsync(tableName, templateName, page, pageSize, sorts, criteriaArg, split, token), token);
        await Task.WhenAll(countTask, dataTask);

        if (token.IsCancellationRequested) return;

        TotalRecords = await countTask;
        TotalPages = (int)Math.Ceiling(TotalRecords / (double)pageSize);

        var pageData = await dataTask;
        FilteredLogLines = pageData ?? new();

        if (pageData?.Any() == true)
        {
            // SourcePath stays on every row — double-click needs it to know which
            // file to open — but it is not a column anyone wants to read. Hiding it
            // here rather than dropping it keeps the grid unchanged while giving the
            // row somewhere to carry its origin.
            TableColumns = pageData
                .OrderByDescending(l => l.Count)
                .FirstOrDefault()?
                .Keys
                .Where(k => !string.Equals(k, DbLogService.SourcePathColumn, StringComparison.Ordinal))
                .ToList() ?? new();
        }

        HasMorePages = pageData?.Count == pageSize && (page + 1) < TotalPages;
        PageInput = page + 1;
    }

    // --- split tabs ---

    /// <summary>
    /// Rebuilds the tab strip from the current split mode and keyword filter.
    /// All is always tab 0, so turning splitting off is never a special case and
    /// the combined view is always one click away.
    /// </summary>
    public async Task RebuildTabsAsync(CancellationToken token)
    {
        var previousValue = ActiveTab?.Value;

        if (SplitMode == LogSplitMode.None || string.IsNullOrEmpty(CurrentTableName))
        {
            Tabs = new List<LogTab> { new() { Value = null, Label = "All", RowCount = TotalRecords } };
            ActiveTabIndex = 0;
            return;
        }

        var criteria = BuildCriteria();
        var groups = await Task.Run(
            () => _logFileService.GetSplitGroupsAsync(CurrentTableName, SplitMode, criteria.HasContent ? criteria : null, token),
            token);

        // All's count is the sum of the groups, not TotalRecords. The groups
        // partition the table under the same filter, so the sum is exact — and
        // TotalRecords describes whichever tab was last queried, which made the All
        // count show the previous tab's total for a moment after switching modes.
        var allTab = new LogTab
        {
            Value = null,
            Label = "All",
            RowCount = groups.Sum(g => g.Count)
        };

        var tabs = new List<LogTab> { allTab };
        tabs.AddRange(groups.Select(g => new LogTab
        {
            Value = g.Value,
            Label = string.IsNullOrEmpty(g.Value) ? "(none)" : g.Value,
            RowCount = g.Count
        }));
        Tabs = tabs;

        // Stay on the same tab across a filter change when it still exists, rather
        // than dumping the user back on All every time they type.
        var index = previousValue is null ? 0 : tabs.FindIndex(t => t.Value == previousValue);
        ActiveTabIndex = index >= 0 ? index : 0;
    }

    public async Task SetSplitModeAsync(LogSplitMode mode)
    {
        if (SplitMode == mode) return;
        SplitMode = mode;
        ActiveTabIndex = 0;

        if (!HasSearched || string.IsNullOrEmpty(CurrentTableName))
        {
            await RebuildTabsAsync(CancellationToken.None);
            Notify();
            return;
        }

        IsLoading = true;
        Notify();
        try
        {
            var token = BeginOperation();
            await RebuildTabsAsync(token);
            ResetPagination();
            await QueryPageCoreAsync(token);

            // Again afterwards so that turning splitting *off* picks up the All
            // total the query just produced; with splitting on this is a no-op.
            await RebuildTabsAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError($"Split failed: {ex.Message}"); }
        finally { IsLoading = false; Notify(); }
    }

    public async Task SelectTabAsync(int index)
    {
        if (index < 0 || index >= Tabs.Count || index == ActiveTabIndex) return;

        // Park the current tab's position so returning to it restores the view.
        if (ActiveTab is { } leaving)
        {
            leaving.CurrentPage = CurrentPage;
            leaving.Sorts = new List<SortColumn>(ActiveSorts);
        }

        ActiveTabIndex = index;

        var entering = Tabs[index];
        CurrentPage = entering.CurrentPage;
        PageInput = entering.CurrentPage + 1;
        ActiveSorts = new List<SortColumn>(entering.Sorts);

        await QueryCurrentPageAsync();
    }

    public LogSearchCriteria BuildCriteria()
    {
        var criteria = new LogSearchCriteria { UseAdvanced = AdvancedRawMode };
        if (AdvancedRawMode)
        {
            criteria.AdvancedExpression = AdvancedExpression;
        }
        else
        {
            foreach (var row in KeywordRows)
            {
                var terms = (row.Text ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                if (terms.Count == 0) continue;
                criteria.Groups.Add(new KeywordGroup { Gate = row.Gate, Terms = terms });
            }
        }
        return criteria;
    }

    public async Task RunLiveQueryAsync()
    {
        if (!HasSearched || string.IsNullOrEmpty(CurrentTableName)) return;
        ResetPagination();
        await QueryCurrentPageAsync();

        // Tab counts are part of the filter's result, so they move with it.
        if (SplitMode != LogSplitMode.None)
        {
            try
            {
                await RebuildTabsAsync(CancellationToken.None);
                Notify();
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException ex) { SetError($"Could not refresh tab counts: {ex.Message}"); }
        }
    }

    public async Task OnAdvancedToggledAsync()
    {
        ActiveSorts.Clear();
        if (HasSearched) await RunLiveQueryAsync();
    }

    // --- saved queries ---

    /// <summary>
    /// Re-reads the saved queries. Failure is reported and leaves the previous list in place: the
    /// picker being stale is a great deal better than the SQL box disappearing behind a banner.
    /// </summary>
    public async Task LoadSavedQueriesAsync()
    {
        try
        {
            SavedQueries = await _savedQueries.GetAllAsync();

            // The active query may have been renamed, regrouped or deleted by the manage dialog.
            // Re-resolving it by id keeps the label honest without disturbing the box.
            if (ActiveSavedQuery is { } active)
                ActiveSavedQuery = SavedQueries.FirstOrDefault(q => q.Id == active.Id);
        }
        catch (InvalidOperationException ex)
        {
            // What YamlStorageService wraps every read failure in — a missing file is not one of
            // them, it returns null, so getting here means the file is there and unreadable.
            SetError($"Could not read the saved queries: {ex.Message}");
        }
        finally
        {
            Notify();
        }
    }

    /// <summary>
    /// Puts a saved query in the SQL box and runs it. Switches advanced mode on if it is off —
    /// choosing a saved SQL query and then not being in SQL mode would be a trap.
    /// </summary>
    public async Task ApplySavedQueryAsync(SavedQuery query)
    {
        if (query is null) return;

        AdvancedRawMode = true;
        AdvancedExpression = query.Sql;
        ActiveSavedQuery = query;
        _activeSavedQuerySql = (query.Sql ?? "").Trim();
        ActiveSorts.Clear();
        Notify();

        if (HasSearched) await RunLiveQueryAsync();
    }

    /// <summary>
    /// Records that the box now holds <paramref name="query"/> exactly — what a save or an update
    /// leaves behind, so the bar stops reporting the query as modified.
    /// </summary>
    public void MarkSavedQueryApplied(SavedQuery query)
    {
        ActiveSavedQuery = query;
        _activeSavedQuerySql = (query?.Sql ?? "").Trim();
        Notify();
    }


    public async Task AddKeywordRowAsync()
    {
        KeywordRows.Add(new KeywordRow());
        await RunLiveQueryAsync();
    }

    public async Task RemoveKeywordRowAsync(int index)
    {
        if (index < 0 || index >= KeywordRows.Count) return;

        // The last row cannot be removed — the strip always shows one — so its X
        // clears the text instead of doing nothing.
        if (KeywordRows.Count == 1)
        {
            if (string.IsNullOrEmpty(KeywordRows[0].Text)) return;
            KeywordRows[0].Text = "";
        }
        else
        {
            KeywordRows.RemoveAt(index);
        }

        await RunLiveQueryAsync();
    }

    public async Task SortByColumnAsync(string column, bool append)
    {
        if (IsLoading || !HasSearched || AdvancedRawMode) return;
        var existing = ActiveSorts.FirstOrDefault(s => s.Column == column);
        if (append)
        {
            if (existing != null) existing.Direction = existing.Direction == "asc" ? "desc" : "asc";
            else ActiveSorts.Add(new SortColumn { Column = column, Direction = "asc" });
        }
        else if (existing != null && ActiveSorts.Count == 1)
        {
            existing.Direction = existing.Direction == "asc" ? "desc" : "asc";
        }
        else
        {
            ActiveSorts = new List<SortColumn> { new() { Column = column, Direction = "asc" } };
        }
        CurrentPage = 0;
        PageInput = 1;
        await QueryCurrentPageAsync();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _discoveryCts.Cancel();
        _discoveryCts.Dispose();
    }
}
