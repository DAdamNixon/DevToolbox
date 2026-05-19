using DevToolbox.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    public class LogFileService : ILogFileService
    {
        private readonly IYamlStorageService _yamlStorage;
        private static readonly Dictionary<(string logFile, string location, DateTime startDate, DateTime endDate, string templateName, string searchTerm), (int count, DateTime timestamp)> _countCache = new();
        private static readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

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

        public async Task<int> CountLogEntriesAsync(string logFile, string location, DateTime startDate, DateTime endDate, string templateName, string searchTerm)
        {
            var key = (logFile, location, startDate, endDate, templateName, searchTerm ?? string.Empty);

            lock (_countCache)
            {
                var expiredKeys = _countCache
                    .Where(kvp => DateTime.UtcNow - kvp.Value.timestamp > _cacheDuration)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var expiredKey in expiredKeys)
                    _countCache.Remove(expiredKey);

                if (_countCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.timestamp <= _cacheDuration)
                    return cached.count;
            }

            int count = 0;
            await foreach (var entry in ((ILogFileService)this).SearchLogFilesAsync_v2(logFile, location, startDate, endDate, templateName))
            {
                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    if (!entry.Values.Any(v => v.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }
                count++;
            }

            lock (_countCache)
            {
                _countCache[key] = (count, DateTime.UtcNow);
            }

            return count;
        }

        public async IAsyncEnumerable<Dictionary<string, string>> SearchLogFilesAsync_v2(string logFile, string location, DateTime startDate, DateTime endDate, string templateName)
        {
            if (!Directory.Exists(location))
                yield break;

            var templateEntries = await GetAvailableLogFileTemplatesAsync();
            var templateEntry = templateEntries.FirstOrDefault(t => t.Name == templateName);
            if (templateEntry == null)
                throw new Exception($"Template '{templateName}' not found in index.");

            var template = await LoadTemplateAsync(templateEntry.File);
            if (template == null)
                throw new Exception($"Template file '{templateEntry.File}' could not be loaded.");

            var columns = await ResolveColumnsAsync(template);

            var files = Directory.GetFiles(location, $"{logFile}*{template.Extension}");
            foreach (var file in files)
            {
                var fileInfo = new System.IO.FileInfo(file);
                var fileDate = fileInfo.LastWriteTime;
                if (fileDate.Date < startDate.Date || fileDate.Date > endDate.Date)
                    continue;

                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);

                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var parts = line.Split(template.Delimiter);
                    var dict = new Dictionary<string, string>();

                    for (int i = 0; i < columns.Count; i++)
                        dict[columns[i]] = parts.Length > i ? parts[i] : "";

                    for (int i = columns.Count; i < parts.Length; i++)
                        dict[$"Message {i - columns.Count + 1}"] = parts[i];

                    yield return dict;
                }
            }
        }

        public async Task<List<Dictionary<string, string>>> SearchLogFilesPageAsync(string logFile, string location, DateTime startDate, DateTime endDate, string templateName, string searchTerm, int pageNumber, int pageSize)
        {
            var results = new List<Dictionary<string, string>>();
            int skip = pageNumber * pageSize;
            int taken = 0;

            await foreach (var entry in ((ILogFileService)this).SearchLogFilesAsync_v2(logFile, location, startDate, endDate, templateName))
            {
                // Apply filter at the service level
                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    if (!entry.Values.Any(v => v.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                if (skip > 0) { skip--; continue; }
                if (taken >= pageSize) break;

                results.Add(entry);
                taken++;
            }

            return results;
        }

        private async Task<List<string>> ResolveColumnsAsync(LogTemplate template)
        {
            if (!string.IsNullOrWhiteSpace(template.Inherits))
            {
                var baseTemplate = await _yamlStorage.LoadAsync<LogTemplate>(template.Inherits);
                if (baseTemplate != null)
                {
                    var merged = new List<string>(baseTemplate.Columns);
                    merged.AddRange(template.Columns);
                    return merged;
                }
            }
            return template.Columns;
        }

        public Task<List<Dictionary<string, string>>> SearchLogFilesPageAsync(string logFile, string location, DateTime startDate, DateTime endDate, string templateName, string searchTerm, int pageNumber, int pageSize, SortColumn sortColumn)
        {
            throw new NotImplementedException();
        }

        public Task<string> DownloadLogCsvAsync(string logFile, string location, DateTime startDate, DateTime endDate, string templateName, string searchTerm, SortColumn sortColumn, string? outputPath = null)
        {
            throw new NotImplementedException();
        }
    }
}