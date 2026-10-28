using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Lakeflow.Contracts;
using Microsoft.Data.SqlClient;

namespace Lakeflow.Connectors.SqlServer;

/// <summary>Column lists and value reading, so every capture method lands values the same way.</summary>
internal static class Rows
{
    private static bool IsClrType(ColumnSchema column) => column.DataType is "geography" or "geometry" or "hierarchyid";

    /// <summary>Selects every column of the schema, CLR types as text, optionally qualified by a table alias.</summary>
    public static string SelectList(TableSchema schema, string? alias = null)
    {
        var prefix = alias is null ? "" : alias + ".";
        return string.Join(", ", schema.Columns.Select(c =>
            IsClrType(c) ? $"{prefix}{SqlText.Quote(c.Name)}.ToString() AS {SqlText.Quote(c.Name)}" : prefix + SqlText.Quote(c.Name)));
    }

    /// <summary>Reads the schema's columns, starting at <paramref name="offset"/>, into an envelope row.</summary>
    public static JsonObject Read(SqlDataReader reader, TableSchema schema, int offset)
    {
        var row = new JsonObject();
        for (var i = 0; i < schema.Columns.Count; i++)
        {
            var column = schema.Columns[i];
            var ordinal = offset + i;
            row[column.Name] = reader.IsDBNull(ordinal) ? null : ColumnValues.Encode(column.DataType switch
            {
                "date" => DateOnly.FromDateTime(reader.GetDateTime(ordinal)),
                "time" => TimeOnly.FromTimeSpan(reader.GetTimeSpan(ordinal)),
                _ => reader.GetValue(ordinal),
            });
        }
        return row;
    }
}

/// <summary>Typed parameters from envelope values, matching the column type so predicates stay sargable.</summary>
internal static class Parameters
{
    public static SqlParameter ForColumn(string name, ColumnSchema column, JsonNode? value)
    {
        var parameter = new SqlParameter { ParameterName = name };
        switch (column.DataType)
        {
            case "bigint": parameter.SqlDbType = SqlDbType.BigInt; break;
            case "int": parameter.SqlDbType = SqlDbType.Int; break;
            case "smallint": parameter.SqlDbType = SqlDbType.SmallInt; break;
            case "tinyint": parameter.SqlDbType = SqlDbType.TinyInt; break;
            case "bit": parameter.SqlDbType = SqlDbType.Bit; break;
            case "decimal" or "numeric":
                parameter.SqlDbType = SqlDbType.Decimal;
                parameter.Precision = (byte)(column.Precision ?? 38);
                parameter.Scale = (byte)(column.Scale ?? 0);
                break;
            case "money" or "smallmoney": parameter.SqlDbType = SqlDbType.Money; break;
            case "float": parameter.SqlDbType = SqlDbType.Float; break;
            case "real": parameter.SqlDbType = SqlDbType.Real; break;
            case "varchar": parameter.SqlDbType = SqlDbType.VarChar; parameter.Size = column.MaxLength ?? -1; break;
            case "char": parameter.SqlDbType = SqlDbType.Char; parameter.Size = column.MaxLength ?? 1; break;
            case "nvarchar" or "sysname": parameter.SqlDbType = SqlDbType.NVarChar; parameter.Size = column.MaxLength ?? -1; break;
            case "nchar": parameter.SqlDbType = SqlDbType.NChar; parameter.Size = column.MaxLength ?? 1; break;
            case "uniqueidentifier": parameter.SqlDbType = SqlDbType.UniqueIdentifier; break;
            case "datetime": parameter.SqlDbType = SqlDbType.DateTime; break;
            case "smalldatetime": parameter.SqlDbType = SqlDbType.SmallDateTime; break;
            case "datetime2": parameter.SqlDbType = SqlDbType.DateTime2; parameter.Scale = 7; break;
            case "date": parameter.SqlDbType = SqlDbType.Date; break;
            case "time": parameter.SqlDbType = SqlDbType.Time; break;
            case "datetimeoffset": parameter.SqlDbType = SqlDbType.DateTimeOffset; break;
            case "binary" or "varbinary" or "timestamp" or "rowversion":
                parameter.SqlDbType = column.DataType == "varbinary" ? SqlDbType.VarBinary : SqlDbType.Binary;
                parameter.Size = column.MaxLength ?? 8;
                break;
            default: throw new CaptureUnavailableException($"Column {column.Name} ({column.DataType}) cannot be used as a key or watermark.");
        }
        parameter.Value = value is null ? DBNull.Value : Decode(column, value);
        return parameter;
    }

    private static object Decode(ColumnSchema column, JsonNode value)
    {
        var text = value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;
        return column.DataType switch
        {
            "bigint" => value.GetValue<long>(),
            "int" => value.GetValue<int>(),
            "smallint" => value.GetValue<short>(),
            "tinyint" => value.GetValue<byte>(),
            "bit" => value.GetValue<bool>(),
            "decimal" or "numeric" or "money" or "smallmoney" => value.GetValue<decimal>(),
            "float" or "real" => value.GetValue<double>(),
            "uniqueidentifier" => Guid.Parse(text!),
            "datetime" or "smalldatetime" or "datetime2" =>
                DateTime.Parse(text!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "date" => DateOnly.ParseExact(text!, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue),
            "time" => TimeOnly.Parse(text!, CultureInfo.InvariantCulture).ToTimeSpan(),
            "datetimeoffset" => DateTimeOffset.Parse(text!, CultureInfo.InvariantCulture),
            "binary" or "varbinary" or "timestamp" or "rowversion" => Convert.FromBase64String(text!),
            _ => text ?? value.ToString(),
        };
    }
}
