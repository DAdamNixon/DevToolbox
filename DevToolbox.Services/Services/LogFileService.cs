using DevToolbox.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    // Legacy config-only implementation. Superseded by DbLogService (the registered ILogFileService).
    public class LogFileService : ILogFileService
    {
        private readonly IYamlStorageService _yamlStorage;

        public LogFileService(IYamlStorageService yamlStorage)
        {
            _yamlStorage = yamlStorage;
        }

        public async Task<List<LogTemplateIndexEntry>> GetAvailableLogFileTemplatesAsync()
        {
            var config = await _yamlStorage.LoadAsync<LogTemplateIndexConfig>("log_templates_index") ?? new LogTemplateIndexConfig();
            return config.Templates;
        }

        public async Task<List<LogLocation>> GetLogLocationsAsync()
        {
            var config = await _yamlStorage.LoadAsync<LogLocationConfig>("log_paths") ?? new LogLocationConfig();
            return config.LogLocations;
        }

        public async Task<LogTemplate?> LoadTemplateAsync(string fileName)
        {
            return await _yamlStorage.LoadAsync<LogTemplate>(Path.GetFileNameWithoutExtension(fileName));
        }

        public Task<List<DiscoveredLogName>> DiscoverLogFileNamesAsync(IReadOnlyList<LogLocation> locations, string templateName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Use DbLogService.");

        public Task<string> PrepareLogTableAsync(string logFile, IReadOnlyList<LogLocation> locations, DateTime startDate, DateTime endDate, string templateName, IProgress<LogIngestProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Use DbLogService.");

        public Task<List<Dictionary<string, string>>> QueryLogPageAsync(string tableName, string templateName, int pageNumber, int pageSize, List<SortColumn>? sortColumns, LogSearchCriteria? criteria, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Use DbLogService.");

        public Task<int> CountLogEntriesAsync(string tableName, LogSearchCriteria? criteria, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Use DbLogService.");

        public Task<string> DownloadLogCsvAsync(string tableName, string templateName, List<SortColumn>? sortColumns, LogSearchCriteria? criteria, string? outputPath = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Use DbLogService.");
    }
}