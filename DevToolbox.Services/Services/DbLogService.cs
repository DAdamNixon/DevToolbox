using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DevToolbox.Services.Services
{
    public class DbLogService : ILogFileService
    {
        private readonly IYamlStorageService _yamlStorage;
        private readonly ILogStorageService _logStorage;
        private static readonly HashSet<string> _loadedArchives = new();

        public DbLogService(IYamlStorageService yamlStorage, ILogStorageService logStorage)
        {
            _yamlStorage = yamlStorage;
            _logStorage = logStorage;
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

        /// <summary>
        /// Placeholder for future optimization: checks if a file is already loaded in the DB.
        /// </summary>
        public Task<bool> IsFileCompletelyLoadedAsync(string tableName, string filePath)
        {
            throw new NotImplementedException("File existence check not implemented yet.");
        }

        private async Task<List<string>> GetAllColumnsFromFilesAsync(IEnumerable<string> files, LogTemplate template)
        {
            var columns = (await ResolveColumnsAsync(template)).ToHashSet();
            foreach (var file in files)
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var parts = line.Split(template.Delimiter);
                    for (int i = template.Columns.Count; i < parts.Length; i++)
                        columns.Add($"Message {i - template.Columns.Count + 1}");
                }
            }
            columns.Add("SourceFile");
            return columns.ToList();
        }

        private async Task LoadAndStoreLogsAsync(
            string logFile,
            string location,
            DateTime startDate,
            DateTime endDate,
            string templateName)
        {
            string archiveKey = $"{logFile}|{location}|{startDate:yyyyMMdd}|{endDate:yyyyMMdd}|{templateName}";
            if (_loadedArchives.Contains(archiveKey))
                return;

            var templateEntries = await GetAvailableLogFileTemplatesAsync();
            var templateEntry = templateEntries.FirstOrDefault(t => t.Name == templateName);
            if (templateEntry == null)
                throw new Exception($"Template '{templateName}' not found in index.");

            var template = await LoadTemplateAsync(templateEntry.File);
            if (template == null)
                throw new Exception($"Template file '{templateEntry.File}' could not be loaded.");

            var files = Directory.GetFiles(location, $"{logFile}*{template.Extension}")
                .Where(f =>
                {
                    var fileDate = new System.IO.FileInfo(f).LastWriteTime;
                    return fileDate.Date >= startDate.Date && fileDate.Date <= endDate.Date;
                })
                .ToList();

            // 1. Scan all files for all possible columns
            var columns = await GetAllColumnsFromFilesAsync(files, template);

            var tableName = $"Log_{logFile}";
            if (await _logStorage.TableExistsAsync(tableName))
                await _logStorage.DropTableAsync(tableName);
            await _logStorage.EnsureTableAsync(tableName, columns);

            // 2. Stream insert each file
            foreach (var file in files)
            {
                const int BatchSize = 1111;
                var batch = new List<Dictionary<string, string>>(BatchSize);

                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var parts = line.Split(template.Delimiter);
                    var dict = new Dictionary<string, string>();

                    var templateColumns = await ResolveColumnsAsync(template);

                    for (int i = 0; i < templateColumns.Count; i++)
                        dict[templateColumns[i]] = parts.Length > i ? parts[i] : "";
                    for (int i = templateColumns.Count; i < parts.Length; i++)
                        dict[$"Message {i - templateColumns.Count + 1}"] = parts[i];
                    dict["SourceFile"] = Path.GetFileName(file);

                    batch.Add(dict);

                    if (batch.Count >= BatchSize)
                    {
                        await _logStorage.InsertLogLinesAsync(tableName, batch);
                        batch.Clear();
                    }
                }
                if (batch.Count > 0)
                {
                    await _logStorage.InsertLogLinesAsync(tableName, batch);
                    batch.Clear();
                }
            }

            _loadedArchives.Add(archiveKey);
        }

        public async Task<List<Dictionary<string, string>>> SearchLogFilesPageAsync(string logFile, string location, DateTime startDate, DateTime endDate, string templateName, string searchTerm, int pageNumber, int pageSize, SortColumn sortColumn)
        {
            await LoadAndStoreLogsAsync(logFile, location, startDate, endDate, templateName);
            var tableName = $"Log_{logFile}";
            var query = new LogQuery
            {
                SearchTerm = searchTerm,
                Page = pageNumber,
                PageSize = pageSize,
                Sort = new List<SortColumn> { sortColumn }
            };
            var (results, _) = await _logStorage.SearchLogsAsync(tableName, query);
            return results.ToList();
        }

        public async Task<int> CountLogEntriesAsync(string logFile, string location, DateTime startDate, DateTime endDate, string templateName, string searchTerm)
        {
            await LoadAndStoreLogsAsync(logFile, location, startDate, endDate, templateName);
            var tableName = $"Log_{logFile}";
            var query = new LogQuery
            {
                SearchTerm = searchTerm,
                Sort = await ResolveSortColumnsAsync(await LoadTemplateAsync(templateName))
            };
            var (_, totalCount) = await _logStorage.SearchLogsAsync(tableName, query);
            return totalCount;
        }

        public async IAsyncEnumerable<Dictionary<string, string>> SearchLogFilesAsync_v2(
            string logFile,
            string location,
            DateTime startDate,
            DateTime endDate,
            string templateName)
        {
            await LoadAndStoreLogsAsync(logFile, location, startDate, endDate, templateName);
            var tableName = $"Log_{logFile}";
            var query = new LogQuery
            {
                Sort = await ResolveSortColumnsAsync(await LoadTemplateAsync(templateName))
            };
            var (results, _) = await _logStorage.SearchLogsAsync(tableName, query);
            foreach (var dict in results)
                yield return dict;
        }

        public async Task<List<Dictionary<string, string>>> SearchLogFilesPageAsync(
            string logFile,
            string location,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            string searchTerm,
            int pageNumber,
            int pageSize)
        {
            await LoadAndStoreLogsAsync(logFile, location, startDate, endDate, templateName);
            var tableName = $"Log_{logFile}";
            var query = new LogQuery
            {
                SearchTerm = searchTerm,
                Page = pageNumber,
                PageSize = pageSize,
                Sort = await ResolveSortColumnsAsync(await LoadTemplateAsync(templateName))
            };
            var (results, _) = await _logStorage.SearchLogsAsync(tableName, query);
            return results.ToList();
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

        private async Task<List<SortColumn>> ResolveSortColumnsAsync(LogTemplate template)
        {
            if (!string.IsNullOrWhiteSpace(template.Inherits))
            {
                var baseTemplate = await _yamlStorage.LoadAsync<LogTemplate>(template.Inherits);
                if (baseTemplate != null)
                {
                    var merged = new List<SortColumn>(baseTemplate.Sort);
                    merged.AddRange(template.Sort);
                    return merged;
                }
            }
            return template.Sort;
        }

        public async Task<string> DownloadLogCsvAsync(
            string logFile,
            string location,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            string searchTerm,
            SortColumn sortColumn,
            string? outputPath = null)
        {
            await LoadAndStoreLogsAsync(logFile, location, startDate, endDate, templateName);
            var tableName = $"Log_{logFile}";
            var query = new LogQuery
            {
                SearchTerm = searchTerm,
                Sort = new List<SortColumn> { sortColumn }
            };
            var (results, _) = await _logStorage.SearchLogsAsync(tableName, query);

            // Use a temp file if outputPath is not provided
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                var safeName = Path.GetFileNameWithoutExtension(logFile);
                outputPath = Path.Combine(Path.GetTempPath(), $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            }

            // Get columns from the first result, or empty if no results
            var columns = results.FirstOrDefault()?.Keys.ToList() ?? new List<string>();

            using (var writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8))
            {
                // Write header
                await writer.WriteLineAsync(string.Join(",", columns.Select(EscapeCsv)));

                // Write each row
                foreach (var line in results)
                {
                    var csvLine = string.Join(",", columns.Select(col => EscapeCsv(line.TryGetValue(col, out var v) ? v : "")));
                    await writer.WriteLineAsync(csvLine);
                }
            }

            return outputPath;

            static string EscapeCsv(string? value)
            {
                if (value == null) return "";
                if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
                    return $"\"{value.Replace("\"", "\"\"")}\"";
                return value;
            }
        }
    }
}