using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces
{
    public interface ILogFileService
    {
        Task<List<LogTemplateIndexEntry>> GetAvailableLogFileTemplatesAsync();
        Task<List<LogLocation>> GetLogLocationsAsync();
        Task<LogTemplate?> LoadTemplateAsync(string fileName);

        /// <summary>
        /// The distinct log-file names present in <paramref name="locations"/>, for
        /// the Log File dropdown.
        /// <para>
        /// Only locations carrying a <see cref="LogLocation.NamePattern"/> contribute;
        /// the rest have no way to tell a project name from a date and are skipped,
        /// leaving the caller on its configured preset list. Returns an empty list
        /// rather than throwing when a share is unreachable — a dropdown that cannot
        /// be filled must not break typing a name by hand.
        /// </para>
        /// </summary>
        Task<List<DiscoveredLogName>> DiscoverLogFileNamesAsync(
            IReadOnlyList<LogLocation> locations,
            string templateName,
            CancellationToken cancellationToken = default);

        // Ingests the selected files into a fresh table (drop + recreate) and returns the table name
        // plus every file it did not manage to read. progress is optional; pass null for a silent
        // ingest. control is optional; pass null to accept the ingest's default settings and get no
        // Skip capability.
        Task<LogPrepareResult> PrepareLogTableAsync(
            string logFile,
            IReadOnlyList<LogLocation> locations,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            IProgress<LogIngestProgress>? progress = null,
            LogIngestControl? control = null,
            CancellationToken cancellationToken = default);

        // Queries an already-prepared table; no re-ingestion.
        // split restricts results to one tab; null is the "All" tab.
        Task<List<Dictionary<string, string>>> QueryLogPageAsync(
            string tableName,
            string templateName,
            int pageNumber,
            int pageSize,
            List<SortColumn>? sortColumns,
            LogSearchCriteria? criteria,
            LogSplitFilter? split = null,
            CancellationToken cancellationToken = default);

        Task<int> CountLogEntriesAsync(
            string tableName,
            LogSearchCriteria? criteria,
            LogSplitFilter? split = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Distinct values of the split column with their row counts, for building
        /// the tab strip. Respects the active keyword filter, so tab counts always
        /// add up to what the All tab shows.
        /// </summary>
        Task<List<LogSplitGroup>> GetSplitGroupsAsync(
            string tableName,
            LogSplitMode mode,
            LogSearchCriteria? criteria,
            CancellationToken cancellationToken = default);

        Task<string> DownloadLogCsvAsync(
            string tableName,
            string templateName,
            List<SortColumn>? sortColumns,
            LogSearchCriteria? criteria,
            string? outputPath = null,
            LogSplitFilter? split = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Collapses a filter's current result — keyword or SQL mode, every page, only
        /// <paramref name="split"/>'s tab — into the <c>results</c> table
        /// (<see cref="DbLogService.ResultsTableName"/>), replacing whatever was there.
        /// Refuses when <paramref name="sourceTable"/> already <em>is</em> <c>results</c>: one level
        /// only.
        /// </summary>
        /// <returns>The row count and column names of the new table.</returns>
        Task<(int Rows, List<string> Columns)> MaterializeResultsAsync(
            string sourceTable,
            string templateName,
            List<SortColumn>? sorts,
            LogSearchCriteria? criteria,
            LogSplitFilter? split,
            CancellationToken cancellationToken = default);

        /// <summary>Drops the <c>results</c> table. Not an error when there is none.</summary>
        Task DropResultsAsync();
    }
}