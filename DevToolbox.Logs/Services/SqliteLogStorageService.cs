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
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddRange(parameters.ToArray());

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                groups.Add(new LogSplitGroup
                {
                    Value = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "",
                    Count = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1))
                });
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

        public async Task InsertLogLinesAsync(string tableName, IEnumerable<Dictionary<string, string>> lines, CancellationToken cancellationToken = default)
        {
            GuardWritable(nameof(InsertLogLinesAsync));

            var logLines = lines as IList<Dictionary<string, string>> ?? lines.ToList();
            if (logLines.Count == 0) return;

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

            // Not passing the token: once the rows are written, rolling back on a
            // late cancellation would waste the work for no benefit. The table is
            // dropped and rebuilt by the next search anyway.
            await tx.CommitAsync();
        }

        public async Task<(IEnumerable<Dictionary<string, string>> Results, int TotalCount)> SearchLogsAsync(string tableName, LogQuery query)
        {
            var page = query.Page ?? 0;
            var pageSize = query.PageSize;
            bool usePaging = pageSize.HasValue && pageSize.Value > 0;

            var parameters = new List<SqliteParameter>();
            string countSql;
            string dataSql;

            if (!string.IsNullOrWhiteSpace(query.RawQuery))
            {
                // Full custom SELECT: run as a subquery so count/paging stay correct; columns come from the reader.
                var inner = query.RawQuery!.Trim().TrimEnd(';').Trim();
                countSql = $"SELECT COUNT(*) FROM ({inner})";
                var sb = new StringBuilder($"SELECT * FROM ({inner})");
                if (usePaging)
                {
                    sb.Append(" LIMIT @limit OFFSET @offset");
                    parameters.Add(new SqliteParameter("@limit", pageSize!.Value));
                    parameters.Add(new SqliteParameter("@offset", page * pageSize.Value));
                }
                dataSql = sb.ToString();
            }
            else
            {
                var filters = query.Filters ?? new();
                var searchTerm = query.SearchTerm;

                // Physical columns (used only to build the WHERE predicate).
                List<string> columns;
                using (var conn = GetConnection())
                {
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"PRAGMA table_info([{tableName}]);";
                    using var reader = await cmd.ExecuteReaderAsync();
                    columns = new();
                    while (await reader.ReadAsync())
                        columns.Add(reader.GetString(1));
                }

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
                    orderBySql = "ORDER BY rowid DESC";
                }

                countSql = $"SELECT COUNT(*) FROM [{tableName}] {whereSql};";

                var dsb = new StringBuilder();
                dsb.Append($"SELECT * FROM [{tableName}] {whereSql} ");
                dsb.Append($"{orderBySql} ");
                if (usePaging)
                {
                    dsb.Append("LIMIT @limit OFFSET @offset;");
                    parameters.Add(new SqliteParameter("@limit", pageSize!.Value));
                    parameters.Add(new SqliteParameter("@offset", page * pageSize.Value));
                }
                dataSql = dsb.ToString();
            }

            int totalCount;
            var results = new List<Dictionary<string, string>>();

            using (var conn = GetConnection())
            {
                await conn.OpenAsync();

                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = countSql;
                    countCmd.Parameters.AddRange(parameters.Where(p => p.ParameterName != "@limit" && p.ParameterName != "@offset").ToArray());
                    totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
                }

                using (var dataCmd = conn.CreateCommand())
                {
                    dataCmd.CommandText = dataSql;
                    dataCmd.Parameters.AddRange(parameters.ToArray());
                    using var reader = await dataCmd.ExecuteReaderAsync();

                    // Column names from the actual result set, so custom SELECTs (computed columns) render correctly.
                    var fieldNames = new List<string>(reader.FieldCount);
                    for (int i = 0; i < reader.FieldCount; i++)
                        fieldNames.Add(reader.GetName(i));

                    while (await reader.ReadAsync())
                    {
                        var dict = new Dictionary<string, string>(fieldNames.Count);
                        for (int i = 0; i < fieldNames.Count; i++)
                        {
                            var val = reader.GetValue(i);
                            dict[fieldNames[i]] = val is DBNull ? "" : val.ToString() ?? "";
                        }
                        results.Add(dict);
                    }
                }
            }

            return (results, totalCount);
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
    }
}