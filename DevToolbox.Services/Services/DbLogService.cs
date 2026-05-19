using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DevToolbox.Services.Services
{
    public class DbLogService : ILogFileService
    {
        private readonly IYamlStorageService _yamlStorage;
        private readonly ILogStorageService _logStorage;
        private static readonly ConcurrentDictionary<string, bool> _loadedArchives = new();
        private static readonly SemaphoreSlim _loadSemaphore = new(1, 1);

        public DbLogService(IYamlStorageService yamlStorage, ILogStorageService logStorage)
        {
            _yamlStorage = yamlStorage;
            _logStorage = logStorage;
        }

        public async Task<List<LogTemplateIndexEntry>> GetAvailableLogFileTemplatesAsync()
        {
            try
            {
                var config = await _yamlStorage.LoadAsync<LogTemplateIndexConfig>("log_templates_index") ?? new LogTemplateIndexConfig();
                return config.Templates ?? new List<LogTemplateIndexEntry>();
            }
            catch (Exception ex)
            {
                // Log the exception (implement logging as needed)
                throw new InvalidOperationException("Failed to load log template configurations", ex);
            }
        }

        public async Task<List<LogLocation>> GetLogLocationsAsync()
        {
            try
            {
                var config = await _yamlStorage.LoadAsync<LogLocationConfig>("log_paths") ?? new LogLocationConfig();
                return config.LogLocations ?? new List<LogLocation>();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to load log location configurations", ex);
            }
        }

        public async Task<LogTemplate?> LoadTemplateAsync(string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    return null;
                    
                return await _yamlStorage.LoadAsync<LogTemplate>(Path.GetFileNameWithoutExtension(fileName));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load template '{fileName}'", ex);
            }
        }

        /// <summary>
        /// Checks if a log archive has been completely loaded into the database.
        /// </summary>
        public Task<bool> IsFileCompletelyLoadedAsync(string tableName, string filePath)
        {
            var archiveKey = GenerateArchiveKey(tableName, filePath);
            return Task.FromResult(_loadedArchives.ContainsKey(archiveKey));
        }

        private static string GenerateArchiveKey(params string[] parts)
        {
            return string.Join("|", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private async Task<List<string>> GetAllColumnsFromFilesAsync(
            IEnumerable<string> files, 
            LogTemplate template, 
            CancellationToken cancellationToken = default)
        {
            var columns = (await ResolveColumnsAsync(template)).ToHashSet();
            
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    
                    // Only sample first few lines to determine columns, not the entire file
                    const int maxSampleLines = 1000;
                    int lineCount = 0;
                    
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null && lineCount < maxSampleLines)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        var parts = line.Split(template.Delimiter);
                        for (int i = template.Columns.Count; i < parts.Length; i++)
                        {
                            columns.Add($"Message {i - template.Columns.Count + 1}");
                        }
                        lineCount++;
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // Log warning but continue with other files
                    continue;
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
            string templateName,
            CancellationToken cancellationToken = default)
        {
            string archiveKey = GenerateArchiveKey(logFile, location, startDate.ToString("yyyyMMdd"), endDate.ToString("yyyyMMdd"), templateName);
            
            if (_loadedArchives.ContainsKey(archiveKey))
                return;

            await _loadSemaphore.WaitAsync(cancellationToken);
            try
            {
                // Double-check after acquiring lock
                if (_loadedArchives.ContainsKey(archiveKey))
                    return;

                var templateEntries = await GetAvailableLogFileTemplatesAsync();
                var templateEntry = templateEntries.FirstOrDefault(t => t.Name == templateName);
                if (templateEntry == null)
                    throw new ArgumentException($"Template '{templateName}' not found in index.");

                var template = await LoadTemplateAsync(templateEntry.File);
                if (template == null)
                    throw new InvalidOperationException($"Template file '{templateEntry.File}' could not be loaded.");

                if (!Directory.Exists(location))
                    throw new DirectoryNotFoundException($"Log directory '{location}' does not exist.");

                var files = Directory.GetFiles(location, $"{logFile}*{template.Extension}")
                    .Where(f =>
                    {
                        try
                        {
                            var fileDate = new System.IO.FileInfo(f).LastWriteTime;
                            return fileDate.Date >= startDate.Date && fileDate.Date <= endDate.Date;
                        }
                        catch
                        {
                            return false; // Skip files we can't read metadata from
                        }
                    })
                    .OrderBy(f => f) // Consistent ordering
                    .ToList();

                if (!files.Any())
                    return; // No files to process

                // 1. Scan files for all possible columns
                var columns = await GetAllColumnsFromFilesAsync(files, template, cancellationToken);

                var tableName = $"Log_{logFile}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}";
                
                // Only recreate table if it doesn't exist or schema changed
                var tableExists = await _logStorage.TableExistsAsync(tableName);
                if (tableExists)
                {
                    await _logStorage.DropTableAsync(tableName);
                }
                
                await _logStorage.EnsureTableAsync(tableName, columns);

                // 2. Stream insert each file with adaptive batching
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessLogFileAsync(file, template, tableName, columns, cancellationToken);
                }

                _loadedArchives.TryAdd(archiveKey, true);
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        private async Task ProcessLogFileAsync(
            string filePath, 
            LogTemplate template, 
            string tableName, 
            List<string> allColumns,
            CancellationToken cancellationToken)
        {
            const int baseBatchSize = 1000;
            var batch = new List<Dictionary<string, string>>(baseBatchSize);

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                
                var templateColumns = await ResolveColumnsAsync(template);
                string? line;
                
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    try
                    {
                        var parts = line.Split(template.Delimiter);
                        var dict = new Dictionary<string, string>(allColumns.Count);

                        // Map template columns
                        for (int i = 0; i < templateColumns.Count; i++)
                        {
                            dict[templateColumns[i]] = parts.Length > i ? parts[i] : "";
                        }
                        
                        // Map additional message columns
                        for (int i = templateColumns.Count; i < parts.Length; i++)
                        {
                            dict[$"Message {i - templateColumns.Count + 1}"] = parts[i];
                        }
                        
                        dict["SourceFile"] = Path.GetFileName(filePath);

                        batch.Add(dict);

                        if (batch.Count >= baseBatchSize)
                        {
                            await _logStorage.InsertLogLinesAsync(tableName, batch);
                            batch.Clear();
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // Log the problematic line but continue processing
                        continue;
                    }
                }
                
                // Insert remaining batch
                if (batch.Count > 0)
                {
                    await _logStorage.InsertLogLinesAsync(tableName, batch);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new InvalidOperationException($"Failed to process log file '{filePath}'", ex);
            }
        }

        public async Task<List<Dictionary<string, string>>> SearchLogFilesPageAsync(
            string logFile, string location, DateTime startDate, DateTime endDate, 
            string templateName, string searchTerm, int pageNumber, int pageSize, 
            SortColumn sortColumn)
        {
            try
            {
                await LoadAndStoreLogsAsync(logFile, location, startDate, endDate, templateName);
                var tableName = $"Log_{logFile}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}";
                
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
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to search log files", ex);
            }
        }

        public async Task<int> CountLogEntriesAsync(
            string logFile, string location, DateTime startDate, DateTime endDate, 
            string templateName, string searchTerm)
        {
            try
            {
                await LoadAndStoreLogsAsync(logFile, location, startDate, endDate, templateName);
                var tableName = $"Log_{logFile}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}";
                
                var query = new LogQuery
                {
                    SearchTerm = searchTerm,
                    Sort = await ResolveSortColumnsAsync(await LoadTemplateAsync(templateName))
                };
                
                var (_, totalCount) = await _logStorage.SearchLogsAsync(tableName, query);
                return totalCount;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to count log entries", ex);
            }
        }

        public async IAsyncEnumerable<Dictionary<string, string>> SearchLogFilesAsync_v2(
            string logFile,
            string location,
            DateTime startDate,
            DateTime endDate,
            string templateName)
        {
            await LoadAndStoreLogsAsync(logFile, location, startDate, endDate, templateName);
            var tableName = $"Log_{logFile}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}";
            
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
            try
            {
                await LoadAndStoreLogsAsync(logFile, location, startDate, endDate, templateName);
                var tableName = $"Log_{logFile}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}";
                
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
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to search log files", ex);
            }
        }

        private async Task<List<string>> ResolveColumnsAsync(LogTemplate? template)
        {
            if (template == null)
                return new List<string>();
                
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
            return new List<string>(template.Columns);
        }

        private async Task<List<SortColumn>> ResolveSortColumnsAsync(LogTemplate? template)
        {
            if (template == null)
                return new List<SortColumn>();
                
            if (!string.IsNullOrWhiteSpace(template.Inherits))
            {
                var baseTemplate = await _yamlStorage.LoadAsync<LogTemplate>(template.Inherits);
                if (baseTemplate != null)
                {
                    var merged = new List<SortColumn>(baseTemplate.Sort ?? new List<SortColumn>());
                    merged.AddRange(template.Sort ?? new List<SortColumn>());
                    return merged;
                }
            }
            return new List<SortColumn>(template.Sort ?? new List<SortColumn>());
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
            try
            {
                await LoadAndStoreLogsAsync(logFile, location, startDate, endDate, templateName);
                var tableName = $"Log_{logFile}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}";
                
                var query = new LogQuery
                {
                    SearchTerm = searchTerm,
                    Sort = new List<SortColumn> { sortColumn }
                };
                
                var (results, _) = await _logStorage.SearchLogsAsync(tableName, query);

                // Use a temp file if outputPath is not provided
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    var safeName = SanitizeFileName(logFile);
                    outputPath = Path.Combine(Path.GetTempPath(), $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                }

                // Ensure directory exists
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
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
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to download log CSV", ex);
            }

            static string EscapeCsv(string? value)
            {
                if (value == null) return "";
                if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
                    return $"\"{value.Replace("\"", "\"\"")}\"";
                return value;
            }

            static string SanitizeFileName(string fileName)
            {
                var invalidChars = Path.GetInvalidFileNameChars();
                return new string(fileName.Where(ch => !invalidChars.Contains(ch)).ToArray());
            }
        }
    }
}