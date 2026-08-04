using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DevToolbox.Services.Services
{
    public class SqliteLogStorageService : ILogStorageService
    {
        private readonly string _dbPath;

        public SqliteLogStorageService(string? dbPath = null)
        {
            _dbPath = dbPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevToolbox", "logs.db"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        }

        private SqliteConnection GetConnection() => new($"Data Source={_dbPath}");

        public async Task EnsureTableAsync(string tableName, IEnumerable<string> columns)
        {
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
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync();
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

        public async Task InsertLogLinesAsync(string tableName, IEnumerable<Dictionary<string, string>> lines)
        {
            var logLines = lines as IList<Dictionary<string, string>> ?? lines.ToList();
            if (logLines.Count == 0) return;

            // Union of keys across the batch keeps the prepared command stable.
            var columns = logLines.SelectMany(d => d.Keys).Distinct().ToList();

            using var conn = GetConnection();
            await conn.OpenAsync();

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA synchronous=NORMAL;";
                await pragma.ExecuteNonQueryAsync();
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
                await cmd.ExecuteNonQueryAsync();
            }

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
            using var conn = GetConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS [{tableName}];";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}