using DevToolbox.Services.Models;
using Microsoft.Data.Sqlite;

namespace DevToolbox.Mcp.Core;

/// <summary>
/// What is actually in each column of a prepared table: how many distinct values, how many rows
/// are non-empty, and the most common values.
/// <para>
/// This is the local analogue of the DB2 server's <c>sample_rows</c>, and it exists for the same
/// reason that server learned the hard way. An agent that has not looked at the data writes
/// filters against values it imagined — and a filter matching nothing is indistinguishable from a
/// question whose answer is genuinely nothing. Handing over the real distribution up front is what
/// makes the difference between a query that is correct and one that merely runs.
/// </para>
/// <para>
/// It reads through a <b>read-only</b> connection and builds its SQL from column names taken from
/// <c>PRAGMA table_info</c> — that is, from the database itself, never from a caller. The table
/// name is a handle <see cref="PreparedTables"/> already refused to resolve unless it issued it.
/// So although these statements are interpolated (a column name cannot be a parameter), no part of
/// them originates with the caller.
/// </para>
/// </summary>
public sealed class ColumnProfiler
{
    private readonly string _dbPath;

    internal ColumnProfiler(string dbPath) => _dbPath = dbPath;

    private SqliteConnection Open() => new($"Data Source={_dbPath};Mode=ReadOnly");

    /// <summary>Columns worth profiling: everything except the provenance path.</summary>
    /// <remarks>
    /// <c>SourcePath</c> is excluded because it is a full filesystem path repeated on every row —
    /// its distribution is exactly <c>SourceFile</c>'s, and printing it spends a lot of context to
    /// say the same thing twice. <c>Location</c>, <c>SourceFile</c> and <c>Sequence</c> stay: the
    /// first two are how a finding gets attributed to a file, and they are cheap.
    /// </remarks>
    private static bool Profilable(string column) =>
        !string.Equals(column, LogProvenanceColumns.SourcePath, StringComparison.OrdinalIgnoreCase);

    internal async Task<(int Rows, List<ColumnProfile> Columns)> ProfileAsync(
        string table,
        int topValues,
        CancellationToken cancellationToken = default)
    {
        using var conn = Open();
        await conn.OpenAsync(cancellationToken);

        var columns = new List<string>();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info([{table}]);";
            using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        var rows = 0;
        using (var count = conn.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM [{table}];";
            rows = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        var profiles = new List<ColumnProfile>();

        foreach (var column in columns.Where(Profilable))
        {
            cancellationToken.ThrowIfCancellationRequested();

            int distinct;
            int nonEmpty;
            using (var stats = conn.CreateCommand())
            {
                // Empty string and NULL are counted as the same absence: the ingest writes "" for a
                // field a line did not carry, so treating them differently would report a
                // distinction the data does not actually make.
                stats.CommandText =
                    $"SELECT COUNT(DISTINCT NULLIF([{column}], '')), " +
                    $"       SUM(CASE WHEN [{column}] IS NULL OR [{column}] = '' THEN 0 ELSE 1 END) " +
                    $"FROM [{table}];";

                using var reader = await stats.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                distinct = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                nonEmpty = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
            }

            var top = new List<ValueCount>();
            using (var frequent = conn.CreateCommand())
            {
                frequent.CommandText =
                    $"SELECT [{column}], COUNT(*) AS n FROM [{table}] " +
                    $"WHERE [{column}] IS NOT NULL AND [{column}] <> '' " +
                    $"GROUP BY [{column}] ORDER BY n DESC, [{column}] ASC LIMIT @top;";
                frequent.Parameters.AddWithValue("@top", topValues);

                using var reader = await frequent.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    top.Add(new ValueCount(
                        reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString() ?? string.Empty,
                        reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1))));
                }
            }

            profiles.Add(new ColumnProfile(column, distinct, nonEmpty, top));
        }

        return (rows, profiles);
    }
}
