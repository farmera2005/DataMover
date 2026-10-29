using System.Diagnostics;
using Lakeflow.Contracts;
using Microsoft.Data.SqlClient;

namespace Lakeflow.Connectors.SqlServer;

/// <summary>
/// Catalog queries for the setup wizard: what the read-only login can see. Every query is a bounded SELECT on catalog
/// views; row counts come from sys.partitions, never from counting the table.
/// </summary>
internal static class SchemaBrowser
{
    private const string ServerSql =
        "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) + ' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), " +
        "DB_NAME(), SUSER_SNAME(), CASE WHEN CHANGE_TRACKING_CURRENT_VERSION() IS NULL THEN 0 ELSE 1 END";

    // Tables the login can see (it has a permission on them), largest first.
    private const string TablesSql = """
        SELECT TOP (@n) s.name, t.name, t.object_id,
               (SELECT SUM(p.rows) FROM sys.partitions AS p WHERE p.object_id = t.object_id AND p.index_id IN (0, 1)),
               CASE WHEN ctt.object_id IS NULL THEN 0 ELSE 1 END,
               (SELECT TOP (1) c.name FROM sys.columns AS c WHERE c.object_id = t.object_id AND c.system_type_id = 189)
        FROM sys.tables AS t
        JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN sys.change_tracking_tables AS ctt ON ctt.object_id = t.object_id
        WHERE t.is_ms_shipped = 0
        ORDER BY 4 DESC, s.name, t.name
        """;

    private const string KeysSql = """
        SELECT TOP (20000) ic.object_id, c.name
        FROM sys.indexes AS i
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.tables AS t ON t.object_id = i.object_id
        WHERE i.is_primary_key = 1 AND ic.key_ordinal > 0 AND t.is_ms_shipped = 0
        ORDER BY ic.object_id, ic.key_ordinal
        """;

    // Date/time columns that lead an index: timestamp polling on them seeks instead of scanning.
    private const string WatermarksSql = """
        SELECT TOP (20000) ic.object_id, c.name
        FROM sys.index_columns AS ic
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.tables AS t ON t.object_id = ic.object_id
        WHERE ic.key_ordinal = 1 AND c.system_type_id IN (40, 42, 43, 58, 61) AND t.is_ms_shipped = 0
        ORDER BY ic.object_id, c.column_id
        """;

    public static async Task<ConnectionTestResult> TestAsync(Func<Task<SqlConnection>> open, int timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var connection = await open();
            await using var command = connection.CreateCommand();
            command.CommandText = SqlText.ReadOnly(ServerSql);
            command.CommandTimeout = timeout;
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new ConnectionTestResult
            {
                Succeeded = true,
                ServerVersion = reader.IsDBNull(0) ? null : reader.GetString(0),
                Database = reader.IsDBNull(1) ? null : reader.GetString(1),
                Login = reader.IsDBNull(2) ? null : reader.GetString(2),
                ChangeTrackingEnabled = reader.GetInt32(3) == 1,
                ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
            };
        }
        catch (Exception ex) when (ex is SqlException or CaptureUnavailableException or InvalidOperationException)
        {
            return new ConnectionTestResult { Succeeded = false, Error = Describe(ex), ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds };
        }
    }

    /// <summary>The usual causes, in words an IT person can act on.</summary>
    internal static string Describe(Exception ex) => ex switch
    {
        SqlException { Number: 18456 } => "Login failed: check the user name and password, and that the login exists on this server.",
        SqlException { Number: 4060 } => "The login cannot open that database: check the database name and that the login has a user in it.",
        SqlException { Number: -2 } => "The server did not answer in time (timeout).",
        SqlException sql when sql.Number is 53 or 2 or 26 or 40 or 10061 or 11001
            || sql.Message.Contains("network-related", StringComparison.OrdinalIgnoreCase) =>
            "The server could not be reached: check the host, port or instance name, and that the scale PC can reach it.",
        SqlException { Number: 0 } sql when sql.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) =>
            "The server's TLS certificate is not trusted: tick 'trust server certificate' for this source if the vendor uses a self-signed one.",
        _ => ex.Message,
    };

    public static async Task<SchemaBrowseResult> BrowseAsync(SqlConnection connection, int maxTables, int timeout, CancellationToken ct)
    {
        var tables = new List<(int ObjectId, string Name, long Rows, bool ChangeTracking, string? RowVersion)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SqlText.ReadOnly(TablesSql);
            command.CommandTimeout = timeout;
            command.Parameters.Add(new SqlParameter("@n", System.Data.SqlDbType.Int) { Value = maxTables + 1 });
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                tables.Add((reader.GetInt32(2), $"{reader.GetString(0)}.{reader.GetString(1)}",
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3), reader.GetInt32(4) == 1, reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        var truncated = tables.Count > maxTables;
        if (truncated) tables.RemoveAt(tables.Count - 1);
        var keys = await ColumnsByObjectAsync(connection, KeysSql, timeout, ct);
        var watermarks = await ColumnsByObjectAsync(connection, WatermarksSql, timeout, ct);

        return new SchemaBrowseResult(tables.Select(t =>
        {
            var key = keys.GetValueOrDefault(t.ObjectId) ?? [];
            var marks = watermarks.GetValueOrDefault(t.ObjectId) ?? [];
            var (method, watermark, note) = SourceTableInfo.Suggest(key.Count > 0, t.ChangeTracking, t.RowVersion, marks);
            return new SourceTableInfo
            {
                Table = t.Name, RowCount = t.Rows, KeyColumns = key, ChangeTrackingEnabled = t.ChangeTracking, RowVersionColumn = t.RowVersion,
                WatermarkCandidates = marks, SuggestedMethod = method, SuggestedWatermark = method == CaptureMethod.ChangeTracking ? null : watermark,
                Note = note,
            };
        }).ToList(), truncated);
    }

    private static async Task<Dictionary<int, List<string>>> ColumnsByObjectAsync(SqlConnection connection, string sql, int timeout, CancellationToken ct)
    {
        var result = new Dictionary<int, List<string>>();
        await using var command = connection.CreateCommand();
        command.CommandText = SqlText.ReadOnly(sql);
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt32(0);
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            list.Add(reader.GetString(1));
        }
        return result;
    }
}
