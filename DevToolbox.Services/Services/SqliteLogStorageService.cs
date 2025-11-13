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
            var logLines = lines.ToList();
            if (!logLines.Any()) return;

            // Get all unique columns from all lines
            var columns = logLines.SelectMany(d => d.Keys).Distinct().ToList();

            using var conn = GetConnection();
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();

            foreach (var line in logLines)
            {
                var colList = string.Join(", ", columns.Select(c => $"[{c}]"));
                var paramList = string.Join(", ", columns.Select((c, i) => $"@p{i}"));
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"INSERT INTO [{tableName}] ({colList}) VALUES ({paramList});";
                for (int i = 0; i < columns.Count; i++)
                    cmd.Parameters.AddWithValue($"@p{i}", line.TryGetValue(columns[i], out var val) ? val ?? "" : "");
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        public async Task<(IEnumerable<Dictionary<string, string>> Results, int TotalCount)> SearchLogsAsync(string tableName, LogQuery query)
        {
            var filters = query.Filters ?? new();
            var page = query.Page ?? 0;
            var pageSize = query.PageSize;
            var searchTerm = query.SearchTerm;

            // Get columns dynamically
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

            // Build WHERE clause
            var where = new List<string>();
            var parameters = new List<SqliteParameter>();

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

            var whereSql = where.Any() ? "WHERE " + string.Join(" AND ", where) : "";

            // Build ORDER BY clause
            string orderBySql;
            if (query.Sort != null && query.Sort.Any(s => !string.IsNullOrWhiteSpace(s.Column) && columns.Contains(s.Column)))
            {
                var orderClauses = query.Sort
                    .Where(s => !string.IsNullOrWhiteSpace(s.Column) && columns.Contains(s.Column))
                    .Select(s => $"[{s.Column}] {(s.Direction?.ToLower() == "desc" ? "DESC" : "ASC")}");
                orderBySql = "ORDER BY " + string.Join(", ", orderClauses);
            }
            else
            {
                orderBySql = "ORDER BY rowid DESC";
            }

            // Count query
            var countSql = $"SELECT COUNT(*) FROM [{tableName}] {whereSql};";

            // Data query
            var dataSql = new StringBuilder();
            dataSql.Append($"SELECT * FROM [{tableName}] {whereSql} ");
            dataSql.Append($"{orderBySql} ");
            bool usePaging = pageSize.HasValue && pageSize.Value > 0;
            if (usePaging)
            {
                dataSql.Append("LIMIT @limit OFFSET @offset;");
                parameters.Add(new SqliteParameter("@limit", pageSize.Value));
                parameters.Add(new SqliteParameter("@offset", page * pageSize.Value));
            }

            int totalCount;
            var results = new List<Dictionary<string, string>>();

            using (var conn = GetConnection())
            {
                await conn.OpenAsync();

                // Count
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = countSql;
                    countCmd.Parameters.AddRange(parameters.Where(p => p.ParameterName != "@limit" && p.ParameterName != "@offset").ToArray());
                    totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
                }

                // Data
                using (var dataCmd = conn.CreateCommand())
                {
                    dataCmd.CommandText = dataSql.ToString();
                    dataCmd.Parameters.AddRange(parameters.ToArray());
                    using var reader = await dataCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var dict = new Dictionary<string, string>();
                        foreach (var col in columns)
                        {
                            var val = reader[col];
                            dict[col] = val is DBNull ? "" : val.ToString() ?? "";
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