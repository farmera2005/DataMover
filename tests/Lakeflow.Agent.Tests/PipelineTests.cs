using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Agent.Tests;

public class PipelineTests
{
    private static Dictionary<string, object?> Row(string? name, decimal? qty = null) => new() { ["name"] = name, ["qty"] = qty };

    private static AgentCommand Command(CommandType type, string? source = "cw", string? table = "dbo.Tickets") => new()
    {
        CommandId = Guid.NewGuid(), Type = type, SourceId = source, Table = table, IssuedUtc = DateTimeOffset.UtcNow, IssuedBy = "adam",
    };

    [Fact]
    public async Task Snapshot_then_stream_delivers_everything_with_increasing_seq()
    {
        await using var h = await Harness.StartAsync();
        var source = h.Source("cw", "Tickets");
        for (var id = 1; id <= 2500; id++) source.Upsert("Tickets", id, Row($"t{id}", id));
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets"))));

        await h.RunAsync(TimeSpan.FromSeconds(30));
        source.Upsert("Tickets", 2501, Row("new"));
        source.Upsert("Tickets", 5, Row("changed", 5.5m));
        source.Delete("Tickets", 6);
        await h.RunAsync(TimeSpan.FromMinutes(1));

        h.AssertSilverMatches("cw", "Tickets");
        var received = h.Sender.Received;
        Assert.Equal(2500, received.Count(e => e.Op == ChangeOp.Snapshot));
        Assert.Single(received, e => e.Op == ChangeOp.SnapshotComplete);
        Assert.Equal(received.Select(e => e.Seq).Order(), received.Select(e => e.Seq));
        Assert.All(received, e => Assert.Equal("stmarys", e.SiteId));
        Assert.All(h.Sender.Hubs, hub => Assert.Equal("grain", hub));
        Assert.Contains(received, e => e.Op == ChangeOp.Delete && e.Key["id"]!.GetValue<int>() == 6);
    }

    [Fact]
    public async Task Each_source_goes_to_its_divisions_hub()
    {
        await using var h = await Harness.StartAsync();
        h.Source("cw", "Tickets").Upsert("Tickets", 1, Row("grain"));
        h.Source("kahler", "Batches").Upsert("Batches", 1, Row("agronomy"));
        var kahler = Harness.SourceConfig("kahler", CaptureMethod.ChangeTracking, "Batches") with { Division = Division.Agronomy, SourceSystem = "kahler" };
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets"), kahler)));
        await h.RunAsync(TimeSpan.FromSeconds(10));

        var routed = h.Sender.Received.Zip(h.Sender.Hubs).ToList();
        Assert.All(routed.Where(r => r.First.SourceId == "cw"), r => Assert.Equal("grain", r.Second));
        Assert.All(routed.Where(r => r.First.SourceId == "kahler"), r => Assert.Equal("agronomy", r.Second));
        Assert.Contains(routed, r => r.Second == "agronomy");
    }

    [Fact]
    public async Task Nightly_marker_carries_the_sources_row_count_and_key_checksum()
    {
        await using var h = await Harness.StartAsync();
        var source = h.Source("cw", "Tickets");
        for (var id = 1; id <= 40; id++) source.Upsert("Tickets", id, Row($"t{id}"));
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets"))));
        await h.RunAsync(TimeSpan.FromMinutes(1));
        source.Delete("Tickets", 3);
        source.Upsert("Tickets", 41, Row("late"));

        // The harness starts at 08:00 site time; run past 02:30.
        await h.RunAsync(TimeSpan.FromHours(19), TimeSpan.FromMinutes(1));

        var marker = Assert.Single(h.Sender.Received, e => e.Op == ChangeOp.Reconcile);
        var expected = new KeyChecksum.Accumulator();
        foreach (var id in source.Ids("Tickets")) expected.Add(ColumnValues.KeyText(new System.Text.Json.Nodes.JsonObject { ["id"] = id }));
        Assert.Equal(40, marker.After![RowChange.RowCount]!.GetValue<long>());
        Assert.Equal(expected.Sum.ToString(), marker.After[RowChange.KeyChecksumField]!.GetValue<string>());
        Assert.Equal("{}", marker.KeyId);
        Assert.Equal("compuweigh", marker.SourceSystem);
        // Every change before the marker in seq order is what the count describes.
        Assert.All(h.Sender.Received.Where(e => e.Key["id"]?.GetValue<int>() is 3 or 41), e => Assert.True(e.Seq < marker.Seq));
        Assert.DoesNotContain(h.Sender.Received, e => e.Op == ChangeOp.Delete && e.Seq > marker.Seq); // CT found the delete itself
    }

