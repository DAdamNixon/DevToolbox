using DevToolbox.Services.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DevToolbox.Services.Interfaces
{
    public interface ILogStorageService
    {
        Task EnsureTableAsync(string tableName, IEnumerable<string> columns);

        /// <summary>
        /// Inserts a batch inside one transaction.
        /// <para>
        /// Takes a token because this is the only part of an ingest that is not
        /// file I/O, and a batch of a thousand rows is long enough that a Cancel
        /// pressed during it would otherwise appear to do nothing.
        /// </para>
        /// </summary>
        Task InsertLogLinesAsync(string tableName, IEnumerable<Dictionary<string, string>> lines, CancellationToken cancellationToken = default);
        Task<(IEnumerable<Dictionary<string, string>> Results, int TotalCount)> SearchLogsAsync(string tableName, LogQuery query);

        /// <summary>
        /// Row counts per distinct value of <paramref name="column"/>, honouring the
        /// same filters as a search. One grouped query rather than a count per tab.
        /// <paramref name="column"/> must pass <see cref="LogSplitColumns.IsAllowed"/>.
        /// </summary>
        Task<List<LogSplitGroup>> GetGroupCountsAsync(string tableName, string column, LogQuery query, CancellationToken cancellationToken = default);

        Task<bool> TableExistsAsync(string tableName);
        Task DropTableAsync(string tableName);
    }
}