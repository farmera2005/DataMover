using System.Globalization;
using System.Text.Json.Nodes;

namespace Lakeflow.Contracts;

/// <summary>
/// The one encoding of source values into envelope JSON, shared by every connector so the same value lands
/// the same way whatever engine it came from:
/// numbers stay JSON numbers (decimals exact), date/times are ISO 8601 strings (no offset added when the source
/// has none), binary is base64, GUIDs are lower-case strings.
/// </summary>
public static class ColumnValues
{
    public static JsonNode? Encode(object? value) => value switch
    {
        null or DBNull => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        byte n => JsonValue.Create(n),
        sbyte n => JsonValue.Create(n),
        short n => JsonValue.Create(n),
        ushort n => JsonValue.Create(n),
        int n => JsonValue.Create(n),
        uint n => JsonValue.Create(n),
        long n => JsonValue.Create(n),
        ulong n => JsonValue.Create(n),
        decimal d => JsonValue.Create(d),
        double d => double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)),
        float f => float.IsFinite(f) ? JsonValue.Create((double)f) : JsonValue.Create(f.ToString(CultureInfo.InvariantCulture)),
        char c => JsonValue.Create(c.ToString()),
        DateTime dt => JsonValue.Create(dt.ToString(dt.Kind == DateTimeKind.Unspecified ? "yyyy-MM-ddTHH:mm:ss.fffffff" : "O", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeOnly t => JsonValue.Create(t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
        TimeSpan ts => JsonValue.Create(ts.ToString("c", CultureInfo.InvariantCulture)),
        Guid g => JsonValue.Create(g.ToString("D")),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        _ => throw new NotSupportedException($"No envelope encoding for {value.GetType().FullName}; the connector must convert it."),
    };

    /// <summary>Builds a row object from column names and values, in column order.</summary>
    public static JsonObject Row(IEnumerable<KeyValuePair<string, object?>> columns)
    {
        var row = new JsonObject();
        foreach (var (name, value) in columns) row[name] = Encode(value);
        return row;
    }

    /// <summary>Picks the key columns out of a row.</summary>
    public static JsonObject KeyOf(JsonObject row, IReadOnlyList<string> keyColumns)
    {
        var key = new JsonObject();
        foreach (var column in keyColumns)
        {
            if (!row.TryGetPropertyValue(column, out var value))
                throw new ArgumentException($"Row has no key column '{column}'.", nameof(row));
            key[column] = value?.DeepClone();
        }
        return key;
    }

    /// <summary>Stable text form of a key, for dictionaries and reconciliation.</summary>
    public static string KeyText(JsonObject key) => key.ToJsonString();
}
