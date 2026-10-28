using Lakeflow.Agent.Capture;
using Lakeflow.Agent.Delivery;
using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent;

/// <summary>
/// The agent's work, as steps the host runs on timers and tests run directly: apply config, capture what is due,
/// deliver what is buffered, report health, carry out commands.
/// </summary>
public sealed class AgentPipeline : IAsyncDisposable
{
    private readonly LocalStore _store;
    private readonly IConnectorFactory _factory;
    private readonly IEventSender _sender;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Dictionary<string, SourceWorker> _workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly Random _random;
    private bool _bufferAlerted;
    private bool _bufferFull;

    /// <param name="random">Jitter for retry back-off; tests pass a seeded one for repeatable runs.</param>
    public AgentPipeline(LocalStore store, IConnectorFactory factory, IEventSender sender, TimeProvider time, ILoggerFactory loggers,
        string agentVersion, Random? random = null)
    {
        (_store, _factory, _sender, _time, _loggers, AgentVersion) = (store, factory, sender, time, loggers, agentVersion);
        _random = random ?? Random.Shared;
        _log = loggers.CreateLogger<AgentPipeline>();
        Delivery = new DeliveryLoop(store, sender, time, loggers.CreateLogger<DeliveryLoop>(), _random);
    }

    public string AgentVersion { get; }
    public AgentConfig? Config { get; private set; }
    public DeliveryLoop Delivery { get; }
    public LocalStore Store => _store;

    /// <summary>Starts from the config cached by the last run, if any.</summary>
    public Task<bool> LoadCachedConfigAsync() => _store.LoadConfig() is { } cached ? ApplyConfigAsync(cached, cache: false) : Task.FromResult(false);

