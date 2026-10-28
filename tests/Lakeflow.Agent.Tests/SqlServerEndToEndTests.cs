using System.Text.Json.Nodes;
using Lakeflow.Agent.Capture;
using Lakeflow.Agent.Storage;
using Lakeflow.Connectors.SqlServer.Tests;
using Lakeflow.Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lakeflow.Agent.Tests;

/// <summary>
/// The pilot's shape: the real agent pipeline and SQL Server connector, a read-only login whose password lives only in
/// the agent's encrypted store, rowversion polling, an Event Hubs outage with a restart, and nightly delete
/// reconciliation. Needs MSSQL_TEST_CONNECTION (scripts/test-sqlserver.sh).
/// </summary>
public sealed class SqlServerEndToEndTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lakeflow-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly TestProtector _protector = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSender _sender = new();
    private LocalStore? _store;
    private AgentPipeline? _pipeline;

    private void Start()
    {
        _store = LocalStore.Open(_dir, _protector);
        _pipeline = new AgentPipeline(_store, new ConnectorFactory(new StoreCredentials(_store), NullLoggerFactory.Instance),
            _sender, _time, NullLoggerFactory.Instance, "1.0.0");
    }

    private async Task RunAsync(TimeSpan duration, TimeSpan step)
    {
        for (var t = TimeSpan.Zero; t <= duration; t += step)
        {
            await _pipeline!.RunCaptureOnceAsync(CancellationToken.None);
            await _pipeline.RunDeliveryOnceAsync(CancellationToken.None);
            _time.Advance(step);
        }
    }

    [SkippableFact]
    public async Task Pilot_table_replicates_through_an_outage_and_restart()
    {
        Skip.If(TestDatabase.SkipReason is not null, TestDatabase.SkipReason);
        var db = await TestDatabase.GetAsync();
        var table = $"Tickets_{Guid.NewGuid():N}"[..20];
        await db.ExecAsync($"CREATE TABLE dbo.[{table}] (TicketId int PRIMARY KEY, Commodity varchar(20), NetLbs decimal(12,2), WeighedOut datetime2(0), rv rowversion)");
        await db.ExecAsync($"CREATE INDEX IX_rv ON dbo.[{table}] (rv)");
        await db.ExecAsync($"INSERT dbo.[{table}] (TicketId, Commodity, NetLbs, WeighedOut) SELECT value, 'CORN', value * 10.5, '2026-10-28T08:00:00' FROM GENERATE_SERIES(1, 1500)");

        Start();
        _store!.SetCredential(TestDatabase.CredentialName, db.ReadOnlyPassword);
        var source = db.Source([new TableConfig { Table = new TableRef("dbo", table), Method = CaptureMethod.RowVersion }]) with { SourceId = "compuweigh", SourceSystem = "compuweigh" };
        Assert.True(await _pipeline!.ApplyConfigAsync(Harness.Config(source)));

        await RunAsync(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10));
        Assert.Equal(1500, _sender.Received.Count(e => e.Op == ChangeOp.Snapshot));

        _sender.Down = true;
        await db.ExecAsync($"INSERT dbo.[{table}] (TicketId, Commodity, NetLbs) VALUES (2001, 'SOYBEANS', 52000)");
        await db.ExecAsync($"UPDATE dbo.[{table}] SET NetLbs = 1, WeighedOut = '2026-10-28T09:00:00' WHERE TicketId <= 10");
        await db.ExecAsync($"DELETE dbo.[{table}] WHERE TicketId BETWEEN 100 AND 104");
        await RunAsync(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));

        await _pipeline.DisposeAsync();
        _store.Dispose();
        Start();
        Assert.True(await _pipeline.LoadCachedConfigAsync());
        await db.ExecAsync($"INSERT dbo.[{table}] (TicketId, Commodity, NetLbs) VALUES (2002, 'WHEAT', 48000)");
        await RunAsync(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));

        _sender.Down = false;
        // Run past 02:30 site time so the nightly reconciliation finds the deleted tickets.
        await RunAsync(TimeSpan.FromHours(15), TimeSpan.FromMinutes(1));
        Assert.Equal(0, _store.GetBufferStats().Count);

        var silver = new Dictionary<int, JsonObject>();
        foreach (var e in _sender.Received.Where(e => e.Op != ChangeOp.SnapshotComplete).OrderBy(e => e.Seq))
        {
            var id = e.Key["TicketId"]!.GetValue<int>();
            if (e.Op == ChangeOp.Delete) silver.Remove(id); else silver[id] = e.After!;
        }

        await using var connection = new SqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT TicketId, Commodity, NetLbs FROM dbo.[{table}]", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            count++;
            var row = silver[reader.GetInt32(0)];
            Assert.Equal(reader.GetString(1), row["Commodity"]!.GetValue<string>());
            Assert.Equal(reader.GetDecimal(2), row["NetLbs"]!.GetValue<decimal>());
        }
        Assert.Equal(1497, count);
        Assert.Equal(count, silver.Count);
        Assert.Contains(_sender.Received, e => e.Op == ChangeOp.Delete && e.Key["TicketId"]!.GetValue<int>() == 102);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipeline is not null) await _pipeline.DisposeAsync();
        _store?.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
