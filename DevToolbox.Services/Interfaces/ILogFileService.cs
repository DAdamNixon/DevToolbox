using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces
{
    public interface ILogFileService
    {
        Task<List<LogTemplateIndexEntry>> GetAvailableLogFileTemplatesAsync();
        Task<List<LogLocation>> GetLogLocationsAsync();
        Task<LogTemplate?> LoadTemplateAsync(string fileName);
        Task<List<Dictionary<string, string>>> SearchLogFilesPageAsync(
            string logFile,
            IReadOnlyList<LogLocation> locations,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            string searchTerm,
            int pageNumber,
            int pageSize,
            List<SortColumn>? sortColumns,
            LogSearchCriteria? criteria = null);
        Task<int> CountLogEntriesAsync(
            string logFile,
            IReadOnlyList<LogLocation> locations,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            string searchTerm,
            LogSearchCriteria? criteria = null);
        Task<string> DownloadLogCsvAsync(
            string logFile,
            IReadOnlyList<LogLocation> locations,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            string searchTerm,
            List<SortColumn>? sortColumns,
            string? outputPath = null,
            LogSearchCriteria? criteria = null
        );
    }
}