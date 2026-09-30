using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DevToolbox.Services.Services
{
    public class SqliteLogStorageService : ILogStorageService
    {
        private readonly string _dbPath;
        private readonly bool _readOnly;

        /// <param name="dbPath">Where the database is. Defaults to <see cref="LogDatabase.Path"/>.</param>
        /// <param name="readOnly">
        /// Opens every connection with <c>Mode=ReadOnly</c> and makes the three writing members
        /// throw.
        /// <para>
        /// For a caller that must be able to query but must never modify — the MCP server's query
        /// path, whose arguments are chosen by an AI agent rather than by the person at the
        /// keyboard. The flag is not a convenience: SQLite's own refusal at the file handle is a
        /// second layer underneath the fact that the writing members already throw, so neither
        /// alone is the whole defence.
        /// </para>
        /// <para>Defaults to false, so every existing caller keeps the behaviour it had.</para>
        /// </param>
        public SqliteLogStorageService(string? dbPath = null, bool readOnly = false)
        {
            // LogDatabase owns the location, because something other than this class has to be able
            // to delete the file at startup - see LogDatabase for why it is thrown away.
            _dbPath = dbPath ?? LogDatabase.Path;
            _readOnly = readOnly;

            // A read-only instance must not bring the folder into being: creating it would make an
            // instance pointed at a typo look like an empty database rather than a mistake.
            if (!readOnly)
                Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        }

        private SqliteConnection GetConnection() =>
            new(_readOnly ? $"Data Source={_dbPath};Mode=ReadOnly" : $"Data Source={_dbPath}");

        /// <summary>
        /// Makes <paramref name="token"/> stop a statement running on <paramref name="conn"/>.
        /// <para>
        /// Microsoft.Data.Sqlite looks at a token only before a statement starts — its
        /// <c>SqliteCommand.Cancel</c> does nothing — so a count over millions of rows ran to the end
        /// whatever the caller did, and a Cancel pressed during one looked ignored. <c>sqlite3_interrupt</c>
        /// makes the running statement fail with SQLITE_INTERRUPT, which <see cref="IsInterrupt"/> turns
        /// back into a cancellation.
        /// </para>
        /// <para>
        /// Declare the registration after the connection, so it is disposed first: interrupting a
        /// connection that is closing is undefined, and disposing a registration waits out a callback
        /// already running.
        /// </para>
        /// </summary>
        private static CancellationTokenRegistration InterruptOn(SqliteConnection conn, CancellationToken token) =>
            token.CanBeCanceled
                ? token.Register(static state =>
                {
                    if (((SqliteConnection)state!).Handle is { } handle)
                        SQLitePCL.raw.sqlite3_interrupt(handle);
                }, conn)
                : default;

        private static bool IsInterrupt(SqliteException ex, CancellationToken token) =>
            ex.SqliteErrorCode == 9 /* SQLITE_INTERRUPT */ && token.IsCancellationRequested;

        /// <summary>A table's own column order, for one already in the order to show — see <see cref="LogQuery.PhysicalColumnOrder"/>.</summary>
        private static string PhysicalList(IEnumerable<string> physicalColumns)
        {
            var list = physicalColumns.ToList();
            return list.Count == 0 ? "*" : string.Join(", ", list.Select(c => $"[{c}]"));
        }

        /// <summary>A keyword query's column list, provenance last — see <see cref="LogProvenanceColumns.InDisplayOrder"/>.</summary>
        private static string SelectList(IEnumerable<string> physicalColumns)
        {
            var ordered = LogProvenanceColumns.InDisplayOrder(physicalColumns);

            // No columns means no such table; let the statement say so rather than failing on "SELECT FROM".
            return ordered.Count == 0 ? "*" : string.Join(", ", ordered.Select(c => $"[{c}]"));
        }

        /// <summary>
        /// Refuses a write on a read-only instance, naming the member rather than the file — the
        /// path is not the caller's business and can contain a user name.
        /// </summary>
        private void GuardWritable(string member)
        {
            if (_readOnly)
                throw new NotSupportedException(
                    $"{member} is not available: this log storage was opened read-only.");
        }

        public async Task EnsureTableAsync(string tableName, IEnumerable<string> columns)
        {
            GuardWritable(nameof(EnsureTableAsync));

            var cols = columns.ToList();
            if (!cols.Any())
                throw new ArgumentException("At least one column is required.");

            var sb = new StringBuilder();
            sb.Append($"CREATE TABLE IF NOT EXISTS [{tableName}] (");
            sb.Append(string.Join(", ", cols.Select(c => $"[{c}] TEXT")));
            sb.Append(");");

            using var conn = GetConnection();
            await conn.OpenAsync();
            using (var walCmd = conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                await walCmd.ExecuteNonQueryAsync();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sb.ToString();
                await cmd.ExecuteNonQueryAsync();
            }

            // Indexes on the split columns. Without them, switching tabs and
            // recomputing per-tab counts are full scans of a table that routinely
            // holds millions of rows. Created only when the column exists, so a
            // template producing neither is unaffected.
            foreach (var column in cols.Where(LogSplitColumns.IsAllowed))
            {
                using var indexCmd = conn.CreateCommand();
                indexCmd.CommandText =
                    $"CREATE INDEX IF NOT EXISTS [ix_{tableName}_{column}] ON [{tableName}] ([{column}]);";
                await indexCmd.ExecuteNonQueryAsync();
            }
        }

        public async Task<List<LogSplitGroup>> GetGroupCountsAsync(
            string tableName, string column, LogQuery query, CancellationToken cancellationToken = default)
        {
            // The column is an identifier, not a parameter, so it is whitelisted
            // rather than escaped.
            if (!LogSplitColumns.IsAllowed(column))
                throw new ArgumentException($"'{column}' is not a groupable column.", nameof(column));

            var parameters = new List<SqliteParameter>();
            string sql;

            if (!string.IsNullOrWhiteSpace(query.RawQuery))
            {
                // Advanced mode: group over the user's own SELECT. If their query
                // does not project the column the statement fails, and the caller
                // reports it — better than silently showing one empty tab.
                var inner = query.RawQuery!.Trim().TrimEnd(';').Trim();
                sql = $"SELECT [{column}] AS v, COUNT(*) AS n FROM ({inner}) GROUP BY [{column}] ORDER BY v;";
            }
            else
            {
                var columns = await GetColumnNamesAsync(tableName, cancellationToken);
                if (!columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    return new List<LogSplitGroup>();

                var where = new List<string>();
                if (query.Criteria != null)
                {
                    var criteriaSql = LogCriteriaTranslator.Build(query.Criteria, columns, parameters);
                    if (!string.IsNullOrEmpty(criteriaSql)) where.Add(criteriaSql);
                }

                var whereSql = where.Any() ? "WHERE " + string.Join(" AND ", where) : "";
                sql = $"SELECT [{column}] AS v, COUNT(*) AS n FROM [{tableName}] {whereSql} GROUP BY [{column}] ORDER BY v;";
            }

            var groups = new List<LogSplitGroup>();

            using var conn = GetConnection();
            await conn.OpenAsync(cancellationToken);
            using var interrupt = InterruptOn(conn, cancellationToken);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddRange(parameters.ToArray());

            try
            {
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    groups.Add(new LogSplitGroup
                    {
                        Value = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "",
                        Count = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1))
                    });
                }
            }
            catch (SqliteException ex) when (IsInterrupt(ex, cancellationToken))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return groups;
        }

        private async Task<List<string>> GetColumnNamesAsync(string tableName, CancellationToken cancellationToken)
        {
            using var conn = GetConnection();
            await conn.OpenAsync(cancellationToken);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info([{tableName}]);";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            var columns = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
            return columns;
        }

        public async Task<bool> TableExistsAsync(string tableName)
        {
            using var conn = GetConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
            cmd.Parameters.AddWithValue("@name", tableName);
            using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync();
        }

        public async Task<LogRowRange> InsertLogLinesAsync(string tableName, IEnumerable<Dictionary<string, string>> lines, CancellationToken cancellationToken = default)
        {
            GuardWritable(nameof(InsertLogLinesAsync));

            var logLines = lines as IList<Dictionary<string, string>> ?? lines.ToList();
            if (logLines.Count == 0) return default;

            // Union of keys across the batch keeps the prepared command stable.
            var columns = logLines.SelectMany(d => d.Keys).Distinct().ToList();

            using var conn = GetConnection();
            await conn.OpenAsync(cancellationToken);

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA synchronous=NORMAL;";
                await pragma.ExecuteNonQueryAsync(cancellationToken);
            }

            using var tx = conn.BeginTransaction();

            var colList = string.Join(", ", columns.Select(c => $"[{c}]"));
            var paramList = string.Join(", ", columns.Select((c, i) => $"@p{i}"));
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO [{tableName}] ({colList}) VALUES ({paramList});";

            var parameters = new SqliteParameter[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                parameters[i] = cmd.CreateParameter();
                parameters[i].ParameterName = $"@p{i}";
                cmd.Parameters.Add(parameters[i]);
            }

            // Reuse one prepared command for every row in the batch.
            foreach (var line in logLines)
            {
                for (int i = 0; i < columns.Count; i++)
                    parameters[i].Value = line.TryGetValue(columns[i], out var val) ? (val ?? "") : "";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Inside the transaction, so nothing else can have inserted in between: with one writer
            // and one transaction per batch, the batch's rowids run unbroken up to this one.
            long last;
            using (var lastCmd = conn.CreateCommand())
            {
                lastCmd.Transaction = tx;
                lastCmd.CommandText = "SELECT last_insert_rowid();";
                last = Convert.ToInt64(await lastCmd.ExecuteScalarAsync(CancellationToken.None));
            }

            // Not passing the token: once the rows are written, rolling back on a
            // late cancellation would waste the work for no benefit. A cancelled load
            // keeps what it committed — the Log Viewer may already be showing it.
            await tx.CommitAsync();

            return new LogRowRange(last - logLines.Count + 1, last);
        }

        public async Task AddColumnsAsync(string tableName, IEnumerable<string> columns)
        {
            GuardWritable(nameof(AddColumnsAsync));

            var toAdd = columns.ToList();
            if (toAdd.Count == 0) return;

            using var conn = GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            foreach (var column in toAdd)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;

                // DEFAULT '' so the rows already there read as the empty field they would have been
                // given had the column existed when they were written — the ingest writes "" for a
                // field a line lacks, never NULL, and SQL typed against these columns expects that.
                cmd.CommandText = $"ALTER TABLE [{tableName}] ADD COLUMN [{column}] TEXT DEFAULT '';";
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        /// <summary>
        /// Builds the keyword-mode WHERE and ORDER BY for <paramref name="tableName"/>: filters,
        /// free-text search and structured criteria into one predicate, bound as parameters, plus
        /// the resolved sort. Shared by <see cref="SearchLogsAsync"/> and
        /// <see cref="CreateTableFromQueryAsync"/> so a keyword collapse can never disagree with
        /// what the grid showed.
        /// </summary>
        private async Task<(List<string> Columns, string WhereSql, string OrderBySql, List<SqliteParameter> Parameters)>
            BuildKeywordQueryAsync(string tableName, LogQuery query)
        {
            // Physical columns (used only to build the WHERE predicate).
            List<string> columns;
            using (var conn = GetConnection())
            {
                await conn.OpenAsync();
                columns = await ReadColumnsAsync(conn, null, tableName, CancellationToken.None);
            }

            return BuildKeywordClauses(columns, query);
        }

        /// <summary>A table's columns in physical order, on <paramref name="conn"/> — inside <paramref name="tx"/>'s snapshot when given.</summary>
        private static async Task<List<string>> ReadColumnsAsync(SqliteConnection conn, SqliteTransaction? tx, string tableName, CancellationToken cancellationToken)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA table_info([{tableName}]);";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var columns = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
            return columns;
        }

        /// <summary><see cref="BuildKeywordQueryAsync"/> over a column list the caller already has.</summary>
        private static (List<string> Columns, string WhereSql, string OrderBySql, List<SqliteParameter> Parameters)
            BuildKeywordClauses(List<string> columns, LogQuery query)
        {
            var parameters = new List<SqliteParameter>();
            var filters = query.Filters ?? new();
            var searchTerm = query.SearchTerm;

            var where = new List<string>();

            foreach (var filter in filters)
            {
                where.Add($"[{filter.Key}] = @{filter.Key}");
                parameters.Add(new SqliteParameter($"@{filter.Key}", filter.Value ?? DBNull.Value));
            }

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var searchClauses = columns.Select(col => $"[{col}] LIKE @searchTerm").ToList();
                where.Add("(" + string.Join(" OR ", searchClauses) + ")");
                parameters.Add(new SqliteParameter("@searchTerm", $"%{searchTerm}%"));
            }

            if (query.Criteria != null)
            {
                var criteriaSql = LogCriteriaTranslator.Build(query.Criteria, columns, parameters);
                if (!string.IsNullOrEmpty(criteriaSql))
                    where.Add(criteriaSql);
            }

            var whereSql = where.Any() ? "WHERE " + string.Join(" AND ", where) : "";

            string orderBySql;
            if (query.Sort != null && query.Sort.Any(s => !string.IsNullOrWhiteSpace(s.Column) && columns.Contains(s.Column)))
            {
                var orderClauses = query.Sort
                    .Where(s => !string.IsNullOrWhiteSpace(s.Column) && columns.Contains(s.Column))
                    .Select(s =>
                    {
                        var dir = s.Direction?.ToLower() == "desc" ? "DESC" : "ASC";
                        // Sequence stores a line number; sort it numerically, not lexically ("10" vs "2").
                        var expr = string.Equals(s.Column, "Sequence", StringComparison.OrdinalIgnoreCase)
                            ? $"CAST([{s.Column}] AS INTEGER)"
                            : $"[{s.Column}]";
                        return $"{expr} {dir}";
                    });
                orderBySql = "ORDER BY " + string.Join(", ", orderClauses);
            }
            else
            {
                // A3: a materialized results table reads back in the order it was collapsed,
                // instead of the newest-first default every other table opens with.
                orderBySql = query.InsertionOrder ? "ORDER BY rowid ASC" : "ORDER BY rowid DESC";
            }

            return (columns, whereSql, orderBySql, parameters);
        }

        /// <summary>
        /// The count and page statements for <paramref name="query"/> and the parameters they bind.
        /// One builder for <see cref="SearchLogsAsync"/> and <see cref="CountLogsAsync"/>, so a count
        /// can never describe a different set of rows than the page beside it.
        /// </summary>
        private async Task<(string CountSql, string DataSql, List<SqliteParameter> Parameters, List<SqliteParameter> Paging)>
            BuildSearchAsync(string tableName, LogQuery query)
        {
            var page = query.Page ?? 0;
            var pageSize = query.PageSize;
            bool usePaging = pageSize.HasValue && pageSize.Value > 0;

            var parameters = new List<SqliteParameter>();
            var paging = new List<SqliteParameter>();
            string countSql;
            string dataSql;

            if (!string.IsNullOrWhiteSpace(query.RawQuery))
            {
                // Full custom SELECT: run as a subquery so count/paging stay correct; columns come from the reader.
                var inner = query.RawQuery!.Trim().TrimEnd(';').Trim();

                // The active split tab, if any. The column is whitelisted rather than trusted,
                // because it reaches SQL as an identifier, not a parameter.
                var tabWhere = "";
                var filters = query.Filters;
                if (filters is { Count: > 0 })
                {
                    var filter = filters.First();
                    if (!LogSplitColumns.IsAllowed(filter.Key))
                        throw new ArgumentException($"'{filter.Key}' is not a groupable column.");
                    tabWhere = $" WHERE [{filter.Key}] = @{filter.Key}";
                    parameters.Add(new SqliteParameter($"@{filter.Key}", filter.Value ?? DBNull.Value));
                }

                countSql = $"SELECT COUNT(*) FROM ({inner}){tabWhere}";
                dataSql = $"SELECT * FROM ({inner}){tabWhere}" + (usePaging ? " LIMIT @limit OFFSET @offset" : "");
            }
            else
            {
                var (columns, whereSql, orderBySql, builtParams) = await BuildKeywordQueryAsync(tableName, query);
                parameters.AddRange(builtParams);

                countSql = $"SELECT COUNT(*) FROM [{tableName}] {whereSql};";
                dataSql = $"SELECT {(query.PhysicalColumnOrder ? PhysicalList(columns) : SelectList(columns))} FROM [{tableName}] {whereSql} {orderBySql}" +
                          (usePaging ? " LIMIT @limit OFFSET @offset;" : ";");
            }

            if (usePaging)
            {
                paging.Add(new SqliteParameter("@limit", pageSize!.Value));
                paging.Add(new SqliteParameter("@offset", page * pageSize.Value));
            }

            return (countSql, dataSql, parameters, paging);
        }

        public async Task<(IEnumerable<Dictionary<string, string>> Results, int TotalCount)> SearchLogsAsync(
            string tableName, LogQuery query, CancellationToken cancellationToken = default)
        {
            var (countSql, dataSql, parameters, paging) = await BuildSearchAsync(tableName, query);

            var totalCount = 0;
            var results = new List<Dictionary<string, string>>();

            using var conn = GetConnection();
            await conn.OpenAsync(cancellationToken);
            using var interrupt = InterruptOn(conn, cancellationToken);

            try
            {
                if (query.IncludeCount)
                {
                    using var countCmd = conn.CreateCommand();
                    countCmd.CommandText = countSql;
                    countCmd.Parameters.AddRange(parameters.ToArray());
                    totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
                }

                // A SQL query that projects the table's columns exactly as they lie — a SELECT * — gets
                // them in display order, as keyword mode does. The ingest adds an overflow column after
                // the provenance ones, so without this "SELECT * FROM logs" showed Message3 after
                // SourcePath where it had always come before Location.
                List<string>? wholeTable = null;
                if (!string.IsNullOrWhiteSpace(query.RawQuery))
                    wholeTable = await ReadColumnsAsync(conn, null, tableName, cancellationToken);

                using var dataCmd = conn.CreateCommand();
                dataCmd.CommandText = dataSql;
                dataCmd.Parameters.AddRange(parameters.ToArray());
                dataCmd.Parameters.AddRange(paging.ToArray());
                using var reader = await dataCmd.ExecuteReaderAsync(cancellationToken);
                results = await ReadRowsAsync(reader, cancellationToken, wholeTable);
            }
            catch (SqliteException ex) when (IsInterrupt(ex, cancellationToken))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return (results, totalCount);
        }

        public async Task<int> CountLogsAsync(string tableName, LogQuery query, CancellationToken cancellationToken = default)
        {
            var (countSql, _, parameters, _) = await BuildSearchAsync(tableName, query);

            using var conn = GetConnection();
            await conn.OpenAsync(cancellationToken);
            using var interrupt = InterruptOn(conn, cancellationToken);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = countSql;
            cmd.Parameters.AddRange(parameters.ToArray());

            try
            {
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
            }
            catch (SqliteException ex) when (IsInterrupt(ex, cancellationToken))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        public async Task<LogLiveSlice> ReadLiveSliceAsync(string tableName, LogLiveRequest request, CancellationToken cancellationToken = default)
        {
            if (request.GroupColumn is { } requestedGroup && !LogSplitColumns.IsAllowed(requestedGroup))
                throw new ArgumentException($"'{requestedGroup}' is not a groupable column.", nameof(request));

            using var conn = GetConnection();
            await conn.OpenAsync(cancellationToken);
            using var interrupt = InterruptOn(conn, cancellationToken);

            try
            {
                // Deferred: the snapshot starts with the first read below and every statement after it
                // sees the same table, while the writer goes on committing batches the next refresh gets.
                using var tx = conn.BeginTransaction(deferred: true);

                long maxRowid;
                using (var maxCmd = conn.CreateCommand())
                {
                    maxCmd.Transaction = tx;
                    maxCmd.CommandText = $"SELECT COALESCE(MAX(rowid), 0) FROM [{tableName}];";
                    maxRowid = Convert.ToInt64(await maxCmd.ExecuteScalarAsync(cancellationToken));
                }

                // The columns inside the same snapshot as the rows. Read before it, an overflow column
                // the writer added in between would be missing from the select and the filter, for rows
                // the snapshot does contain — and the cursor has moved past them for good.
                var columns = await ReadColumnsAsync(conn, tx, tableName, cancellationToken);

                // The counted rows (filter and tab) and the grouped rows (filter only) are built exactly
                // as a search builds them, so a live count can never disagree with the grid a later
                // search shows. Arrival order unless a sort is given: InsertionOrder is what makes "no
                // sort" mean rowid ASC.
                var (_, rowWhere, orderBy, rowParams) = BuildKeywordClauses(columns, new LogQuery
                {
                    Criteria = request.Criteria,
                    Filters = request.Split?.ToFilters(),
                    Sort = request.Sort,
                    InsertionOrder = true
                });

                // A table without the column has nothing to group, the same as GetGroupCountsAsync says.
                string? groupColumn = null;
                string groupWhere = "";
                var groupParams = new List<SqliteParameter>();
                if (request.GroupColumn is { } requested && columns.Contains(requested, StringComparer.OrdinalIgnoreCase))
                {
                    groupColumn = requested;
                    (_, groupWhere, _, groupParams) = BuildKeywordClauses(columns, new LogQuery { Criteria = request.Criteria });
                }

                // The rowid range is what makes every figure here cost the new rows, not the table:
                // a range on rowid is a seek into the table's own b-tree.
                const string range = "rowid > @liveAfter AND rowid <= @liveMax";
                SqliteParameter[] RangeParams() => new[]
                {
                    new SqliteParameter("@liveAfter", request.AfterRowid),
                    new SqliteParameter("@liveMax", maxRowid)
                };
                static string And(string whereSql, string clause) =>
                    string.IsNullOrEmpty(whereSql) ? $"WHERE {clause}" : $"{whereSql} AND {clause}";

                int count;
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.Transaction = tx;
                    countCmd.CommandText = $"SELECT COUNT(*) FROM [{tableName}] {And(rowWhere, range)};";
                    countCmd.Parameters.AddRange(rowParams.ToArray());
                    countCmd.Parameters.AddRange(RangeParams());
                    count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
                }

                var groups = new List<LogSplitGroup>();
                if (groupColumn is not null)
                {
                    using var groupCmd = conn.CreateCommand();
                    groupCmd.Transaction = tx;
                    groupCmd.CommandText =
                        $"SELECT [{groupColumn}] AS v, COUNT(*) AS n FROM [{tableName}] {And(groupWhere, range)} GROUP BY [{groupColumn}] ORDER BY v;";
                    groupCmd.Parameters.AddRange(groupParams.ToArray());
                    groupCmd.Parameters.AddRange(RangeParams());
                    using var reader = await groupCmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        groups.Add(new LogSplitGroup
                        {
                            Value = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "",
                            Count = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1))
                        });
                    }
                }

                var rows = new List<Dictionary<string, string>>();
                if (request.Take > 0)
                {
                    using var dataCmd = conn.CreateCommand();
                    dataCmd.Transaction = tx;
                    dataCmd.CommandText =
                        $"SELECT {SelectList(columns)} FROM [{tableName}] {And(rowWhere, range)} {orderBy} LIMIT @liveTake OFFSET @liveSkip;";
                    dataCmd.Parameters.AddRange(rowParams.ToArray());
                    dataCmd.Parameters.AddRange(RangeParams());
                    dataCmd.Parameters.Add(new SqliteParameter("@liveTake", request.Take));
                    dataCmd.Parameters.Add(new SqliteParameter("@liveSkip", Math.Max(0, request.Skip)));
                    using var reader = await dataCmd.ExecuteReaderAsync(cancellationToken);
                    rows = await ReadRowsAsync(reader, cancellationToken);
                }

                tx.Commit();
                return new LogLiveSlice { MaxRowid = maxRowid, Count = count, Groups = groups, Rows = rows };
            }
            catch (SqliteException ex) when (IsInterrupt(ex, cancellationToken))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        /// <summary>Every row a reader returns, as column-name → text; NULL reads as empty.</summary>
        /// <param name="wholeTable">
        /// The table's physical columns. When the result's columns are exactly those, in that order,
        /// each row is filled in display order instead (see <see cref="LogProvenanceColumns.InDisplayOrder"/>).
        /// </param>
        private static async Task<List<Dictionary<string, string>>> ReadRowsAsync(
            SqliteDataReader reader, CancellationToken cancellationToken, IReadOnlyList<string>? wholeTable = null)
        {
            // Column names from the actual result set, so custom SELECTs (computed columns) render correctly.
            var fieldNames = new List<string>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                fieldNames.Add(reader.GetName(i));

            var order = Enumerable.Range(0, fieldNames.Count).ToList();
            if (wholeTable is not null && fieldNames.SequenceEqual(wholeTable, StringComparer.OrdinalIgnoreCase))
            {
                var display = LogProvenanceColumns.InDisplayOrder(fieldNames);
                order = display.Select(name => fieldNames.IndexOf(name)).ToList();
            }

            var results = new List<Dictionary<string, string>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var dict = new Dictionary<string, string>(fieldNames.Count);
                foreach (var i in order)
                {
                    var val = reader.GetValue(i);
                    dict[fieldNames[i]] = val is DBNull ? "" : val.ToString() ?? "";
                }
                results.Add(dict);
            }
            return results;
        }

        public async Task DropTableAsync(string tableName)
        {
            GuardWritable(nameof(DropTableAsync));

            using var conn = GetConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS [{tableName}];";
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task DeleteRowsForFileAsync(string tableName, string column, string value, CancellationToken cancellationToken = default)
        {
            GuardWritable(nameof(DeleteRowsForFileAsync));

            using var conn = GetConnection();
            await conn.OpenAsync(cancellationToken);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM [{tableName}] WHERE [{column}] = @value;";
            cmd.Parameters.AddWithValue("@value", value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<int> DeleteRowRangesAsync(string tableName, IReadOnlyList<LogRowRange> ranges, CancellationToken cancellationToken = default)
        {
            GuardWritable(nameof(DeleteRowRangesAsync));
            if (ranges.Count == 0) return 0;

            using var conn = GetConnection();
            await conn.OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM [{tableName}] WHERE rowid BETWEEN @first AND @last;";
            var first = cmd.Parameters.Add("@first", SqliteType.Integer);
            var last = cmd.Parameters.Add("@last", SqliteType.Integer);

            var deleted = 0;
            foreach (var range in ranges)
            {
                first.Value = range.First;
                last.Value = range.Last;
                deleted += await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(CancellationToken.None);
            return deleted;
        }

        public async Task<(int Rows, List<string> Columns)> CreateTableFromQueryAsync(
            string source, string target, LogQuery query, CancellationToken cancellationToken = default)
        {
            GuardWritable(nameof(CreateTableFromQueryAsync));

            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("A target table name is required.", nameof(target));

            var parameters = new List<SqliteParameter>();
            string selectSql;

            if (!string.IsNullOrWhiteSpace(query.RawQuery))
            {
                // Same subquery-plus-tab shape as SearchLogsAsync, minus paging: this is the whole
                // result, not one page of it, and the inner query's own order (or lack of one)
                // decides — never an outer ORDER BY of ours.
                var inner = query.RawQuery!.Trim().TrimEnd(';').Trim();
                var tabWhere = "";
                var filters = query.Filters;
                if (filters is { Count: > 0 })
                {
                    var filter = filters.First();
                    if (!LogSplitColumns.IsAllowed(filter.Key))
                        throw new ArgumentException($"'{filter.Key}' is not a groupable column.");
                    tabWhere = $" WHERE [{filter.Key}] = @{filter.Key}";
                    parameters.Add(new SqliteParameter($"@{filter.Key}", filter.Value ?? DBNull.Value));
                }
                selectSql = $"SELECT * FROM ({inner}){tabWhere}";
            }
            else
            {
                // The display order, not SELECT *: the collapsed table then has its columns the way the
                // grid showed them, even when the ingest added an overflow column after the provenance ones.
                var (sourceColumns, whereSql, orderBySql, builtParams) = await BuildKeywordQueryAsync(source, query);
                parameters.AddRange(builtParams);
                selectSql = $"SELECT {SelectList(sourceColumns)} FROM [{source}] {whereSql} {orderBySql}";
            }

            using var conn = GetConnection();
            await conn.OpenAsync(cancellationToken);

            using (var dropCmd = conn.CreateCommand())
            {
                dropCmd.CommandText = $"DROP TABLE IF EXISTS [{target}];";
                await dropCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var ctasCmd = conn.CreateCommand())
            {
                ctasCmd.CommandText = $"CREATE TABLE [{target}] AS {selectSql};";
                ctasCmd.Parameters.AddRange(parameters.ToArray());
                await ctasCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var columns = await GetColumnNamesAsync(target, cancellationToken);

            // ']' would break the [name] quoting every later query on this table uses — refuse
            // rather than hand back a table nothing can safely read.
            var badColumn = columns.FirstOrDefault(c => c.Contains(']'));
            if (badColumn is not null)
            {
                using var dropBad = conn.CreateCommand();
                dropBad.CommandText = $"DROP TABLE IF EXISTS [{target}];";
                await dropBad.ExecuteNonQueryAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Column '{badColumn}' cannot be collapsed into a table — rename it with AS in the SELECT.");
            }

            foreach (var column in columns.Where(LogSplitColumns.IsAllowed))
            {
                using var indexCmd = conn.CreateCommand();
                indexCmd.CommandText =
                    $"CREATE INDEX IF NOT EXISTS [ix_{target}_{column}] ON [{target}] ([{column}]);";
                await indexCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM [{target}];";
            var rows = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));

            return (rows, columns);
        }
    }
}