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
        IAsyncEnumerable<Dictionary<string, string>> SearchLogFilesAsync_v2(
            string logFile,
            string location,
            DateTime startDate,
            DateTime endDate,
            string templateName
        );
        Task<List<Dictionary<string, string>>> SearchLogFilesPageAsync(
            string logFile,
            string location,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            string searchTerm,
            int pageNumber,
            int pageSize);

        Task<int> CountLogEntriesAsync(
            string logFile,
            string location,
            DateTime startDate,
            DateTime endDate,
            string templateName,
            string searchTerm);
    }
}