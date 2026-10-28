using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Lakeflow.Contracts;
using Microsoft.Data.SqlClient;

namespace Lakeflow.Connectors.SqlServer;

internal interface ICapture
{
    /// <summary>Position to read changes from once a snapshot that starts now has finished.</summary>
    Task<string> StartPositionAsync(SqlConnection connection, TableConfig table, TableSchema schema, CancellationToken ct);

    Task<ChangeBatch> ReadAsync(SqlConnection connection, TableConfig table, TableSchema schema, Checkpoint checkpoint,
        int maxRows, int timeout, CancellationToken ct);
}

/// <summary>
/// SQL Server Change Tracking: net changes per key since a version. Reads only; Change Tracking must already be
/// enabled on the database and table (with the vendor's written OK).
/// </summary>
internal sealed class ChangeTrackingCapture : ICapture
{
    public static readonly ChangeTrackingCapture Instance = new();

    private static async Task<(long Current, long MinValid)> VersionsAsync(SqlConnection connection, TableConfig table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SqlText.ReadOnly(
            "SELECT CHANGE_TRACKING_CURRENT_VERSION(), CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@t))");
        command.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 512) { Value = SqlText.Table(table.Table) });
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.IsDBNull(0))
            throw new CaptureUnavailableException("Change Tracking is not enabled on this database; use polling until the vendor approves enabling it.");
        if (reader.IsDBNull(1))
            throw new CaptureUnavailableException($"Change Tracking is not enabled on {table.Table}, or the login lacks VIEW CHANGE TRACKING on it.");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public async Task<string> StartPositionAsync(SqlConnection connection, TableConfig table, TableSchema schema, CancellationToken ct)
    {
        RequirePrimaryKey(table);
        var (current, _) = await VersionsAsync(connection, table, ct);
        return current.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<ChangeBatch> ReadAsync(SqlConnection connection, TableConfig table, TableSchema schema, Checkpoint checkpoint,
        int maxRows, int timeout, CancellationToken ct)
    {
        RequirePrimaryKey(table);
        var from = long.Parse(checkpoint.Position, CultureInfo.InvariantCulture);
        var (to, minValid) = await VersionsAsync(connection, table, ct);
        if (from < minValid)
            throw new CheckpointExpiredException(
                $"{table.Table}: Change Tracking retention has passed checkpoint version {from} (oldest valid {minValid}); resnapshot.");

        var page = await ReadPageAsync(connection, table, schema, from, to, maxRows, null, timeout, ct);
        if (page.Count < maxRows)
            return Batch(table, page, to, hasMore: false);

        // Full page: end on a whole version so a re-read never skips part of a transaction.
        var lastVersion = page[^1].Version;
        var complete = page.Where(c => c.Version < lastVersion).ToList();
        if (complete.Count > 0)
            return Batch(table, complete, complete[^1].Version, hasMore: true);
        // One version larger than a page: take all of it.
        var whole = await ReadPageAsync(connection, table, schema, from, to, null, lastVersion, timeout, ct);
        return Batch(table, whole, lastVersion, hasMore: lastVersion < to);
    }

    private static ChangeBatch Batch(TableConfig table, List<(long Version, RowChange? Change)> rows, long position, bool hasMore) =>
        new(rows.Where(r => r.Change is not null).Select(r => r.Change!).ToList(),
            new Checkpoint(table.Method, CheckpointPhase.Incremental, position.ToString(CultureInfo.InvariantCulture)),
            hasMore);

    private static async Task<List<(long Version, RowChange? Change)>> ReadPageAsync(SqlConnection connection, TableConfig table,
        TableSchema schema, long from, long to, int? top, long? onlyVersion, int timeout, CancellationToken ct)
    {
        var keys = schema.KeyColumns.Select(SqlText.Quote).ToList();
        var keySchema = schema with { Columns = schema.KeyColumns.Select(k => schema.Find(k)!).ToList() };
        await using var command = connection.CreateCommand();
        command.CommandTimeout = timeout;
        command.Parameters.Add(new SqlParameter("@from", SqlDbType.BigInt) { Value = from });
        command.Parameters.Add(new SqlParameter("@to", SqlDbType.BigInt) { Value = to });
        var topClause = "";
        if (top is { } n)
        {
            command.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = n });
            topClause = "TOP (@n) ";
        }
        var versionFilter = "ct.SYS_CHANGE_VERSION <= @to";
        if (onlyVersion is { } v)
        {
            command.Parameters.Add(new SqlParameter("@v", SqlDbType.BigInt) { Value = v });
            versionFilter = "ct.SYS_CHANGE_VERSION = @v";
        }
        command.CommandText = SqlText.ReadOnly(
            $"SELECT {topClause}ct.SYS_CHANGE_VERSION, ct.SYS_CHANGE_OPERATION, " +
            $"CASE WHEN t.{keys[0]} IS NULL THEN 0 ELSE 1 END, " +
            $"{string.Join(", ", keys.Select(k => "ct." + k))}, {Rows.SelectList(schema, "t")} " +
            $"FROM CHANGETABLE(CHANGES {SqlText.Table(table.Table)}, @from) AS ct " +
            $"LEFT JOIN {SqlText.Table(table.Table)} AS t ON {string.Join(" AND ", keys.Select(k => $"t.{k} = ct.{k}"))} " +
            $"WHERE {versionFilter} ORDER BY ct.SYS_CHANGE_VERSION, {string.Join(", ", keys.Select(k => "ct." + k))}");

        var rows = new List<(long, RowChange?)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var version = reader.GetInt64(0);
            var operation = reader.GetString(1);
            var present = reader.GetInt32(2) == 1;
            RowChange? change;
            if (operation == "D")
            {
                change = new RowChange(ChangeOp.Delete, Rows.Read(reader, keySchema, 3), null, null);
            }
            else if (present)
            {
                var after = Rows.Read(reader, schema, 3 + keys.Count);
                change = new RowChange(operation == "I" ? ChangeOp.Insert : ChangeOp.Update,
                    ColumnValues.KeyOf(after, schema.KeyColumns), null, after);
            }
            else
            {
                change = null; // deleted after @to; the delete arrives in a later read
            }
            rows.Add((version, change));
        }
        return rows;
    }

    private static void RequirePrimaryKey(TableConfig table)
    {
        if (table.KeyColumns is { Count: > 0 })
            throw new CaptureUnavailableException($"{table.Table}: Change Tracking keys rows by the primary key; remove key_columns.");
    }
}

