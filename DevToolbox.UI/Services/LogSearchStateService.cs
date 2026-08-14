using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

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
    public bool ShowLocationMenu { get; set; }
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

    public LogSearchStateService(ILogFileService logFileService, IYamlStorageService yamlStorage)
    {
        _logFileService = logFileService;
        _yamlStorage = yamlStorage;
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

    // --- location helpers ---

    public string LocationSummary => SelectedLocations.Count switch
    {
        0 => "Select location(s)...",
        1 => SelectedLocations[0].Name,
        _ => $"{SelectedLocations.Count} locations selected"
    };

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
            await QueryPageCoreAsync(token);
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

        // Task.Run keeps SQLite queries off the UI thread
        var countTask = Task.Run(() => _logFileService.CountLogEntriesAsync(tableName, criteriaArg, token), token);
        var dataTask = Task.Run(() => _logFileService.QueryLogPageAsync(tableName, templateName, page, pageSize, sorts, criteriaArg, token), token);
        await Task.WhenAll(countTask, dataTask);

        if (token.IsCancellationRequested) return;

        TotalRecords = await countTask;
        TotalPages = (int)Math.Ceiling(TotalRecords / (double)pageSize);

        var pageData = await dataTask;
        FilteredLogLines = pageData ?? new();

        if (pageData?.Any() == true)
            TableColumns = pageData.OrderByDescending(l => l.Count).FirstOrDefault()?.Keys.ToList() ?? new();

        HasMorePages = pageData?.Count == pageSize && (page + 1) < TotalPages;
        PageInput = page + 1;
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
    }

    public async Task OnAdvancedToggledAsync()
    {
        ActiveSorts.Clear();
        if (HasSearched) await RunLiveQueryAsync();
    }

    public async Task AddKeywordRowAsync()
    {
        KeywordRows.Add(new KeywordRow());
        await RunLiveQueryAsync();
    }

    public async Task RemoveKeywordRowAsync(int index)
    {
        if (KeywordRows.Count > 1 && index >= 0 && index < KeywordRows.Count)
        {
            KeywordRows.RemoveAt(index);
            await RunLiveQueryAsync();
        }
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
