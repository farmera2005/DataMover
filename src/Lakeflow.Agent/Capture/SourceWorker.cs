using System.Text.Json.Nodes;
using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent.Capture;

/// <summary>Runtime state of one captured table (not persisted; the store holds what must survive a restart).</summary>
internal sealed class TableRuntime(TableConfig config)
{
    public TableConfig Config { get; set; } = config;
    public DateTimeOffset NextRun { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastSchemaCheck { get; set; } = DateTimeOffset.MinValue;
    public string? LastError { get; set; }
    public bool Failing { get; set; }
    public Queue<(DateTimeOffset At, int Rows)> Recent { get; } = new();

    public double RowsPerMinute(DateTimeOffset now)
    {
        while (Recent.Count > 0 && Recent.Peek().At < now.AddMinutes(-1)) Recent.Dequeue();
        return Recent.Sum(r => r.Rows);
    }
}

/// <summary>
/// Captures every table of one source, one table at a time over the source's single connection. A failing source
/// backs off on its own; other sources are unaffected.
/// </summary>
internal sealed class SourceWorker : IAsyncDisposable
{
    private const int BatchRows = 1000;
    private static readonly TimeSpan SchemaCheckEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TableRetry = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxSourceBackoff = TimeSpan.FromMinutes(15);
    public const int FailuresBeforeAlert = 3;

    private readonly LocalStore _store;
    private readonly IConnectorFactory _factory;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Dictionary<string, TableRuntime> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random;
    private ISourceConnector? _connector;

    public SourceWorker(SourceConfig config, LocalStore store, IConnectorFactory factory, TimeProvider time, ILogger log, Random random)
    {
        (Config, _store, _factory, _time, _log, _random) = (config, store, factory, time, log, random);
        Update(config);
    }