/// <summary>
/// Polls a rowversion column. Reads only rows below MIN_ACTIVE_ROWVERSION(), so rows of transactions still in flight
/// are never passed over; an open transaction anywhere in the database therefore holds capture back until it ends.
/// Deletes are found by key reconciliation.
/// </summary>
internal sealed class RowVersionCapture : ICapture
{
    public static readonly RowVersionCapture Instance = new();

    private static ColumnSchema Column(TableConfig table, TableSchema schema)
    {
        var column = table.WatermarkColumn is { } name
            ? schema.Find(name) ?? throw new CaptureUnavailableException($"{table.Table} has no column {name}.")
            : schema.Columns.FirstOrDefault(c => c.DataType is "timestamp" or "rowversion")
              ?? throw new CaptureUnavailableException($"{table.Table} has no rowversion column; use timestamp polling.");
        if (column.DataType is not ("timestamp" or "rowversion"))
            throw new CaptureUnavailableException($"{table.Table}.{column.Name} is {column.DataType}, not rowversion.");
        return column;
    }

    public async Task<string> StartPositionAsync(SqlConnection connection, TableConfig table, TableSchema schema, CancellationToken ct)
    {
        Column(table, schema);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlText.ReadOnly("SELECT CAST(MIN_ACTIVE_ROWVERSION() AS bigint) - 1");
        var value = (long)(await command.ExecuteScalarAsync(ct))!;
        return value.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<ChangeBatch> ReadAsync(SqlConnection connection, TableConfig table, TableSchema schema, Checkpoint checkpoint,
        int maxRows, int timeout, CancellationToken ct)
    {
        var column = SqlText.Quote(Column(table, schema).Name);
        var from = long.Parse(checkpoint.Position, CultureInfo.InvariantCulture);
        var fromBytes = BitConverter.GetBytes(from);
        if (BitConverter.IsLittleEndian) Array.Reverse(fromBytes);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = timeout;
        command.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = maxRows });
        command.Parameters.Add(new SqlParameter("@from", SqlDbType.Binary, 8) { Value = fromBytes });
        command.CommandText = SqlText.ReadOnly(
            // READUNCOMMITTED: rows below MIN_ACTIVE_ROWVERSION() are committed, and an open transaction's rows carry a
            // rowversion at or above it, so no shared locks are needed and a long writer cannot block the poll.
            $"SELECT TOP (@n) CAST({column} AS bigint), {Rows.SelectList(schema)} FROM {SqlText.Table(table.Table)} WITH (READUNCOMMITTED) " +
            $"WHERE {column} > @from AND {column} < MIN_ACTIVE_ROWVERSION() ORDER BY {column}");

