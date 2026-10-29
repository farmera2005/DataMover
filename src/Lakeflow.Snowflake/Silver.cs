using System.Text;
using System.Text.Json.Nodes;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Snowflake;

/// <summary>One source table's changes in the current batch.</summary>
public sealed record BatchTable(string SourceSystem, string TableName, long Rows)
{
    /// <summary>Silver table name, as in Fabric but upper case: <c>COMPUWEIGH_TICKETS</c>.</summary>
    public string SilverTable => LakeNames.SilverTable(SourceSystem, TableRef.Parse(TableName))["silver.".Length..].ToUpperInvariant();
}

/// <summary>A snapshot_complete or reconcile marker, with its after image (start seq, or count and checksum).</summary>
public sealed record SilverMarker(string SiteId, string SourceId, string SourceSystem, string TableName, long Seq, string Op,
    JsonObject After, DateTimeOffset? CaptureTs);

/// <summary>Which data rows of a table a merge applies: one site and source between two seqs, or everything not yet merged.</summary>
public sealed record Segment(string? SiteId, string? SourceId, long Lower, long Upper, IReadOnlyList<(string SiteId, string SourceId, long Bound)> Done)
{
    public static Segment Between(string siteId, string sourceId, long lower, long upper) => new(siteId, sourceId, lower, upper, []);

    public static Segment Rest(IReadOnlyList<(string, string, long)> done) => new(null, null, long.MinValue, long.MaxValue, done);
}

/// <summary>The silver steps, against Snowflake (or an in-memory model in tests).</summary>
public interface ISilverStore
{
    /// <summary>Takes the bronze rows not yet applied into the work table; returns the source tables in it.</summary>
    Task<IReadOnlyList<BatchTable>> PrepareBatchAsync(CancellationToken ct);
    Task<IReadOnlyList<SilverMarker>> MarkersAsync(BatchTable table, CancellationToken ct);
    Task<IReadOnlyList<string>> SourceColumnsAsync(BatchTable table, CancellationToken ct);
    Task<IReadOnlyList<string>> ExistingColumnsAsync(string silverTable, CancellationToken ct);
    Task EnsureTableAsync(string silverTable, IReadOnlyList<string> newColumns, CancellationToken ct);
    Task MergeAsync(BatchTable table, IReadOnlyList<(string Source, string Column)> columns, Segment segment, CancellationToken ct);
    Task SnapshotCompleteAsync(string silverTable, string siteId, string sourceId, long startSeq, CancellationToken ct);
    Task<(long Rows, string Checksum)> CountAsync(string silverTable, string siteId, string sourceId, CancellationToken ct);
    Task RecordReconciliationAsync(ReconciliationResult result, DateTimeOffset? captureTs, CancellationToken ct);
    /// <summary>Marks the batch applied (the last step: a failed run is simply repeated, and every step is idempotent).</summary>
    Task SaveProgressAsync(CancellationToken ct);
    Task<int> PurgeTombstonesAsync(int days, CancellationToken ct);
}

/// <summary>
/// Bronze to silver in Snowflake, with the same rules as the Fabric notebook: one silver table per source table, rows
/// matched on site, source and key, a change applied only if its seq is higher (replays change nothing), deletes kept
/// as tombstones for 8 days, a snapshot_complete marker deleting the site's rows not re-sent since the snapshot began,
/// and a reconcile marker compared with silver at exactly its point in the stream. Every step is idempotent, so a run
/// that fails part-way is simply run again.
/// </summary>
public sealed class SilverJob(ISilverStore store, ILogger log)
{
    public const int TombstoneDays = 8;
    public const double MismatchTolerancePercent = 0.1;

    public sealed record RunResult(int Tables, long Rows, IReadOnlyList<ReconciliationResult> Reconciliations);

    public async Task<RunResult> RunAsync(CancellationToken ct)
    {
        var tables = await store.PrepareBatchAsync(ct);
        var reconciliations = new List<ReconciliationResult>();
        foreach (var table in tables)
        {
            var columns = await PrepareTableAsync(table, ct);
            reconciliations.AddRange(await ApplyAsync(table, columns, ct));
        }
        await store.SaveProgressAsync(ct);
        if (tables.Count > 0)
            log.LogInformation("Snowflake silver: {Rows} changes applied to {Tables} tables", tables.Sum(t => t.Rows), tables.Count);
        return new RunResult(tables.Count, tables.Sum(t => t.Rows), reconciliations);
    }

