using System.Buffers;
using System.Text.Json;

namespace Lakeflow.Snowflake;

/// <summary>One event from a hub partition: its position, when the hub took it, and the envelope it carries.</summary>
public sealed record HubEvent(long SequenceNumber, DateTimeOffset EnqueuedTime, ReadOnlyMemory<byte> Body);

/// <summary>
/// A change envelope as a row of <c>BRONZE.EVENTS</c>, in the NDJSON Snowpipe Streaming takes (keys match the column
/// names). Values stay exact: the key, before and after images are kept as their original JSON text, and
/// <c>AFTER_VALUES</c> holds the after image with every value as text, numbers exactly as written (so silver never
/// sees a number through a floating-point type). An event that is not a valid envelope still lands, with
/// <c>PARSE_ERROR</c> and its raw body, so nothing disappears silently.
/// </summary>
public static class BronzeRows
{
    public static readonly string[] Columns =
    [
        "DIVISION", "PARTITION_ID", "EH_SEQUENCE", "ENQUEUED_AT", "ENVELOPE_VERSION", "SITE_ID", "SOURCE_ID", "SOURCE_SYSTEM",
        "TABLE_NAME", "OP", "KEY_ID", "SEQ", "SOURCE_TS", "CAPTURE_TS", "KEY_JSON", "BEFORE_JSON", "AFTER_JSON", "AFTER_VALUES",
        "PARSE_ERROR", "RAW_BODY",
    ];

    public static byte[] Row(string division, string partitionId, HubEvent e)
    {
        var buffer = new ArrayBufferWriter<byte>(e.Body.Length * 2 + 256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("DIVISION", division);
            w.WriteString("PARTITION_ID", partitionId);
            w.WriteNumber("EH_SEQUENCE", e.SequenceNumber);
            w.WriteString("ENQUEUED_AT", e.EnqueuedTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"));
            try
            {
                using var envelope = JsonDocument.Parse(e.Body);
                var root = envelope.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("not a JSON object");
                Raw(w, "ENVELOPE_VERSION", root, "envelope_version");
                Text(w, "SITE_ID", root, "site_id");
                Text(w, "SOURCE_ID", root, "source_id");
                Text(w, "SOURCE_SYSTEM", root, "source_system");
                Text(w, "TABLE_NAME", root, "table");
                Text(w, "OP", root, "op");
                Text(w, "KEY_ID", root, "key_id");
                Raw(w, "SEQ", root, "seq");
                Text(w, "SOURCE_TS", root, "source_ts");
                Text(w, "CAPTURE_TS", root, "capture_ts");
                Json(w, "KEY_JSON", root, "key");
                Json(w, "BEFORE_JSON", root, "before");
                Json(w, "AFTER_JSON", root, "after");
                w.WritePropertyName("AFTER_VALUES");
                if (root.TryGetProperty("after", out var after) && after.ValueKind == JsonValueKind.Object)
                {
                    w.WriteStartObject();
                    foreach (var column in after.EnumerateObject())
                    {
                        if (ValueText(column.Value) is { } text) w.WriteString(column.Name, text);
                        else w.WriteNull(column.Name);
                    }
                    w.WriteEndObject();
                }
                else w.WriteNullValue();
            }
            catch (JsonException ex)
            {
                w.WriteString("PARSE_ERROR", ex.Message);
                w.WriteString("RAW_BODY", System.Text.Encoding.UTF8.GetString(e.Body.Span));
            }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A column value as text, exactly as the collector wrote it (numbers keep every digit and their form).</summary>
    internal static string? ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(), // numbers as written; nested objects and arrays as their JSON
    };

    private static void Text(Utf8JsonWriter w, string column, JsonElement root, string field)
    {
        if (root.TryGetProperty(field, out var v) && ValueText(v) is { } text) w.WriteString(column, text);
    }

    private static void Raw(Utf8JsonWriter w, string column, JsonElement root, string field)
    {
        if (root.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number)
        {
            w.WritePropertyName(column);
            w.WriteRawValue(v.GetRawText());
        }
    }

    private static void Json(Utf8JsonWriter w, string column, JsonElement root, string field)
    {
        if (root.TryGetProperty(field, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array) w.WriteString(column, v.GetRawText());
    }
}

/// <summary>Rows joined into NDJSON requests below Snowpipe Streaming's 4 MB limit.</summary>
public static class Ndjson
{
    public const int MaxRequestBytes = 3_500_000;

    public sealed record Chunk(byte[] Bytes, long FirstSequence, long LastSequence, int Rows);

    public static IEnumerable<Chunk> Chunks(IReadOnlyList<(long Sequence, byte[] Row)> rows, int maxBytes = MaxRequestBytes)
    {
        var start = 0;
        while (start < rows.Count)
        {
            var size = 0;
            var end = start;
            while (end < rows.Count && (end == start || size + rows[end].Row.Length + 1 <= maxBytes))
            {
                size += rows[end].Row.Length + 1;
                end++;
            }
            var bytes = new byte[size];
            var offset = 0;
            for (var i = start; i < end; i++)
            {
                rows[i].Row.CopyTo(bytes, offset);
                offset += rows[i].Row.Length;
                bytes[offset++] = (byte)'\n';
            }
            yield return new Chunk(bytes, rows[start].Sequence, rows[end - 1].Sequence, end - start);
            start = end;
        }
    }
}
