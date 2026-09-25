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

    /// <summary>
    /// Set by <see cref="ApplyLocationDefaultTemplateAsync"/> when the selected locations' default
    /// templates disagree or name one this machine does not have. Null the rest of the time,
    /// including right after a manual template pick.
    /// </summary>
    public string? LocationTemplateHint { get; set; }

    // --- the two table views ---

    /// <summary>The always-present view over the ingested <c>logs</c> table.</summary>
    public LogFilterState Logs { get; } = new()
    {
        TableName = DbLogService.DefaultTableName,
        SavedQueryTarget = SavedQueryTargets.Logs
    };

    /// <summary>
    /// The view over the collapsed <c>results</c> table, or null when nothing has been collapsed
    /// (or it has since been discarded). Its existence is what locks the logs filter card.
    /// </summary>
    public LogFilterState? Results { get; private set; }

    /// <summary>
    /// Whichever view the user can currently type into: <see cref="Results"/> once it exists,
    /// otherwise <see cref="Logs"/>. Every interactive member below — sorting, paging, split,
    /// saved queries, the keyword/SQL box — forwards here, so there is one code path for each,
    /// wherever it happens to be pointed.
    /// </summary>
    public LogFilterState Active => Results ?? Logs;

    // --- search results (forwarded to Active) ---
    public List<Dictionary<string, string>> FilteredLogLines { get => Active.FilteredLogLines; set => Active.FilteredLogLines = value; }
    public List<string> TableColumns { get => Active.TableColumns; set => Active.TableColumns = value; }
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
    /// What the last search did not manage to read, for the partial-results banner. Null once a new
    /// search starts, and while none has ever run.
    /// </summary>
    public LogPrepareResult? LastPrepareResult { get; set; }

    /// <summary>The running search's Skip/SkipAll handle. Null while nothing is loading.</summary>
    private LogIngestControl? _ingestControl;

    /// <summary>Abandons one stalled file, named by <see cref="FileProgressSnapshot.FileKey"/>.</summary>
    public void SkipFile(string fileKey) => _ingestControl?.Skip(fileKey);

    /// <summary>
    /// Cancel has been pressed but the operation has not unwound yet. Drives the
    /// button's "Cancelling…" state so a Cancel during a long SQLite batch does not
    /// look ignored.
    /// </summary>
    public bool IsCancelling { get; set; }
    public List<SortColumn> ActiveSorts { get => Active.ActiveSorts; set => Active.ActiveSorts = value; }
    public bool HasSearched { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string CurrentTableName { get => Active.TableName; set => Active.TableName = value; }

    // --- advanced search (forwarded to Active) ---
    public class KeywordRow { public string Gate { get; set; } = "AND"; public string Text { get; set; } = ""; }
    public List<KeywordRow> KeywordRows { get => Active.KeywordRows; set => Active.KeywordRows = value; }
    public bool AdvancedRawMode { get => Active.AdvancedRawMode; set => Active.AdvancedRawMode = value; }
    public string AdvancedExpression { get => Active.AdvancedExpression; set => Active.AdvancedExpression = value; }

    // --- saved queries (forwarded to Active) ---

    /// <summary>Every saved advanced-mode query for Active's target, ordered group-then-name.</summary>
    public List<SavedQuery> SavedQueries => Active.SavedQueries;

    /// <summary>Whichever saved query the SQL box was last loaded from, or null.</summary>
    public SavedQuery? ActiveSavedQuery => Active.ActiveSavedQuery;

    /// <summary>True when a saved query is loaded and the box no longer matches it.</summary>
    public bool SavedQueryIsModified => Active.SavedQueryIsModified;

    /// <summary>"Checkout / Orders by hour", or just the name when it is ungrouped.</summary>
    public string? ActiveSavedQueryLabel => Active.ActiveSavedQueryLabel;

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

    public LogSplitMode SplitMode { get => Active.SplitMode; set => Active.SplitMode = value; }
    public List<LogTab> Tabs { get => Active.Tabs; set => Active.Tabs = value; }
    public int ActiveTabIndex { get => Active.ActiveTabIndex; set => Active.ActiveTabIndex = value; }

    public LogTab? ActiveTab => Active.ActiveTab;

    /// <summary>The predicate for the active tab, or null on All.</summary>
    public LogSplitFilter? CurrentSplitFilter => Active.CurrentSplitFilter;

    // --- pagination (forwarded to Active; PageSize stays shared) ---
    public int CurrentPage { get => Active.CurrentPage; set => Active.CurrentPage = value; }
    public int PageSize { get; set; } = 500;
    public bool HasMorePages { get => Active.HasMorePages; set => Active.HasMorePages = value; }
    public int PageInput { get => Active.PageInput; set => Active.PageInput = value; }
    public int TotalPages { get => Active.TotalPages; set => Active.TotalPages = value; }
    public int TotalRecords { get => Active.TotalRecords; set => Active.TotalRecords = value; }

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
            await ApplyLocationDefaultTemplateAsync();

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
                Logs.TableColumns = new();
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
        Logs.ResetPagination();
        await ApplyLocationDefaultTemplateAsync();
        await RefreshLogFileNamesAsync();
    }

    public async Task ToggleAllLocationsAsync()
    {
        SelectedLocations = AllLocationsSelected ? new() : new(LogLocations);
        Logs.ResetPagination();
        await ApplyLocationDefaultTemplateAsync();
        await RefreshLogFileNamesAsync();
    }

    /// <summary>
    /// Switches to the selected locations' default template when every one of them agrees on it —
    /// see <see cref="DefaultTemplateResolver"/> for the exact rule — and sets
    /// <see cref="LocationTemplateHint"/> to explain when it does not. Runs the same path as a manual
    /// template pick (<see cref="ApplyPresetsForTemplate"/>, <see cref="UpdateTableColumnsAsync"/>),
    /// but never <see cref="RefreshLogFileNamesAsync"/> — the caller's own refresh covers that, so a
    /// location toggle does not discover twice.
    /// </summary>
    public async Task ApplyLocationDefaultTemplateAsync()
    {
        var resolution = DefaultTemplateResolver.Resolve(SelectedLocations, AvailableTemplates.Select(t => t.Name).ToList());

        LocationTemplateHint = resolution.Hint switch
        {
            DefaultTemplateHint.Differ => "These locations use different default templates.",
            DefaultTemplateHint.Missing => $"Default template '{CommonLocationDefault()}' is not a known template.",
            _ => null
        };

        if (resolution.Template is not null &&
            !string.Equals(resolution.Template, SelectedTemplateName, StringComparison.Ordinal))
        {
            SelectedTemplateName = resolution.Template;
            ApplyPresetsForTemplate();
            await UpdateTableColumnsAsync();
        }
    }

    /// <summary>The default every selected location agrees on, for the "not a known template" hint. Only
    /// meaningful when that is in fact what they agree on.</summary>
    private string CommonLocationDefault() =>
        SelectedLocations.Select(l => (l.DefaultTemplate ?? "").Trim()).FirstOrDefault(d => d.Length > 0) ?? "";

    // --- template ---

    public async Task OnTemplateChangedAsync()
    {
        // A manual pick always wins: the resolver only runs from a location change.
        LocationTemplateHint = null;
        ApplyPresetsForTemplate();
        await UpdateTableColumnsAsync();
        Logs.ResetPagination();

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

    /// <summary>
    /// The template's own column list, written to <see cref="Logs"/> only — a template describes
    /// what an ingest produces, and <see cref="Results"/> never comes from an ingest. Writing here
    /// unconditionally would repaint a collapsed results grid with columns it may not even have.
    /// </summary>
    public async Task UpdateTableColumnsAsync()
    {
        try
        {
            var templateEntry = AvailableTemplates.FirstOrDefault(t => t.Name == SelectedTemplateName);
            if (templateEntry != null)
            {
                var template = await _logFileService.LoadTemplateAsync(templateEntry.File);
                Logs.TableColumns = template?.Columns ?? new();
                TemplateDelimiter = template?.Delimiter ?? "|";
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to update table columns: {ex.Message}");
        }
    }

    // --- pagination ---

    public void ResetPagination() => Active.ResetPagination();

    public async Task OnPageSizeChangedAsync()
    {
        Active.CurrentPage = 0;
        Active.PageInput = 1;
        if (HasSearched) await QueryCurrentPageAsync();
    }

    public async Task JumpToPageAsync()
    {
        var state = Active;
        if (state.PageInput < 1) state.PageInput = 1;
        if (state.PageInput > state.TotalPages) state.PageInput = state.TotalPages;
        var newPage = state.PageInput - 1;
        if (newPage != state.CurrentPage)
        {
            state.CurrentPage = newPage;
            await QueryCurrentPageAsync();
        }
    }

    public async Task NextPageAsync()
    {
        var state = Active;
        if (state.HasMorePages && state.CurrentPage < state.TotalPages - 1)
        {
            state.CurrentPage++;
            await QueryCurrentPageAsync();
        }
    }

    public async Task PrevPageAsync()
    {
        var state = Active;
        if (state.CurrentPage > 0)
        {
            state.CurrentPage--;
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
        _ingestControl?.SkipAll();
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

    /// <summary>
    /// "Load Logs": re-ingests the selected files. Drops <see cref="Results"/> first, before the
    /// prepare runs, so a failed or cancelled load leaves none behind — results are scratch
    /// (2026-08-18) and a stale collapse pointed at a table about to be rebuilt from underneath it
    /// would be actively misleading, not merely unhelpful.
    /// </summary>
    public async Task SearchAsync()
    {
        ClearError();
        if (string.IsNullOrWhiteSpace(SelectedTemplateName)) { SetError("Please select a template."); return; }
        if (SelectedLocations.Count == 0) { SetError("Please select at least one location."); return; }
        if (StartDate > EndDate) { SetError("Start date cannot be after end date."); return; }

        await DropResultsAsync();

        Logs.ResetPagination();
        HasSearched = true;
        Logs.ActiveSorts.Clear();
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
        LastPrepareResult = null;
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

            var settings = await LogIngestSettingsStore.LoadAsync(_yamlStorage);
            _ingestControl = new LogIngestControl(settings);

            // Task.Run keeps heavy file I/O off the UI thread (Blazor Hybrid sync context)
            var prepared = await Task.Run(
                () => _logFileService.PrepareLogTableAsync(logFile, locations, start, end, templateName, progress, _ingestControl, token),
                token);

            if (token.IsCancellationRequested) return;

            Logs.TableName = prepared.TableName;
            LastPrepareResult = prepared;

            // Counts first: the All tab's total comes from the page query, so the
            // strip is rebuilt again afterwards to pick it up.
            await RebuildTabsAsync(Logs, token);
            await QueryPageCoreAsync(Logs, token);
            await RebuildTabsAsync(Logs, token);
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
            await QueryPageCoreAsync(Active, BeginOperation());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError($"Search failed: {ex.Message}"); }
        finally { IsLoading = false; IsCancelling = false; Notify(); }
    }

    private async Task QueryPageCoreAsync(LogFilterState state, CancellationToken token)
    {
        var templateEntry = AvailableTemplates.FirstOrDefault(t => t.Name == SelectedTemplateName);
        if (templateEntry == null) { SetError($"Template '{SelectedTemplateName}' not found."); return; }

        var criteria = BuildCriteria(state);
        var criteriaArg = criteria.HasContent ? criteria : null;
        var sorts = state.AdvancedRawMode ? null : (state.ActiveSorts.Count > 0 ? state.ActiveSorts : null);

        // Snapshot locals for Task.Run closures
        var tableName = state.TableName;
        var templateName = templateEntry.Name;
        var page = state.CurrentPage;
        var pageSize = PageSize;

        var split = state.CurrentSplitFilter;

        // Task.Run keeps SQLite queries off the UI thread
        var countTask = Task.Run(() => _logFileService.CountLogEntriesAsync(tableName, criteriaArg, split, token), token);
        var dataTask = Task.Run(() => _logFileService.QueryLogPageAsync(tableName, templateName, page, pageSize, sorts, criteriaArg, split, token), token);
        await Task.WhenAll(countTask, dataTask);

        if (token.IsCancellationRequested) return;

        state.TotalRecords = await countTask;
        state.TotalPages = (int)Math.Ceiling(state.TotalRecords / (double)pageSize);

        var pageData = await dataTask;
        state.FilteredLogLines = pageData ?? new();

        if (pageData?.Any() == true)
        {
            // SourcePath stays on every row — double-click needs it to know which
            // file to open — but it is not a column anyone wants to read. Hiding it
            // here rather than dropping it keeps the grid unchanged while giving the
            // row somewhere to carry its origin.
            state.TableColumns = pageData
                .OrderByDescending(l => l.Count)
                .FirstOrDefault()?
                .Keys
                .Where(k => !string.Equals(k, DbLogService.SourcePathColumn, StringComparison.Ordinal))
                .ToList() ?? new();
        }

        state.HasMorePages = pageData?.Count == pageSize && (page + 1) < state.TotalPages;
        state.PageInput = page + 1;
    }

    // --- split tabs ---

    /// <summary>
    /// Rebuilds the tab strip from the current split mode and keyword filter.
    /// All is always tab 0, so turning splitting off is never a special case and
    /// the combined view is always one click away.
    /// </summary>
    private async Task RebuildTabsAsync(LogFilterState state, CancellationToken token)
    {
        var previousValue = state.ActiveTab?.Value;

        if (state.SplitMode == LogSplitMode.None || string.IsNullOrEmpty(state.TableName))
        {
            state.Tabs = new List<LogTab> { new() { Value = null, Label = "All", RowCount = state.TotalRecords } };
            state.ActiveTabIndex = 0;
            return;
        }

        var criteria = BuildCriteria(state);
        var groups = await Task.Run(
            () => _logFileService.GetSplitGroupsAsync(state.TableName, state.SplitMode, criteria.HasContent ? criteria : null, token),
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
        state.Tabs = tabs;

        // Stay on the same tab across a filter change when it still exists, rather
        // than dumping the user back on All every time they type.
        var index = previousValue is null ? 0 : tabs.FindIndex(t => t.Value == previousValue);
        state.ActiveTabIndex = index >= 0 ? index : 0;
    }

    public async Task SetSplitModeAsync(LogSplitMode mode)
    {
        var state = Active;
        if (state.SplitMode == mode) return;
        state.SplitMode = mode;
        state.ActiveTabIndex = 0;

        if (!HasSearched || string.IsNullOrEmpty(state.TableName))
        {
            await RebuildTabsAsync(state, CancellationToken.None);
            Notify();
            return;
        }

        IsLoading = true;
        Notify();
        try
        {
            var token = BeginOperation();
            await RebuildTabsAsync(state, token);
            state.ResetPagination();
            await QueryPageCoreAsync(state, token);

            // Again afterwards so that turning splitting *off* picks up the All
            // total the query just produced; with splitting on this is a no-op.
            await RebuildTabsAsync(state, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError($"Split failed: {ex.Message}"); }
        finally { IsLoading = false; Notify(); }
    }

    public async Task SelectTabAsync(int index)
    {
        var state = Active;
        if (index < 0 || index >= state.Tabs.Count || index == state.ActiveTabIndex) return;

        // Park the current tab's position so returning to it restores the view.
        if (state.ActiveTab is { } leaving)
        {
            leaving.CurrentPage = state.CurrentPage;
            leaving.Sorts = new List<SortColumn>(state.ActiveSorts);
        }

        state.ActiveTabIndex = index;

        var entering = state.Tabs[index];
        state.CurrentPage = entering.CurrentPage;
        state.PageInput = entering.CurrentPage + 1;
        state.ActiveSorts = new List<SortColumn>(entering.Sorts);

        await QueryCurrentPageAsync();
    }

    /// <summary>Builds the search criteria for a specific view — the locked logs line needs
    /// <see cref="Logs"/>'s even once <see cref="Active"/> has moved on to <see cref="Results"/>.</summary>
    public static LogSearchCriteria BuildCriteria(LogFilterState state)
    {
        var criteria = new LogSearchCriteria { UseAdvanced = state.AdvancedRawMode };
        if (state.AdvancedRawMode)
        {
            criteria.AdvancedExpression = state.AdvancedExpression;
        }
        else
        {
            foreach (var row in state.KeywordRows)
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

    public LogSearchCriteria BuildCriteria() => BuildCriteria(Active);

    public async Task RunLiveQueryAsync()
    {
        var state = Active;
        if (!HasSearched || string.IsNullOrEmpty(state.TableName)) return;
        state.ResetPagination();
        await QueryCurrentPageAsync();

        // Tab counts are part of the filter's result, so they move with it.
        if (state.SplitMode != LogSplitMode.None)
        {
            try
            {
                await RebuildTabsAsync(state, CancellationToken.None);
                Notify();
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException ex) { SetError($"Could not refresh tab counts: {ex.Message}"); }
        }
    }

    public async Task OnAdvancedToggledAsync()
    {
        Active.ActiveSorts.Clear();
        if (HasSearched) await RunLiveQueryAsync();
    }

    // --- saved queries ---

    /// <summary>
    /// Re-reads the saved queries for Active's target. Failure is reported and leaves the previous
    /// list in place: the picker being stale is a great deal better than the SQL box disappearing
    /// behind a banner.
    /// </summary>
    public async Task LoadSavedQueriesAsync()
    {
        var state = Active;
        try
        {
            var all = await _savedQueries.GetAllAsync();
            state.SavedQueries = all.Where(q => SavedQueryTargets.IsFor(q, state.SavedQueryTarget)).ToList();

            // The active query may have been renamed, regrouped or deleted by the manage dialog.
            // Re-resolving it by id keeps the label honest without disturbing the box.
            if (state.ActiveSavedQuery is { } active)
                state.ActiveSavedQuery = state.SavedQueries.FirstOrDefault(q => q.Id == active.Id);
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
        var state = Active;

        state.AdvancedRawMode = true;
        state.AdvancedExpression = query.Sql;
        state.ActiveSavedQuery = query;
        state.ActiveSavedQuerySql = (query.Sql ?? "").Trim();
        state.ActiveSorts.Clear();
        Notify();

        if (HasSearched) await RunLiveQueryAsync();
    }

    /// <summary>
    /// Records that the box now holds <paramref name="query"/> exactly — what a save or an update
    /// leaves behind, so the bar stops reporting the query as modified.
    /// </summary>
    public void MarkSavedQueryApplied(SavedQuery query)
    {
        var state = Active;
        state.ActiveSavedQuery = query;
        state.ActiveSavedQuerySql = (query?.Sql ?? "").Trim();
        Notify();
    }

    /// <summary>
    /// Stops using the active saved query: clears the box, not the saved copy. Re-picking it from
    /// the list still restores it. Advanced mode stays on, and an empty box has no content to
    /// re-query, so a search already on screen falls back to the plain page rather than showing
    /// the cleared query's result.
    /// </summary>
    public async Task ClearSavedQueryAsync()
    {
        var state = Active;
        state.ActiveSavedQuery = null;
        state.ActiveSavedQuerySql = "";
        state.AdvancedExpression = "";
        Notify();

        if (HasSearched) await RunLiveQueryAsync();
    }

    public async Task AddKeywordRowAsync()
    {
        Active.KeywordRows.Add(new KeywordRow());
        await RunLiveQueryAsync();
    }

    public async Task RemoveKeywordRowAsync(int index)
    {
        var rows = Active.KeywordRows;
        if (index < 0 || index >= rows.Count) return;

        // The last row cannot be removed — the strip always shows one — so its X
        // clears the text instead of doing nothing.
        if (rows.Count == 1)
        {
            if (string.IsNullOrEmpty(rows[0].Text)) return;
            rows[0].Text = "";
        }
        else
        {
            rows.RemoveAt(index);
        }

        await RunLiveQueryAsync();
    }

    public async Task SortByColumnAsync(string column, bool append)
    {
        var state = Active;
        if (IsLoading || !HasSearched || state.AdvancedRawMode) return;
        var existing = state.ActiveSorts.FirstOrDefault(s => s.Column == column);
        if (append)
        {
            if (existing != null) existing.Direction = existing.Direction == "asc" ? "desc" : "asc";
            else state.ActiveSorts.Add(new SortColumn { Column = column, Direction = "asc" });
        }
        else if (existing != null && state.ActiveSorts.Count == 1)
        {
            existing.Direction = existing.Direction == "asc" ? "desc" : "asc";
        }
        else
        {
            state.ActiveSorts = new List<SortColumn> { new() { Column = column, Direction = "asc" } };
        }
        state.CurrentPage = 0;
        state.PageInput = 1;
        await QueryCurrentPageAsync();
    }

    // --- results (collapse the filter into a second table) ---

    /// <summary>
    /// True when collapsing the logs filter is offered: a search has run, nothing is loading,
    /// there is no <see cref="Results"/> already, the filter has content, and the last query
    /// matched at least one row.
    /// </summary>
    public bool CanCollapseToResults =>
        Results is null && HasSearched && !IsLoading &&
        BuildCriteria(Logs).HasContent && Logs.TotalRecords > 0;

    /// <summary>
    /// Copies the logs filter's current result — keyword or SQL mode, every page, only the active
    /// split tab — into the <c>results</c> table, and switches the active view to it. Uses what is
    /// in the box <em>now</em>: a keystroke still inside the debounce window is included, since the
    /// page disposes that timer before calling this.
    /// <para>
    /// Not cancellable — it is one SQL statement, and the grid shows the spinner until it returns.
    /// </para>
    /// </summary>
    public async Task CollapseToResultsAsync()
    {
        if (!CanCollapseToResults) return;

        IsLoading = true;
        Notify();
        try
        {
            var criteria = BuildCriteria(Logs);
            var (rows, columns) = await _logFileService.MaterializeResultsAsync(
                Logs.TableName, SelectedTemplateName, Logs.ActiveSorts, criteria, Logs.CurrentSplitFilter);

            if (rows == 0)
            {
                await _logFileService.DropResultsAsync();
                SetError("Nothing matched — nothing was collapsed.");
                return;
            }

            var results = new LogFilterState
            {
                TableName = DbLogService.ResultsTableName,
                SavedQueryTarget = SavedQueryTargets.Results,
                TableColumns = columns,
                CollapsedRowCount = rows
            };
            Results = results;
            await LoadSavedQueriesAsync();

            await QueryPageCoreAsync(results, BeginOperation());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError($"Could not collapse into results: {ex.Message}"); }
        finally { IsLoading = false; Notify(); }
    }

    /// <summary>
    /// Drops the <c>results</c> table and brings the logs filter back exactly as it was — nothing
    /// about <see cref="Logs"/> was ever touched by a collapse, so this is only forgetting
    /// <see cref="Results"/> and re-querying at its parked page.
    /// </summary>
    public async Task DiscardResultsAsync()
    {
        if (Results is null) return;

        await DropResultsAsync();
        ClearError();

        if (Logs.CurrentPage >= Logs.TotalPages && Logs.TotalPages > 0)
            Logs.CurrentPage = Logs.TotalPages - 1;

        Notify();
        await QueryCurrentPageAsync();
    }

    /// <summary>Drops the <c>results</c> table, if any, and forgets it. Safe to call when there is none.</summary>
    private async Task DropResultsAsync()
    {
        if (Results is null) return;
        try
        {
            await _logFileService.DropResultsAsync();
        }
        finally
        {
            Results = null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _discoveryCts.Cancel();
        _discoveryCts.Dispose();
    }
}
