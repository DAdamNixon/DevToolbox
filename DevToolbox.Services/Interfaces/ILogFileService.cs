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

        // Ingests the selected files into a fresh table (drop + recreate) and returns the table name.
        // progress is optional; pass null for a silent ingest.
        Task<string> PrepareLogTableAsync(
            string logFile,
            IReadOnlyList<LogLocation> locations,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            IProgress<LogIngestProgress>? progress = null,
            CancellationToken cancellationToken = default);

        // Queries an already-prepared table; no re-ingestion.
        Task<List<Dictionary<string, string>>> QueryLogPageAsync(
            string tableName,
            string templateName,
            int pageNumber,
            int pageSize,
            List<SortColumn>? sortColumns,
            LogSearchCriteria? criteria,
            CancellationToken cancellationToken = default);

        Task<int> CountLogEntriesAsync(
            string tableName,
            LogSearchCriteria? criteria,
            CancellationToken cancellationToken = default);

        Task<string> DownloadLogCsvAsync(
            string tableName,
            string templateName,
            List<SortColumn>? sortColumns,
            LogSearchCriteria? criteria,
            string? outputPath = null,
            CancellationToken cancellationToken = default);
    }
}