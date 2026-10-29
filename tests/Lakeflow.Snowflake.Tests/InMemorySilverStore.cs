using System.Text.Json.Nodes;
using Lakeflow.Contracts;

namespace Lakeflow.Snowflake.Tests;

/// <summary>
/// The silver steps in memory, with the semantics the Snowflake SQL implements (latest per key, seq guard, tombstones,
/// whole-row replacement), so <see cref="SilverJob"/>'s ordering around markers can be tested without Snowflake.
/// </summary>
public sealed class InMemorySilverStore : ISilverStore
{
    public List<JsonObject> Bronze { get; } = [];
    public Dictionary<(string, string), long> Progress { get; } = [];
    public Dictionary<string, List<string>> Columns { get; } = [];
    public Dictionary<string, Dictionary<(string Site, string Source, string Key), Dictionary<string, string?>>> Tables { get; } = [];
    public List<ReconciliationResult> Reconciliations { get; } = [];
    private List<JsonObject> _batch = [];

    /// <summary>Lands rows exactly as the forwarder sends them (NDJSON from <see cref="BronzeRows"/>).</summary>
    public void Land(byte[] ndjsonRow) => Bronze.Add(JsonNode.Parse(ndjsonRow)!.AsObject());

    private static string? S(JsonObject row, string name) => row[name]?.ToString();
    private static long L(JsonObject row, string name) => long.Parse(row[name]!.ToString());

    public Task<IReadOnlyList<BatchTable>> PrepareBatchAsync(CancellationToken ct)
    {
        _batch = Bronze.Where(r => L(r, "EH_SEQUENCE") > Progress.GetValueOrDefault((S(r, "DIVISION")!, S(r, "PARTITION_ID")!), -1)).ToList();
        IReadOnlyList<BatchTable> tables = _batch.Where(r => S(r, "OP") is not null)
            .GroupBy(r => (S(r, "SOURCE_SYSTEM")!, S(r, "TABLE_NAME")!))
            .Select(g => new BatchTable(g.Key.Item1, g.Key.Item2, g.Count())).OrderBy(t => t.SourceSystem).ThenBy(t => t.TableName).ToList();
        return Task.FromResult(tables);
    }

    private IEnumerable<JsonObject> Of(BatchTable t) => _batch.Where(r => S(r, "SOURCE_SYSTEM") == t.SourceSystem && S(r, "TABLE_NAME") == t.TableName);

    private static bool IsData(JsonObject r) => S(r, "OP") is "insert" or "update" or "delete" or "snapshot";

    public Task<IReadOnlyList<SilverMarker>> MarkersAsync(BatchTable table, CancellationToken ct)
    {
        IReadOnlyList<SilverMarker> markers = Of(table).Where(r => S(r, "OP") is "snapshot_complete" or "reconcile")
            .Select(r => new SilverMarker(S(r, "SITE_ID")!, S(r, "SOURCE_ID")!, table.SourceSystem, table.TableName, L(r, "SEQ"), S(r, "OP")!,
                JsonNode.Parse(S(r, "AFTER_JSON") ?? "{}")!.AsObject(), null)).OrderBy(m => m.Seq).ToList();
        return Task.FromResult(markers);
    }

    public Task<IReadOnlyList<string>> SourceColumnsAsync(BatchTable table, CancellationToken ct)
    {
        IReadOnlyList<string> columns = Of(table).Where(IsData).SelectMany(r => r["AFTER_VALUES"] is JsonObject o ? o.Select(kv => kv.Key) : []).Distinct().ToList();
        return Task.FromResult(columns);
    }

    public Task<IReadOnlyList<string>> ExistingColumnsAsync(string silverTable, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Columns.TryGetValue(silverTable, out var c) ? [.. SnowflakeSilverStore.MetaColumns, .. c] : []);

    public Task EnsureTableAsync(string silverTable, IReadOnlyList<string> newColumns, CancellationToken ct)
    {
        if (!Columns.ContainsKey(silverTable))
        {
            Columns[silverTable] = [];
            Tables[silverTable] = [];
        }
        foreach (var c in newColumns) if (!Columns[silverTable].Contains(c, StringComparer.OrdinalIgnoreCase)) Columns[silverTable].Add(c);
        return Task.CompletedTask;
    }

