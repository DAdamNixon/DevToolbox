using DevToolbox.Services.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DevToolbox.Services.Interfaces
{
    public interface ILogStorageService
    {
        Task EnsureTableAsync(string tableName, IEnumerable<string> columns);
        Task InsertLogLinesAsync(string tableName, IEnumerable<Dictionary<string, string>> lines);
        Task<(IEnumerable<Dictionary<string, string>> Results, int TotalCount)> SearchLogsAsync(string tableName, LogQuery query);
        Task<bool> TableExistsAsync(string tableName);
    }
}