    [Fact]
    public async Task Added_column_flows_on_and_a_dropped_column_blocks_until_resumed()
    {
        await using var h = await Harness.StartAsync();
        var source = h.Source("cw", "Tickets");
        source.Upsert("Tickets", 1, Row("a"));
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets"))));
        await h.RunAsync(TimeSpan.FromMinutes(1));

        source.AddColumn("Tickets", new ColumnSchema("moisture", "decimal", true, 0, Precision: 5, Scale: 2));
        source.Upsert("Tickets", 2, new Dictionary<string, object?> { ["name"] = "b", ["moisture"] = 14.5m });
        await h.RunAsync(TimeSpan.FromMinutes(6));
        h.AssertSilverMatches("cw", "Tickets");
        Assert.Contains(h.Store.PeekEvents(50), e => e.Event.Kind == AgentEventKind.SchemaChange && e.Event.Message.Contains("added moisture"));

        source.DropColumn("Tickets", "qty");
        await h.RunAsync(TimeSpan.FromMinutes(6));
        source.Upsert("Tickets", 3, new Dictionary<string, object?> { ["name"] = "c" });
        await h.RunAsync(TimeSpan.FromMinutes(1));
        var table = Assert.Single(h.Pipeline.BuildHeartbeat().Tables);
        Assert.Equal(TableState.Blocked, table.State);
        Assert.Contains("dropped qty", table.LastError);
        Assert.DoesNotContain(h.Sender.Received, e => e.Key["id"]?.GetValue<int>() == 3);

        await h.Pipeline.ExecuteAsync(Command(CommandType.ResumeTable));
        await h.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(TableState.Streaming, Assert.Single(h.Pipeline.BuildHeartbeat().Tables).State);
        Assert.Contains(h.Sender.Received, e => e.Key["id"]?.GetValue<int>() == 3);
    }

