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

        /// <summary>
        /// Materializes <paramref name="query"/>'s result against <paramref name="source"/> into a
        /// new table <paramref name="target"/> (dropped first if it already exists) — the Log
        /// Viewer's "collapse into results". Keyword mode reuses the same WHERE/ORDER builder as
        /// <see cref="SearchLogsAsync"/>, so the copy can never disagree with what the grid showed;
        /// SQL mode wraps <see cref="LogQuery.RawQuery"/> and applies the split tab exactly as
        /// <see cref="SearchLogsAsync"/> does. Refused, with the table dropped again and nothing left
        /// behind, if a resulting column name contains <c>]</c> — that would break the <c>[name]</c>
        /// quoting every later query on the table uses.
        /// </summary>
        /// <returns>The row count and column names of the new table.</returns>
        Task<(int Rows, List<string> Columns)> CreateTableFromQueryAsync(
            string source, string target, LogQuery query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes every row whose <paramref name="column"/> equals <paramref name="value"/> — the
        /// row-leak guard's purge (D4): a skipped file must contribute zero rows, including ones it
        /// already committed before the skip was noticed.
        /// </summary>
        Task DeleteRowsForFileAsync(string tableName, string column, string value, CancellationToken cancellationToken = default);
    }
}