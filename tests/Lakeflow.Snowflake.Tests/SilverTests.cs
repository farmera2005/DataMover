using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lakeflow.Snowflake.Tests;

public sealed class SilverTests
{
    /// <summary>The real collector's envelopes (FabricFixtureTests writes them), the same ones the Fabric tests replay.</summary>
    private static (List<JsonObject> Envelopes, JsonObject Expected) Fixture()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "agent_run.json")))!.AsObject();
        return (json["envelopes"]!.AsArray().Select(e => e!.AsObject()).ToList(), json["expected"]!.AsObject());
    }

    private static void Land(InMemorySilverStore store, IEnumerable<JsonObject> envelopes, ref long sequence)
    {
        foreach (var envelope in envelopes)
            store.Land(BronzeRows.Row("grain", "0", new HubEvent(sequence++, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes(envelope.ToJsonString()))));
    }

    private static void AssertMatchesSource(InMemorySilverStore store, JsonObject expected)
    {
        var live = store.Live("COMPUWEIGH_TICKETS");
        Assert.Equal(expected.Select(kv => kv.Key).Order(), live.Keys.Order());
        foreach (var (key, row) in expected)
            foreach (var (column, value) in row!.AsObject())
                Assert.Equal(value is null ? null : value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString(),
                    live[key][column]);
    }

    [Fact]
    public async Task The_collectors_changes_end_as_the_source_rows_with_a_clean_reconciliation()
    {
        var (envelopes, expected) = Fixture();
        var store = new InMemorySilverStore();
        long sequence = 0;
        Land(store, envelopes, ref sequence);

        var result = await new SilverJob(store, NullLogger.Instance).RunAsync(CancellationToken.None);

        AssertMatchesSource(store, expected);
        var check = Assert.Single(result.Reconciliations);
        Assert.Equal("ok", check.Status);
        Assert.Equal(expected.Count, check.SilverRows);
        Assert.Equal("snowflake", check.Destination);
        // Numbers keep every digit (no floating point on the way).
        Assert.Contains(store.Live("COMPUWEIGH_TICKETS").Values, r => r["qty"] == "123456789013.3456");
    }

    [Fact]
    public async Task Changes_arriving_over_several_runs_and_replays_give_the_same_result()
    {
        var (envelopes, expected) = Fixture();
        var store = new InMemorySilverStore();
        var job = new SilverJob(store, NullLogger.Instance);
        long sequence = 0;
        foreach (var part in envelopes.Chunk(37))
        {
            Land(store, part, ref sequence);
            await job.RunAsync(CancellationToken.None);
        }
        AssertMatchesSource(store, expected);
        // At-least-once delivery: everything again, later. Nothing changes.
        Land(store, envelopes, ref sequence);
        await job.RunAsync(CancellationToken.None);
        AssertMatchesSource(store, expected);
        Assert.All(store.Reconciliations, r => Assert.NotEqual("mismatch", r.Status));
    }

    [Fact]
    public async Task A_run_that_fails_part_way_is_repeated_with_the_same_result()
    {
        var (envelopes, expected) = Fixture();
        var store = new InMemorySilverStore();
        long sequence = 0;
        Land(store, envelopes, ref sequence);
        var failing = new FailAfter(store, merges: 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SilverJob(failing, NullLogger.Instance).RunAsync(CancellationToken.None));
        Assert.Empty(store.Progress); // not marked applied
        await new SilverJob(store, NullLogger.Instance).RunAsync(CancellationToken.None);
        AssertMatchesSource(store, expected);
    }

    private sealed class FailAfter(InMemorySilverStore inner, int merges) : ISilverStore
    {
        private int _merges;
        public Task<IReadOnlyList<BatchTable>> PrepareBatchAsync(CancellationToken ct) => inner.PrepareBatchAsync(ct);
        public Task<IReadOnlyList<SilverMarker>> MarkersAsync(BatchTable t, CancellationToken ct) => inner.MarkersAsync(t, ct);
        public Task<IReadOnlyList<string>> SourceColumnsAsync(BatchTable t, CancellationToken ct) => inner.SourceColumnsAsync(t, ct);
        public Task<IReadOnlyList<string>> ExistingColumnsAsync(string s, CancellationToken ct) => inner.ExistingColumnsAsync(s, ct);
        public Task EnsureTableAsync(string s, IReadOnlyList<string> c, CancellationToken ct) => inner.EnsureTableAsync(s, c, ct);
        public Task MergeAsync(BatchTable t, IReadOnlyList<(string Source, string Column)> c, Segment s, CancellationToken ct) =>
            ++_merges > merges ? throw new InvalidOperationException("Snowflake went away") : inner.MergeAsync(t, c, s, ct);
        public Task SnapshotCompleteAsync(string s, string a, string b, long x, CancellationToken ct) => inner.SnapshotCompleteAsync(s, a, b, x, ct);
        public Task<(long Rows, string Checksum)> CountAsync(string s, string a, string b, CancellationToken ct) => inner.CountAsync(s, a, b, ct);
        public Task RecordReconciliationAsync(Contracts.ReconciliationResult r, DateTimeOffset? c, CancellationToken ct) => inner.RecordReconciliationAsync(r, c, ct);
        public Task SaveProgressAsync(CancellationToken ct) => inner.SaveProgressAsync(ct);
        public Task<int> PurgeTombstonesAsync(int d, CancellationToken ct) => inner.PurgeTombstonesAsync(d, ct);
    }

    [Fact]
    public void Columns_map_like_fabric_and_stay_stable()
    {
        Assert.Equal([("Gross Weight", "Gross_Weight"), ("_rowid", "c_rowid"), ("id", "id")],
            SilverJob.MapColumns(["id", "Gross Weight", "_rowid"], []));
        // Case-insensitive clash within a batch gets a suffix; an existing column is reused whatever its case.
        Assert.Equal([("ID", "ID"), ("id", "id_2")], SilverJob.MapColumns(["id", "ID"], []));
        Assert.Equal([("Name", "NAME")], SilverJob.MapColumns(["Name"], ["_SITE_ID", "NAME"]));
    }

    [Fact]
    public void Merge_sql_escapes_every_name_and_value()
    {
        var table = new BatchTable("compu'weigh", "dbo.Tick\"ets", 1);
        var sql = SnowflakeSilverStore.MergeSql(table, [("a'b\\c", "a'b\\c"), ("q\"uote", "q\"uote")],
            Segment.Rest([("site'1", "src", 42)]));
        Assert.Contains("SOURCE_SYSTEM = 'compu''weigh'", sql);
        Assert.Contains("TABLE_NAME = 'dbo.Tick\"ets'", sql);
        Assert.Contains("GET(s.AFTER_VALUES, 'a''b\\\\c')", sql);
        Assert.Contains("\"q\"\"uote\"", sql);
        Assert.Contains("NOT (SITE_ID = 'site''1' AND SOURCE_ID = 'src' AND SEQ <= 42)", sql);
        Assert.Contains("WHEN MATCHED AND s.SEQ > t.\"_SEQ\" AND s.DELETED THEN UPDATE SET", sql);
        Assert.Contains("QUALIFY ROW_NUMBER() OVER (PARTITION BY SITE_ID, SOURCE_ID, KEY_ID ORDER BY SEQ DESC) = 1", sql);
        Assert.Equal("COMPU_WEIGH_TICK_ETS", table.SilverTable);
    }

    [Fact]
    public void Reconciliation_status_follows_the_tolerance()
    {
        var marker = (long rows, string checksum) => new SilverMarker("s", "src", "sys", "dbo.T", 9, "reconcile",
            new JsonObject { ["row_count"] = rows, ["key_checksum"] = checksum }, null);
        Assert.Equal("ok", SilverJob.Compare(marker(1000, "5"), "T", 1000, "5").Status);
        Assert.Equal("within_tolerance", SilverJob.Compare(marker(1000, "5"), "T", 1000, "6").Status);
        Assert.Equal("within_tolerance", SilverJob.Compare(marker(1000, "5"), "T", 999, "6").Status);
        Assert.Equal("mismatch", SilverJob.Compare(marker(1000, "5"), "T", 990, "6").Status);
    }
}