    /// <summary>Applies a config from the control API; an invalid one is refused and the current one kept.</summary>
    public async Task<bool> ApplyConfigAsync(AgentConfig config, bool cache = true)
    {
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            _log.LogError("Config version {Version} refused: {Errors}", config.ConfigVersion, string.Join(" ", errors));
            return false;
        }
        await _captureGate.WaitAsync();
        try
        {
            _sender.UpdateTargets(config.EventHubs);
            var wanted = config.Sources.ToDictionary(s => s.SourceId, StringComparer.Ordinal);
            foreach (var gone in _workers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                await _workers[gone].DisposeAsync();
                _workers.Remove(gone);
            }
            foreach (var (id, source) in wanted)
            {
                if (_workers.TryGetValue(id, out var worker)) await worker.UpdateAsync(source);
                else _workers[id] = new SourceWorker(source, _store, _factory, _time, _loggers.CreateLogger<SourceWorker>(), _random);
            }
            if (Config?.ConfigVersion != config.ConfigVersion) _log.LogInformation("Applied config version {Version}", config.ConfigVersion);
            Config = config;
            if (cache) _store.SaveConfig(config);
            return true;
        }
        finally
        {
            _captureGate.Release();
        }
    }

    /// <summary>Runs every source's due tables, sources side by side so one slow site system cannot hold up another.</summary>
    public async Task RunCaptureOnceAsync(CancellationToken ct)
    {
        await _captureGate.WaitAsync(ct);
        try
        {
            if (Config is not { } config) return;
            CheckBuffer(config);
            await Task.WhenAll(_workers.Values.Select(w => w.RunDueAsync(config, () => CheckBuffer(config), ct)));
        }
        finally
        {
            _captureGate.Release();
        }
    }

    /// <summary>
    /// True when the buffer is at its cap: capture waits (the data stays safely at the source) until it drains.
    /// The read that crosses the cap is kept, so the buffer can exceed it by at most one batch.
    /// </summary>
    private bool CheckBuffer(AgentConfig config)
    {
        var stats = _store.GetBufferStats();
        var percent = 100.0 * stats.Bytes / config.Buffer.CapBytes;
        if (percent >= config.Buffer.AlertPercent && !_bufferAlerted)
        {
            _bufferAlerted = true;
            _log.LogWarning("Local buffer at {Percent:F0}% of its {Cap} byte cap", percent, config.Buffer.CapBytes);
            AddEvent(AgentEventKind.Error, EventSeverity.Warning, $"Local buffer is {percent:F0}% full.");
        }
        else if (percent < config.Buffer.AlertPercent - 10)
        {
            _bufferAlerted = false;
        }
        var full = stats.Bytes >= config.Buffer.CapBytes;
        if (full != _bufferFull)
        {
            _bufferFull = full;
            if (full) _log.LogWarning("Local buffer full; capture paused until Event Hubs accepts the backlog");
            else _log.LogInformation("Local buffer has room again; capture resumed");
        }
        return full;
    }

    /// <summary>Sends until the buffer is empty, a send fails, or <paramref name="maxRounds"/> rounds have run.</summary>
    public async Task<int> RunDeliveryOnceAsync(CancellationToken ct, int maxRounds = 20)
    {
        if (Config is not { } config) return 0;
        var total = 0;
        for (var round = 0; round < maxRounds; round++)
        {
            var sent = await Delivery.SendOnceAsync(config.Buffer.SendBatchMaxEvents, ct);
            total += sent;
            if (sent == 0) break;
        }
        return total;
    }

    /// <summary>Lets a backed-off source try again now (e.g. once its password has arrived).</summary>
    public void RetrySourceSoon(string sourceId)
    {
        if (_workers.TryGetValue(sourceId, out var worker))
            foreach (var table in worker.Tables.Keys) worker.RunTableSoon(table);
    }

    public async Task<AgentEvent> ExecuteAsync(AgentCommand command)
    {
        await _captureGate.WaitAsync();
        try
        {
            var result = Execute(command);
            _log.LogInformation("Command {Type} {Source} {Table}: {Result}", command.Type, command.SourceId, command.Table, result.Message);
            _store.AddEvent(result);
            return result;
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private AgentEvent Execute(AgentCommand command)
    {
        var worker = command.SourceId is null ? null : _workers.GetValueOrDefault(command.SourceId);
        string message;
        var ok = true;
        if (command.SourceId is not null && worker is null)
        {
            (message, ok) = ($"Unknown source {command.SourceId}.", false);
        }
        else if (command.Table is not null && !worker!.Tables.ContainsKey(command.Table))
        {
            (message, ok) = ($"Unknown table {command.Table}.", false);
        }
        else
        {
            switch (command.Type)
            {
                case CommandType.ResnapshotTable:
                    _store.ResetCheckpoint(command.SourceId!, command.Table!);
                    _store.SetMode(command.SourceId!, command.Table!, TableMode.Active);
                    worker!.RunTableSoon(command.Table!);
                    message = "Resnapshot queued.";
                    break;
                case CommandType.PauseTable:
                    _store.SetMode(command.SourceId!, command.Table!, TableMode.Paused);
                    message = "Table paused.";
                    break;
                case CommandType.ResumeTable:
                    Resume(worker!, command.Table!);
                    message = "Table resumed.";
                    break;
                case CommandType.PauseSource:
                    foreach (var table in worker!.Tables.Keys) _store.SetMode(command.SourceId!, table, TableMode.Paused);
                    message = "Source paused.";
                    break;
                case CommandType.ResumeSource:
                    foreach (var table in worker!.Tables.Keys) Resume(worker, table);
                    message = "Source resumed.";
                    break;
                default:
                    (message, ok) = ($"{command.Type} is handled by the agent host.", false);
                    break;
            }
        }
        return new AgentEvent
        {
            EventId = Guid.NewGuid(), OccurredUtc = _time.GetUtcNow(), Kind = AgentEventKind.CommandResult,
            Severity = ok ? EventSeverity.Info : EventSeverity.Warning, CommandId = command.CommandId,
            SourceId = command.SourceId, Table = command.Table, Message = message,
        };
    }

    // Resuming a table blocked by a schema change accepts the new schema.
    private void Resume(SourceWorker worker, string table)
    {
        var record = _store.GetTable(worker.Config.SourceId, table);
        if (record.Mode == TableMode.Blocked) _store.SetSchema(worker.Config.SourceId, table, null);
        _store.SetMode(worker.Config.SourceId, table, TableMode.Active);
        worker.RunTableSoon(table);
    }

    public Heartbeat BuildHeartbeat()
    {
        var config = Config ?? throw new InvalidOperationException("No config yet.");
        var now = _time.GetUtcNow();
        var buffer = _store.GetBufferStats();
        var tables = new List<TableStatus>();
        foreach (var worker in _workers.Values)
        {
            foreach (var (name, runtime) in worker.Tables)
            {
                var record = _store.GetTable(worker.Config.SourceId, name);
                var oldestUnsent = _store.OldestUnsent(worker.Config.SourceId, name);
                // Fresh as of the older of: the last read that reached the end of the source's changes, and the
                // oldest change Event Hubs has not yet acknowledged.
                DateTimeOffset? freshAsOf = record.LastCaughtUp;
                if (oldestUnsent is { } unsent && (freshAsOf is null || unsent < freshAsOf)) freshAsOf = unsent;
                var snapshotting = record.Checkpoint is null || !record.Checkpoint.IsSnapshotComplete || record.Checkpoint.Method != runtime.Config.Method;
                tables.Add(new TableStatus
                {
                    SourceId = worker.Config.SourceId,
                    Table = name,
                    Method = runtime.Config.Method,
                    State = record.Mode switch
                    {
                        TableMode.Paused => TableState.Paused,
                        TableMode.Blocked => TableState.Blocked,
                        _ when worker.Config.Paused || runtime.Config.Paused => TableState.Paused,
                        _ when runtime.Failing => TableState.Failing,
                        _ when snapshotting => TableState.Snapshotting,
                        _ => TableState.Streaming,
                    },
                    LagSeconds = freshAsOf is null ? 0 : Math.Max(0, (now - freshAsOf.Value).TotalSeconds),
                    RowsPerMinute = runtime.RowsPerMinute(now),
                    LastCaptureUtc = record.LastCapture,
                    Checkpoint = record.Checkpoint is null ? null : EnvelopeJson.Serialize(record.Checkpoint),
                    LastError = record.BlockedReason ?? runtime.LastError,
                });
            }
        }
        return new Heartbeat
        {
            AgentId = config.AgentId,
            SiteId = config.SiteId,
            AgentVersion = AgentVersion,
            SentUtc = now,
            ConfigVersion = config.ConfigVersion,
            BufferBytes = buffer.Bytes,
            BufferCapBytes = config.Buffer.CapBytes,
            BufferedChanges = buffer.Count,
            OldestBufferedUtc = buffer.Oldest,
            EventHubReachable = Delivery.Reachable,
            Sources = _workers.Values.Select(w => new SourceStatus
            {
                SourceId = w.Config.SourceId, Connected = w.Connected, ConsecutiveFailures = w.ConsecutiveFailures, LastError = w.LastError,
            }).ToList(),
            Tables = tables,
            LastError = Delivery.LastError,
        };
    }

    private void AddEvent(AgentEventKind kind, EventSeverity severity, string message) =>
        _store.AddEvent(new AgentEvent { EventId = Guid.NewGuid(), OccurredUtc = _time.GetUtcNow(), Kind = kind, Severity = severity, Message = message });

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers.Values) await worker.DisposeAsync();
        _workers.Clear();
        await _sender.DisposeAsync();
        _captureGate.Dispose();
    }
}