    [Fact]
    public async Task Failing_source_backs_off_alerts_once_and_leaves_other_sources_alone()
    {
        await using var h = await Harness.StartAsync();
        var bad = h.Source("cw", "Tickets");
        var good = h.Source("kahler", "Tickets");
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(
            Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets"),
            Harness.SourceConfig("kahler", CaptureMethod.ChangeTracking, "Tickets"))));
        await h.RunAsync(TimeSpan.FromSeconds(10));
        h.Connectors["cw"].FailWith = new TimeoutException("connection timed out");

        for (var minute = 0; minute < 60; minute++)
        {
            bad.Upsert("Tickets", minute + 1, Row("x"));
            good.Upsert("Tickets", minute + 1, Row("y"));
            await h.RunAsync(TimeSpan.FromMinutes(1));
        }

        var heartbeat = h.Pipeline.BuildHeartbeat();
        var cw = heartbeat.Sources.Single(s => s.SourceId == "cw");
        Assert.False(cw.Connected);
        Assert.InRange(cw.ConsecutiveFailures, 4, 12); // backing off to 15 min, not retrying every 30 s
        Assert.Single(h.Store.PeekEvents(100), e => e.Event.Severity == EventSeverity.Critical && e.Event.SourceId == "cw");
        Assert.Equal(TableState.Failing, heartbeat.Tables.Single(t => t.SourceId == "cw").State);
        Assert.True(heartbeat.Tables.Single(t => t.SourceId == "cw").LagSeconds > 50 * 60);
        h.AssertSilverMatches("kahler", "Tickets");
        Assert.True(heartbeat.Tables.Single(t => t.SourceId == "kahler").LagSeconds < 120);

        h.Connectors["cw"].FailWith = null;
        await h.RunAsync(TimeSpan.FromMinutes(16));
        h.AssertSilverMatches("cw", "Tickets");
        Assert.True(h.Pipeline.BuildHeartbeat().Sources.Single(s => s.SourceId == "cw").Connected);
    }

    [Fact]
    public async Task Full_buffer_pauses_capture_and_alerts_then_catches_up_without_loss()
    {
        await using var h = await Harness.StartAsync();
        var source = h.Source("cw", "Tickets");
        var config = Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets")) with
        {
            Buffer = new BufferSettings { CapBytes = 10L * 1024 * 1024 },
        };
        Assert.True(await h.Pipeline.ApplyConfigAsync(config));
        await h.RunAsync(TimeSpan.FromSeconds(10));
        h.Sender.Down = true;

        var big = new string('w', 8000);
        for (var id = 1; id <= 3000; id++) source.Upsert("Tickets", id, Row(big + id)); // ~24 MB, over the 10 MB cap
        await h.RunAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(30));

        var stats = h.Store.GetBufferStats();
        // Stopped at the cap; the read that crossed it (at most one 1000-row batch) is kept, never dropped.
        Assert.InRange(stats.Bytes, 10L * 1024 * 1024, 10L * 1024 * 1024 + 1000 * 8300);
        Assert.True(source.Ids("Tickets").Count > stats.Count, "capture should have stopped before the whole backlog");
        Assert.Contains(h.Store.PeekEvents(50), e => e.Event.Message.StartsWith("Local buffer is"));
        Assert.True(h.Pipeline.BuildHeartbeat().BufferPercent >= 100);

        h.Sender.Down = false;
        h.Sender.MaxBatchEvents = 500;
        await h.RunAsync(TimeSpan.FromMinutes(15));
        Assert.Equal(0, h.Store.GetBufferStats().Count);
        h.AssertSilverMatches("cw", "Tickets");
    }

    [Fact]
    public async Task Pause_resume_and_resnapshot_commands()
    {
        await using var h = await Harness.StartAsync();
        var source = h.Source("cw", "Tickets");
        for (var id = 1; id <= 10; id++) source.Upsert("Tickets", id, Row($"t{id}"));
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets"))));
        await h.RunAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(EventSeverity.Info, (await h.Pipeline.ExecuteAsync(Command(CommandType.PauseTable))).Severity);
        source.Upsert("Tickets", 11, Row("while paused"));
        await h.RunAsync(TimeSpan.FromMinutes(2));
        Assert.DoesNotContain(h.Sender.Received, e => e.Key["id"]?.GetValue<int>() == 11);
        Assert.Equal(TableState.Paused, Assert.Single(h.Pipeline.BuildHeartbeat().Tables).State);

        await h.Pipeline.ExecuteAsync(Command(CommandType.ResumeTable));
        await h.RunAsync(TimeSpan.FromSeconds(10));
        h.AssertSilverMatches("cw", "Tickets");

        var before = h.Sender.Received.Count;
        var lastSeq = h.Sender.Received.Max(e => e.Seq);
        await h.Pipeline.ExecuteAsync(Command(CommandType.ResnapshotTable));
        await h.RunAsync(TimeSpan.FromSeconds(10));
        var resent = h.Sender.Received.Skip(before).ToList();
        Assert.Equal(11, resent.Count(e => e.Op == ChangeOp.Snapshot));
        Assert.All(resent, e => Assert.True(e.Seq > lastSeq, "seq carries on through a resnapshot"));
        h.AssertSilverMatches("cw", "Tickets");

        var unknown = await h.Pipeline.ExecuteAsync(Command(CommandType.PauseTable, table: "dbo.Nope"));
        Assert.Equal(EventSeverity.Warning, unknown.Severity);
        Assert.Contains(h.Store.PeekEvents(100), e => e.Event.Kind == AgentEventKind.CommandResult && e.Event.Message == "Resnapshot queued.");
    }

    [Fact]
    public async Task Invalid_config_is_refused_and_the_cached_one_is_used_after_restart()
    {
        await using var h = await Harness.StartAsync();
        h.Source("cw", "Tickets");
        var good = Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets"));
        Assert.True(await h.Pipeline.ApplyConfigAsync(good));
        Assert.False(await h.Pipeline.ApplyConfigAsync(good with { ConfigVersion = 2, SiteId = "Not Valid" }));
        Assert.Equal(1, h.Pipeline.Config!.ConfigVersion);

        await h.RestartAsync();
        Assert.Equal(1, h.Pipeline.Config!.ConfigVersion);
        Assert.Equal("sas", h.Sender.Targets[0].SasToken);
    }

    [Fact]
    public async Task Reads_are_throttled_to_the_sources_row_budget()
    {
        await using var h = await Harness.StartAsync();
        var source = h.Source("cw", "Tickets");
        for (var id = 1; id <= 5000; id++) source.Upsert("Tickets", id, Row("x"));
        var config = Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets") with { MaxRowsPerSecond = 100 };
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(config)));

        await h.RunAsync(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(1));
        Assert.InRange(h.Sender.Received.Count, 2000, 3000); // 1000 rows, then 10 s rest, per batch
        Assert.Equal(TableState.Snapshotting, Assert.Single(h.Pipeline.BuildHeartbeat().Tables).State);
        await h.RunAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        h.AssertSilverMatches("cw", "Tickets");
        Assert.Equal(TableState.Streaming, Assert.Single(h.Pipeline.BuildHeartbeat().Tables).State);
    }

    [Fact]
    public async Task Heartbeat_reports_health_without_secrets()
    {
        await using var h = await Harness.StartAsync();
        var source = h.Source("cw", "Tickets");
        source.Upsert("Tickets", 1, Row("a"));
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets"))));
        await h.RunAsync(TimeSpan.FromMinutes(2));

        var heartbeat = h.Pipeline.BuildHeartbeat();
        Assert.Equal("1.0.0", heartbeat.AgentVersion);
        Assert.True(heartbeat.EventHubReachable);
        Assert.Equal(0, heartbeat.BufferedChanges);
        var table = Assert.Single(heartbeat.Tables);
        Assert.Equal(TableState.Streaming, table.State);
        Assert.InRange(table.LagSeconds, 0, 35);
        Assert.DoesNotContain("sas", EnvelopeJson.Serialize(heartbeat).Replace("\"", " "), StringComparison.Ordinal);
    }
}
