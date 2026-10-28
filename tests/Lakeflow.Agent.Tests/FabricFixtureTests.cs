using System.Text.Json.Nodes;
using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Agent.Tests;

/// <summary>
/// Produces fabric/tests/data/agent_run.json: every envelope the real agent sent in a realistic run (snapshot, changes,
/// Change Tracking retention lost and a resnapshot, the nightly reconcile marker) and the source's final rows. The
/// Fabric tests replay it through the notebook code, so the agent and the lake are tested against each other.
/// The file is checked in; this test fails when the agent's output no longer matches it.
/// Set LAKEFLOW_WRITE_FABRIC_FIXTURE=1 to rewrite it after an intended change.
/// </summary>
public class FabricFixtureTests
{
    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lakeflow.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "fabric", "tests", "data", "agent_run.json");
    }

    [Fact]
    public async Task Agent_output_matches_the_fabric_fixture()
    {
        await using var h = await Harness.StartAsync();
        var source = h.Source("compuweigh", "Tickets");
        var random = new Random(7);
        for (var id = 1; id <= 120; id++)
            source.Upsert("Tickets", id, new Dictionary<string, object?>
            {
                ["name"] = id % 10 == 0 ? $"Bürger #{id}" : $"truck {id}",
                ["qty"] = id % 7 == 0 ? null : Math.Round((decimal)random.NextDouble() * 1_000_000_000m, 4),
            });
        Assert.True(await h.Pipeline.ApplyConfigAsync(Harness.Config(Harness.SourceConfig("compuweigh", CaptureMethod.ChangeTracking, "Tickets"))));
        await h.RunAsync(TimeSpan.FromMinutes(2));

        for (var id = 1; id <= 30; id++) source.Upsert("Tickets", id, new Dictionary<string, object?> { ["qty"] = 123456789012.3456m + id });
        for (var id = 100; id <= 105; id++) source.Delete("Tickets", id);
        source.AddColumn("Tickets", new ColumnSchema("moisture", "decimal", true, 0, Precision: 5, Scale: 2));
        source.Upsert("Tickets", 121, new Dictionary<string, object?> { ["name"] = "new", ["moisture"] = 14.50m });
        await h.RunAsync(TimeSpan.FromMinutes(10));

        // Lost connection long enough for Change Tracking retention to pass: deletes in the gap are never seen as deletes.
        h.Connectors["compuweigh"].FailWith = new TimeoutException("offline");
        await h.RunAsync(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));
        for (var id = 50; id <= 54; id++) source.Delete("Tickets", id);
        source.Upsert("Tickets", 7, new Dictionary<string, object?> { ["name"] = "changed in the gap" });
        source.MinValidVersion = source.CurrentVersion;
        h.Connectors["compuweigh"].FailWith = null;
        await h.RunAsync(TimeSpan.FromMinutes(20));

        // Through the night: the reconcile marker.
        source.Upsert("Tickets", 122, new Dictionary<string, object?> { ["name"] = "late" });
        await h.RunAsync(TimeSpan.FromHours(19), TimeSpan.FromMinutes(5));
        Assert.Contains(h.Sender.Received, e => e.Op == ChangeOp.Reconcile);
        Assert.Contains(h.Sender.Received, e => e.Op == ChangeOp.SnapshotComplete && e.Seq > 200);
        h.AssertSilverMatches("compuweigh", "Tickets");

        var expected = new JsonObject();
        var connector = new Testing.InMemory.InMemoryConnector(h.Sources["compuweigh"]);
        var table = new TableConfig { Table = new TableRef("dbo", "Tickets"), Method = CaptureMethod.ChangeTracking };
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);
        await foreach (var batch in connector.SnapshotAsync(table, schema, null, 100_000, CancellationToken.None))
            foreach (var row in batch.Changes) expected[ColumnValues.KeyText(row.Key)] = row.After!.DeepClone();

        var fixture = new JsonObject
        {
            ["about"] = "Written by FabricFixtureTests (Lakeflow.Agent.Tests). Envelopes exactly as sent to Event Hubs; expected = source rows at the end.",
            ["envelopes"] = new JsonArray(h.Sender.Received.Select(e => JsonNode.Parse(EnvelopeJson.Serialize(e))).ToArray()),
            ["expected"] = expected,
        };
        var text = fixture.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        var path = FixturePath();
        if (Environment.GetEnvironmentVariable("LAKEFLOW_WRITE_FABRIC_FIXTURE") == "1") await File.WriteAllTextAsync(path, text);
        Assert.True(File.Exists(path), $"{path} is missing; run with LAKEFLOW_WRITE_FABRIC_FIXTURE=1");
        Assert.True(text == await File.ReadAllTextAsync(path), "The agent's envelopes changed; rerun with LAKEFLOW_WRITE_FABRIC_FIXTURE=1 and rerun the Fabric tests.");
    }
}