        var changes = new List<RowChange>();
        var position = from;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            position = reader.GetInt64(0);
            var after = Rows.Read(reader, schema, 1);
            changes.Add(new RowChange(ChangeOp.Update, ColumnValues.KeyOf(after, schema.KeyColumns), null, after));
        }
        return new ChangeBatch(changes,
            new Checkpoint(table.Method, CheckpointPhase.Incremental, position.ToString(CultureInfo.InvariantCulture)),
            HasMore: changes.Count == maxRows);
    }
}

/// <summary>
/// Polls a date/time column from a high-water mark minus a look-back, in (timestamp, key) order. Widens the
/// look-back by the clock change when the server falls back from daylight time, caps the high-water mark near the
/// server's clock so one future-dated row cannot hide later ones, and drops look-back repeats it has already sent.
/// Rows with a null timestamp are seen only by snapshots. Deletes are found by key reconciliation.
/// </summary>
internal sealed class TimestampCapture : ICapture
{
    private const int MaxRemembered = 200_000;

    /// <summary>Position, stored as JSON in the checkpoint.</summary>
    private sealed record State
    {
        public JsonNode? Hw { get; init; }
        public int Offset { get; init; }
        public JsonNode? Lower { get; init; }
        public bool InPass { get; init; }
        public JsonNode? CursorTs { get; init; }
        public JsonObject? CursorKey { get; init; }
        public JsonNode? PassMax { get; init; }
    }

    // Key -> (hash of the row last sent, its timestamp), to skip unchanged rows re-read by the look-back.
    // Only trusted while the caller continues from the checkpoint we last returned: a caller that goes back to an
    // older one did not keep what we sent, so it must all be sent again.
    private readonly Dictionary<string, (string Hash, DateTimeOffset Ts)> _sent = new(StringComparer.Ordinal);
    private string? _lastReturned;

    private static ColumnSchema Column(TableConfig table, TableSchema schema)
    {
        var name = table.WatermarkColumn ?? throw new CaptureUnavailableException($"{table.Table}: timestamp polling needs a watermark column.");
        var column = schema.Find(name) ?? throw new CaptureUnavailableException($"{table.Table} has no column {name}.");
        if (column.DataType is not ("datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "date"))
            throw new CaptureUnavailableException($"{table.Table}.{column.Name} is {column.DataType}, not a date/time.");
        return column;
    }

    private static async Task<(DateTime Local, DateTime Utc, int Offset)> ClockAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SqlText.ReadOnly("SELECT SYSDATETIME(), SYSUTCDATETIME(), DATEPART(TZOFFSET, SYSDATETIMEOFFSET())");
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetDateTime(0), reader.GetDateTime(1), reader.GetInt32(2));
    }

    public async Task<string> StartPositionAsync(SqlConnection connection, TableConfig table, TableSchema schema, CancellationToken ct)
    {
        var column = Column(table, schema);
        var clock = await ClockAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlText.ReadOnly($"SELECT MAX({SqlText.Quote(column.Name)}) FROM {SqlText.Table(table.Table)}");
        var max = await command.ExecuteScalarAsync(ct);
        var hw = Cap(column, max is null or DBNull ? null : ColumnValues.Encode(max), clock);
        return EnvelopeJson.Serialize(new State { Hw = hw, Offset = clock.Offset });
    }

