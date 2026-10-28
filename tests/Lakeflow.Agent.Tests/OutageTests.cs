using Lakeflow.Contracts;
using Lakeflow.Testing.InMemory;
using Xunit;
using Xunit.Abstractions;

namespace Lakeflow.Agent.Tests;

/// <summary>
/// Phase 1 gate: the site loses internet for 24 hours (Event Hubs and the control API unreachable) while the scale
/// system keeps writing, and the agent is restarted mid-outage. Nothing may be lost.
/// </summary>
public class OutageTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(CaptureMethod.ChangeTracking)]
    [InlineData(CaptureMethod.RowVersion)]
    public async Task Survives_a_24_hour_internet_outage_with_a_restart_and_loses_nothing(CaptureMethod method)
    {
        await using var h = await Harness.StartAsync();
        var source = h.Source("compuweigh", "Tickets", "Bins");
        var random = new Random(20261028);
        var nextId = new Dictionary<string, int> { ["Tickets"] = 1, ["Bins"] = 1 };
        void Write(string table, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var live = source.Ids(table);
                var roll = random.Next(10);
                if (roll < 5 || live.Count < 5)
                    source.Upsert(table, nextId[table]++, new Dictionary<string, object?> { ["name"] = $"truck {random.Next(1000)}", ["qty"] = random.Next(100_000) / 100m });
                else if (roll < 9)
                    source.Upsert(table, live[random.Next(live.Count)], new Dictionary<string, object?> { ["qty"] = random.Next(100_000) / 100m });
                else
                    source.Delete(table, live[random.Next(live.Count)]);
            }
        }
        Write("Tickets", 300);
        Write("Bins", 40);
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(Harness.SourceConfig("compuweigh", method, "Tickets", "Bins"))));

        // Normal running: snapshot, then streaming.
        await h.RunAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(0, h.Store.GetBufferStats().Count);
        var beforeOutage = h.Sender.Received.Count;

        // Outage: 24 hours of trucks, nothing reaches Azure, restart half-way through.
        h.Sender.Down = true;
        var attemptsBefore = h.Sender.Attempts;
        double lagAtOneHour = 0;
        for (var minute = 1; minute <= 24 * 60; minute++)
        {
            Write("Tickets", 3);
            if (minute % 10 == 0) Write("Bins", 1);
            await h.RunAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));
            if (minute == 60)
            {
                var heartbeat = h.Pipeline.BuildHeartbeat();
                Assert.False(heartbeat.EventHubReachable);
                lagAtOneHour = heartbeat.Tables.Max(t => t.LagSeconds);
                Assert.True(heartbeat.BufferedChanges > 0);
            }
            if (minute == 12 * 60) await h.RestartAsync();
        }
        Assert.Equal(beforeOutage, h.Sender.Received.Count);
        Assert.True(lagAtOneHour > 15 * 60, $"lag after an hour offline should alert, was {lagAtOneHour} s");
        var buffered = h.Store.GetBufferStats();
        var attempts = h.Sender.Attempts - attemptsBefore;
        output.WriteLine($"{method}: {buffered.Count} changes ({buffered.Bytes / 1024} KB) buffered; {attempts} send attempts in 24 h");
        Assert.InRange(attempts, 200, 600); // back-off caps at 5 min: ~288 attempts a day, not one per second

        // Internet back.
        h.Sender.Down = false;
        await h.RunAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(0, h.Store.GetBufferStats().Count);
        Assert.Equal(0, h.Store.DeadLetterCount());

        // No gaps: every sequence number of every table arrived (repeats are allowed, silver dedupes them).
        foreach (var table in h.Sender.Received.GroupBy(e => e.Table))
        {
            var seqs = table.Select(e => e.Seq).ToHashSet();
            Assert.Equal(Enumerable.Range(1, (int)seqs.Max()).Select(i => (long)i), seqs.Order());
        }
        // Order kept: within a table, delivery order follows seq.
        foreach (var table in h.Sender.Received.GroupBy(e => e.Table))
        {
            var seqs = table.Select(e => e.Seq).ToList();
            Assert.Equal(seqs.Order(), seqs);
        }

        if (method == CaptureMethod.RowVersion)
        {
            // Polling finds deletes in the nightly key reconciliation (02:30 site time).
            await h.RunAsync(TimeSpan.FromHours(20), TimeSpan.FromMinutes(1));
        }
        h.AssertSilverMatches("compuweigh", "Tickets");
        h.AssertSilverMatches("compuweigh", "Bins");
        Assert.True(h.Pipeline.BuildHeartbeat().Tables.All(t => t.LagSeconds < 120 && t.State == TableState.Streaming));
    }

    [Fact]
    public async Task Change_tracking_retention_lost_during_a_long_outage_falls_back_to_a_resnapshot()
    {
        await using var h = await Harness.StartAsync();
        var source = h.Source("cw", "Tickets");
        for (var id = 1; id <= 20; id++) source.Upsert("Tickets", id, new Dictionary<string, object?> { ["name"] = $"t{id}" });
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets"))));
        await h.RunAsync(TimeSpan.FromMinutes(1));

        // The scale PC loses its connection to the database for days; Change Tracking cleanup passes our checkpoint.
        h.Connectors["cw"].FailWith = new TimeoutException("server unreachable");
        await h.RunAsync(TimeSpan.FromHours(3), TimeSpan.FromMinutes(1));
        for (var id = 21; id <= 30; id++) source.Upsert("Tickets", id, new Dictionary<string, object?> { ["name"] = $"t{id}" });
        source.Delete("Tickets", 3);
        source.MinValidVersion = source.CurrentVersion;
        h.Connectors["cw"].FailWith = null;
        await h.RunAsync(TimeSpan.FromMinutes(20));

        h.AssertSilverMatches("cw", "Tickets");
        Assert.Contains(h.Store.PeekEvents(100), e => e.Event.Kind == AgentEventKind.Snapshot && e.Event.Message.StartsWith("Resnapshot"));
    }
}
