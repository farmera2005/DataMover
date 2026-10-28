using System.Security.Cryptography;
using System.Text;
using Lakeflow.Contracts;
using Microsoft.Data.Sqlite;

namespace Lakeflow.Agent.Storage;

public enum TableMode { Active, Paused, Blocked }

/// <summary>What the agent remembers about one captured table.</summary>
public sealed record TableRecord(
    string SourceId,
    string Table,
    Checkpoint? Checkpoint,
    long LastSeq,
    TableSchema? Schema,
    TableMode Mode,
    string? BlockedReason,
    DateTimeOffset? LastCaughtUp,
    DateTimeOffset? LastCapture,
    DateOnly? LastReconciled,
    long? SnapshotStartSeq);

/// <summary>A buffered envelope waiting for Event Hubs to acknowledge it.</summary>
public sealed record OutboxEvent(long Id, string SourceId, string Table, string PartitionKey, long Seq, DateTimeOffset CaptureTs, byte[] Payload);

public sealed record BufferStats(long Bytes, long Count, DateTimeOffset? Oldest);

/// <summary>
/// The agent's local state in one SQLCipher-encrypted SQLite database (WAL, synchronous=FULL): the outbox buffer,
/// per-table checkpoints and sequence numbers, the key index used for delete reconciliation, pending agent events,
/// source credentials and the cached config. Change rows and the checkpoint that follows them are written in one
/// transaction, so a crash can repeat changes but never lose them. Rows leave the outbox only when acknowledged.
/// </summary>
public sealed class LocalStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _db;
    private readonly ISecretProtector _protector;
    private readonly object _lock = new();

    static LocalStore() => SQLitePCL.Batteries_V2.Init();

    private LocalStore(SqliteConnection db, ISecretProtector protector) => (_db, _protector) = (db, protector);

    public string Path => _db.DataSource;

    /// <summary>
    /// Opens (or creates) the store in <paramref name="directory"/>. The database key is random, kept next to the
    /// database encrypted by <paramref name="protector"/> (DPAPI), so the files are useless on another machine.
    /// </summary>
    public static LocalStore Open(string directory, ISecretProtector protector)
    {
        Directory.CreateDirectory(directory);
        var keyFile = System.IO.Path.Combine(directory, "buffer.key");
        var dbFile = System.IO.Path.Combine(directory, "buffer.db");
        byte[] key;
        if (File.Exists(keyFile))
        {
            key = protector.Unprotect(File.ReadAllBytes(keyFile));
        }
        else
        {
            if (File.Exists(dbFile)) throw new InvalidOperationException($"{dbFile} exists but its key file is missing; it cannot be read.");
            key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyFile, protector.Protect(key));
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = Convert.ToHexString(key),
            Pooling = false,
        };
        var db = new SqliteConnection(builder.ToString());
        db.Open();
        var store = new LocalStore(db, protector);
        store.Migrate();
        return store;
    }

    private void Migrate()
    {
        Exec("PRAGMA auto_vacuum = INCREMENTAL");
        Exec("PRAGMA journal_mode = WAL");
        Exec("PRAGMA synchronous = FULL");
        Exec("PRAGMA foreign_keys = ON");
        Exec("""
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id TEXT NOT NULL, table_name TEXT NOT NULL, partition_key TEXT NOT NULL,
                seq INTEGER NOT NULL, capture_ts INTEGER NOT NULL, payload BLOB NOT NULL, bytes INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS outbox_table ON outbox (source_id, table_name, id);
            CREATE TABLE IF NOT EXISTS dead_letter (
                id INTEGER PRIMARY KEY, source_id TEXT NOT NULL, table_name TEXT NOT NULL, seq INTEGER NOT NULL,
                payload BLOB NOT NULL, reason TEXT NOT NULL, failed_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS stats (id INTEGER PRIMARY KEY CHECK (id = 1), bytes INTEGER NOT NULL, count INTEGER NOT NULL);
            INSERT OR IGNORE INTO stats VALUES (1, 0, 0);
            CREATE TABLE IF NOT EXISTS tables (
                source_id TEXT NOT NULL, table_name TEXT NOT NULL,
                checkpoint TEXT, last_seq INTEGER NOT NULL DEFAULT 0, schema TEXT,
                mode TEXT NOT NULL DEFAULT 'active', blocked_reason TEXT,
                last_caught_up INTEGER, last_capture INTEGER, last_reconciled TEXT, snapshot_start_seq INTEGER,
                PRIMARY KEY (source_id, table_name));
            CREATE TABLE IF NOT EXISTS row_keys (
                source_id TEXT NOT NULL, table_name TEXT NOT NULL, key TEXT NOT NULL,
                PRIMARY KEY (source_id, table_name, key)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS agent_events (id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS credentials (name TEXT PRIMARY KEY, value BLOB NOT NULL);
            """);
        var version = Scalar<long?>("SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'schema_version'");
        if (version is > SchemaVersion) throw new InvalidOperationException($"Store schema {version} is newer than this agent ({SchemaVersion}).");
        Exec("INSERT OR REPLACE INTO meta VALUES ('schema_version', $v)", ("$v", SchemaVersion));
    }

    // ---- tables and checkpoints ----

    public TableRecord GetTable(string sourceId, string table)
    {
        lock (_lock)
        {
            using var command = Command(
                "SELECT checkpoint, last_seq, schema, mode, blocked_reason, last_caught_up, last_capture, last_reconciled, snapshot_start_seq " +
                "FROM tables WHERE source_id = $s AND table_name = $t", ("$s", sourceId), ("$t", table));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return new TableRecord(sourceId, table, null, 0, null, TableMode.Active, null, null, null, null, null);
            return new TableRecord(
                sourceId, table,
                reader.IsDBNull(0) ? null : EnvelopeJson.Deserialize<Checkpoint>(reader.GetString(0)),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : EnvelopeJson.Deserialize<TableSchema>(reader.GetString(2)),
                Enum.Parse<TableMode>(reader.GetString(3), ignoreCase: true),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : DateOnly.Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetInt64(8));
        }
    }

    private void EnsureTable(string sourceId, string table) =>
        Exec("INSERT OR IGNORE INTO tables (source_id, table_name) VALUES ($s, $t)", ("$s", sourceId), ("$t", table));

    /// <summary>Records the schema capture runs against; null forgets it, so the next read accepts the current one.</summary>
    public void SetSchema(string sourceId, string table, TableSchema? schema)
    {
        lock (_lock)
        {
            EnsureTable(sourceId, table);
            Exec("UPDATE tables SET schema = $schema WHERE source_id = $s AND table_name = $t",
                ("$schema", schema is null ? null : EnvelopeJson.Serialize(schema)), ("$s", sourceId), ("$t", table));
        }
    }

    public void SetMode(string sourceId, string table, TableMode mode, string? reason = null)
    {
        lock (_lock)
        {
            EnsureTable(sourceId, table);
            Exec("UPDATE tables SET mode = $m, blocked_reason = $r WHERE source_id = $s AND table_name = $t",
                ("$m", mode.ToString().ToLowerInvariant()), ("$r", reason), ("$s", sourceId), ("$t", table));
        }
    }

    /// <summary>Forgets the checkpoint so the table is snapshotted again; the sequence carries on.</summary>
    public void ResetCheckpoint(string sourceId, string table)
    {
        lock (_lock)
        {
            EnsureTable(sourceId, table);
            Exec("UPDATE tables SET checkpoint = NULL WHERE source_id = $s AND table_name = $t", ("$s", sourceId), ("$t", table));
        }
    }

    public void SetReconciled(string sourceId, string table, DateOnly day)
    {
        lock (_lock)
        {
            EnsureTable(sourceId, table);
            Exec("UPDATE tables SET last_reconciled = $d WHERE source_id = $s AND table_name = $t",
                ("$d", day.ToString("yyyy-MM-dd")), ("$s", sourceId), ("$t", table));
        }
    }

    /// <summary>Notes the first seq of a snapshot that is starting, for its <see cref="ChangeOp.SnapshotComplete"/> marker.</summary>
    public long StartSnapshot(string sourceId, string table)
    {
        lock (_lock)
        {
            EnsureTable(sourceId, table);
            Exec("UPDATE tables SET snapshot_start_seq = last_seq + 1 WHERE source_id = $s AND table_name = $t", ("$s", sourceId), ("$t", table));
            return Scalar<long>("SELECT snapshot_start_seq FROM tables WHERE source_id = $s AND table_name = $t", ("$s", sourceId), ("$t", table));
        }
    }

    /// <summary>
    /// Buffers changes and moves the table's checkpoint in one transaction. Assigns each change the next sequence
    /// number. With <paramref name="trackKeys"/> the key index follows the changes (for delete reconciliation).
    /// </summary>
    /// <param name="next">New checkpoint, or null to leave it (reconciliation deletes).</param>
    /// <param name="caughtUp">The read reached the end of the source's changes.</param>
    /// <returns>Bytes added to the buffer.</returns>
    public long Append(string siteId, string sourceId, string table, IReadOnlyList<RowChange> changes, Checkpoint? next,
        bool caughtUp, bool trackKeys, DateTimeOffset now)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            EnsureTable(sourceId, table);
            var seq = Scalar<long>("SELECT last_seq FROM tables WHERE source_id = $s AND table_name = $t", ("$s", sourceId), ("$t", table));
            long bytes = 0;
            using (var insert = Command(
                "INSERT INTO outbox (source_id, table_name, partition_key, seq, capture_ts, payload, bytes) VALUES ($s, $t, $p, $seq, $ts, $payload, $bytes)"))
            using (var upsertKey = Command("INSERT OR IGNORE INTO row_keys VALUES ($s, $t, $k)"))
            using (var deleteKey = Command("DELETE FROM row_keys WHERE source_id = $s AND table_name = $t AND key = $k"))
            {
                var partitionKey = LakeNames.PartitionKey(siteId, table);
                foreach (var change in changes)
                {
                    var envelope = change.ToEnvelope(siteId, sourceId, table, ++seq, now);
                    var payload = EnvelopeJson.SerializeToUtf8(envelope);
                    bytes += payload.Length;
                    Run(insert, ("$s", sourceId), ("$t", table), ("$p", partitionKey), ("$seq", seq),
                        ("$ts", now.ToUnixTimeMilliseconds()), ("$payload", payload), ("$bytes", payload.Length));
                    if (trackKeys && change.Op != ChangeOp.SnapshotComplete)
                        Run(change.Op == ChangeOp.Delete ? deleteKey : upsertKey, ("$s", sourceId), ("$t", table), ("$k", ColumnValues.KeyText(change.Key)));
                }
            }
            Exec("UPDATE stats SET bytes = bytes + $b, count = count + $c", ("$b", bytes), ("$c", changes.Count));
            Exec("UPDATE tables SET last_seq = $seq, " +
                 "checkpoint = COALESCE($cp, checkpoint), " +
                 "last_caught_up = CASE WHEN $caught THEN $now ELSE last_caught_up END, " +
                 "last_capture = CASE WHEN $n > 0 THEN $now ELSE last_capture END " +
                 "WHERE source_id = $s AND table_name = $t",
                ("$seq", seq), ("$cp", next is null ? null : EnvelopeJson.Serialize(next)), ("$caught", caughtUp),
                ("$now", now.ToUnixTimeMilliseconds()), ("$n", changes.Count), ("$s", sourceId), ("$t", table));
            tx.Commit();
            return bytes;
        }
    }

    // ---- outbox ----

    /// <summary>Oldest buffered events, at most <paramref name="maxEvents"/> and (after the first) <paramref name="maxBytes"/>.</summary>
    public IReadOnlyList<OutboxEvent> Peek(int maxEvents, long maxBytes)
    {
        lock (_lock)
        {
            using var command = Command(
                "SELECT id, source_id, table_name, partition_key, seq, capture_ts, payload, bytes FROM outbox ORDER BY id LIMIT $n",
                ("$n", maxEvents));
            using var reader = command.ExecuteReader();
            var events = new List<OutboxEvent>();
            long total = 0;
            while (reader.Read())
            {
                total += reader.GetInt64(7);
                if (events.Count > 0 && total > maxBytes) break;
                events.Add(new OutboxEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)), (byte[])reader[6]));
            }
            return events;
        }
    }

    /// <summary>Removes events Event Hubs has acknowledged.</summary>
    public void Acknowledge(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            RemoveFromOutbox(ids);
            tx.Commit();
        }
    }

    /// <summary>Moves an event Event Hubs will never accept (e.g. larger than 1 MB) aside, so it cannot block the rest.</summary>
    public void DeadLetter(OutboxEvent outboxEvent, string reason, DateTimeOffset now)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            Exec("INSERT OR REPLACE INTO dead_letter VALUES ($id, $s, $t, $seq, $p, $r, $now)",
                ("$id", outboxEvent.Id), ("$s", outboxEvent.SourceId), ("$t", outboxEvent.Table), ("$seq", outboxEvent.Seq),
                ("$p", outboxEvent.Payload), ("$r", reason), ("$now", now.ToUnixTimeMilliseconds()));
            RemoveFromOutbox([outboxEvent.Id]);
            tx.Commit();
        }
    }

    private void RemoveFromOutbox(IReadOnlyCollection<long> ids)
    {
        using var size = Command("SELECT bytes FROM outbox WHERE id = $id");
        using var delete = Command("DELETE FROM outbox WHERE id = $id");
        long bytes = 0, count = 0;
        foreach (var id in ids)
        {
            size.Parameters.Clear();
            size.Parameters.AddWithValue("$id", id);
            if (size.ExecuteScalar() is not long b) continue;
            Run(delete, ("$id", id));
            bytes += b;
            count++;
        }
        Exec("UPDATE stats SET bytes = bytes - $b, count = count - $c", ("$b", bytes), ("$c", count));
    }

    public BufferStats GetBufferStats()
    {
        lock (_lock)
        {
            using var command = Command("SELECT bytes, count, (SELECT capture_ts FROM outbox ORDER BY id LIMIT 1) FROM stats");
            using var reader = command.ExecuteReader();
            reader.Read();
            return new BufferStats(reader.GetInt64(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
        }
    }

    /// <summary>Capture time of the table's oldest unacknowledged change.</summary>
    public DateTimeOffset? OldestUnsent(string sourceId, string table)
    {
        lock (_lock)
        {
            var ms = Scalar<long?>("SELECT capture_ts FROM outbox WHERE source_id = $s AND table_name = $t ORDER BY id LIMIT 1",
                ("$s", sourceId), ("$t", table));
            return ms is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);
        }
    }

    public long DeadLetterCount() { lock (_lock) return Scalar<long>("SELECT COUNT(*) FROM dead_letter"); }

    /// <summary>Returns free pages to the file system after a large backlog drains.</summary>
    public void Compact() { lock (_lock) Exec("PRAGMA incremental_vacuum(10000)"); }

    // ---- key index ----

    public long KeyCount(string sourceId, string table)
    {
        lock (_lock) return Scalar<long>("SELECT COUNT(*) FROM row_keys WHERE source_id = $s AND table_name = $t", ("$s", sourceId), ("$t", table));
    }

    /// <summary>Keys the agent has sent that are no longer at the source.</summary>
    public async Task<IReadOnlyList<string>> FindMissingKeysAsync(string sourceId, string table, IAsyncEnumerable<string> sourceKeys,
        CancellationToken ct)
    {
        lock (_lock)
        {
            Exec("CREATE TEMP TABLE IF NOT EXISTS seen_keys (key TEXT PRIMARY KEY) WITHOUT ROWID");
            Exec("DELETE FROM seen_keys");
        }
        var batch = new List<string>(5000);
        await foreach (var key in sourceKeys.WithCancellation(ct))
        {
            batch.Add(key);
            if (batch.Count == 5000) { AddSeen(batch); batch.Clear(); }
        }
        AddSeen(batch);
        lock (_lock)
        {
            using var command = Command(
                "SELECT key FROM row_keys WHERE source_id = $s AND table_name = $t AND key NOT IN (SELECT key FROM seen_keys)",
                ("$s", sourceId), ("$t", table));
            using var reader = command.ExecuteReader();
            var missing = new List<string>();
            while (reader.Read()) missing.Add(reader.GetString(0));
            Exec("DELETE FROM seen_keys");
            return missing;
        }
    }

    private void AddSeen(List<string> keys)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var insert = Command("INSERT OR IGNORE INTO seen_keys VALUES ($k)");
            foreach (var key in keys) Run(insert, ("$k", key));
            tx.Commit();
        }
    }

    // ---- agent events (posted to the control API later) ----

    public void AddEvent(AgentEvent agentEvent)
    {
        lock (_lock) Exec("INSERT INTO agent_events (body) VALUES ($b)", ("$b", EnvelopeJson.Serialize(agentEvent)));
    }

    public IReadOnlyList<(long Id, AgentEvent Event)> PeekEvents(int max)
    {
        lock (_lock)
        {
            using var command = Command("SELECT id, body FROM agent_events ORDER BY id LIMIT $n", ("$n", max));
            using var reader = command.ExecuteReader();
            var events = new List<(long, AgentEvent)>();
            while (reader.Read()) events.Add((reader.GetInt64(0), EnvelopeJson.Deserialize<AgentEvent>(reader.GetString(1))));
            return events;
        }
    }

    public void RemoveEvents(IEnumerable<long> ids)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var delete = Command("DELETE FROM agent_events WHERE id = $id");
            foreach (var id in ids) Run(delete, ("$id", id));
            tx.Commit();
        }
    }

    // ---- secrets: credentials and cached config, encrypted again with the protector inside the encrypted database ----

    public void SetCredential(string name, string secret)
    {
        lock (_lock)
            Exec("INSERT OR REPLACE INTO credentials VALUES ($n, $v)", ("$n", name), ("$v", _protector.Protect(Encoding.UTF8.GetBytes(secret))));
    }

    public string? GetCredential(string name)
    {
        lock (_lock)
        {
            var value = Scalar<byte[]?>("SELECT value FROM credentials WHERE name = $n", ("$n", name));
            return value is null ? null : Encoding.UTF8.GetString(_protector.Unprotect(value));
        }
    }

    /// <summary>The last config received, so the agent keeps running when the control API is down.</summary>
    public void SaveConfig(AgentConfig config)
    {
        var protectedJson = Convert.ToBase64String(_protector.Protect(EnvelopeJson.SerializeToUtf8(config)));
        lock (_lock) Exec("INSERT OR REPLACE INTO meta VALUES ('config', $v)", ("$v", protectedJson));
    }

    public AgentConfig? LoadConfig()
    {
        lock (_lock)
        {
            var value = Scalar<string?>("SELECT value FROM meta WHERE key = 'config'");
            return value is null ? null : EnvelopeJson.Deserialize<AgentConfig>(_protector.Unprotect(Convert.FromBase64String(value)));
        }
    }

    // ---- plumbing ----

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Run(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        command.Parameters.Clear();
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void Exec(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default! : (T)value;
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
    }
}
