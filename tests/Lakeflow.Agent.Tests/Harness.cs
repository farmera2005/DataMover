using System.Text.Json.Nodes;
using Lakeflow.Agent.Capture;
using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;
using Lakeflow.Testing;
using Lakeflow.Testing.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lakeflow.Agent.Tests;

/// <summary>
/// An agent on simulated time over in-memory sources and a fake Event Hubs. Tests drive it step by step, so a
/// 24-hour outage runs in seconds and every step is deterministic.
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "lakeflow-agent-" + Guid.NewGuid().ToString("N"));
    public TestProtector Protector { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    public FakeSender Sender { get; } = new();
    public Dictionary<string, InMemorySource> Sources { get; } = [];
    public Dictionary<string, InMemoryConnector> Connectors { get; } = [];
    public LocalStore Store { get; private set; } = null!;
    public AgentPipeline Pipeline { get; private set; } = null!;
    public ILoggerFactory Loggers { get; set; } = NullLoggerFactory.Instance;

    private sealed class Factory(Harness harness) : IConnectorFactory
    {
        public ISourceConnector Create(SourceConfig source)
        {
            var connector = new InMemoryConnector(harness.Sources[source.SourceId]);
            if (harness.Connectors.TryGetValue(source.SourceId, out var previous)) connector.FailWith = previous.FailWith;
            harness.Connectors[source.SourceId] = connector;
            return connector;
        }
    }

    public static async Task<Harness> StartAsync(AgentConfig? config = null)
    {
        var harness = new Harness();
        harness.Open();
        if (config is not null) Assert.True(await harness.Pipeline.ApplyConfigAsync(config));
        return harness;
    }

    /// <summary>Opens the store and pipeline (again), e.g. after changing <see cref="Loggers"/>.</summary>
    public Task ReopenAsync()
    {
        Open();
        return Task.CompletedTask;
    }

    private void Open()
    {
        Store = LocalStore.Open(Directory, Protector);
        Pipeline = new AgentPipeline(Store, new Factory(this), Sender, Time, Loggers, "1.0.0", new Random(1));
    }

    /// <summary>Stops the agent (as a service restart or reboot would) and starts it again on the same files.</summary>
    public async Task RestartAsync()
    {
        await Pipeline.DisposeAsync();
        Store.Dispose();
        Open();
        Assert.True(await Pipeline.LoadCachedConfigAsync(), "the restarted agent should run on its cached config");
    }

    public InMemorySource Source(string sourceId, params string[] tables)
    {
        var source = new InMemorySource();
        foreach (var table in tables) source.CreateTable(table);
        Sources[sourceId] = source;
        return source;
    }

    public static AgentConfig Config(params SourceConfig[] sources) => new()
    {
        AgentId = "agent-test",
        SiteId = "stmarys",
        ConfigVersion = 1,
        EventHubs =
        [
            new EventHubTarget
            {
                Division = Division.Grain,
                FullyQualifiedNamespace = "test.servicebus.windows.net", HubName = "grain",
                SasToken = "sas", SasExpiresUtc = DateTimeOffset.MaxValue,
            },
            new EventHubTarget
            {
                Division = Division.Agronomy,
                FullyQualifiedNamespace = "test.servicebus.windows.net", HubName = "agronomy",
                SasToken = "sas", SasExpiresUtc = DateTimeOffset.MaxValue,
            },
        ],
        Sources = sources,
    };

    public static SourceConfig SourceConfig(string sourceId, CaptureMethod method, params string[] tables) => new()
    {
        SourceId = sourceId,
        SourceSystem = "compuweigh",
        Division = Division.Grain,
        Engine = SourceEngine.SqlServer,
        Connection = new ConnectionSettings { Host = "scale-pc", CredentialName = "cw" },
        Tables = tables.Select(t => new TableConfig { Table = new TableRef("dbo", t), Method = method }).ToList(),
    };

    /// <summary>Advances simulated time, running capture and delivery as the service loops would.</summary>
    public async Task RunAsync(TimeSpan duration, TimeSpan? step = null)
    {
        var tick = step ?? TimeSpan.FromSeconds(5);
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += tick)
        {
            await StepAsync();
            Time.Advance(tick);
        }
        await StepAsync();
    }

    public async Task StepAsync()
    {
        await Pipeline.RunCaptureOnceAsync(CancellationToken.None);
        await Pipeline.RunDeliveryOnceAsync(CancellationToken.None);
    }

    /// <summary>
    /// What silver would hold after MERGEing everything delivered: per table and key, the change with the highest seq;
    /// deletes remove the row.
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonObject>> Silver()
    {
        var silver = new Dictionary<string, Dictionary<string, JsonObject>>();
        foreach (var group in Sender.Received.GroupBy(e => (e.SourceId, e.Table)))
        {
            var latest = new Dictionary<string, ChangeEnvelope>();
            foreach (var change in group.Where(e => e.Op is not (ChangeOp.SnapshotComplete or ChangeOp.Reconcile)))
            {
                var key = ColumnValues.KeyText(change.Key);
                if (!latest.TryGetValue(key, out var seen) || seen.Seq < change.Seq) latest[key] = change;
            }
            // After a snapshot, rows not re-sent since it started are gone from the source.
            var lastSnapshotStart = group.Where(e => e.Op == ChangeOp.SnapshotComplete)
                .Select(e => e.After![RowChange.SnapshotStartSeq]!.GetValue<long>()).DefaultIfEmpty(0).Max();
            silver[$"{group.Key.SourceId}/{group.Key.Table}"] = latest
                .Where(e => e.Value.Op != ChangeOp.Delete && e.Value.Seq >= lastSnapshotStart)
                .ToDictionary(e => e.Key, e => e.Value.After!);
        }
        return silver;
    }

    /// <summary>Asserts silver matches the source table exactly.</summary>
    public void AssertSilverMatches(string sourceId, string table)
    {
        var expected = new Dictionary<int, (string? Name, decimal? Qty)>();
        var connector = new InMemoryConnector(Sources[sourceId]);
        var config = new TableConfig { Table = new TableRef("dbo", table), Method = CaptureMethod.ChangeTracking };
        var schema = connector.GetSchemaAsync(config.Table, CancellationToken.None).Result;
        var replica = new Replica();
        foreach (var batch in connector.SnapshotAsync(config, schema, null, 100_000, CancellationToken.None).ToBlockingEnumerable())
            replica.Apply(batch.Changes);

        var silver = Silver().GetValueOrDefault($"{sourceId}/dbo.{table}") ?? [];
        Assert.Equal(replica.Count, silver.Count);
        foreach (var row in replica.Rows)
        {
            var key = ColumnValues.KeyText(ColumnValues.KeyOf(row, ["id"]));
            Assert.True(silver.TryGetValue(key, out var landed), $"row {key} missing from silver");
            // A column added after a row last changed is absent from it; downstream (Delta schema merge) reads it as null.
            foreach (var (column, value) in row)
                Assert.True(JsonNode.DeepEquals(value, landed!.TryGetPropertyValue(column, out var got) ? got : null),
                    $"row {key}: source {row.ToJsonString()} but silver {landed.ToJsonString()}");
            Assert.All(landed!.Select(p => p.Key), column => Assert.True(row.ContainsKey(column), $"row {key}: silver has extra column {column}"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Pipeline is not null) await Pipeline.DisposeAsync();
        Store?.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
