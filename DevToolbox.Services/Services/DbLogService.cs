using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

        /// <summary>
        /// Column holding each row's originating file path. Public so the UI can
        /// both find it and know to keep it out of the visible grid.
        /// </summary>
        public const string SourcePathColumn = "SourcePath";

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
        /// Cache of discovery results, keyed by location path + extension.
        /// <para>
        /// Static and process-wide because this runs on every template or location
        /// change while someone is setting up a search, and walking a 238,000-file
        /// share each time would make the form unusable. The TTL is short because a
        /// new day's log appearing is exactly what someone would be looking for.
        /// </para>
        /// </summary>
        private static readonly ConcurrentDictionary<string, (DateTime At, List<DiscoveredLogName> Names)> _nameCache = new();

        /// <summary>
        /// Measured: a full walk of the archive share — 238,000 files — takes about
        /// 17 seconds and yields 194 names. That is fine once, and unacceptable on
        /// every location toggle, so the window is wide enough to cover setting up a
        /// search but short enough that a log rolling over during the day appears.
        /// </summary>
        private static readonly TimeSpan NameCacheTtl = TimeSpan.FromMinutes(5);

        public async Task<List<DiscoveredLogName>> DiscoverLogFileNamesAsync(
            IReadOnlyList<LogLocation> locations,
            string templateName,
            CancellationToken cancellationToken = default)
        {
            var templateEntry = (await GetAvailableLogFileTemplatesAsync())
                .FirstOrDefault(t => t.Name == templateName);
            if (templateEntry is null) return new List<DiscoveredLogName>();

            var template = await LoadTemplateAsync(templateEntry.File);
            var extension = template?.Extension ?? ".txt";

            // Counts are summed across locations, so the same project seen on four
            // servers reads as one entry rather than four.
            var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var loc in locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(loc.NamePattern) || string.IsNullOrWhiteSpace(loc.Path))
                    continue;

                foreach (var found in await DiscoverInLocationAsync(loc, extension, cancellationToken))
                {
                    totals.TryGetValue(found.Name, out var running);
                    totals[found.Name] = running + found.FileCount;
                }
            }

            return totals
                .Select(kv => new DiscoveredLogName { Name = kv.Key, FileCount = kv.Value })
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static async Task<List<DiscoveredLogName>> DiscoverInLocationAsync(
            LogLocation loc,
            string extension,
            CancellationToken cancellationToken)
        {
            var cacheKey = $"{loc.Path}|{extension}|{loc.NamePattern}";
            if (_nameCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < NameCacheTtl)
                return cached.Names;

            Regex regex;
            try
            {
                // Compiled: this pattern runs against every file name in the
                // directory, which on the archive share is six figures.
                regex = new Regex(loc.NamePattern!, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            catch (ArgumentException)
            {
                // A bad pattern in hand-edited YAML disables discovery for that
                // location and nothing else; free text still works.
                return new List<DiscoveredLogName>();
            }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(loc.Path)) return;

                    foreach (var path in Directory.EnumerateFiles(loc.Path, $"*{extension}"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var match = regex.Match(Path.GetFileName(path));
                        if (!match.Success) continue;

                        var group = match.Groups["name"];
                        if (!group.Success || group.Value.Length == 0) continue;

                        counts.TryGetValue(group.Value, out var running);
                        counts[group.Value] = running + 1;
                    }
                }
                catch (IOException)
                {
                    // Share went away mid-walk. Whatever was counted still stands.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }, cancellationToken).ConfigureAwait(false);

            var names = counts
                .Select(kv => new DiscoveredLogName { Name = kv.Key, FileCount = kv.Value })
                .ToList();

            _nameCache[cacheKey] = (DateTime.UtcNow, names);
            return names;
        }

        private async Task<List<string>> GetAllColumnsFromFilesAsync(
            IEnumerable<string> files,
            LogTemplate template,
            LogIngestProgressReporter reporter,
            CancellationToken cancellationToken = default)
        {
            var baseColumns = await ResolveColumnsAsync(template);
            int maxMessageColumns = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reporter.FileStarted(Path.GetFileName(file));

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
                        
                        var parts = SplitLine(line, template.Delimiter);
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
                finally
                {
                    // In finally so a file that could not be opened still advances the
                    // bar; otherwise one unreadable file makes progress appear stuck.
                    reporter.FileCompleted();
                }
            }

            var columns = new List<string>(baseColumns);
            for (int i = 1; i <= maxMessageColumns; i++)
                columns.Add($"Message {i}");
            
            // Provenance columns: Location precedes SourceFile, Sequence follows it.
            columns.Add("Location");
            columns.Add("SourceFile");
            columns.Add("Sequence");

            // Full path, so a row can be opened in an editor without having to
            // reconstruct where it came from. SourceFile is only the file name, and
            // Location is the location's *name* rather than its path, so between
            // them the original file is not actually recoverable. Kept last and
            // hidden by the grid — it is provenance, not something to read.
            columns.Add(SourcePathColumn);
            return columns;
        }

        private static string[] SplitLine(string line, string? delimiter)
        {
            // Empty delimiter means "row mode": keep the full line in one field.
            if (string.IsNullOrEmpty(delimiter))
                return new[] { line };

            return line.Split(delimiter);
        }

        public async Task<string> PrepareLogTableAsync(
            string logFile,
            IReadOnlyList<LogLocation> locations,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            IProgress<LogIngestProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var reporter = new LogIngestProgressReporter(progress);
            reporter.EnterPhase(LogIngestPhase.Listing);

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

                var taggedFiles = EnumerateMatchingFiles(
                    logFile, locations, startDate, endDate, template, reporter, cancellationToken);

                reporter.EnterPhase(LogIngestPhase.Scanning);

                // Scanning is measured in files, not bytes: it reads only the head of
                // each one, so a byte total would make the bar crawl and stop.
                reporter.SetTotals(taggedFiles.Count, bytesTotal: 0);

                // Determine columns (works with an empty file set: template + provenance columns).
                var columns = await GetAllColumnsFromFilesAsync(
                    taggedFiles.Select(t => t.FilePath), template, reporter, cancellationToken);

                // Always recreate so each Search reflects the current selection.
                if (await _logStorage.TableExistsAsync(TableName))
                    await _logStorage.DropTableAsync(TableName);
                await _logStorage.EnsureTableAsync(TableName, columns);

                reporter.EnterPhase(LogIngestPhase.Ingesting);
                reporter.SetTotals(taggedFiles.Count, taggedFiles.Sum(f => f.Length));
                await IngestFilesAsync(taggedFiles, template, TableName, columns, reporter, cancellationToken);

                reporter.Complete(LogIngestPhase.Querying);
                return TableName;
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        /// <summary>
        /// Finds the files to ingest, tagged with their location name and size.
        /// <para>
        /// <see cref="Directory.EnumerateFiles(string, string)"/>, not <c>GetFiles</c>.
        /// GetFiles builds the entire array before it returns, and against the
        /// archive share — 238,000 files — that is a blocking call lasting the better
        /// part of a minute during which nothing can be reported and the
        /// cancellation token cannot be observed. Cancel genuinely did nothing until
        /// it returned. Streaming the walk makes both work: the counter moves, and
        /// the token is checked per entry.
        /// </para>
        /// <para>
        /// Sizes are captured here rather than re-read later, because a second stat
        /// of every file over a slow share costs as much as the walk itself.
        /// </para>
        /// </summary>
        private static List<(string LocationName, string FilePath, long Length)> EnumerateMatchingFiles(
            string logFile,
            IReadOnlyList<LogLocation> locations,
            DateTime startDate,
            DateTime endDate,
            LogTemplate template,
            LogIngestProgressReporter reporter,
            CancellationToken cancellationToken)
        {
            var taggedFiles = new List<(string LocationName, string FilePath, long Length)>();

            foreach (var loc in locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(loc.Path) || !Directory.Exists(loc.Path))
                    continue; // Tolerate missing/offline locations.

                reporter.FileStarted(loc.Name);

                var matchedHere = new List<(string Path, long Length)>();
                IEnumerable<System.IO.FileInfo> entries;
                try
                {
                    // DirectoryInfo.EnumerateFiles, not Directory.EnumerateFiles: this
                    // yields FileInfo objects already populated from the directory
                    // walk, because the underlying FindFirstFile/FindNextFile returns
                    // size and timestamps in the same call. Enumerating paths and then
                    // constructing a FileInfo per path costs an extra round trip each,
                    // which over SMB against thousands of matches is most of the wait.
                    entries = new DirectoryInfo(loc.Path).EnumerateFiles($"{logFile}*{template.Extension}");
                }
                catch (DirectoryNotFoundException)
                {
                    continue; // Vanished between the Exists check and the walk.
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var info in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var keep = false;
                    long length = 0;
                    try
                    {
                        var written = info.LastWriteTime.Date;
                        if (written >= startDate.Date && written <= endDate.Date)
                        {
                            keep = true;
                            length = info.Length;
                        }
                    }
                    catch (IOException)
                    {
                        // Unreadable metadata: skip the file rather than the search.
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }

                    reporter.ItemExamined(keep);
                    if (keep) matchedHere.Add((info.FullName, length));
                }

                foreach (var f in matchedHere.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
                    taggedFiles.Add((loc.Name, f.Path, f.Length));
            }

            return taggedFiles;
        }

        // Parses files in parallel and inserts on a single writer to respect SQLite's single-writer model.
        private async Task IngestFilesAsync(
            List<(string LocationName, string FilePath, long Length)> taggedFiles,
            LogTemplate template,
            string tableName,
            List<string> columns,
            LogIngestProgressReporter reporter,
            CancellationToken cancellationToken)
        {
            if (taggedFiles.Count == 0)
                return;

            var channel = Channel.CreateBounded<List<Dictionary<string, string>>>(
                new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = false });

            var writerTask = Task.Run(async () =>
            {
                await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    await _logStorage.InsertLogLinesAsync(tableName, batch, cancellationToken);

                    // Counted here rather than at parse time so the figure means rows
                    // actually committed, not rows queued.
                    reporter.AddRows(batch.Count);
                }
            }, cancellationToken);

            int maxParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
            using var throttler = new SemaphoreSlim(maxParallelism);

            var parseTasks = taggedFiles.Select(async tf =>
            {
                await throttler.WaitAsync(cancellationToken);
                try
                {
                    await ParseFileToChannelAsync(tf.FilePath, tf.LocationName, template, columns, channel.Writer, reporter, cancellationToken);
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
            LogIngestProgressReporter reporter,
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
                long reportedBytes = 0;
                string? line;

                reporter.FileStarted(sourceFileName);

                while ((line = await reader.ReadLineAsync()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sequence++; // 1-based line number; advances even for skipped lines to preserve order.

                    // Progress comes from the underlying stream position rather than
                    // the characters handed back, so multi-byte encodings and line
                    // endings are accounted for without decoding them twice. It moves
                    // in reader-buffer steps, which is fine for a progress bar.
                    if (reporter.IsActive)
                    {
                        var position = fs.Position;
                        if (position > reportedBytes)
                        {
                            reporter.AddBytes(position - reportedBytes);
                            reportedBytes = position;
                        }
                    }

                    try
                    {
                        var parts = SplitLine(line, template.Delimiter);
                        var dict = new Dictionary<string, string>(allColumns.Count);

                        for (int i = 0; i < templateColumns.Count; i++)
                            dict[templateColumns[i]] = parts.Length > i ? parts[i] : "";

                        for (int i = templateColumns.Count; i < parts.Length; i++)
                            dict[$"Message {i - templateColumns.Count + 1}"] = parts[i];

                        dict["Location"] = locationName;
                        dict["SourceFile"] = sourceFileName;
                        dict["Sequence"] = sequence.ToString();
                        dict[SourcePathColumn] = filePath;

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

                reporter.FileCompleted();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new InvalidOperationException($"Failed to process log file '{filePath}'", ex);
            }
        }

        public async Task<List<Dictionary<string, string>>> QueryLogPageAsync(
            string tableName, string templateName, int pageNumber, int pageSize,
            List<SortColumn>? sortColumns, LogSearchCriteria? criteria,
            LogSplitFilter? split = null,
            CancellationToken cancellationToken = default)
        {
            var query = new LogQuery
            {
                Page = pageNumber,
                PageSize = pageSize,
                Filters = split?.ToFilters()
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
            string tableName, LogSearchCriteria? criteria, LogSplitFilter? split = null,
            CancellationToken cancellationToken = default)
        {
            var query = new LogQuery { Filters = split?.ToFilters() };
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

        public async Task<List<LogSplitGroup>> GetSplitGroupsAsync(
            string tableName, LogSplitMode mode, LogSearchCriteria? criteria,
            CancellationToken cancellationToken = default)
        {
            if (!LogSplitColumns.TryResolve(mode, out var column))
                return new List<LogSplitGroup>();

            var query = new LogQuery();
            ApplyCriteria(query, criteria);

            try
            {
                return await _logStorage.GetGroupCountsAsync(tableName, column, query, cancellationToken);
            }
            catch (Exception ex)
            {
                throw ToUserFacing(ex, query, "Failed to group log entries");
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
            LogSplitFilter? split = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Exports what is on screen, so a CSV taken from a split tab holds
                // that tab's rows rather than the whole result set.
                var query = new LogQuery { Filters = split?.ToFilters() };
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