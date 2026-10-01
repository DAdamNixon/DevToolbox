using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DevToolbox.Services.Services
{
    public class DbLogService : ILogFileService, IDisposable
    {
        private readonly IYamlStorageService _yamlStorage;
        private readonly ILogStorageService _logStorage;
        private static readonly SemaphoreSlim _loadSemaphore = new(1, 1);

        /// <summary>The table this instance ingests into and returns from a prepare.</summary>
        /// <remarks>
        /// An instance field rather than a constant because nothing that uses this runs one search
        /// at a time. An agent investigating a bug prepares log A, then log B, then wants to go back
        /// to A — against a single shared table, A was silently destroyed by the second prepare, and
        /// the query that followed returned real rows from the wrong file with no error to notice.
        /// The interface already anticipated this: PrepareLogTableAsync RETURNS a table name and
        /// every query method TAKES one. The constant was the anomaly.
        /// <para>
        /// Nor does the UI, which is less obvious: the window and every browser view of it each have
        /// a Log Viewer of their own, on one database file. Sharing <c>logs</c>, one view's load
        /// dropped and recreated the table under another, which then showed the first one's rows or
        /// failed with "no such table". So each DI scope gets tables of its own (<see cref="ForScope"/>),
        /// and SQL mode goes on calling them <c>logs</c> and <c>results</c> (<see cref="LogSqlTableNames"/>).
        /// </para>
        /// <para>
        /// <see cref="_loadSemaphore"/> stays static, so concurrent prepares still queue
        /// process-wide. That is a throughput limit, not a correctness one — the tables they build
        /// are separate.
        /// </para>
        /// </remarks>
        private readonly string TableName;

        /// <summary>
        /// The table <see cref="MaterializeResultsAsync"/> collapses a filter into — this instance's
        /// own, like <see cref="TableName"/>, so a collapse in one window never replaces another's.
        /// <see cref="QueryLogPageAsync"/> and <see cref="DownloadLogCsvAsync"/> recognise it and skip
        /// the template-sort fallback: a page over results orders itself by when it was collapsed, not
        /// by a template that may not even describe its columns.
        /// </summary>
        public string ResultsTableName { get; }

        /// <summary>
        /// What SQL mode calls the ingested table, whatever it is really named. <c>FROM logs</c> is
        /// what the SQL box's placeholder and every saved query say, and it has to go on meaning this
        /// instance's table.
        /// </summary>
        public const string LogsSqlName = "logs";

        /// <summary>What SQL mode calls the collapsed table, whatever it is really named.</summary>
        public const string ResultsSqlName = "results";

        /// <summary>What the table is called when a caller does not name one.</summary>
        public const string DefaultTableName = LogsSqlName;

        /// <summary>What the collapsed table is called when a caller does not name one.</summary>
        public const string DefaultResultsTableName = ResultsSqlName;

        /// <summary>
        /// Column holding each row's originating file path. Public so the UI can
        /// both find it and know to keep it out of the visible grid.
        /// </summary>
        public const string SourcePathColumn = LogProvenanceColumns.SourcePath;

        /// <summary>
        /// Letters, digits and underscores, not starting with a digit. Every statement quotes a table
        /// as <c>[name]</c>, which a <c>]</c> would end, and the index names are built from it too.
        /// </summary>
        private static readonly Regex PlainIdentifier = new(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.Compiled);

        /// <summary>The names SQL mode writes, and the tables they mean here.</summary>
        private readonly Dictionary<string, string> _sqlNames;

        /// <summary>Set by <see cref="ForScope"/>: these tables are this instance's alone, and go when it does.</summary>
        private bool _ownsTables;
        private int _disposed;

        /// <param name="tableName">
        /// The table to ingest into. Null keeps <see cref="DefaultTableName"/> — see the remarks on
        /// <see cref="TableName"/> for when a caller should pass its own.
        /// </param>
        /// <param name="resultsTableName">The table to collapse into. Null keeps <see cref="DefaultResultsTableName"/>.</param>
        public DbLogService(IYamlStorageService yamlStorage, ILogStorageService logStorage, string? tableName = null, string? resultsTableName = null)
        {
            _yamlStorage = yamlStorage;
            _logStorage = logStorage;
            TableName = RequirePlainIdentifier(tableName, DefaultTableName, nameof(tableName));
            ResultsTableName = RequirePlainIdentifier(resultsTableName, DefaultResultsTableName, nameof(resultsTableName));
            if (string.Equals(TableName, ResultsTableName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The results table cannot be the logs table.", nameof(resultsTableName));

            _sqlNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [LogsSqlName] = TableName,
                [ResultsSqlName] = ResultsTableName
            };
        }

        /// <summary>
        /// A DbLogService with tables of its own, for one DI scope of a host where several Log Viewers
        /// share a database — see the remarks on <see cref="TableName"/>. Disposing it drops them.
        /// </summary>
        public static DbLogService ForScope(IYamlStorageService yamlStorage, ILogStorageService logStorage)
        {
            // The MCP server's handle shape (PreparedTables): lower-case hex after a prefix, so the
            // name is a plain identifier however it is generated.
            var suffix = Guid.NewGuid().ToString("n")[..16];
            return new DbLogService(yamlStorage, logStorage, $"logs_{suffix}", $"results_{suffix}") { _ownsTables = true };
        }

        private static string RequirePlainIdentifier(string? name, string fallback, string parameter)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            if (!PlainIdentifier.IsMatch(name))
                throw new ArgumentException($"'{name}' is not a usable table name: letters, digits and underscores only.", parameter);
            return name;
        }

        /// <summary>Finished once <see cref="Dispose"/>'s drop has run. For tests.</summary>
        internal Task TablesDropped { get; private set; } = Task.CompletedTask;

        /// <summary>
        /// Drops this instance's tables if they are its own (<see cref="ForScope"/>) — a browser view
        /// closing, say. Left behind, every view ever opened would keep a full copy of what it loaded
        /// until the next start, which is the disk-filling <see cref="LogDatabase"/> exists to prevent.
        /// <para>
        /// In the background, and only once no load holds <see cref="_loadSemaphore"/>: the scope's
        /// own load may still be unwinding from the cancel its state service just sent, and a drop
        /// landing before that load's create would leave the table behind after all. Waiting here
        /// instead would hold up the window closing behind another view's load.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (!_ownsTables || Interlocked.Exchange(ref _disposed, 1) == 1) return;

            TablesDropped = Task.Run(async () =>
            {
                await _loadSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _logStorage.DropTableAsync(ResultsTableName).ConfigureAwait(false);
                    await _logStorage.DropTableAsync(TableName).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best effort: the file is thrown away at the next start regardless.
                }
                finally
                {
                    _loadSemaphore.Release();
                }
            });
        }

        /// <summary>
        /// Opens a log file for reading. Test seam: a fake can return a stream that blocks or
        /// throws, to simulate a hung or failing share without one. Defaults to a real,
        /// share-friendly, overlapped <see cref="FileStream"/>.
        /// </summary>
        internal Func<string, Stream> FileOpener { get; set; } = static path =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, FileOptions.Asynchronous);

        /// <summary>
        /// The columns a template actually produces once <c>inherits</c> has been applied — the
        /// base's columns followed by its own.
        /// <para>
        /// Public because a caller describing a template to someone who will then write SQL against
        /// it needs the resolved list, and resolving inheritance a second time somewhere else is
        /// how the two copies come to disagree. Does NOT include the overflow (<c>Message…</c>) or
        /// provenance columns: those depend on the files an ingest actually read, not on the
        /// template, and are only knowable after a prepare.
        /// </para>
        /// </summary>
        public async Task<List<string>> GetEffectiveColumnsAsync(string templateName)
        {
            var entry = (await GetAvailableLogFileTemplatesAsync())
                .FirstOrDefault(t => t.Name == templateName);
            if (entry is null) return new List<string>();

            return await ResolveColumnsAsync(await LoadTemplateAsync(entry.File));
        }

        /// <summary>The template's own multi-column sort, inheritance applied.</summary>
        public Task<List<SortColumn>> GetEffectiveSortAsync(string templateName)
            => ResolveEffectiveSortAsync(null, templateName);

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

        /// <summary>
        /// The columns a new table starts with: the template's, then provenance. Overflow columns
        /// (<c>Message1</c>, …) are not guessed up front any more — the writer adds each the first
        /// time a line needs it (see <see cref="IngestFilesAsync"/>).
        /// <para>
        /// This used to be a Scanning pass that opened every file and read up to 1000 lines of it,
        /// before a single row could be stored. For many small files over SMB that pass was most of
        /// the wait, every file was opened twice, and a line past the sample with more fields than
        /// any before it named a column the table lacked — the insert failed and the load hung
        /// behind it. Adding a column in SQLite changes only the schema, so doing it on demand costs
        /// nothing, and rows can reach the table (and the Log Viewer's grid) from the first file on.
        /// </para>
        /// </summary>
        private static List<string> StartingColumns(IEnumerable<string> templateColumns)
        {
            var columns = new List<string>(templateColumns);

            // Refused before anything is dropped. The editor refuses these too, but a template can be
            // edited by hand, and one that got through would have purges delete the wrong rows.
            if (columns.FirstOrDefault(LogTemplateValidator.IsReservedByDatabase) is { } reserved)
                throw new InvalidOperationException(
                    $"The template's column \"{reserved}\" is a name the database keeps for itself. Rename it in the template.");

            // Provenance columns: Location precedes SourceFile, Sequence follows it.
            columns.Add(LogProvenanceColumns.Location);
            columns.Add(LogProvenanceColumns.SourceFile);
            columns.Add(LogProvenanceColumns.Sequence);

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

        public async Task<LogPrepareResult> PrepareLogTableAsync(
            string logFile,
            IReadOnlyList<LogLocation> locations,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            IProgress<LogIngestProgress>? progress = null,
            LogIngestControl? control = null,
            CancellationToken cancellationToken = default)
        {
            control ??= new LogIngestControl();
            using var reporter = new LogIngestProgressReporter(progress, control);
            reporter.EnterPhase(LogIngestPhase.Listing);

            await _loadSemaphore.WaitAsync(cancellationToken);
            List<MatchedFile>? files = null;
            try
            {
                var templateEntries = await GetAvailableLogFileTemplatesAsync();
                var templateEntry = templateEntries.FirstOrDefault(t => t.Name == templateName);
                if (templateEntry == null)
                    throw new ArgumentException($"Template '{templateName}' not found in index.");

                var template = await LoadTemplateAsync(templateEntry.File);
                if (template == null)
                    throw new InvalidOperationException($"Template file '{templateEntry.File}' could not be loaded.");

                files = EnumerateMatchingFiles(
                    logFile, locations, startDate, endDate, template, reporter, cancellationToken);

                // Resolved once for the whole load, not once per file — it reads YAML.
                var templateColumns = await ResolveColumnsAsync(template);
                var columns = StartingColumns(templateColumns);

                // The last moment a Cancel leaves the previous search's table standing. Past here it
                // is gone, and whatever a caller was showing from it goes with it — which is exactly
                // what TableDropped tells them.
                cancellationToken.ThrowIfCancellationRequested();

                // Always recreate so each Search reflects the current selection.
                control.MarkTableDropped();
                if (await _logStorage.TableExistsAsync(TableName))
                    await _logStorage.DropTableAsync(TableName);
                await _logStorage.EnsureTableAsync(TableName, columns);
                control.MarkTableReady(TableName);

                reporter.EnterPhase(LogIngestPhase.Ingesting);
                reporter.SetTotals(files.Count, files.Sum(f => f.Length));
                await IngestFilesAsync(files, template, templateColumns, TableName, columns, reporter, control, cancellationToken);

                // SkipAll without the token — the control's documented half of a Cancel — finishes
                // normally; its files still need the cut-short / never-read account.
                if (control.AllSkipped)
                    reporter.CancelOutstanding(Outstanding(files));

                var notIngested = reporter.GetNotIngested();
                reporter.Complete(LogIngestPhase.Querying);

                return new LogPrepareResult { TableName = TableName, NotIngested = notIngested };
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && ex is not LogIngestCancelledException)
            {
                // A cancelled load keeps what it read, and the Log Viewer goes on showing it — so say
                // which files those rows are complete for and which they are not.
                if (files is not null)
                    reporter.CancelOutstanding(Outstanding(files));
                reporter.Flush();

                var result = new LogPrepareResult { TableName = TableName, NotIngested = reporter.GetNotIngested() };
                throw new LogIngestCancelledException(result, control.TableDropped, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The table is shown as far as the load got, so the count shown with it must be too.
                reporter.Flush();
                throw;
            }
            finally
            {
                _loadSemaphore.Release();
            }

            static IEnumerable<(string, string, string, long)> Outstanding(List<MatchedFile> files) =>
                files.Select(f => (f.FilePath, Path.GetFileName(f.FilePath), f.LocationName, f.Length));
        }

        /// <summary>
        /// Runs one file's work racing against its own abandon signal, so a caller can stop waiting
        /// on a blocked read without waiting on it. <paramref name="work"/> must not let an exception
        /// escape — it owns reporting its own <see cref="LogIngestProgressReporter.FileFailed"/>, so
        /// a bad file never faults the whole ingest (D5).
        /// </summary>
        private static async Task<T?> RunAbandonableAsync<T>(
            string fileKey,
            string fileName,
            string locationName,
            LogIngestControl control,
            LogIngestProgressReporter reporter,
            Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken)
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var workTask = work(linkedCts.Token);
            var abandonTask = control.WaitForAbandonAsync(fileKey);

            var finished = await Task.WhenAny(workTask, abandonTask);

            if (finished == abandonTask)
            {
                // Best-effort: the file was opened synchronously, so this may do nothing at all —
                // the read that matters is the one we are choosing not to wait for any longer.
                linkedCts.Cancel();

                var reason = DescribeSkip(abandonTask.Result, control);
                reporter.FileSkipped(fileKey, fileName, locationName, reason);

                // The orphan keeps running; observe its eventual result so it never becomes an
                // unobserved task exception, and dispose the CTS only once nothing references it.
                _ = workTask.ContinueWith(_ => linkedCts.Dispose(), TaskScheduler.Default);
                return default;
            }

            linkedCts.Dispose();
            return await workTask;
        }

        private static string DescribeSkip(SkipReason reason, LogIngestControl control) => reason switch
        {
            SkipReason.Manual => "stalled — skipped by you",
            SkipReason.Auto => $"stalled — auto-skipped after {control.AutoSkipTimeoutSeconds}s",
            SkipReason.Cancelled => LogIngestProgressReporter.CancelledBeforeReading,
            _ => "not read"
        };

        /// <summary>A file to ingest, with what the directory walk already told us about it.</summary>
        private readonly record struct MatchedFile(string LocationName, string FilePath, long Length, DateTime Written, int LocationIndex);

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
        /// <para>
        /// Oldest first across every location, then location order, then path — not location by
        /// location. Rows reach the table in roughly the order the files are read, and the Log
        /// Viewer shows them in that order while a load is still running; ingesting the earliest
        /// file from each location first makes that order close to the date-ascending sort the
        /// templates finish on, instead of all of one server before any of the next.
        /// </para>
        /// </summary>
        private static List<MatchedFile> EnumerateMatchingFiles(
            string logFile,
            IReadOnlyList<LogLocation> locations,
            DateTime startDate,
            DateTime endDate,
            LogTemplate template,
            LogIngestProgressReporter reporter,
            CancellationToken cancellationToken)
        {
            var matched = new List<MatchedFile>();

            for (var locationIndex = 0; locationIndex < locations.Count; locationIndex++)
            {
                var loc = locations[locationIndex];
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(loc.Path) || !Directory.Exists(loc.Path))
                    continue; // Tolerate missing/offline locations.

                reporter.FileStarted(loc.Name);

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
                    var written = DateTime.MinValue;
                    try
                    {
                        written = info.LastWriteTime;
                        if (written.Date >= startDate.Date && written.Date <= endDate.Date)
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
                    if (keep) matched.Add(new MatchedFile(loc.Name, info.FullName, length, written, locationIndex));
                }
            }

            return matched
                .OrderBy(f => f.Written)
                .ThenBy(f => f.LocationIndex)
                .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// A parsed batch, carrying the file it came from — what lets the writer purge a skip (D4).
        /// An empty batch is a purge request: a file skipped on its own sends one as it stops, so its
        /// committed rows leave the table while the load is still running rather than at the end.
        /// </summary>
        private readonly record struct LogBatch(string FileKey, List<Dictionary<string, string>> Rows);

        private static readonly List<Dictionary<string, string>> PurgeRequest = new();

        // Parses files in parallel and inserts on a single writer to respect SQLite's single-writer model.
        private async Task IngestFilesAsync(
            List<MatchedFile> files,
            LogTemplate template,
            List<string> templateColumns,
            string tableName,
            List<string> columns,
            LogIngestProgressReporter reporter,
            LogIngestControl control,
            CancellationToken cancellationToken)
        {
            if (files.Count == 0)
                return;

            var channel = Channel.CreateBounded<LogBatch>(
                new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = false });

            // The parsers get a token of their own, linked to the caller's, so a writer that fails can
            // stop them. Otherwise they fill the bounded channel and wait in WriteAsync forever — the
            // token never cancels — and the load hangs with every healthy file showing as stalled.
            using var parseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var parseToken = parseCts.Token;

            // Writer-only state; read again below only once the writer has finished.
            var known = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
            var committed = new Dictionary<string, List<LogRowRange>>(StringComparer.Ordinal);
            var purged = new HashSet<string>(StringComparer.Ordinal);

            // D4: a file skipped on its own contributes zero rows. By rowid range, so the cost is the
            // file's rows rather than a scan of the table for an unindexed path — and nothing at all for
            // the usual stall, a file that never committed a batch.
            async Task PurgeAsync(string fileKey, CancellationToken token)
            {
                if (purged.Contains(fileKey)) return;
                if (committed.TryGetValue(fileKey, out var ranges))
                {
                    var deleted = await _logStorage.DeleteRowRangesAsync(tableName, ranges, token);
                    reporter.RowsPurged(fileKey, deleted);
                }

                // Only once the delete has gone through: one a Cancel interrupts leaves the ranges
                // where the sweep below will find them.
                committed.Remove(fileKey);
                purged.Add(fileKey);
            }

            var writerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                    {
                        if (control.ShouldPurge(item.FileKey))
                        {
                            // The single writer is the second and final check in the row-leak guard: the
                            // parser already stops adding to a skipped file's batch, but a batch built
                            // just before the skip can already be in flight here.
                            await PurgeAsync(item.FileKey, cancellationToken);
                            continue;
                        }

                        // Stopped by a Cancel rather than skipped on its own: what it committed stays —
                        // a cancelled load keeps what it read — but nothing more is stored for it. A file
                        // that had already read to its end is complete, so its queued rows still go in.
                        if (item.Rows.Count == 0 || (control.IsSkipped(item.FileKey) && !control.IsFinished(item.FileKey)))
                            continue;

                        // A field beyond any line seen so far gets its column now, before the insert
                        // that needs it. Rows carry Message1..n contiguously, so the new names arrive
                        // in ascending order.
                        List<string>? added = null;
                        foreach (var row in item.Rows)
                            foreach (var key in row.Keys)
                                if (known.Add(key))
                                    (added ??= new()).Add(key);
                        if (added is not null)
                            await _logStorage.AddColumnsAsync(tableName, added);

                        var range = await _logStorage.InsertLogLinesAsync(tableName, item.Rows, cancellationToken);
                        if (range.Count > 0)
                        {
                            if (!committed.TryGetValue(item.FileKey, out var ranges))
                                committed[item.FileKey] = ranges = new List<LogRowRange>();
                            ranges.Add(range);
                        }

                        // Counted here rather than at parse time so the figure means rows
                        // actually committed, not rows queued.
                        reporter.AddRows(item.FileKey, item.Rows.Count);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Nothing will drain the channel from here on, so stop the parsers rather than
                    // leave them blocked on it; the failure itself is rethrown to the prepare below.
                    parseCts.Cancel();
                    throw;
                }
            }, cancellationToken);

            int maxParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
            using var throttler = new SemaphoreSlim(maxParallelism);

            var parseTasks = files.Select(async tf =>
            {
                var fileKey = tf.FilePath;
                var fileName = Path.GetFileName(tf.FilePath);

                if (control.IsSkipped(fileKey))
                {
                    var reason = DescribeSkip(await control.WaitForAbandonAsync(fileKey), control);
                    reporter.FileSkipped(fileKey, fileName, tf.LocationName, reason);
                    return;
                }

                await throttler.WaitAsync(parseToken);
                try
                {
                    reporter.FileOpening(fileKey, fileName, tf.LocationName, tf.Length);
                    await RunAbandonableAsync<object?>(fileKey, fileName, tf.LocationName, control, reporter, async token =>
                    {
                        await ParseFileToChannelAsync(fileKey, tf.LocationName, template, templateColumns, channel.Writer, reporter, control, token);
                        return null;
                    }, parseToken);
                }
                finally
                {
                    // Released exactly once here, whether the file finished, failed, or was
                    // abandoned — the orphan an abandonment leaves behind holds no worker slot.
                    throttler.Release();
                }

                // Skipped on its own: ask the writer to take back what it already committed now, while
                // the rows may be on screen, not only at the end. After the slot is released, so a full
                // channel never holds one; the sweep below covers a request that cannot be sent.
                if (control.ShouldPurge(fileKey))
                {
                    try
                    {
                        await channel.Writer.WriteAsync(new LogBatch(fileKey, PurgeRequest), parseToken);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
                    {
                    }
                }
            }).ToList();

            Exception? parseFailure = null;
            try
            {
                await Task.WhenAll(parseTasks);
            }
            catch (Exception ex)
            {
                parseFailure = ex;
            }

            channel.Writer.TryComplete();

            // Never return while the writer is still going. A cancel used to throw out of the WhenAll
            // above and leave it committing behind the caller's back; now whoever catches that cancel
            // can query the table and see all there is going to be.
            Exception? writerFailure = null;
            try
            {
                await writerTask;
            }
            catch (Exception ex)
            {
                writerFailure = ex;
            }

            // Every committed row of a file skipped on its own comes out, Cancel or not (D4). The
            // purge request above only made it prompt; one still queued when the writer stopped, or
            // never sent, is caught here. CancellationToken.None: this is the contract, not work a
            // Cancel should be able to interrupt.
            var writerFailed = writerFailure is not null and not OperationCanceledException;
            foreach (var fileKey in committed.Keys.ToList())
            {
                if (!control.ShouldPurge(fileKey)) continue;

                if (!writerFailed)
                {
                    await PurgeAsync(fileKey, CancellationToken.None);
                    continue;
                }

                // After a writer failure the table is shown as far as it got, so a skipped file's rows
                // still have to go — but on a table that may have just failed a write, this is best
                // effort, and the failure below is the one to report.
                try
                {
                    await PurgeAsync(fileKey, CancellationToken.None);
                }
                catch (Exception)
                {
                }
            }

            // A real writer failure is what happened, whatever the parsers did because of it.
            if (writerFailed)
                ExceptionDispatchInfo.Capture(writerFailure!).Throw();

            if (parseFailure is not null)
                ExceptionDispatchInfo.Capture(parseFailure).Throw();
            if (writerFailure is not null)
                ExceptionDispatchInfo.Capture(writerFailure).Throw();
        }

        private async Task ParseFileToChannelAsync(
            string filePath,
            string locationName,
            LogTemplate template,
            List<string> templateColumns,
            ChannelWriter<LogBatch> writer,
            LogIngestProgressReporter reporter,
            LogIngestControl control,
            CancellationToken cancellationToken)
        {
            const int baseBatchSize = 1000;
            var batch = new List<Dictionary<string, string>>(baseBatchSize);
            var fileKey = filePath;
            var fileName = Path.GetFileName(filePath);

            try
            {
                // The open must not run inline: it is a synchronous call and, against a dead server,
                // can block a thread for as long as the read would. On a pool thread it can be raced
                // against abandonment at all — inline, a blocked open would never reach the WhenAny.
                using var fs = await Task.Run(() => FileOpener(filePath), cancellationToken);
                using var reader = new StreamReader(fs);

                string sourceFileName = fileName;
                long sequence = 0;
                string? line;

                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    sequence++; // 1-based line number; advances even for skipped lines to preserve order.

                    // Progress comes from the underlying stream position rather than
                    // the characters handed back, so multi-byte encodings and line
                    // endings are accounted for without decoding them twice. It moves
                    // in reader-buffer steps, which is fine for a progress bar.
                    reporter.FileBytesRead(fileKey, fs.Position);

                    if (control.IsSkipped(fileKey))
                        break; // Cooperative stop, in case this read can observe it; the abandon path does not depend on it.

                    try
                    {
                        var parts = SplitLine(line, template.Delimiter);
                        var dict = new Dictionary<string, string>(templateColumns.Count + 4);

                        for (int i = 0; i < templateColumns.Count; i++)
                            dict[templateColumns[i]] = parts.Length > i ? parts[i] : "";

                        for (int i = templateColumns.Count; i < parts.Length; i++)
                            dict[LogOverflowColumns.Name(i - templateColumns.Count + 1)] = parts[i];

                        dict[LogProvenanceColumns.Location] = locationName;
                        dict[LogProvenanceColumns.SourceFile] = sourceFileName;
                        dict[LogProvenanceColumns.Sequence] = sequence.ToString();
                        dict[SourcePathColumn] = filePath;

                        batch.Add(dict);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // Log the problematic line but continue processing
                        continue;
                    }

                    // Outside the per-line catch: a failed hand-off (a cancel, a closed channel) is not a
                    // bad line to step over. Swallowed there, it left the batch growing without bound.
                    if (batch.Count >= baseBatchSize)
                    {
                        reporter.BatchSent(fileKey);
                        await writer.WriteAsync(new LogBatch(fileKey, batch), cancellationToken);
                        batch = new List<Dictionary<string, string>>(baseBatchSize);
                    }
                }

                if (batch.Count > 0 && !control.IsSkipped(fileKey))
                {
                    reporter.BatchSent(fileKey);
                    await writer.WriteAsync(new LogBatch(fileKey, batch), cancellationToken);
                }

                // Sealed as finished unless a skip got in first; after this a Skip is too late to
                // take a complete file's rows back out.
                if (control.TryMarkFinished(fileKey))
                {
                    reporter.FileDone(fileKey);
                }
                else
                {
                    var reason = DescribeSkip(await control.WaitForAbandonAsync(fileKey), control);
                    reporter.FileSkipped(fileKey, fileName, locationName, reason);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !control.IsSkipped(fileKey))
            {
                // The load was cancelled, or its writer failed, with this file mid-read. Not a failure of
                // the file: the prepare accounts for it as cut short (CancelOutstanding) or rethrows the
                // writer's error, and a "failed: The operation was canceled" here would say otherwise.
            }
            catch (Exception ex)
            {
                // Real failure, or the exception our own abandonment left behind in the orphan —
                // the latter already has its Skipped state recorded, so it is not overwritten (D5).
                if (!control.IsSkipped(fileKey))
                    reporter.FileFailed(fileKey, fileName, locationName, $"failed: {ex.Message}");
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
                Filters = split?.ToFilters(),

                // Rows only: CountLogEntriesAsync is the count, and running a second one here — a full
                // pass whenever a keyword filter is on — bought a number nobody read.
                IncludeCount = false
            };
            ApplyCriteria(query, criteria);
            if (query.RawQuery == null)
                await ResolveResultsOrTemplateSortAsync(query, tableName, sortColumns, templateName);

            try
            {
                var (results, _) = await _logStorage.SearchLogsAsync(tableName, query, cancellationToken);
                return results.ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
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
                // A count, and only a count. This went through SearchLogsAsync with no page size, which
                // also read every matching row into memory to throw away — on a table of a million rows,
                // a gigabyte or more on every filter keystroke.
                return await _logStorage.CountLogsAsync(tableName, query, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ToUserFacing(ex, query, "Failed to count log entries");
            }
        }

        public async Task<LogLiveSlice> ReadLiveSliceAsync(
            string tableName, LogLiveRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                return await _logStorage.ReadLiveSliceAsync(tableName, request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to read the rows loaded so far", ex);
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
            catch (OperationCanceledException)
            {
                // A cancel is a cancel, not "failed to group": the statement is interrupted now,
                // and wrapped here it came out as an error banner for a query nobody wanted any more.
                throw;
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

        /// <summary>
        /// Resolves <paramref name="query"/>'s sort the way a page query always has — except on
        /// <see cref="ResultsTableName"/>, which never falls back to the template's sort (that
        /// template may not even describe these columns) and instead reads back in the order it was
        /// collapsed in when nobody has clicked a header since.
        /// </summary>
        private async Task ResolveResultsOrTemplateSortAsync(
            LogQuery query, string tableName, List<SortColumn>? requested, string templateName)
        {
            if (string.Equals(tableName, ResultsTableName, StringComparison.Ordinal))
            {
                query.Sort = requested;
                query.InsertionOrder = true;

                // Its columns as they are: a keyword collapse wrote them in display order already, and a
                // SQL collapse in the order its SELECT chose, which moving provenance to the end would undo.
                query.PhysicalColumnOrder = true;
            }
            else
            {
                query.Sort = await ResolveEffectiveSortAsync(requested, templateName);
            }
        }

        public async Task<(string TableName, int Rows, List<string> Columns)> MaterializeResultsAsync(
            string sourceTable,
            string templateName,
            List<SortColumn>? sorts,
            LogSearchCriteria? criteria,
            LogSplitFilter? split,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(sourceTable, ResultsTableName, StringComparison.Ordinal))
                throw new InvalidOperationException("Results cannot themselves be collapsed.");

            var query = new LogQuery { Filters = split?.ToFilters() };
            ApplyCriteria(query, criteria);
            if (query.RawQuery == null)
                query.Sort = await ResolveEffectiveSortAsync(sorts, templateName);

            try
            {
                var (rows, columns) = await _logStorage.CreateTableFromQueryAsync(sourceTable, ResultsTableName, query, cancellationToken);
                return (ResultsTableName, rows, columns);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ToUserFacing(ex, query, "Failed to collapse the filter into results");
            }
        }

        public Task DropResultsAsync() => _logStorage.DropTableAsync(ResultsTableName);

        private void ApplyCriteria(LogQuery query, LogSearchCriteria? criteria)
        {
            if (criteria == null)
                return;
            if (criteria.UseAdvanced)
            {
                // The user writes logs and results; this instance's tables may be called otherwise.
                if (!string.IsNullOrWhiteSpace(criteria.AdvancedExpression))
                    query.RawQuery = LogSqlTableNames.Resolve(criteria.AdvancedExpression, _sqlNames);
            }
            else
            {
                query.Criteria = criteria;
            }
        }

        // Raw SQL errors are shown verbatim — in the names the user wrote, not the tables they were
        // pointed at ("no such table: results", not results_3f9a…). Other failures get a generic message.
        private Exception ToUserFacing(Exception ex, LogQuery query, string genericMessage)
        {
            if (string.IsNullOrWhiteSpace(query.RawQuery))
                return new InvalidOperationException(genericMessage, ex);

            var message = ex.Message;
            foreach (var (written, actual) in _sqlNames)
                if (!string.Equals(written, actual, StringComparison.OrdinalIgnoreCase))
                    message = message.Replace(actual, written, StringComparison.OrdinalIgnoreCase);
            return new InvalidOperationException(message, ex);
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
                var query = new LogQuery { Filters = split?.ToFilters(), IncludeCount = false };
                ApplyCriteria(query, criteria);
                if (query.RawQuery == null)
                    await ResolveResultsOrTemplateSortAsync(query, tableName, sortColumns, templateName);

                var (results, _) = await _logStorage.SearchLogsAsync(tableName, query, cancellationToken);

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