    public async Task<ChangeBatch> ReadAsync(SqlConnection connection, TableConfig table, TableSchema schema, Checkpoint checkpoint,
        int maxRows, int timeout, CancellationToken ct)
    {
        var column = Column(table, schema);
        var quoted = SqlText.Quote(column.Name);
        var state = EnvelopeJson.Deserialize<State>(checkpoint.Position);
        if (checkpoint.Position != _lastReturned) _sent.Clear();
        _lastReturned = null;
        var clock = await ClockAsync(connection, ct);

        if (!state.InPass)
        {
            // A clock that fell back (offset shrank) re-uses an hour of timestamps: look back that much further.
            var fallBack = Math.Max(0, state.Offset - clock.Offset);
            var lower = state.Hw is null ? null
                : ColumnValues.Encode(ToClr(column, state.Hw) switch
                {
                    DateTimeOffset dto => dto.AddSeconds(-table.LookbackSeconds).AddMinutes(-fallBack),
                    DateTime dt => dt.AddSeconds(-table.LookbackSeconds).AddMinutes(-fallBack),
                    var other => other,
                });
            if (column.DataType == "date" && lower is not null)
                lower = ColumnValues.Encode(DateOnly.FromDateTime((DateTime)ToClr(column, lower)));
            state = state with { InPass = true, Lower = lower, CursorTs = null, CursorKey = null, PassMax = null };
        }

        var orderColumns = new[] { quoted }.Concat(schema.KeyColumns.Select(SqlText.Quote)).ToList();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = timeout;
        command.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = maxRows });
        var where = new List<string> { $"{quoted} IS NOT NULL" };
        if (state.Lower is not null)
        {
            command.Parameters.Add(Parameters.ForColumn("@lower", column, state.Lower));
            where.Add($"{quoted} >= @lower");
        }
        if (state.CursorTs is not null && state.CursorKey is not null)
        {
            var values = new List<SqlParameter> { Parameters.ForColumn("@c0", column, state.CursorTs) };
            values.AddRange(schema.KeyColumns.Select((k, i) => Parameters.ForColumn($"@c{i + 1}", schema.Find(k)!, state.CursorKey[k])));
            command.Parameters.AddRange(values.ToArray());
            where.Add(SqlText.After(orderColumns, values));
        }
        command.CommandText = SqlText.ReadOnly(
            $"SELECT TOP (@n) {Rows.SelectList(schema)} FROM {SqlText.Table(table.Table)} " +
            $"WHERE {string.Join(" AND ", where)} ORDER BY {string.Join(", ", orderColumns)}");

        var changes = new List<RowChange>();
        var read = 0;
        JsonObject? last = null;
        var passMax = state.PassMax;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                read++;
                var after = Rows.Read(reader, schema, 0);
                last = after;
                var ts = after[column.Name]!;
                if (passMax is null || Compare(column, ts, passMax) > 0) passMax = ts.DeepClone();
                var key = ColumnValues.KeyOf(after, schema.KeyColumns);
                if (Remember(ColumnValues.KeyText(key), after, ToInstant(column, ts)))
                    changes.Add(new RowChange(ChangeOp.Update, key, null, after));
            }
        }

        if (read == maxRows)
        {
            state = state with
            {
                CursorTs = last![column.Name]!.DeepClone(),
                CursorKey = ColumnValues.KeyOf(last, schema.KeyColumns),
                PassMax = passMax?.DeepClone(),
            };
            return Returned(table, state, changes, hasMore: true);
        }

        // Pass finished: move the high-water mark (never backwards, never far beyond the server clock).
        var hw = state.Hw;
        var capped = Cap(column, passMax, clock);
        if (capped is not null && (hw is null || Compare(column, capped, hw) > 0)) hw = capped;
        Forget(state.Lower is null ? null : ToInstant(column, state.Lower));
        state = new State { Hw = hw?.DeepClone(), Offset = clock.Offset };
        return Returned(table, state, changes, hasMore: false);
    }

    private ChangeBatch Returned(TableConfig table, State state, List<RowChange> changes, bool hasMore)
    {
        _lastReturned = EnvelopeJson.Serialize(state);
        return new ChangeBatch(changes, new Checkpoint(table.Method, CheckpointPhase.Incremental, _lastReturned), hasMore);
    }

    /// <summary>True when the row differs from what was last sent for its key.</summary>
    private bool Remember(string key, JsonObject row, DateTimeOffset ts)
    {
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(row.ToJsonString())), 0, 16);
        if (_sent.TryGetValue(key, out var previous) && previous.Hash == hash) return false;
        if (_sent.Count >= MaxRemembered) _sent.Clear();
        _sent[key] = (hash, ts);
        return true;
    }

    private void Forget(DateTimeOffset? before)
    {
        if (before is null) return;
        foreach (var key in _sent.Where(e => e.Value.Ts < before.Value.AddHours(-1)).Select(e => e.Key).ToList()) _sent.Remove(key);
    }

    private static JsonNode? Cap(ColumnSchema column, JsonNode? value, (DateTime Local, DateTime Utc, int Offset) clock)
    {
        if (value is null) return null;
        var limit = (clock.Local > clock.Utc ? clock.Local : clock.Utc).AddHours(1);
        return ToClr(column, value) switch
        {
            DateTimeOffset dto when dto.UtcDateTime > clock.Utc.AddHours(1) => ColumnValues.Encode(new DateTimeOffset(clock.Utc.AddHours(1), TimeSpan.Zero)),
            DateTime dt when dt > limit => ColumnValues.Encode(column.DataType == "date" ? DateOnly.FromDateTime(limit) : limit),
            _ => value.DeepClone(),
        };
    }

    private static object ToClr(ColumnSchema column, JsonNode value)
    {
        var text = value.GetValue<string>();
        return column.DataType switch
        {
            "datetimeoffset" => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            "date" => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue),
            _ => DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Unspecified),
        };
    }

    private static DateTimeOffset ToInstant(ColumnSchema column, JsonNode value) => ToClr(column, value) switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        _ => DateTimeOffset.MinValue,
    };

    private static int Compare(ColumnSchema column, JsonNode a, JsonNode b) => ToInstant(column, a).CompareTo(ToInstant(column, b));
}
