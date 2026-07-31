using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DevToolbox.Services.Services
{
    public class DbLogService : ILogFileService
    {
        private readonly IYamlStorageService _yamlStorage;
        private readonly ILogStorageService _logStorage;
        private static readonly SemaphoreSlim _loadSemaphore = new(1, 1);

        // Single active search table; dropped and recreated on every Search.
        private const string TableName = "logs";

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

        private async Task<List<string>> GetAllColumnsFromFilesAsync(
            IEnumerable<string> files, 
            LogTemplate template, 
            CancellationToken cancellationToken = default)
        {
            var baseColumns = await ResolveColumnsAsync(template);
            int maxMessageColumns = 0;
            
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
                        int extra = parts.Length - baseColumns.Count;
                        if (extra > maxMessageColumns)
                            maxMessageColumns = extra;
                        lineCount++;
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // Log warning but continue with other files
                    continue;
                }
            }
            
            var columns = new List<string>(baseColumns);
            for (int i = 1; i <= maxMessageColumns; i++)
                columns.Add($"Message {i}");
            
            // Provenance columns: Location precedes SourceFile, Sequence follows it.
            columns.Add("Location");
            columns.Add("SourceFile");
            columns.Add("Sequence");
            return columns;
        }

        public async Task<string> PrepareLogTableAsync(
            string logFile,
            IReadOnlyList<LogLocation> locations,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            CancellationToken cancellationToken = default)
        {
            await _loadSemaphore.WaitAsync(cancellationToken);
            try
            {
                var templateEntries = await GetAvailableLogFileTemplatesAsync();
                var templateEntry = templateEntries.FirstOrDefault(t => t.Name == templateName);
                if (templateEntry == null)
                    throw new ArgumentException($"Template '{templateName}' not found in index.");

                var template = await LoadTemplateAsync(templateEntry.File);
                if (template == null)
                    throw new InvalidOperationException($"Template file '{templateEntry.File}' could not be loaded.");

                // Gather matching files from every selected location, tagged with the location name.
                var taggedFiles = new List<(string LocationName, string FilePath)>();
                foreach (var loc in locations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(loc.Path) || !Directory.Exists(loc.Path))
                        continue; // Tolerate missing/offline locations.

                    var matched = Directory.GetFiles(loc.Path, $"{logFile}*{template.Extension}")
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
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

                    foreach (var f in matched)
                        taggedFiles.Add((loc.Name, f));
                }

                // Determine columns (works with an empty file set: template + provenance columns).
                var columns = await GetAllColumnsFromFilesAsync(taggedFiles.Select(t => t.FilePath), template, cancellationToken);

                // Always recreate so each Search reflects the current selection.
                if (await _logStorage.TableExistsAsync(TableName))
                    await _logStorage.DropTableAsync(TableName);
                await _logStorage.EnsureTableAsync(TableName, columns);

                await IngestFilesAsync(taggedFiles, template, TableName, columns, cancellationToken);

                return TableName;
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        // Parses files in parallel and inserts on a single writer to respect SQLite's single-writer model.
        private async Task IngestFilesAsync(
            List<(string LocationName, string FilePath)> taggedFiles,
            LogTemplate template,
            string tableName,
            List<string> columns,
            CancellationToken cancellationToken)
        {
            if (taggedFiles.Count == 0)
                return;

            var channel = Channel.CreateBounded<List<Dictionary<string, string>>>(
                new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = false });

            var writerTask = Task.Run(async () =>
            {
                await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken))
                    await _logStorage.InsertLogLinesAsync(tableName, batch);
            }, cancellationToken);

            int maxParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
            using var throttler = new SemaphoreSlim(maxParallelism);

            var parseTasks = taggedFiles.Select(async tf =>
            {
                await throttler.WaitAsync(cancellationToken);
                try
                {
                    await ParseFileToChannelAsync(tf.FilePath, tf.LocationName, template, columns, channel.Writer, cancellationToken);
                }
                finally
                {
                    throttler.Release();
                }
            }).ToList();

            try
            {
                await Task.WhenAll(parseTasks);
            }
            finally
            {
                channel.Writer.Complete();
            }

            await writerTask;
        }

        private async Task ParseFileToChannelAsync(
            string filePath,
            string locationName,
            LogTemplate template,
            List<string> allColumns,
            ChannelWriter<List<Dictionary<string, string>>> writer,
            CancellationToken cancellationToken)
        {
            const int baseBatchSize = 1000;
            var batch = new List<Dictionary<string, string>>(baseBatchSize);

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);

                var templateColumns = await ResolveColumnsAsync(template);
                string sourceFileName = Path.GetFileName(filePath);
                long sequence = 0;
                string? line;

                while ((line = await reader.ReadLineAsync()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sequence++; // 1-based line number; advances even for skipped lines to preserve order.

                    try
                    {
                        var parts = line.Split(template.Delimiter);
                        var dict = new Dictionary<string, string>(allColumns.Count);

                        for (int i = 0; i < templateColumns.Count; i++)
                            dict[templateColumns[i]] = parts.Length > i ? parts[i] : "";

                        for (int i = templateColumns.Count; i < parts.Length; i++)
                            dict[$"Message {i - templateColumns.Count + 1}"] = parts[i];

                        dict["Location"] = locationName;
                        dict["SourceFile"] = sourceFileName;
                        dict["Sequence"] = sequence.ToString();

                        batch.Add(dict);

                        if (batch.Count >= baseBatchSize)
                        {
                            await writer.WriteAsync(batch, cancellationToken);
                            batch = new List<Dictionary<string, string>>(baseBatchSize);
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // Log the problematic line but continue processing
                        continue;
                    }
                }

                if (batch.Count > 0)
                    await writer.WriteAsync(batch, cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new InvalidOperationException($"Failed to process log file '{filePath}'", ex);
            }
        }

        public async Task<List<Dictionary<string, string>>> QueryLogPageAsync(
            string tableName, string templateName, int pageNumber, int pageSize,
            List<SortColumn>? sortColumns, LogSearchCriteria? criteria,
            CancellationToken cancellationToken = default)
        {
            var query = new LogQuery
            {
                Page = pageNumber,
                PageSize = pageSize
            };
            ApplyCriteria(query, criteria);
            if (query.RawQuery == null)
                query.Sort = await ResolveEffectiveSortAsync(sortColumns, templateName);

            try
            {
                var (results, _) = await _logStorage.SearchLogsAsync(tableName, query);
                return results.ToList();
            }
            catch (Exception ex)
            {
                throw ToUserFacing(ex, query, "Failed to search log files");
            }
        }

        public async Task<int> CountLogEntriesAsync(
            string tableName, LogSearchCriteria? criteria, CancellationToken cancellationToken = default)
        {
            var query = new LogQuery();
            ApplyCriteria(query, criteria);

            try
            {
                var (_, totalCount) = await _logStorage.SearchLogsAsync(tableName, query);
                return totalCount;
            }
            catch (Exception ex)
            {
                throw ToUserFacing(ex, query, "Failed to count log entries");
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

        // Falls back to the template's configured multi-column sort when the caller supplies none.
        private async Task<List<SortColumn>> ResolveEffectiveSortAsync(List<SortColumn>? requested, string templateName)
        {
            if (requested != null && requested.Any(s => !string.IsNullOrWhiteSpace(s.Column)))
                return requested;

            var templateEntry = (await GetAvailableLogFileTemplatesAsync())
                .FirstOrDefault(t => t.Name == templateName);
            var template = templateEntry != null ? await LoadTemplateAsync(templateEntry.File) : null;
            return await ResolveSortColumnsAsync(template);
        }

        private static void ApplyCriteria(LogQuery query, LogSearchCriteria? criteria)
        {
            if (criteria == null)
                return;
            if (criteria.UseAdvanced)
            {
                if (!string.IsNullOrWhiteSpace(criteria.AdvancedExpression))
                    query.RawQuery = criteria.AdvancedExpression;
            }
            else
            {
                query.Criteria = criteria;
            }
        }

        // Raw SQL errors are shown verbatim; other failures get a generic message.
        private static Exception ToUserFacing(Exception ex, LogQuery query, string genericMessage)
        {
            if (!string.IsNullOrWhiteSpace(query.RawQuery))
                return new InvalidOperationException(ex.Message, ex);
            return new InvalidOperationException(genericMessage, ex);
        }

        public async Task<string> DownloadLogCsvAsync(
            string tableName,
            string templateName,
            List<SortColumn>? sortColumns,
            LogSearchCriteria? criteria,
            string? outputPath = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new LogQuery();
                ApplyCriteria(query, criteria);
                if (query.RawQuery == null)
                    query.Sort = await ResolveEffectiveSortAsync(sortColumns, templateName);

                var (results, _) = await _logStorage.SearchLogsAsync(tableName, query);

                // Use a temp file if outputPath is not provided
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    outputPath = Path.Combine(Path.GetTempPath(), $"LogSearch_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
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
        }
    }
}