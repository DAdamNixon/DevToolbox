using System.Diagnostics;
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

    /// <summary>
    /// The template the <c>logs</c> table was loaded with, empty until a load has replaced it.
    /// <para>
    /// Every query against that table resolves its default sort from this rather than from
    /// <see cref="SelectedTemplateName"/>, which is the <em>next</em> load's template and can be
    /// changed at any time. Resolved from the picker, a different template's sort columns are not in
    /// the table, they drop out, and the order silently falls back to newest-inserted first.
    /// </para>
    /// </summary>
    public string LoadedTemplateName { get; private set; } = "";

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

    // --- what is running ---

    /// <summary>
    /// A Load Logs is running: from the click until its final query has landed. Separate from
    /// <see cref="IsBusy"/> so a sort, a page or a filter can run <em>during</em> a load, against the
    /// rows it has stored so far, without cancelling it — which is the whole point of showing them.
    /// </summary>
    public bool IsIngesting { get; private set; }

    /// <summary>
    /// A short operation holds the view: a page query, a split change, a collapse, a CSV export,
    /// the first load of the configuration. One at a time; a query asked for meanwhile is queued
    /// and run when it finishes, with whatever the filter says by then.
    /// </summary>
    public bool IsBusy { get; private set; }

    /// <summary>Anything at all is running. What disables Load Logs and shows Cancel.</summary>
    public bool IsLoading => IsIngesting || IsBusy;

    /// <summary>
    /// The running load's rows are on screen: the grid shows the table as it fills, and the loading
    /// card has given way to the drawer at the top of the results. False while nothing has been
    /// stored yet, and again once the load is over.
    /// </summary>
    public bool IsLive { get; private set; }

    /// <summary>A sort, page, tab or split change would run now rather than wait.</summary>
    public bool CanQuery => !IsBusy && (!IsIngesting || IsLive);

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

    /// <summary>The loading drawer's file list is showing. Folded again at the start of every load.</summary>
    public bool IngestDrawerOpen { get; set; }

    /// <summary>Latest ingest progress, or null when not ingesting.</summary>
    public LogIngestProgress? Progress { get; private set; }

    /// <summary>
    /// Raised for every progress report, separately from <see cref="OnChanged"/>. Progress arrives
    /// several times a second; once rows are on screen only the drawer needs it, and re-rendering
    /// the whole page — grid included — that often costs more than the load.
    /// </summary>
    public event Action? ProgressChanged;

    /// <summary>
    /// What the last load did not manage to read, for the outcome line. Null once a new load starts,
    /// and while none has ever run. Set by a cancelled load too: its rows stay, so which files they
    /// are complete for matters.
    /// </summary>
    public LogPrepareResult? LastPrepareResult { get; private set; }

    /// <summary>How the last load ended, for the outcome line at the top of the results. Null while a load runs.</summary>
    public LoadSummary? LastLoad { get; private set; }

    /// <summary>What a finished load reports about itself.</summary>
    /// <param name="Rows">Rows in the table when it ended.</param>
    /// <param name="Files">Files it set out to read.</param>
    /// <param name="Cancelled">Stopped by Cancel rather than finishing.</param>
    /// <param name="TableReplaced">
    /// It got as far as replacing the previous table. False on a cancel or failure before that: the
    /// rows on screen are still the previous load's, which is what the outcome line has to say.
    /// </param>
    /// <param name="Failed">Ended in an error; the error banner says which.</param>
    public sealed record LoadSummary(long Rows, int Files, TimeSpan Elapsed, bool Cancelled, bool TableReplaced, bool Failed);

    /// <summary>Hides the outcome line until the next load.</summary>
    public void DismissLoadSummary()
    {
        LastLoad = null;
        Notify();
    }

    /// <summary>
    /// Why the rows loaded so far could not be shown, when a live refresh failed. Cleared by the next
    /// one that succeeds. The load itself carries on regardless.
    /// </summary>
    public string? LiveError { get; private set; }

    /// <summary>
    /// Rises every time the view is replaced — a page, a sort, a filter, a tab, the final query —
    /// but not when a live refresh only adds rows to it. The page scrolls the grid back to the top
    /// when it moves, so a new page does not open at the old one's scroll offset.
    /// </summary>
    public int ViewVersion { get; private set; }

    /// <summary>The running search's Skip/SkipAll handle. Null while nothing is loading.</summary>
    private LogIngestControl? _ingestControl;

    /// <summary>Abandons one stalled file, named by <see cref="FileProgressSnapshot.FileKey"/>.</summary>
    public void SkipFile(string fileKey) => _ingestControl?.Skip(fileKey);

    /// <summary>
    /// Cancel has been pressed but the operation has not unwound yet. Drives the
    /// button's "Cancelling…" state so a Cancel during a long SQLite batch does not
    /// look ignored.
    /// </summary>
    public bool IsCancelling { get; private set; }
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
        // than resetting to page 1 of the default sort. Carried over whenever the strip is rebuilt
        // with the tab still in it — which during a load is every refresh.
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

    /// <summary>The running load's own token. Cancel stops it; nothing a user does to the view does.</summary>
    private CancellationTokenSource? _ingestCts;

    /// <summary>The view operation running now. Replaced, and so cancelled, by the next one.</summary>
    private CancellationTokenSource? _viewCts;

    /// <summary>The live refresh in flight, if any. Cancelled by anything that replaces the view.</summary>
    private CancellationTokenSource? _tickCts;

    /// <summary>
    /// Moves with every view-defining operation. Each applies its result only if it is still the
    /// latest when the result arrives, so a slow query can never paint over a newer one — and a
    /// live refresh, which reads without moving it, is dropped when anything replaces the view.
    /// </summary>
    private int _viewGeneration;

    /// <summary>A query was asked for while another operation held the view; run it when that finishes.</summary>
    private bool _viewQueued;

    /// <summary>The queued query should rebuild the tab strip too — the filter behind the counts changed.</summary>
    private bool _tabsQueued;

    /// <summary>The operation holding <see cref="IsBusy"/>, so the end of a load can wait its turn.</summary>
    private Task _busyTask = Task.CompletedTask;

    /// <summary>Numbers each load, so a progress report posted by one that has finished is recognised and dropped.</summary>
    private int _ingestId;

    // Written by the ingest's threads the moment a snapshot is taken, read by the follower.
    private volatile LogIngestProgress? _latestProgress;
    private LiveCursor? _live;

    /// <summary>The follower has stopped: the table is as complete as this load will make it, so a query can run against it again.</summary>
    private bool _pumpDone;
    private string? _pendingTemplateName;

    /// <summary>The loaded template's file, so a rename in the editor can be followed. See <see cref="LoadedTemplateName"/>.</summary>
    private string? _loadedTemplateFile;
    private string _pendingDelimiter = "|";
    private bool _foldedByThisLoad;
    private bool _sourceTouchedSinceFold;
    private bool _disposed;

    // Discovery gets its own token source: it is triggered by changing a filter,
    // which must neither cancel a running search nor be cancelled by one.
    private CancellationTokenSource _discoveryCts = new();

    // --- live refresh pacing; settable for tests, which cannot wait out real seconds ---

    /// <summary>How often the load is checked for new rows. Only reads a field; nothing is queried unless something changed.</summary>
    internal TimeSpan LivePollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A load must have run this long before its rows replace the loading card. A small load is
    /// over before then, and gets one change on screen — card, then the finished grid — instead of
    /// three in a second.
    /// </summary>
    internal TimeSpan LiveStartDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>The least time between two live refreshes.</summary>
    internal TimeSpan LiveMinInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// After a refresh taking d, the next waits at least this many d. A refresh reads only the rows
    /// that are new, so d stays small; this is the backstop that keeps reads a small share of the
    /// load's time even when a filter makes each new row expensive to test.
    /// </summary>
    internal double LiveCostFactor { get; set; } = 4;

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
        IsBusy = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _busyTask = done.Task;
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
            IsBusy = false;
            done.TrySetResult();
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

            // The template the table was loaded with: renamed, it is the one with the same file;
            // deleted, queries fall back to the picker's, rather than every one failing on a name
            // that no longer exists.
            if (!string.IsNullOrEmpty(LoadedTemplateName) && !AvailableTemplates.Any(t => t.Name == LoadedTemplateName))
            {
                LoadedTemplateName = AvailableTemplates.FirstOrDefault(t =>
                    _loadedTemplateFile is not null && string.Equals(t.File, _loadedTemplateFile, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
            }

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
            else if (!HasSearched && !IsIngesting)
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
        await ApplyLocationDefaultTemplateAsync();
        await RefreshLogFileNamesAsync();
    }

    public async Task ToggleAllLocationsAsync()
    {
        SelectedLocations = AllLocationsSelected ? new() : new(LogLocations);
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
    /// <para>
    /// And only until a load has filled <see cref="Logs"/>: after that its columns are the table's,
    /// overflow and provenance included, and a template picked for the <em>next</em> load must not
    /// strip them off the rows already on screen.
    /// </para>
    /// </summary>
    public async Task UpdateTableColumnsAsync()
    {
        try
        {
            var templateEntry = AvailableTemplates.FirstOrDefault(t => t.Name == SelectedTemplateName);
            if (templateEntry != null && !HasSearched && !IsIngesting)
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
    /// Cancels whatever is running and leaves it cancelled. This is the Cancel button.
    /// <para>
    /// During a load it stops the load — the rows already stored stay, and stay searchable — and
    /// never just the query that happens to be running beside it. The token goes first, then
    /// <see cref="LogIngestControl.SkipAll"/>: the writer decides what to purge by each file's skip
    /// reason, so the order is not what keeps those rows, but a cancelled token is what stops it
    /// storing any more.
    /// </para>
    /// </summary>
    public void CancelSearch()
    {
        if (IsIngesting)
        {
            // Every file is read; what is left is the final query, which is not the load and is over
            // in a moment. Cancelling now would only mislabel a complete load as a cancelled one.
            if (_pumpDone) return;

            // The queue is left alone: a filter typed while the files were being listed still has to
            // run, against the previous table if the cancel lands before this one replaced it.
            _ingestCts?.Cancel();
            _ingestControl?.SkipAll();
            _tickCts?.Cancel();
        }
        else
        {
            _viewCts?.Cancel();
            _viewQueued = false;
            _tabsQueued = false;
        }

        IsCancelling = IsLoading;
        Notify();
    }

    public void SetError(string message) { ErrorMessage = message; Notify(); }
    public void ClearError() { ErrorMessage = ""; Notify(); }

    /// <summary>
    /// "Load Logs": re-ingests the selected files. Drops <see cref="Results"/> first, before the
    /// prepare runs, so a failed or cancelled load leaves none behind — results are scratch
    /// (2026-08-18) and a stale collapse pointed at a table about to be rebuilt from underneath it
    /// would be actively misleading, not merely unhelpful.
    /// <para>
    /// The logs view itself is left exactly as it is until the load replaces the table: a load
    /// cancelled before that point has changed nothing, and the rows on screen are still right.
    /// </para>
    /// </summary>
    public async Task SearchAsync()
    {
        ClearError();
        if (string.IsNullOrWhiteSpace(SelectedTemplateName)) { SetError("Please select a template."); return; }
        if (SelectedLocations.Count == 0) { SetError("Please select at least one location."); return; }
        if (StartDate > EndDate) { SetError("Start date cannot be after end date."); return; }

        await DropResultsAsync();
        await PrepareAndQueryAsync();
    }

    /// <summary>
    /// Runs a load and shows its rows as they arrive.
    /// <para>
    /// The ingest runs on the pool; this method stays on the caller's context and follows it
    /// (<see cref="FollowIngestAsync"/>). Once the load has replaced the table and has been running
    /// a moment, the grid shows the rows stored so far, in the order they were read, and keeps up as
    /// more arrive: each refresh reads only the rows added since the last one, in one snapshot, so it
    /// costs the new rows and not the table (see <see cref="LogLiveRequest"/>). When the load
    /// finishes, one final query puts the grid in the template's order, as it always has.
    /// </para>
    /// </summary>
    public async Task PrepareAndQueryAsync()
    {
        if (IsLoading) return;

        var templateEntry = AvailableTemplates.FirstOrDefault(t => t.Name == SelectedTemplateName);
        if (templateEntry == null) { SetError($"Template '{SelectedTemplateName}' not found."); return; }

        IsIngesting = true;
        IsLive = false;
        IsCancelling = false;
        LiveError = null;
        Progress = null;
        _latestProgress = null;
        _live = null;
        LastPrepareResult = null;
        LastLoad = null;
        IngestDrawerOpen = false;
        _foldedByThisLoad = false;
        _sourceTouchedSinceFold = false;
        _pumpDone = false;
        var ingestId = ++_ingestId;
        Notify();

        var started = Stopwatch.GetTimestamp();
        var cancelled = false;
        var tableReplaced = false;
        Exception? failure = null;
        LogIngestControl? control = null;
        var fileCount = 0;

        try
        {
            _ingestCts?.Dispose();
            _ingestCts = new CancellationTokenSource();
            var token = _ingestCts.Token;

            // Snapshot inputs before entering Task.Run to avoid capturing mutable state
            var logFile = LogFile;
            var locations = SelectedLocations.ToList();
            var start = StartDate;
            var end = EndDate;
            var templateName = templateEntry.Name;

            _pendingTemplateName = templateName;
            try
            {
                var template = await _logFileService.LoadTemplateAsync(templateEntry.File);
                _pendingDelimiter = template?.Delimiter ?? "|";
            }
            catch (InvalidOperationException)
            {
                _pendingDelimiter = TemplateDelimiter;
            }

            var settings = await LogIngestSettingsStore.LoadAsync(_yamlStorage);
            control = _ingestControl = new LogIngestControl(settings);

            // Progress arrives on the caller's context (Progress<T> captures it). A report from a
            // load that has since finished — an abandoned read waking long after, a heartbeat racing
            // the end — is dropped, or it would put the old load's headline back on screen.
            var sink = new Progress<LogIngestProgress>(p =>
            {
                if (ingestId != _ingestId || !IsIngesting) return;
                Progress = p;
                ProgressChanged?.Invoke();

                // Before the rows are on screen the loading card shows this, so the page re-renders
                // for it; after, only the drawer does, and it listens to ProgressChanged itself.
                if (!IsLive) Notify();
            });
            var progress = new LatestProgress(sink, p =>
            {
                if (ingestId == _ingestId) _latestProgress = p;
            });

            // Task.Run keeps heavy file I/O off the UI thread (Blazor Hybrid sync context)
            var prepareTask = Task.Run(
                () => _logFileService.PrepareLogTableAsync(logFile, locations, start, end, templateName, progress, control, token),
                token);

            try
            {
                await FollowIngestAsync(prepareTask, control, started, token);
            }
            catch (Exception)
            {
                // The follower is built not to throw. If it does anyway, stop the load rather than
                // leave it writing unwatched under a page that thinks it has finished.
                _ingestCts.Cancel();
                control.SkipAll();
            }
            finally
            {
                _pumpDone = true;
            }

            LogPrepareResult? prepared = null;
            try
            {
                prepared = await prepareTask;
            }
            catch (LogIngestCancelledException ex)
            {
                cancelled = true;
                LastPrepareResult = ex.Result;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            fileCount = _latestProgress?.FilesTotal ?? 0;
            tableReplaced = control.TableDropped;

            // The prepare waits for its writer, so the table now holds everything this load is ever
            // going to store. Whatever query the user started meanwhile finishes first.
            await WaitForIdleAsync();

            if (_disposed)
            {
                // The window this belonged to has gone; a final query over the whole table would be
                // for nobody.
            }
            else if (prepared is not null)
            {
                LastPrepareResult = prepared;
                await RunBusyAsync(async () =>
                {
                    if (!IsLive) AdoptTable(prepared.TableName);
                    Logs.TableName = prepared.TableName;
                    await FinalQueryAsync();
                }, "Search failed");
            }
            else if (control.ReadyTable is { } partialTable)
            {
                // Cancelled or failed after the table was replaced: the previous rows are gone, and
                // what is there now is what this load read. Show that, not what was on screen before.
                await RunBusyAsync(async () =>
                {
                    if (!IsLive) AdoptTable(partialTable);
                    await FinalQueryAsync();
                }, "Could not show the rows read before the load stopped");
            }
            else if (_viewQueued && !tableReplaced)
            {
                // Stopped before touching the previous table, with a query asked for meanwhile — a
                // filter typed while the files were being listed. That table is still the right one.
                _viewQueued = false;
                var tabs = _tabsQueued;
                _tabsQueued = false;
                await RequestViewAsync(tabs);
            }
            else if (tableReplaced)
            {
                // Dropped and never recreated: there is no table, so there is nothing to show.
                AdoptTable(Logs.TableName);
                HasSearched = false;
            }

            // Otherwise it stopped before touching the previous table, and the view — rows, counts,
            // sort, page — still describes it exactly. Leave it be.
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }
        finally
        {
            if (failure is not null)
                SetError($"Search failed: {failure.Message}");

            var rows = _latestProgress?.RowsInTable ?? 0;
            LastLoad = new LoadSummary(rows, fileCount, Stopwatch.GetElapsedTime(started), cancelled, tableReplaced, failure is not null);

            if (cancelled || failure is not null)
            {
                // The page's rule: an error or a cancel means the user is about to adjust the very
                // fields the fold would hide. Undo this load's own fold, unless they have touched the
                // card since.
                if (_foldedByThisLoad && !_sourceTouchedSinceFold) SourceCollapsed = false;
            }
            else if (!_foldedByThisLoad && !_sourceTouchedSinceFold && string.IsNullOrEmpty(ErrorMessage) && Logs.FilteredLogLines.Count > 0)
            {
                // Fold the source card once a search lands, so the results get the
                // screen. Only on success: an error or a cancel means the user is about
                // to adjust the very fields the fold would hide.
                SourceCollapsed = true;
            }

            IsIngesting = false;
            IsLive = false;
            IsCancelling = false;
            Progress = null;
            _live = null;
            _ingestControl = null;
            _tickCts?.Cancel();
            Notify();
        }
    }

    /// <summary>The source card's fold was toggled by hand, so this load will not undo it.</summary>
    public void ToggleSourceCollapsed()
    {
        SourceCollapsed = !SourceCollapsed;
        _sourceTouchedSinceFold = true;
    }

    /// <summary>
    /// The first query of a table the running load has just built: forgets everything that
    /// described the previous one — page, sort, counts, tabs, rows — and remembers the template the
    /// new one was built from.
    /// </summary>
    private void AdoptTable(string tableName)
    {
        Logs.TableName = tableName;
        LoadedTemplateName = _pendingTemplateName ?? SelectedTemplateName;
        _loadedTemplateFile = AvailableTemplates.FirstOrDefault(t => t.Name == LoadedTemplateName)?.File;
        TemplateDelimiter = _pendingDelimiter;
        Logs.ResetPagination();
        Logs.ActiveSorts.Clear();
        Logs.FilteredLogLines = new();
        Logs.ActiveTabIndex = 0;
        Logs.Tabs = new List<LogTab> { new() { Value = null, Label = "All" } };
        HasSearched = true;
        _live = null;
    }

    /// <summary>
    /// Follows a running prepare until it ends, putting its rows on screen as they arrive.
    /// <para>
    /// A loop on the caller's context rather than a timer, so every refresh is applied where the
    /// rest of the state is changed, one at a time. Each pass only reads a field unless something
    /// is worth querying: the table exists (<see cref="LogIngestControl.ReadyTable"/>), the page is
    /// showing, nothing else holds the view, the table has changed since the last refresh
    /// (<see cref="LogIngestProgress.TableVersion"/>), and the last refresh was long enough ago.
    /// </para>
    /// <para>
    /// Never throws, and never outlives the prepare: a refresh still in flight when the prepare
    /// finishes is interrupted, so the final query is not held up behind it.
    /// </para>
    /// </summary>
    private async Task FollowIngestAsync(Task prepareTask, LogIngestControl control, long started, CancellationToken ingestToken)
    {
        var nextAllowed = 0L;

        while (!prepareTask.IsCompleted)
        {
            await Task.WhenAny(prepareTask, Task.Delay(LivePollInterval));
            if (prepareTask.IsCompleted) return;

            // Cancelled, or the window closed: nothing more to show, but the load is not over until the
            // prepare has unwound — the table may be half dropped, half filled — so wait it out here
            // rather than let the table count as queryable before it is.
            if (ingestToken.IsCancellationRequested || _disposed)
            {
                await Task.WhenAny(prepareTask);
                return;
            }

            if (control.ReadyTable is not { } table) continue;
            if (_latestProgress is not { } p) continue;
            if (IsBusy) continue;

            if (!IsLive)
            {
                if (Stopwatch.GetElapsedTime(started) < LiveStartDelay) continue;
                if (p.RowsInTable == 0) continue;

                await RunLiveAsync(prepareTask, () => GoLiveAsync(table, p));
                continue;
            }

            // Nobody is looking (the page is on another tab), or SQL mode, whose query is the user's
            // own and is run when they run it, not on a timer.
            if (OnChanged is null || Logs.AdvancedRawMode || Results is not null) continue;
            if (_live is { } cursor && cursor.Version == p.TableVersion) continue;
            if (Stopwatch.GetTimestamp() < nextAllowed) continue;

            var tickStarted = Stopwatch.GetTimestamp();
            await RunLiveAsync(prepareTask, () => _live is { } c && c.PurgedSeen == p.RowsPurged
                ? LiveTickAsync(c, p)
                : LiveResetFromTickAsync());

            var took = Stopwatch.GetElapsedTime(tickStarted);
            var wait = TimeSpan.FromTicks(Math.Max(LiveMinInterval.Ticks, (long)(took.Ticks * LiveCostFactor)));
            nextAllowed = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        }
    }

    /// <summary>
    /// Runs one live refresh under its own token, racing the prepare: if the prepare ends first the
    /// refresh is interrupted rather than waited for. Swallows everything — a refresh that fails says
    /// so in <see cref="LiveError"/> and the next one tries again; the load carries on regardless.
    /// </summary>
    private async Task RunLiveAsync(Task prepareTask, Func<Task> refresh)
    {
        // Replaced, not disposed: a statement still unwinding may yet register on the old token.
        _tickCts = CancellationTokenSource.CreateLinkedTokenSource(_ingestCts?.Token ?? CancellationToken.None);

        Task work;
        try
        {
            work = refresh();
        }
        catch (Exception ex)
        {
            LiveError = ex.Message;
            return;
        }

        await Task.WhenAny(work, prepareTask);
        if (!work.IsCompleted)
        {
            // The load ended mid-refresh; the final query supersedes it, so do not make that wait.
            _tickCts.Cancel();
            _viewCts?.Cancel();
        }

        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LiveError = ex.Message;
            Notify();
        }
    }

    /// <summary>
    /// The moment the rows start showing: the view forgets the previous table and reads this one
    /// from the start, with the filter and split the user has set. Held as a view operation, so a
    /// request made meanwhile queues behind it and runs against the live table straight after.
    /// </summary>
    private Task GoLiveAsync(string table, LogIngestProgress progress) =>
        RunBusyAsync(async () =>
        {
            AdoptTable(table);

            // Live from here even if this first read fails: a carried-over SQL query that does not fit
            // this load's columns must show its error over an empty grid the user can fix, not keep the
            // loading card up for the whole load with no explanation.
            IsLive = true;

            // This read uses the filter, sort and page as they are now, which covers anything asked
            // for while the loading card was up; a request made during it still queues behind it.
            _viewQueued = false;
            _tabsQueued = false;
            var (token, gen) = BeginView();
            await QueryViewAsync(Logs, token, gen, rebuildTabs: true);

            // Fold the source card as the rows arrive, so they get the screen — but only if there
            // are rows to give it to, and the load is still going.
            if (Logs.FilteredLogLines.Count > 0 && string.IsNullOrEmpty(ErrorMessage) &&
                _ingestCts is { IsCancellationRequested: false } && !SourceCollapsed)
            {
                SourceCollapsed = true;
                _foldedByThisLoad = true;
                _sourceTouchedSinceFold = false;
            }
        }, "Could not show the rows loaded so far");

    // --- the live view ---

    /// <summary>
    /// What the live grid was built from, and where it has read up to. Ticks extend it; anything
    /// that changes what the user is looking at replaces it.
    /// <para>
    /// Holds its own copy of the filter and sort rather than reading the boxes: those change on
    /// every keystroke, and a refresh landing between two of them would otherwise run a half-typed
    /// filter the debounce exists to hold back.
    /// </para>
    /// </summary>
    private sealed class LiveCursor
    {
        public required string Table { get; init; }
        public required LogSearchCriteria? Criteria { get; init; }
        public required LogSplitFilter? Split { get; init; }
        public required LogSplitMode SplitMode { get; init; }

        /// <summary>The user's sort, or null for the order the rows were read in. A sorted grid is not appended to.</summary>
        public required List<SortColumn>? Sort { get; init; }
        public required int Page { get; init; }
        public required int PageSize { get; init; }

        /// <summary>The newest row reflected.</summary>
        public long MaxRowid { get; set; }

        /// <summary>Matches so far, filter and tab applied.</summary>
        public int Count { get; set; }

        /// <summary>Matches per split value, filter applied, in the order the values first appeared.</summary>
        public List<LogSplitGroup> Groups { get; } = new();

        /// <summary>The table version reflected, so a tick with nothing new is skipped.</summary>
        public long Version { get; set; }

        /// <summary>Rows purged when this was last read from the start. A purge breaks append-only, so it means reading from the start again.</summary>
        public long PurgedSeen { get; set; }
    }

    /// <summary>
    /// Reads the live view from the start: count, groups and the current page, in one snapshot. The
    /// page is in arrival order unless the user has sorted — a sort is a full pass over everything
    /// read so far, run because they asked, once.
    /// </summary>
    /// <param name="basis">
    /// The view to read, when it is not to be read from the boxes: the cursor being replaced, for a
    /// reset a live refresh started after a purge — the user has changed nothing, and the boxes may be
    /// halfway through an edit the debounce is still holding back. Null reads the view as it is now.
    /// </param>
    private async Task LiveResetAsync(CancellationToken token, int gen, bool fromTick = false, LiveCursor? basis = null)
    {
        var state = Logs;
        var table = state.TableName;
        var progressAtStart = _latestProgress;

        LiveCursor cursor;
        if (basis is not null)
        {
            cursor = new LiveCursor
            {
                Table = table,
                Criteria = basis.Criteria,
                Split = basis.Split,
                SplitMode = basis.SplitMode,
                Sort = basis.Sort,
                Page = basis.Page,
                PageSize = basis.PageSize,
                Version = progressAtStart?.TableVersion ?? -1,
                PurgedSeen = progressAtStart?.RowsPurged ?? 0
            };
        }
        else
        {
            var criteria = BuildCriteria(state);
            cursor = new LiveCursor
            {
                Table = table,
                Criteria = criteria.HasContent ? criteria : null,
                Split = state.CurrentSplitFilter,
                SplitMode = state.SplitMode,
                Sort = state.ActiveSorts.Count > 0
                    ? state.ActiveSorts.Select(s => new SortColumn { Column = s.Column, Direction = s.Direction }).ToList()
                    : null,
                Page = state.CurrentPage,
                PageSize = PageSize,
                Version = progressAtStart?.TableVersion ?? -1,
                PurgedSeen = progressAtStart?.RowsPurged ?? 0
            };
        }

        var slice = await ReadSliceAsync(cursor, afterRowid: 0, skip: cursor.Page * cursor.PageSize, take: cursor.PageSize, sort: cursor.Sort, token);
        if (token.IsCancellationRequested || gen != _viewGeneration) return;

        cursor.MaxRowid = slice.MaxRowid;
        cursor.Count = slice.Count;
        cursor.Groups.AddRange(slice.Groups);
        _live = cursor;

        state.TotalRecords = cursor.Count;
        state.TotalPages = (int)Math.Ceiling(cursor.Count / (double)cursor.PageSize);

        // The page no longer exists — a purge took rows out from under it. Go to the last one.
        if (state.TotalPages > 0 && cursor.Page >= state.TotalPages)
        {
            state.CurrentPage = state.TotalPages - 1;
            state.PageInput = state.CurrentPage + 1;
            await LiveResetAsync(token, gen, fromTick, Reread(cursor, cursor.Split, state.CurrentPage));
            return;
        }

        ApplyRows(state, LogRowReuse.Reuse(state.FilteredLogLines, slice.Rows), cursor.Page, cursor.PageSize, fromTick);
        ApplyLiveTabs(state, cursor);
        LiveError = null;

        // The tab being read is gone — a purge emptied it — and the strip fell back to All. Read All;
        // left alone, the cursor would go on counting a tab nobody can see, and All would show nothing.
        if (cursor.Split is not null && state.CurrentSplitFilter is null)
        {
            state.CurrentPage = 0;
            state.PageInput = 1;
            await LiveResetAsync(token, gen, fromTick, Reread(cursor, null, 0));
        }
    }

    /// <summary>The same view as <paramref name="cursor"/>, on another tab or page.</summary>
    private static LiveCursor Reread(LiveCursor cursor, LogSplitFilter? split, int page) => new()
    {
        Table = cursor.Table,
        Criteria = cursor.Criteria,
        Split = split,
        SplitMode = cursor.SplitMode,
        Sort = cursor.Sort,
        Page = page,
        PageSize = cursor.PageSize
    };

    /// <summary>A tick that has to start over — the first after a purge. The view is not being replaced, so the generation does not move.</summary>
    private async Task LiveResetFromTickAsync()
    {
        var gen = _viewGeneration;
        var token = _tickCts?.Token ?? CancellationToken.None;
        await LiveResetAsync(token, gen, fromTick: true, basis: _live);
        if (gen == _viewGeneration && !token.IsCancellationRequested) Notify();
    }

    /// <summary>
    /// A refresh that reads only what arrived since the last one and adds it on: counts and tab
    /// counts grow, and a page that was not yet full gets its next rows. Rows already on screen are
    /// never moved or replaced, so nothing shifts under someone reading them.
    /// </summary>
    private async Task LiveTickAsync(LiveCursor cursor, LogIngestProgress progress)
    {
        var gen = _viewGeneration;
        var token = _tickCts?.Token ?? CancellationToken.None;
        var state = Logs;
        var versionAtStart = progress.TableVersion;

        // Matches are numbered in arrival order, and the new ones come after every match already
        // counted. The page covers [first, end); what of it the new matches can still fill starts at
        // whichever is later, the end of the old matches or the start of the page.
        var first = cursor.Page * cursor.PageSize;
        var end = first + cursor.PageSize;
        var fillFrom = Math.Max(cursor.Count, first);
        var take = cursor.Sort is null && fillFrom < end ? end - fillFrom : 0;
        var skip = Math.Max(0, fillFrom - cursor.Count);

        var slice = await ReadSliceAsync(cursor, afterRowid: cursor.MaxRowid, skip, take, sort: null, token);
        if (token.IsCancellationRequested || gen != _viewGeneration || !ReferenceEquals(_live, cursor)) return;

        cursor.MaxRowid = slice.MaxRowid;
        cursor.Count += slice.Count;
        cursor.Version = versionAtStart;
        foreach (var group in slice.Groups)
        {
            var existing = cursor.Groups.FirstOrDefault(g => g.Value == group.Value);
            if (existing is null) cursor.Groups.Add(new LogSplitGroup { Value = group.Value, Count = group.Count });
            else existing.Count += group.Count;
        }

        state.TotalRecords = cursor.Count;
        state.TotalPages = (int)Math.Ceiling(cursor.Count / (double)cursor.PageSize);

        if (slice.Rows.Count > 0)
        {
            // The rows already on screen stay the same objects; the new ones go after them.
            var rows = new List<Dictionary<string, string>>(state.FilteredLogLines.Count + slice.Rows.Count);
            rows.AddRange(state.FilteredLogLines);
            rows.AddRange(slice.Rows);
            ApplyRows(state, rows, cursor.Page, cursor.PageSize, fromTick: true);
        }
        else
        {
            state.HasMorePages = state.FilteredLogLines.Count == cursor.PageSize && (cursor.Page + 1) < state.TotalPages;
        }

        ApplyLiveTabs(state, cursor);
        LiveError = null;
        Notify();
    }

    private Task<LogLiveSlice> ReadSliceAsync(LiveCursor cursor, long afterRowid, int skip, int take, List<SortColumn>? sort, CancellationToken token)
    {
        LogSplitColumns.TryResolve(cursor.SplitMode, out var groupColumn);
        var request = new LogLiveRequest
        {
            AfterRowid = afterRowid,
            Criteria = cursor.Criteria,
            Split = cursor.Split,
            GroupColumn = string.IsNullOrEmpty(groupColumn) ? null : groupColumn,
            Skip = skip,
            Take = take,
            Sort = sort
        };

        // Task.Run keeps SQLite off the UI thread
        return Task.Run(() => _logFileService.ReadLiveSliceAsync(cursor.Table, request, token), token);
    }

    /// <summary>
    /// The tab strip from the live counts. Tabs keep their order and their parked page and sort as
    /// the counts grow, and a value seen for the first time is added at the end — never slotted in
    /// before the tab under the pointer. The final query puts them in order.
    /// </summary>
    private static void ApplyLiveTabs(LogFilterState state, LiveCursor cursor)
    {
        var previous = state.Tabs;
        var activeValue = state.ActiveTab?.Value;

        LogTab Carry(LogTab tab)
        {
            var old = previous.FirstOrDefault(t => t.Value == tab.Value);
            if (old is not null)
            {
                tab.CurrentPage = old.CurrentPage;
                tab.Sorts = old.Sorts;
            }
            return tab;
        }

        if (cursor.SplitMode == LogSplitMode.None)
        {
            state.Tabs = new List<LogTab> { Carry(new LogTab { Value = null, Label = "All", RowCount = cursor.Count }) };
            state.ActiveTabIndex = 0;
            return;
        }

        var order = previous.Where(t => t.Value is not null).Select(t => t.Value!).ToList();
        foreach (var group in cursor.Groups)
            if (!order.Contains(group.Value)) order.Add(group.Value);

        var counts = cursor.Groups.ToDictionary(g => g.Value, g => g.Count);
        var tabs = new List<LogTab>
        {
            Carry(new LogTab { Value = null, Label = "All", RowCount = cursor.Groups.Sum(g => g.Count) })
        };
        tabs.AddRange(order
            .Where(counts.ContainsKey)
            .Select(v => Carry(new LogTab { Value = v, Label = string.IsNullOrEmpty(v) ? "(none)" : v, RowCount = counts[v] })));

        state.Tabs = tabs;
        var index = activeValue is null ? 0 : tabs.FindIndex(t => t.Value == activeValue);
        state.ActiveTabIndex = index >= 0 ? index : 0;
    }

    // --- view operations ---

    /// <summary>
    /// Runs one operation that holds the view, with <see cref="IsBusy"/> set, then any query asked
    /// for while it ran. Every owner of <see cref="IsBusy"/> comes through here, so a query queued
    /// behind any of them — a split, a collapse, a CSV export, the end of a load — is never lost.
    /// </summary>
    private async Task RunBusyAsync(Func<Task> work, string failure)
    {
        IsBusy = true;
        Notify();

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _busyTask = done.Task;
        try
        {
            var drain = true;
            try
            {
                await work();
            }
            catch (OperationCanceledException)
            {
                // Cancelled: Cancel cleared the queue itself, or the end of a load supersedes it.
                drain = false;
            }
            catch (Exception ex)
            {
                // A failed query still leaves the box holding whatever was typed behind it, and that
                // is what the grid has to end up showing.
                SetError($"{failure}: {ex.Message}");
            }

            if (drain) await DrainQueuedAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetError($"{failure}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            if (!IsIngesting) IsCancelling = false;
            done.TrySetResult();
            Notify();
        }
    }

    /// <summary>Waits until no operation holds the view.</summary>
    private async Task WaitForIdleAsync()
    {
        while (IsBusy)
            await _busyTask;
    }

    /// <summary>
    /// Starts a view-defining operation: the previous one's token is cancelled (and its statement
    /// interrupted), a live refresh in flight is dropped, and the generation moves.
    /// </summary>
    private (CancellationToken Token, int Generation) BeginView()
    {
        // Cancelled and replaced, not disposed: the operation it belonged to may still be unwinding,
        // and a storage call it makes on the way out registers on its token.
        _viewCts?.Cancel();
        _viewCts = new CancellationTokenSource();
        _tickCts?.Cancel();
        return (_viewCts.Token, ++_viewGeneration);
    }

    /// <summary>
    /// Whether the table can be queried now. Not while a load is running and has not put its rows on
    /// screen: until then the table is the previous search's, or between its drop and create, nothing.
    /// </summary>
    private bool TableIsQueryable =>
        !string.IsNullOrEmpty(CurrentTableName) && (!IsIngesting || IsLive || _pumpDone);

    private async Task DrainQueuedAsync()
    {
        while (_viewQueued && TableIsQueryable)
        {
            var tabs = _tabsQueued;
            _viewQueued = false;
            _tabsQueued = false;
            var (token, gen) = BeginView();
            await QueryViewAsync(Active, token, gen, tabs);
        }
    }

    /// <summary>
    /// Asks for the view to be queried again. Runs now if nothing holds the view; otherwise it is
    /// queued and runs as soon as that finishes, with whatever the filter says by then. Before a
    /// load's rows are on screen it only queues: the first live read, or the final query, will use
    /// the filter, sort and page as they are then.
    /// </summary>
    private Task RequestViewAsync(bool rebuildTabs, string failure = "Search failed")
    {
        if (string.IsNullOrEmpty(CurrentTableName)) return Task.CompletedTask;

        if (IsBusy || !TableIsQueryable)
        {
            _viewQueued = true;
            _tabsQueued |= rebuildTabs;
            return Task.CompletedTask;
        }

        return RunBusyAsync(async () =>
        {
            var (token, gen) = BeginView();
            await QueryViewAsync(Active, token, gen, rebuildTabs);
        }, failure);
    }

    public Task QueryCurrentPageAsync() => RequestViewAsync(rebuildTabs: false);

    /// <summary>
    /// Queries <paramref name="state"/> for its current filter, sort, tab and page. On the live
    /// table in keyword mode that is a live read from the start; everywhere else the page, its
    /// count and — when asked — the tab strip.
    /// </summary>
    private async Task QueryViewAsync(LogFilterState state, CancellationToken token, int gen, bool rebuildTabs)
    {
        // Live reads only while the load is still being followed. After that the table is complete,
        // and a request drained behind the final query must read it in the template's order, not
        // put the finished grid back into the order the rows were read in.
        if (IsLive && !_pumpDone && ReferenceEquals(state, Logs) && !state.AdvancedRawMode)
        {
            await LiveResetAsync(token, gen);
            return;
        }

        var splitQueried = state.CurrentSplitFilter;
        await QueryPageCoreAsync(state, token, gen);
        if (!rebuildTabs || token.IsCancellationRequested || gen != _viewGeneration) return;

        try
        {
            await RebuildTabsAsync(state, token, gen);
        }
        catch (InvalidOperationException ex)
        {
            // Tab counts are part of the filter's result, but a SQL query that does not project the
            // split column fails here and should not take the page it did produce down with it.
            SetError($"Could not refresh tab counts: {ex.Message}");
            return;
        }

        // The tab the page was read for is gone — the new filter leaves nothing in it — and the strip
        // fell back to All. Read All, rather than leave that tab's rows under All's heading.
        if (splitQueried is not null && state.CurrentSplitFilter is null && gen == _viewGeneration && !token.IsCancellationRequested)
        {
            state.CurrentPage = 0;
            await QueryPageCoreAsync(state, token, gen);
        }
    }

    /// <summary>
    /// The query that ends a load: the whole table in the template's order, with the tab strip
    /// rebuilt and sorted. A grid that was following the rows in arrival order goes back to page 1 —
    /// its page N meant nothing in sorted order. A grid the user sorted keeps its page.
    /// </summary>
    private async Task FinalQueryAsync()
    {
        // Reads the view as it is now, which covers any query queued before it.
        _viewQueued = false;
        _tabsQueued = false;
        var (token, gen) = BeginView();
        if (Logs.ActiveSorts.Count == 0)
        {
            Logs.CurrentPage = 0;
            Logs.PageInput = 1;
        }

        await QueryPageCoreAsync(Logs, token, gen);
        if (token.IsCancellationRequested || gen != _viewGeneration) return;
        await RebuildTabsAsync(Logs, token, gen);
    }

    /// <summary>The template to resolve <paramref name="state"/>'s default sort from.</summary>
    private string TemplateFor(LogFilterState state) =>
        ReferenceEquals(state, Logs) && !string.IsNullOrEmpty(LoadedTemplateName) ? LoadedTemplateName : SelectedTemplateName;

    private async Task QueryPageCoreAsync(LogFilterState state, CancellationToken token, int gen)
    {
        var templateName = TemplateFor(state);
        if (!AvailableTemplates.Any(t => t.Name == templateName)) { SetError($"Template '{templateName}' not found."); return; }

        var criteria = BuildCriteria(state);
        var criteriaArg = criteria.HasContent ? criteria : null;
        var sorts = state.AdvancedRawMode ? null : (state.ActiveSorts.Count > 0 ? state.ActiveSorts : null);

        // Snapshot locals for Task.Run closures
        var tableName = state.TableName;
        var page = state.CurrentPage;
        var pageSize = PageSize;

        var split = state.CurrentSplitFilter;

        // Task.Run keeps SQLite queries off the UI thread
        var countTask = Task.Run(() => _logFileService.CountLogEntriesAsync(tableName, criteriaArg, split, token), token);
        var dataTask = Task.Run(() => _logFileService.QueryLogPageAsync(tableName, templateName, page, pageSize, sorts, criteriaArg, split, token), token);
        await Task.WhenAll(countTask, dataTask);

        if (token.IsCancellationRequested || gen != _viewGeneration) return;

        state.TotalRecords = await countTask;
        state.TotalPages = (int)Math.Ceiling(state.TotalRecords / (double)pageSize);

        // Past the end — rows went while the user was on the last page. Go to the one that is.
        if (state.TotalPages > 0 && page >= state.TotalPages)
        {
            state.CurrentPage = state.TotalPages - 1;
            await QueryPageCoreAsync(state, token, gen);
            return;
        }

        ApplyRows(state, LogRowReuse.Reuse(state.FilteredLogLines, await dataTask ?? new()), page, pageSize);
    }

    /// <summary>Puts a page of rows on <paramref name="state"/>, with the columns and paging that follow from it.</summary>
    /// <param name="fromTick">
    /// A live refresh, which never touches the page box: the user may be halfway through typing a
    /// page number into it, and the page has not changed anyway.
    /// </param>
    private void ApplyRows(LogFilterState state, List<Dictionary<string, string>> rows, int page, int pageSize, bool fromTick = false)
    {
        state.FilteredLogLines = rows;

        if (rows.Count > 0)
        {
            // SourcePath stays on every row — double-click needs it to know which
            // file to open — but it is not a column anyone wants to read. Hiding it
            // here rather than dropping it keeps the grid unchanged while giving the
            // row somewhere to carry its origin.
            var columns = rows
                .OrderByDescending(l => l.Count)
                .First()
                .Keys
                .Where(k => !string.Equals(k, DbLogService.SourcePathColumn, StringComparison.Ordinal))
                .ToList();

            // Only replaced when it actually differs, so the page does not re-lay out its header
            // for a refresh that only added rows.
            if (!columns.SequenceEqual(state.TableColumns))
                state.TableColumns = columns;
        }

        state.HasMorePages = rows.Count == pageSize && (page + 1) < state.TotalPages;
        if (fromTick) return;

        // A new view's rows are in: the page scrolls back to the top for them (see ViewVersion).
        ViewVersion++;

        // Not when a request is queued behind this one: it already set the page the user asked for —
        // page 1 for a new filter, the one typed into the box — and writing this query's page back
        // over it would send the queued request to the wrong page.
        if (!_viewQueued) state.PageInput = page + 1;
    }

    // --- split tabs ---

    /// <summary>
    /// Rebuilds the tab strip from the current split mode and keyword filter.
    /// All is always tab 0, so turning splitting off is never a special case and
    /// the combined view is always one click away. Each tab still in the strip keeps its parked page
    /// and sort.
    /// </summary>
    private async Task RebuildTabsAsync(LogFilterState state, CancellationToken token, int gen)
    {
        var previous = state.Tabs;
        var previousValue = state.ActiveTab?.Value;

        LogTab Carry(LogTab tab)
        {
            var old = previous.FirstOrDefault(t => t.Value == tab.Value);
            if (old is not null)
            {
                tab.CurrentPage = old.CurrentPage;
                tab.Sorts = old.Sorts;
            }
            return tab;
        }

        if (state.SplitMode == LogSplitMode.None || string.IsNullOrEmpty(state.TableName))
        {
            state.Tabs = new List<LogTab> { Carry(new LogTab { Value = null, Label = "All", RowCount = state.TotalRecords }) };
            state.ActiveTabIndex = 0;
            return;
        }

        var criteria = BuildCriteria(state);
        var groups = await Task.Run(
            () => _logFileService.GetSplitGroupsAsync(state.TableName, state.SplitMode, criteria.HasContent ? criteria : null, token),
            token);

        if (token.IsCancellationRequested || gen != _viewGeneration) return;

        // All's count is the sum of the groups, not TotalRecords. The groups
        // partition the table under the same filter, so the sum is exact — and
        // TotalRecords describes whichever tab was last queried, which made the All
        // count show the previous tab's total for a moment after switching modes.
        var tabs = new List<LogTab>
        {
            Carry(new LogTab { Value = null, Label = "All", RowCount = groups.Sum(g => g.Count) })
        };
        tabs.AddRange(groups.Select(g => Carry(new LogTab
        {
            Value = g.Value,
            Label = string.IsNullOrEmpty(g.Value) ? "(none)" : g.Value,
            RowCount = g.Count
        })));
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
            state.Tabs = new List<LogTab> { new() { Value = null, Label = "All", RowCount = state.TotalRecords } };
            Notify();
            return;
        }

        state.CurrentPage = 0;
        state.PageInput = 1;
        await RequestViewAsync(rebuildTabs: true, failure: "Split failed");
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

    /// <summary>
    /// The filter changed: back to page 1 and query again, tab counts included. Not guarded on
    /// anything running — a change made while a query is in flight is queued behind it, never
    /// dropped, so the grid always ends up matching the box.
    /// </summary>
    public async Task RunLiveQueryAsync()
    {
        var state = Active;
        if (!HasSearched || string.IsNullOrEmpty(state.TableName)) return;

        // Page only; the counts stay until the query replaces them, rather than reading "of 0" meanwhile.
        state.CurrentPage = 0;
        state.PageInput = 1;
        await RequestViewAsync(rebuildTabs: state.SplitMode != LogSplitMode.None);
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

    /// <summary>Clears every keyword row — the empty state's way out of a filter that matches nothing.</summary>
    public async Task ClearFilterAsync()
    {
        var state = Active;
        state.KeywordRows = new() { new KeywordRow() };
        if (state.AdvancedRawMode)
        {
            state.AdvancedExpression = "";
            state.ActiveSavedQuery = null;
            state.ActiveSavedQuerySql = "";
        }
        Notify();
        await RunLiveQueryAsync();
    }

    public async Task SortByColumnAsync(string column, bool append)
    {
        var state = Active;
        if (!HasSearched || state.AdvancedRawMode || (IsIngesting && !IsLive)) return;
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

    // --- CSV ---

    /// <summary>
    /// Writes the active view — its filter, sort and tab, every page — to a CSV file and returns its
    /// path, or null when nothing was written. Refused while a load is running: the table is still
    /// filling, and a file of some of the rows would look like all of them.
    /// </summary>
    public async Task<string?> DownloadCsvAsync()
    {
        if (IsLoading || !HasSearched) return null;

        string? path = null;
        await RunBusyAsync(async () =>
        {
            var state = Active;
            var sorts = state.AdvancedRawMode ? null : (state.ActiveSorts.Count > 0 ? state.ActiveSorts : null);
            var criteria = BuildCriteria(state);
            var criteriaArg = criteria.HasContent ? criteria : null;
            var templateName = TemplateFor(state);
            var tableName = state.TableName;
            var split = state.CurrentSplitFilter;

            // Exports the active tab, not the whole table — the CSV should hold
            // what the grid is showing.
            path = await Task.Run(() => _logFileService.DownloadLogCsvAsync(tableName, templateName, sorts, criteriaArg, null, split));
        }, "CSV download failed");
        return path;
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

        await RunBusyAsync(async () =>
        {
            var criteria = BuildCriteria(Logs);
            var (rows, columns) = await _logFileService.MaterializeResultsAsync(
                Logs.TableName, TemplateFor(Logs), Logs.ActiveSorts, criteria, Logs.CurrentSplitFilter);

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

            var (token, gen) = BeginView();
            await QueryPageCoreAsync(results, token, gen);
        }, "Could not collapse into results");
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

    /// <summary>
    /// Stops everything, a running load included. Cancelling the token alone is not enough: a
    /// file blocked on a dead share ignores it, and only <see cref="LogIngestControl.SkipAll"/> lets
    /// the prepare stop waiting and release the process-wide load lock. A closed browser view left
    /// holding that lock would stall every later Load Logs, in every window, until a restart.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;

        // Cancelled, not disposed: the load unwinding from this still touches them on its way out.
        _ingestCts?.Cancel();
        _ingestControl?.SkipAll();
        _viewCts?.Cancel();
        _tickCts?.Cancel();
        _discoveryCts.Cancel();
        _discoveryCts.Dispose();
    }

    /// <summary>
    /// Keeps the newest snapshot the moment it is reported, then passes it on. The follower reads it
    /// from here rather than from <see cref="Progress"/>, which is set only once the post to the
    /// caller's context has run — and in a host without one, possibly out of order.
    /// </summary>
    private sealed class LatestProgress : IProgress<LogIngestProgress>
    {
        private readonly IProgress<LogIngestProgress> _inner;
        private readonly Action<LogIngestProgress> _store;

        public LatestProgress(IProgress<LogIngestProgress> inner, Action<LogIngestProgress> store)
        {
            _inner = inner;
            _store = store;
        }

        public void Report(LogIngestProgress value)
        {
            _store(value);
            _inner.Report(value);
        }
    }
}
