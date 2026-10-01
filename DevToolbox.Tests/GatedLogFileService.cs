using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>
/// A real <see cref="DbLogService"/> with its page query open to being held — so a test can have a
/// query in flight, deterministically, while it asks for another.
/// </summary>
internal sealed class GatedLogFileService : ILogFileService
{
    private readonly DbLogService _inner;

    public GatedLogFileService(DbLogService inner) => _inner = inner;

    /// <summary>Runs before each page query; can hold it. Set to null to stop holding.</summary>
    public Func<Task>? BeforePage { get; set; }

    public async Task<List<Dictionary<string, string>>> QueryLogPageAsync(string tableName, string templateName, int pageNumber, int pageSize,
        List<SortColumn>? sortColumns, LogSearchCriteria? criteria, LogSplitFilter? split = null, CancellationToken cancellationToken = default)
    {
        if (BeforePage is { } hold) await hold();
        return await _inner.QueryLogPageAsync(tableName, templateName, pageNumber, pageSize, sortColumns, criteria, split, cancellationToken);
    }

    public Task<List<LogTemplateIndexEntry>> GetAvailableLogFileTemplatesAsync() => _inner.GetAvailableLogFileTemplatesAsync();
    public Task<List<LogLocation>> GetLogLocationsAsync() => _inner.GetLogLocationsAsync();
    public Task<LogTemplate?> LoadTemplateAsync(string fileName) => _inner.LoadTemplateAsync(fileName);
    public Task<List<DiscoveredLogName>> DiscoverLogFileNamesAsync(IReadOnlyList<LogLocation> locations, string templateName, CancellationToken cancellationToken = default) =>
        _inner.DiscoverLogFileNamesAsync(locations, templateName, cancellationToken);
    public Task<LogPrepareResult> PrepareLogTableAsync(string logFile, IReadOnlyList<LogLocation> locations, DateTime startDate, DateTime endDate, string templateName,
        IProgress<LogIngestProgress>? progress = null, LogIngestControl? control = null, CancellationToken cancellationToken = default) =>
        _inner.PrepareLogTableAsync(logFile, locations, startDate, endDate, templateName, progress, control, cancellationToken);
    public Task<int> CountLogEntriesAsync(string tableName, LogSearchCriteria? criteria, LogSplitFilter? split = null, CancellationToken cancellationToken = default) =>
        _inner.CountLogEntriesAsync(tableName, criteria, split, cancellationToken);
    public Task<LogLiveSlice> ReadLiveSliceAsync(string tableName, LogLiveRequest request, CancellationToken cancellationToken = default) =>
        _inner.ReadLiveSliceAsync(tableName, request, cancellationToken);
    public Task<List<LogSplitGroup>> GetSplitGroupsAsync(string tableName, LogSplitMode mode, LogSearchCriteria? criteria, CancellationToken cancellationToken = default) =>
        _inner.GetSplitGroupsAsync(tableName, mode, criteria, cancellationToken);
    public Task<string> DownloadLogCsvAsync(string tableName, string templateName, List<SortColumn>? sortColumns, LogSearchCriteria? criteria,
        string? outputPath = null, LogSplitFilter? split = null, CancellationToken cancellationToken = default) =>
        _inner.DownloadLogCsvAsync(tableName, templateName, sortColumns, criteria, outputPath, split, cancellationToken);
    public Task<(string TableName, int Rows, List<string> Columns)> MaterializeResultsAsync(string sourceTable, string templateName, List<SortColumn>? sorts,
        LogSearchCriteria? criteria, LogSplitFilter? split, CancellationToken cancellationToken = default) =>
        _inner.MaterializeResultsAsync(sourceTable, templateName, sorts, criteria, split, cancellationToken);
    public Task DropResultsAsync() => _inner.DropResultsAsync();
}
