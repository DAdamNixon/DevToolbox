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
        /// <returns>
        /// The rowids the batch was given — contiguous, since one writer inserts each batch in one
        /// transaction — so a skipped file's rows can later be purged by range.
        /// </returns>
        Task<LogRowRange> InsertLogLinesAsync(string tableName, IEnumerable<Dictionary<string, string>> lines, CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds columns to an existing table. The ingest calls it for an overflow column the first
        /// time a line needs one; in SQLite it changes only the schema, whatever the row count.
        /// </summary>
        Task AddColumnsAsync(string tableName, IEnumerable<string> columns);

        /// <summary>
        /// A page of <paramref name="query"/>, and its total unless <see cref="LogQuery.IncludeCount"/>
        /// is false (then 0). Keyword mode returns columns in
        /// <see cref="LogProvenanceColumns.InDisplayOrder"/>; SQL mode in whatever order the query
        /// projects. Cancelling <paramref name="cancellationToken"/> interrupts a running statement.
        /// </summary>
        Task<(IEnumerable<Dictionary<string, string>> Results, int TotalCount)> SearchLogsAsync(string tableName, LogQuery query, CancellationToken cancellationToken = default);

        /// <summary>
        /// How many rows <paramref name="query"/> matches, without reading any of them — the count a
        /// search would report, for callers that need nothing else.
        /// </summary>
        Task<int> CountLogsAsync(string tableName, LogQuery query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads what arrived after <see cref="LogLiveRequest.AfterRowid"/> — count, per-group
        /// counts and a page of rows — in one read transaction, so all of it describes the same
        /// moment of a table that is still being written. See <see cref="LogLiveRequest"/>.
        /// </summary>
        Task<LogLiveSlice> ReadLiveSliceAsync(string tableName, LogLiveRequest request, CancellationToken cancellationToken = default);

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

        /// <summary>
        /// Deletes the rows in <paramref name="ranges"/>, in one transaction, and returns how many
        /// went. The purge the ingest actually uses: a range delete walks only the file's own rows,
        /// where <see cref="DeleteRowsForFileAsync"/> has to scan the table for an unindexed path.
        /// </summary>
        Task<int> DeleteRowRangesAsync(string tableName, IReadOnlyList<LogRowRange> ranges, CancellationToken cancellationToken = default);
    }
}