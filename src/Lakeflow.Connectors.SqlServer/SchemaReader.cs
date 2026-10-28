using System.Data;
using Lakeflow.Contracts;
using Microsoft.Data.SqlClient;

namespace Lakeflow.Connectors.SqlServer;

internal static class SchemaReader
{
    private const string ColumnsSql =
        "SELECT TOP (4096) c.name, CASE WHEN ty.is_assembly_type = 1 THEN ty.name ELSE TYPE_NAME(c.system_type_id) END, " +
        "c.is_nullable, c.column_id, c.max_length, c.precision, c.scale " +
        "FROM sys.columns AS c JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id " +
        "WHERE c.object_id = OBJECT_ID(@t) ORDER BY c.column_id";

    private const string PrimaryKeySql =
        "SELECT TOP (64) c.name FROM sys.indexes AS i " +
        "JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
        "JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
        "WHERE i.object_id = OBJECT_ID(@t) AND i.is_primary_key = 1 AND ic.key_ordinal > 0 ORDER BY ic.key_ordinal";

    /// <returns>The schema; its key is the primary key, empty for a table without one.</returns>
    public static async Task<TableSchema> ReadAsync(SqlConnection connection, TableRef table, int timeout, CancellationToken ct)
    {
        var columns = new List<ColumnSchema>();
        await using (var command = Command(connection, ColumnsSql, table, timeout))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(1).ToLowerInvariant();
                int length = reader.GetInt16(4);
                columns.Add(new ColumnSchema(
                    reader.GetString(0),
                    type,
                    reader.GetBoolean(2),
                    reader.GetInt32(3),
                    MaxLength: type switch
                    {
                        "nvarchar" or "nchar" => length == -1 ? -1 : length / 2,
                        "varchar" or "char" or "varbinary" or "binary" => length,
                        _ => null,
                    },
                    Precision: type is "decimal" or "numeric" ? reader.GetByte(5) : null,
                    Scale: type is "decimal" or "numeric" ? reader.GetByte(6) : null));
            }
        }
        if (columns.Count == 0)
            throw new CaptureUnavailableException($"Table {table} was not found, or the login has no SELECT permission on it.");

        var key = new List<string>();
        await using (var command = Command(connection, PrimaryKeySql, table, timeout))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) key.Add(reader.GetString(0));
        }
        return new TableSchema(table, columns, key);
    }

    private static SqlCommand Command(SqlConnection connection, string sql, TableRef table, int timeout)
    {
        var command = connection.CreateCommand();
        command.CommandText = SqlText.ReadOnly(sql);
        command.CommandTimeout = timeout;
        command.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 512) { Value = SqlText.Table(table) });
        return command;
    }
}