    public Task MergeAsync(BatchTable table, IReadOnlyList<(string Source, string Column)> columns, Segment segment, CancellationToken ct)
    {
        var rows = Of(table).Where(IsData).Where(r =>
            (segment.SiteId is null || (S(r, "SITE_ID") == segment.SiteId && S(r, "SOURCE_ID") == segment.SourceId))
            && L(r, "SEQ") > segment.Lower && L(r, "SEQ") < segment.Upper
            && !segment.Done.Any(d => S(r, "SITE_ID") == d.SiteId && S(r, "SOURCE_ID") == d.SourceId && L(r, "SEQ") <= d.Bound));
        var latest = rows.GroupBy(r => (S(r, "SITE_ID")!, S(r, "SOURCE_ID")!, S(r, "KEY_ID")!)).Select(g => g.MaxBy(r => L(r, "SEQ"))!);
        var target = Tables[table.SilverTable];
        foreach (var s in latest)
        {
            var key = (S(s, "SITE_ID")!, S(s, "SOURCE_ID")!, S(s, "KEY_ID")!);
            var deleted = S(s, "OP") == "delete";
            var after = s["AFTER_VALUES"] as JsonObject;
            if (target.TryGetValue(key, out var row))
            {
                if (L(s, "SEQ") <= long.Parse(row["_SEQ"]!)) continue;
                row["_SEQ"] = S(s, "SEQ");
                row["_OP"] = S(s, "OP");
                row["_DELETED"] = deleted ? "true" : "false";
                if (deleted) continue;
            }
            else target[key] = row = new Dictionary<string, string?> { ["_SEQ"] = S(s, "SEQ"), ["_OP"] = S(s, "OP"), ["_DELETED"] = deleted ? "true" : "false" };
            foreach (var (source, column) in columns) row[column] = after?[source]?.GetValue<string>();
        }
        return Task.CompletedTask;
    }

    public Task SnapshotCompleteAsync(string silverTable, string siteId, string sourceId, long startSeq, CancellationToken ct)
    {
        if (Tables.TryGetValue(silverTable, out var target))
            foreach (var ((site, source, _), row) in target)
                if (site == siteId && source == sourceId && long.Parse(row["_SEQ"]!) < startSeq && row["_DELETED"] == "false")
                    (row["_DELETED"], row["_OP"]) = ("true", "delete");
        return Task.CompletedTask;
    }

    public Task<(long Rows, string Checksum)> CountAsync(string silverTable, string siteId, string sourceId, CancellationToken ct)
    {
        var sum = new KeyChecksum.Accumulator();
        foreach (var ((site, source, key), row) in Tables.GetValueOrDefault(silverTable) ?? [])
            if (site == siteId && source == sourceId && row["_DELETED"] == "false") sum.Add(key);
        return Task.FromResult((sum.Count, sum.Sum.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public Task RecordReconciliationAsync(ReconciliationResult result, DateTimeOffset? captureTs, CancellationToken ct)
    {
        Reconciliations.Add(result);
        return Task.CompletedTask;
    }

    public Task SaveProgressAsync(CancellationToken ct)
    {
        foreach (var g in _batch.GroupBy(r => (S(r, "DIVISION")!, S(r, "PARTITION_ID")!)))
            Progress[g.Key] = Math.Max(Progress.GetValueOrDefault(g.Key, -1), g.Max(r => L(r, "EH_SEQUENCE")));
        return Task.CompletedTask;
    }

    public Task<int> PurgeTombstonesAsync(int days, CancellationToken ct) => Task.FromResult(0);

    /// <summary>Live rows of a table, by key.</summary>
    public Dictionary<string, Dictionary<string, string?>> Live(string silverTable) =>
        Tables[silverTable].Where(kv => kv.Value["_DELETED"] == "false").ToDictionary(kv => kv.Key.Key, kv => kv.Value);
}
