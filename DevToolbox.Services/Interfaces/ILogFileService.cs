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
        Task<List<Dictionary<string, string>>> SearchLogFilesAsync(
            string logFile,
            string location,
            DateTime startDate,
            DateTime endDate,
            string templateName
        );
    }
}