    public SourceConfig Config { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MinValue;
    public string? LastError { get; private set; }
    public bool Connected => _connector is not null && ConsecutiveFailures == 0;
    internal IReadOnlyDictionary<string, TableRuntime> Tables => _tables;

    public async Task UpdateAsync(SourceConfig config)
    {
        if (config.Connection != Config.Connection || config.Engine != Config.Engine) await DropConnectorAsync();
        Update(config);
    }

    private void Update(SourceConfig config)
    {
        Config = config;
        var wanted = config.Tables.ToDictionary(t => t.Table.QualifiedName, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _tables.Keys.Where(k => !wanted.ContainsKey(k)).ToList()) _tables.Remove(gone);
        foreach (var (name, table) in wanted)
        {
            if (_tables.TryGetValue(name, out var runtime))
            {
                if (runtime.Config != table) runtime.NextRun = DateTimeOffset.MinValue;
                runtime.Config = table;
            }
            else _tables[name] = new TableRuntime(table);
        }
    }

    public void RunTableSoon(string table)
    {
        if (_tables.TryGetValue(table, out var runtime)) runtime.NextRun = DateTimeOffset.MinValue;
        NextAttempt = DateTimeOffset.MinValue;
    }

    /// <summary>Runs every table that is due. Stops early when the buffer is full or the source fails.</summary>
    public async Task RunDueAsync(AgentConfig agent, Func<bool> bufferFull, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (Config.Paused || now < NextAttempt) return;
        foreach (var runtime in _tables.Values.Where(t => !t.Config.Paused && t.NextRun <= now).OrderBy(t => t.NextRun).ToList())
        {
            if (bufferFull()) return;
            var name = runtime.Config.Table.QualifiedName;
            using var scope = _log.BeginScope(new Dictionary<string, object> { ["source_id"] = Config.SourceId, ["table"] = name });
            var record = _store.GetTable(Config.SourceId, name);
            if (record.Mode != TableMode.Active) continue;
            try
            {
                _connector ??= _factory.Create(Config);
                await RunTableAsync(agent, runtime, record, ct);
                if (ConsecutiveFailures > 0) _log.LogInformation("Source reachable again after {Failures} failures", ConsecutiveFailures);
                ConsecutiveFailures = 0;
                LastError = null;
                runtime.Failing = false;
                runtime.LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (CheckpointExpiredException ex)
            {
                _log.LogWarning("{Message}", ex.Message);
                _store.ResetCheckpoint(Config.SourceId, name);
                Event(AgentEventKind.Snapshot, EventSeverity.Warning, name, $"Resnapshot started: {ex.Message}");
                runtime.NextRun = now;
            }
            catch (CaptureUnavailableException ex)
            {
                if (runtime.LastError != ex.Message)
                {
                    _log.LogError("{Message}", ex.Message);
                    Event(AgentEventKind.Error, EventSeverity.Error, name, ex.Message);
                }
                runtime.Failing = true;
                runtime.LastError = ex.Message;
                runtime.NextRun = now + TableRetry;
            }
            catch (Exception ex)
            {
                // Anything else is the source (network, login, timeouts): back off the whole source.
                ConsecutiveFailures++;
                LastError = ex.Message;
                runtime.Failing = true;
                runtime.LastError = ex.Message;
                var delay = Backoff.Delay(ConsecutiveFailures, TimeSpan.FromSeconds(runtime.Config.PollIntervalSeconds), MaxSourceBackoff, _random);
                NextAttempt = now + delay;
                _log.LogWarning(ex, "Source read failed ({Failures} in a row); retrying in {Delay}", ConsecutiveFailures, delay);
                if (ConsecutiveFailures == FailuresBeforeAlert)
                    Event(AgentEventKind.Error, EventSeverity.Critical, null, $"Source connection failing: {ex.Message}");
                await DropConnectorAsync();
                return;
            }
        }
    }

    private async Task RunTableAsync(AgentConfig agent, TableRuntime runtime, TableRecord record, CancellationToken ct)
    {
        var connector = _connector!;
        var table = runtime.Config;
        var name = table.Table.QualifiedName;
        var now = _time.GetUtcNow();

        // Schema drift: new columns flow on; dropped or retyped columns stop the table until someone decides.
        var schema = record.Schema;
        if (schema is null || now - runtime.LastSchemaCheck >= SchemaCheckEvery)
        {
            var current = await connector.GetSchemaAsync(table.Table, ct);
            runtime.LastSchemaCheck = now;
            if (schema is not null)
            {
                var diff = SchemaComparer.Compare(schema, current);
                if (diff.Kind == SchemaChangeKind.Breaking)
                {
                    var reason = $"Schema change blocked the table: {diff.Describe()}";
                    _store.SetMode(Config.SourceId, name, TableMode.Blocked, reason);
                    _log.LogWarning("{Reason}", reason);
                    Event(AgentEventKind.SchemaChange, EventSeverity.Warning, name, reason);
                    return;
                }
                if (diff.Kind == SchemaChangeKind.Additive)
                {
                    _log.LogInformation("Schema changed: {Change}", diff.Describe());
                    Event(AgentEventKind.SchemaChange, EventSeverity.Info, name, $"Schema changed: {diff.Describe()}");
                }
            }
            if (schema is null || !Equals(EnvelopeJson.Serialize(schema), EnvelopeJson.Serialize(current))) _store.SetSchema(Config.SourceId, name, current);
            schema = current;
        }

        var trackKeys = !connector.GetCapabilities(table).NativeDeletes;
        var checkpoint = record.Checkpoint;
        int rows;
        bool hasMore;
        if (checkpoint is null || checkpoint.Method != table.Method || checkpoint.Phase == CheckpointPhase.Snapshot)
        {
            var resume = checkpoint is { Phase: CheckpointPhase.Snapshot } && checkpoint.Method == table.Method ? checkpoint : null;
            var startSeq = record.SnapshotStartSeq;
            if (resume is null || startSeq is null)
            {
                startSeq = _store.StartSnapshot(Config.SourceId, name);
                _log.LogInformation("Snapshot starting ({Method})", table.Method);
                Event(AgentEventKind.Snapshot, EventSeverity.Info, name, $"Snapshot started ({table.Method}).");
            }
            ChangeBatch? batch = null;
            await foreach (var b in connector.SnapshotAsync(table, schema, resume, BatchRows, ct))
            {
                batch = b;
                break; // one batch per run; the next run resumes from the stored checkpoint
            }
            var changes = batch!.Next.IsSnapshotComplete ? [.. batch.Changes, RowChange.SnapshotCompleted(startSeq.Value)] : batch.Changes;
            _store.Append(Origin(agent), name, changes, batch.Next, caughtUp: false, trackKeys, now);
            rows = batch.Changes.Count;
            hasMore = batch.HasMore || !batch.Next.IsSnapshotComplete;
            if (batch.Next.IsSnapshotComplete)
            {
                // A snapshot is a full read: it stands in for today's reconciliation, the next one is tonight.
                _store.SetReconciled(Config.SourceId, name, SiteToday(agent, now));
                _log.LogInformation("Snapshot finished");
                Event(AgentEventKind.Snapshot, EventSeverity.Info, name, "Snapshot finished.");
                hasMore = true; // read changes made during the snapshot straight away
            }
        }
        else
        {
            var batch = await connector.ReadChangesAsync(table, schema, checkpoint, BatchRows, ct);
            _store.Append(Origin(agent), name, batch.Changes, batch.Next, caughtUp: !batch.HasMore, trackKeys, now);
            rows = batch.Changes.Count;
            hasMore = batch.HasMore;
            if (!hasMore && ReconcileDue(agent, record, now))
                rows += await ReconcileAsync(agent, table, schema, trackKeys, now, ct);
        }

        runtime.Recent.Enqueue((now, rows));
        // Throttle: never read faster than the source's row budget.
        var pause = TimeSpan.FromSeconds((double)rows / Config.MaxRowsPerSecond);
        runtime.NextRun = hasMore ? now + pause : now + TimeSpan.FromSeconds(Math.Max(table.PollIntervalSeconds, pause.TotalSeconds));
    }

    private static bool ReconcileDue(AgentConfig agent, TableRecord record, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(agent.SiteTimeZone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        return (record.LastReconciled is null || record.LastReconciled < today) && TimeOnly.FromDateTime(local.DateTime) >= agent.ReconcileAt;
    }

    /// <summary>
    /// Nightly, straight after a read that caught up: reads every key once, finds deleted rows (polling tables, which
    /// see no deletes otherwise) and sends a <see cref="ChangeOp.Reconcile"/> marker with the source's row count and key
    /// checksum, which the Fabric notebook compares with silver at exactly this point in the stream.
    /// </summary>
    private async Task<int> ReconcileAsync(AgentConfig agent, TableConfig table, TableSchema schema, bool findDeletes,
        DateTimeOffset now, CancellationToken ct)
    {
        var name = table.Table.QualifiedName;
        var reader = _connector as IKeyReader
            ?? throw new CaptureUnavailableException($"{Config.Engine} connector cannot list keys, so the table cannot be reconciled.");
        var checksum = new KeyChecksum.Accumulator();
        var keys = Counted(reader.ReadKeysAsync(table, schema, ct), checksum);
        var changes = new List<RowChange>();
        if (findDeletes)
        {
            var missing = await _store.FindMissingKeysAsync(Config.SourceId, name, keys, ct);
            changes.AddRange(missing.Select(k => new RowChange(ChangeOp.Delete, JsonNode.Parse(k)!.AsObject(), null, null)));
        }
        else
        {
            await foreach (var _ in keys) { }
        }
        changes.Add(RowChange.Reconciled(checksum.Count, checksum.Sum));
        _store.Append(Origin(agent), name, changes, next: null, caughtUp: false, trackKeys: findDeletes, now);
        _store.SetReconciled(Config.SourceId, name, SiteToday(agent, now));
        _log.LogInformation("Nightly reconciliation: {Rows} rows at the source, {Deleted} deleted rows found", checksum.Count, changes.Count - 1);
        return changes.Count - 1;
    }

    private static async IAsyncEnumerable<string> Counted(IAsyncEnumerable<JsonObject> keys, KeyChecksum.Accumulator checksum)
    {
        await foreach (var key in keys)
        {
            var text = ColumnValues.KeyText(key);
            checksum.Add(text);
            yield return text;
        }
    }

    private static DateOnly SiteToday(AgentConfig agent, DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(agent.SiteTimeZone)).DateTime);

    private ChangeOrigin Origin(AgentConfig agent) =>
        new(agent.SiteId, LocalStore.HubKey(Config.Division), Config.SourceId, Config.SourceSystem);

    private void Event(AgentEventKind kind, EventSeverity severity, string? table, string message) =>
        _store.AddEvent(new AgentEvent
        {
            EventId = Guid.NewGuid(), OccurredUtc = _time.GetUtcNow(), Kind = kind, Severity = severity,
            SourceId = Config.SourceId, Table = table, Message = message,
        });

    private async Task DropConnectorAsync()
    {
        if (_connector is null) return;
        try { await _connector.DisposeAsync(); }
        catch (Exception ex) { _log.LogDebug(ex, "Ignoring error while closing the source connection"); }
        _connector = null;
    }

    public ValueTask DisposeAsync() => new(DropConnectorAsync());
}