    /// <summary>Creates or widens the silver table for the batch's columns; returns source column to silver column.</summary>
    private async Task<IReadOnlyList<(string Source, string Column)>> PrepareTableAsync(BatchTable table, CancellationToken ct)
    {
        var sources = await store.SourceColumnsAsync(table, ct);
        var existing = await store.ExistingColumnsAsync(table.SilverTable, ct);
        var map = MapColumns(sources, existing);
        var known = existing.Select(c => c.ToUpperInvariant()).ToHashSet();
        var added = map.Select(m => m.Column).Where(c => !known.Contains(c.ToUpperInvariant())).ToList();
        if (existing.Count == 0 || added.Count > 0) await store.EnsureTableAsync(table.SilverTable, added, ct);
        return map;
    }

    /// <summary>
    /// Source columns to silver columns (<see cref="LakeNames.ColumnName"/>), case-insensitively unique, as in Fabric:
    /// an existing column is reused, a new clash gets <c>_2</c>, <c>_3</c>...
    /// </summary>
    public static IReadOnlyList<(string Source, string Column)> MapColumns(IEnumerable<string> sourceColumns, IReadOnlyList<string> existingColumns)
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in existingColumns) existing.TryAdd(column, column);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new List<(string, string)>();
        foreach (var source in sourceColumns.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var name = LakeNames.ColumnName(source);
            var candidate = name;
            for (var n = 2; taken.Contains(candidate); n++) candidate = $"{name}_{n}";
            // An existing column of that name (any case) is the same column: keep writing to it.
            if (existing.TryGetValue(candidate, out var actual)) candidate = actual;
            taken.Add(candidate);
            map.Add((source, candidate));
        }
        return map;
    }

    /// <summary>Merges in seq order around the markers, per site and source, as the Fabric notebook does.</summary>
    private async Task<List<ReconciliationResult>> ApplyAsync(BatchTable table, IReadOnlyList<(string, string)> columns, CancellationToken ct)
    {
        var results = new List<ReconciliationResult>();
        var markers = (await store.MarkersAsync(table, ct)).OrderBy(m => m.Seq).ToList();
        var mergedUpTo = new Dictionary<(string, string), long>();
        foreach (var marker in markers)
        {
            var siteSource = (marker.SiteId, marker.SourceId);
            var lower = mergedUpTo.GetValueOrDefault(siteSource, long.MinValue);
            await store.MergeAsync(table, columns, Segment.Between(marker.SiteId, marker.SourceId, lower, marker.Seq), ct);
            mergedUpTo[siteSource] = marker.Seq;
            if (marker.Op == "snapshot_complete")
            {
                var start = long.Parse(marker.After["snapshot_start_seq"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
                await store.SnapshotCompleteAsync(table.SilverTable, marker.SiteId, marker.SourceId, start, ct);
            }
            else
            {
                var (rows, checksum) = await store.CountAsync(table.SilverTable, marker.SiteId, marker.SourceId, ct);
                var result = Compare(marker, table.SilverTable, rows, checksum);
                await store.RecordReconciliationAsync(result, marker.CaptureTs, ct);
                results.Add(result);
            }
        }
        await store.MergeAsync(table, columns, Segment.Rest(mergedUpTo.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList()), ct);
        return results;
    }

    /// <summary>ok, within_tolerance (counts within 0.1%, or keys differ with equal counts) or mismatch.</summary>
    public static ReconciliationResult Compare(SilverMarker marker, string silverTable, long silverRows, string silverChecksum)
    {
        var sourceRows = long.Parse(marker.After["row_count"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        var sourceChecksum = marker.After["key_checksum"]!.ToString();
        var diff = silverRows - sourceRows;
        var pct = sourceRows == 0 && silverRows == 0 ? 0.0 : Math.Abs(diff) * 100.0 / Math.Max(sourceRows, 1);
        var status = diff == 0 && silverChecksum == sourceChecksum ? "ok" : pct <= MismatchTolerancePercent ? "within_tolerance" : "mismatch";
        return new ReconciliationResult
        {
            CheckedUtc = DateTimeOffset.UtcNow, SiteId = marker.SiteId, SourceId = marker.SourceId, SourceSystem = marker.SourceSystem,
            Table = marker.TableName, SilverTable = silverTable, AsOfSeq = marker.Seq, SourceRows = sourceRows, SilverRows = silverRows,
            SourceChecksum = sourceChecksum, SilverChecksum = silverChecksum, RowDiff = diff, MismatchPercent = pct, Status = status,
            Destination = "snowflake",
        };
    }
}

/// <summary>The silver steps as Snowflake SQL, run through the SQL API.</summary>
public sealed class SnowflakeSilverStore(SqlApi sql) : ISilverStore
{
    public const string Bronze = "BRONZE";
    public const string Silver = "SILVER";
    public const string Batch = "BRONZE.LAKEFLOW_BATCH";
    public const string Progress = "BRONZE.LAKEFLOW_PROGRESS";
    public const string Reconciliation = "SILVER.LAKEFLOW_RECONCILIATION";
    private const string DataOps = "('insert', 'update', 'delete', 'snapshot')";

    public static readonly string[] MetaColumns = ["_SITE_ID", "_SOURCE_ID", "_KEY_ID", "_SEQ", "_OP", "_DELETED", "_CAPTURE_TS", "_SOURCE_TS", "_MERGED_AT"];

    /// <summary>Lakeflow's own tables; the forwarder runs this on start (it owns the database).</summary>
    public static string BootstrapScript() => $"""
        CREATE SCHEMA IF NOT EXISTS {Bronze};
        CREATE SCHEMA IF NOT EXISTS {Silver};
        CREATE TABLE IF NOT EXISTS {Bronze}.EVENTS (
            DIVISION VARCHAR, PARTITION_ID VARCHAR, EH_SEQUENCE NUMBER(38,0), ENQUEUED_AT TIMESTAMP_TZ,
            ENVELOPE_VERSION NUMBER(10,0), SITE_ID VARCHAR, SOURCE_ID VARCHAR, SOURCE_SYSTEM VARCHAR, TABLE_NAME VARCHAR,
            OP VARCHAR, KEY_ID VARCHAR, SEQ NUMBER(38,0), SOURCE_TS TIMESTAMP_TZ, CAPTURE_TS TIMESTAMP_TZ,
            KEY_JSON VARCHAR, BEFORE_JSON VARCHAR, AFTER_JSON VARCHAR, AFTER_VALUES VARIANT, PARSE_ERROR VARCHAR, RAW_BODY VARCHAR
        ) COMMENT = 'Every change from Lakeflow, as sent by the collectors (one row per change; key, before and after as exact JSON text)';
        CREATE TABLE IF NOT EXISTS {Progress} (
            DIVISION VARCHAR, PARTITION_ID VARCHAR, APPLIED_SEQUENCE NUMBER(38,0), UPDATED_AT TIMESTAMP_NTZ
        ) COMMENT = 'How far silver has applied BRONZE.EVENTS, per hub partition';
        CREATE TABLE IF NOT EXISTS {Reconciliation} (
            CHECKED_AT TIMESTAMP_NTZ, SITE_ID VARCHAR, SOURCE_ID VARCHAR, SOURCE_SYSTEM VARCHAR, TABLE_NAME VARCHAR,
            SILVER_TABLE VARCHAR, AS_OF_SEQ NUMBER(38,0), CAPTURE_TS TIMESTAMP_TZ, SOURCE_ROWS NUMBER(38,0), SILVER_ROWS NUMBER(38,0),
            SOURCE_CHECKSUM VARCHAR, SILVER_CHECKSUM VARCHAR, ROW_DIFF NUMBER(38,0), MISMATCH_PCT FLOAT, STATUS VARCHAR
        ) COMMENT = 'Nightly source-vs-silver reconciliation results'
        """;

    public async Task<IReadOnlyList<BatchTable>> PrepareBatchAsync(CancellationToken ct)
    {
        await sql.ExecuteScriptAsync($"""
            CREATE OR REPLACE TABLE {Batch} AS
            SELECT e.* FROM {Bronze}.EVENTS e
            LEFT JOIN {Progress} p ON p.DIVISION = e.DIVISION AND p.PARTITION_ID = e.PARTITION_ID
            WHERE e.EH_SEQUENCE > COALESCE(p.APPLIED_SEQUENCE, -1)
            """, ct);
        var result = await sql.QueryAsync($"""
            SELECT SOURCE_SYSTEM, TABLE_NAME, COUNT(*) FROM {Batch}
            WHERE OP IS NOT NULL AND SOURCE_SYSTEM IS NOT NULL AND TABLE_NAME IS NOT NULL GROUP BY 1, 2 ORDER BY 1, 2
            """, ct);
        return result.Rows.Select(r => new BatchTable(r[0]!, r[1]!, long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture))).ToList();
    }

    public async Task<IReadOnlyList<SilverMarker>> MarkersAsync(BatchTable table, CancellationToken ct)
    {
        var result = await sql.QueryAsync($"""
            SELECT SITE_ID, SOURCE_ID, SEQ, OP, AFTER_JSON, TO_VARCHAR(CAPTURE_TS, 'YYYY-MM-DD"T"HH24:MI:SS.FF6TZH:TZM')
            FROM {Batch} WHERE {TableFilter(table)} AND OP IN ('snapshot_complete', 'reconcile') ORDER BY SEQ
            """, ct);
        return result.Rows.Select(r => new SilverMarker(r[0]!, r[1]!, table.SourceSystem, table.TableName,
            long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture), r[3]!,
            JsonNode.Parse(r[4] ?? "{}")!.AsObject(), r[5] is { } ts ? DateTimeOffset.Parse(ts, System.Globalization.CultureInfo.InvariantCulture) : null)).ToList();
    }

    public async Task<IReadOnlyList<string>> SourceColumnsAsync(BatchTable table, CancellationToken ct) =>
        (await sql.QueryAsync($"""
            SELECT DISTINCT f.KEY FROM {Batch} b, LATERAL FLATTEN(INPUT => b.AFTER_VALUES) f
            WHERE {TableFilter(table, "b.")} AND b.OP IN {DataOps}
            """, ct)).Rows.Select(r => r[0]!).ToList();

    public async Task<IReadOnlyList<string>> ExistingColumnsAsync(string silverTable, CancellationToken ct) =>
        (await sql.QueryAsync($"""
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = '{Silver}' AND TABLE_NAME = {Sql.Literal(silverTable)} ORDER BY ORDINAL_POSITION
            """, ct)).Rows.Select(r => r[0]!).ToList();

    public async Task EnsureTableAsync(string silverTable, IReadOnlyList<string> newColumns, CancellationToken ct)
    {
        var script = new StringBuilder($"""
            CREATE TABLE IF NOT EXISTS {Silver}.{Sql.Ident(silverTable)} (
                "_SITE_ID" VARCHAR, "_SOURCE_ID" VARCHAR, "_KEY_ID" VARCHAR, "_SEQ" NUMBER(38,0), "_OP" VARCHAR, "_DELETED" BOOLEAN,
                "_CAPTURE_TS" TIMESTAMP_TZ, "_SOURCE_TS" TIMESTAMP_TZ, "_MERGED_AT" TIMESTAMP_LTZ
            ) CHANGE_TRACKING = TRUE COMMENT = 'Current state per site and key, from Lakeflow. Filter "_DELETED" = FALSE.'
            """);
        foreach (var column in newColumns)
            script.Append($";\nALTER TABLE {Silver}.{Sql.Ident(silverTable)} ADD COLUMN IF NOT EXISTS {Sql.Ident(column)} VARCHAR");
        await sql.ExecuteScriptAsync(script.ToString(), ct);
    }

    public Task MergeAsync(BatchTable table, IReadOnlyList<(string Source, string Column)> columns, Segment segment, CancellationToken ct) =>
        sql.QueryAsync(MergeSql(table, columns, segment), ct);

    /// <summary>
    /// The latest change per key in the segment, applied only over an older seq: a delete leaves a tombstone (data
    /// columns as they were), anything else replaces the whole row (a column missing from the after image is null).
    /// </summary>
    public static string MergeSql(BatchTable table, IReadOnlyList<(string Source, string Column)> columns, Segment segment)
    {
        var filter = new StringBuilder($"{TableFilter(table)} AND OP IN {DataOps}");
        if (segment.SiteId is not null)
            filter.Append($" AND SITE_ID = {Sql.Literal(segment.SiteId)} AND SOURCE_ID = {Sql.Literal(segment.SourceId)}");
        if (segment.Lower != long.MinValue) filter.Append($" AND SEQ > {Sql.Number(segment.Lower)}");
        if (segment.Upper != long.MaxValue) filter.Append($" AND SEQ < {Sql.Number(segment.Upper)}");
        foreach (var (site, source, bound) in segment.Done)
            filter.Append($" AND NOT (SITE_ID = {Sql.Literal(site)} AND SOURCE_ID = {Sql.Literal(source)} AND SEQ <= {Sql.Number(bound)})");
        var value = (string source) => $"AS_VARCHAR(GET(s.AFTER_VALUES, {Sql.Literal(source)}))";
        var meta = """
            "_SEQ" = s.SEQ, "_OP" = s.OP, "_DELETED" = s.DELETED, "_CAPTURE_TS" = s.CAPTURE_TS, "_SOURCE_TS" = s.SOURCE_TS, "_MERGED_AT" = CURRENT_TIMESTAMP()
            """;
        var sets = string.Concat(columns.Select(c => $", {Sql.Ident(c.Column)} = {value(c.Source)}"));
        var insertColumns = string.Join(", ", MetaColumns.Select(Sql.Ident).Concat(columns.Select(c => Sql.Ident(c.Column))));
        var insertValues = string.Join(", ", new[] { "s.SITE_ID", "s.SOURCE_ID", "s.KEY_ID", "s.SEQ", "s.OP", "s.DELETED", "s.CAPTURE_TS", "s.SOURCE_TS", "CURRENT_TIMESTAMP()" }
            .Concat(columns.Select(c => value(c.Source))));
        return $"""
            MERGE INTO {Silver}.{Sql.Ident(table.SilverTable)} t
            USING (
                SELECT SITE_ID, SOURCE_ID, KEY_ID, SEQ, OP, OP = 'delete' AS DELETED, CAPTURE_TS, SOURCE_TS, AFTER_VALUES
                FROM {Batch}
                WHERE {filter}
                QUALIFY ROW_NUMBER() OVER (PARTITION BY SITE_ID, SOURCE_ID, KEY_ID ORDER BY SEQ DESC) = 1
            ) s
            ON t."_SITE_ID" = s.SITE_ID AND t."_SOURCE_ID" = s.SOURCE_ID AND t."_KEY_ID" = s.KEY_ID
            WHEN MATCHED AND s.SEQ > t."_SEQ" AND s.DELETED THEN UPDATE SET {meta}
            WHEN MATCHED AND s.SEQ > t."_SEQ" THEN UPDATE SET {meta}{sets}
            WHEN NOT MATCHED THEN INSERT ({insertColumns}) VALUES ({insertValues})
            """;
    }

    public Task SnapshotCompleteAsync(string silverTable, string siteId, string sourceId, long startSeq, CancellationToken ct) =>
        sql.QueryAsync($"""
            UPDATE {Silver}.{Sql.Ident(silverTable)} SET "_DELETED" = TRUE, "_OP" = 'delete', "_MERGED_AT" = CURRENT_TIMESTAMP()
            WHERE "_SITE_ID" = {Sql.Literal(siteId)} AND "_SOURCE_ID" = {Sql.Literal(sourceId)} AND "_SEQ" < {Sql.Number(startSeq)} AND NOT "_DELETED"
            """, ct);

    /// <summary>Live rows and the key checksum (sum mod 2^64 of the first 8 bytes of SHA-256 of each key, as the collector computes it).</summary>
    public static string CountSql(string silverTable, string siteId, string sourceId) => $"""
        SELECT COUNT(*), TO_VARCHAR(MOD(COALESCE(SUM(TO_NUMBER(UPPER(SUBSTR(SHA2("_KEY_ID", 256), 1, 16)), 'XXXXXXXXXXXXXXXX')), 0), 18446744073709551616))
        FROM {Silver}.{Sql.Ident(silverTable)}
        WHERE "_SITE_ID" = {Sql.Literal(siteId)} AND "_SOURCE_ID" = {Sql.Literal(sourceId)} AND NOT "_DELETED"
        """;

    public async Task<(long Rows, string Checksum)> CountAsync(string silverTable, string siteId, string sourceId, CancellationToken ct)
    {
        var row = (await sql.QueryAsync(CountSql(silverTable, siteId, sourceId), ct)).Rows[0];
        return (long.Parse(row[0]!, System.Globalization.CultureInfo.InvariantCulture), row[1] ?? "0");
    }

    public Task RecordReconciliationAsync(ReconciliationResult r, DateTimeOffset? captureTs, CancellationToken ct) =>
        sql.QueryAsync($"""
            INSERT INTO {Reconciliation} (CHECKED_AT, SITE_ID, SOURCE_ID, SOURCE_SYSTEM, TABLE_NAME, SILVER_TABLE, AS_OF_SEQ, CAPTURE_TS,
                SOURCE_ROWS, SILVER_ROWS, SOURCE_CHECKSUM, SILVER_CHECKSUM, ROW_DIFF, MISMATCH_PCT, STATUS)
            SELECT SYSDATE(), {Sql.Literal(r.SiteId)}, {Sql.Literal(r.SourceId)}, {Sql.Literal(r.SourceSystem)}, {Sql.Literal(r.Table)},
                {Sql.Literal(r.SilverTable)}, {Sql.Number(r.AsOfSeq)}, {(captureTs is { } ts ? $"TO_TIMESTAMP_TZ({Sql.Literal(ts.ToString("O"))})" : "NULL")},
                {Sql.Number(r.SourceRows)}, {Sql.Number(r.SilverRows)}, {Sql.Literal(r.SourceChecksum)}, {Sql.Literal(r.SilverChecksum)},
                {Sql.Number(r.RowDiff)}, {r.MismatchPercent.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, {Sql.Literal(r.Status)}
            """, ct);

    public Task SaveProgressAsync(CancellationToken ct) =>
        sql.QueryAsync($"""
            MERGE INTO {Progress} p
            USING (SELECT DIVISION, PARTITION_ID, MAX(EH_SEQUENCE) AS APPLIED FROM {Batch} GROUP BY 1, 2) b
            ON p.DIVISION = b.DIVISION AND p.PARTITION_ID = b.PARTITION_ID
            WHEN MATCHED AND b.APPLIED > p.APPLIED_SEQUENCE THEN UPDATE SET APPLIED_SEQUENCE = b.APPLIED, UPDATED_AT = SYSDATE()
            WHEN NOT MATCHED THEN INSERT (DIVISION, PARTITION_ID, APPLIED_SEQUENCE, UPDATED_AT) VALUES (b.DIVISION, b.PARTITION_ID, b.APPLIED, SYSDATE())
            """, ct);

    public async Task<int> PurgeTombstonesAsync(int days, CancellationToken ct)
    {
        var tables = (await sql.QueryAsync($"""
            SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{Silver}' AND TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME <> 'LAKEFLOW_RECONCILIATION'
            """, ct)).Rows.Select(r => r[0]!).ToList();
        var purged = 0;
        foreach (var table in tables)
        {
            var result = await sql.QueryAsync($"""
                DELETE FROM {Silver}.{Sql.Ident(table)} WHERE "_DELETED" AND "_MERGED_AT" < DATEADD(day, -{days}, CURRENT_TIMESTAMP())
                """, ct);
            purged += int.TryParse(result.Scalar, out var n) ? n : 0;
        }
        return purged;
    }

    private static string TableFilter(BatchTable table, string prefix = "") =>
        $"{prefix}SOURCE_SYSTEM = {Sql.Literal(table.SourceSystem)} AND {prefix}TABLE_NAME = {Sql.Literal(table.TableName)}";
}
