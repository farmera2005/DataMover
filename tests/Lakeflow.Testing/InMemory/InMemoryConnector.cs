using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Lakeflow.Contracts;

namespace Lakeflow.Testing.InMemory;

/// <summary>
/// Reference connector over <see cref="InMemorySource"/>. With <see cref="CaptureMethod.ChangeTracking"/> it
/// behaves like SQL Server Change Tracking (net changes, native deletes); with <see cref="CaptureMethod.RowVersion"/>
/// like rowversion polling (upserts only, deletes by key reconciliation). Used to prove the contract suite and to
/// drive agent tests without a database.
/// </summary>
public sealed class InMemoryConnector(InMemorySource source) : ISourceConnector, IKeyReader, ISchemaBrowser
{
    public SourceEngine Engine => SourceEngine.SqlServer;

    /// <summary>Calls made, for tests that check the agent's behaviour (e.g. throttling, retries).</summary>
    public int ReadCalls { get; private set; }

    /// <summary>When set, every read throws it (simulates an unreachable source).</summary>
    public Exception? FailWith { get; set; }

    public ConnectorCapabilities GetCapabilities(TableConfig table) =>
        new(NativeDeletes: table.Method == CaptureMethod.ChangeTracking, ProvidesBefore: false);

    public Task<TableSchema> GetSchemaAsync(TableRef table, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (source.Gate)
        {
            var t = source.Get(table.Name);
            return Task.FromResult(new TableSchema(table, t.Columns.ToList(), ["id"]));
        }
    }

    public async IAsyncEnumerable<ChangeBatch> SnapshotAsync(TableConfig table, TableSchema schema, Checkpoint? resumeFrom,
        int batchSize, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resuming = resumeFrom is { Phase: CheckpointPhase.Snapshot } && resumeFrom.Method == table.Method;
        var start = resuming ? resumeFrom!.Position : source.CurrentVersion.ToString(CultureInfo.InvariantCulture);
        var afterId = resuming ? resumeFrom!.SnapshotAfterKey?["id"]?.GetValue<int>() : null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFailing();
            ReadCalls++;
            List<RowChange> changes;
            lock (source.Gate)
            {
                changes = source.Get(table.Table.Name).Rows
                    .Where(r => afterId is null || r.Key > afterId)
                    .Take(batchSize)
                    .Select(r => new RowChange(ChangeOp.Snapshot, Key(r.Key), null, Row(r.Value.Values)))
                    .ToList();
            }
            var done = changes.Count < batchSize;
            if (changes.Count > 0) afterId = changes[^1].Key["id"]!.GetValue<int>();
            var next = done
                ? new Checkpoint(table.Method, CheckpointPhase.Incremental, start)
                : new Checkpoint(table.Method, CheckpointPhase.Snapshot, start, Key(afterId!.Value));
            yield return new ChangeBatch(changes, next, !done);
            if (done) yield break;
            await Task.Yield();
        }
    }

    public Task<ChangeBatch> ReadChangesAsync(TableConfig table, TableSchema schema, Checkpoint checkpoint, int maxRows,
        CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        ReadCalls++;
        var from = long.Parse(checkpoint.Position, CultureInfo.InvariantCulture);
        if (table.Method == CaptureMethod.ChangeTracking && from < source.MinValidVersion)
            throw new CheckpointExpiredException($"Checkpoint {from} is older than retention ({source.MinValidVersion}).");
        lock (source.Gate)
        {
            var t = source.Get(table.Table.Name);
            return Task.FromResult(table.Method == CaptureMethod.ChangeTracking
                ? NetChanges(t, table, from, maxRows)
                : RowVersionChanges(t, table, from, maxRows));
        }
    }

    private static ChangeBatch NetChanges(InMemorySource.Table t, TableConfig table, long from, int maxRows)
    {
        var pending = t.Log.Where(e => e.Version > from).OrderBy(e => e.Version).ToList();
        var ids = new List<int>();
        var to = from;
        // Take whole versions until maxRows distinct rows are covered.
        foreach (var entry in pending)
        {
            if (!ids.Contains(entry.Id))
            {
                if (ids.Count == maxRows) break;
                ids.Add(entry.Id);
            }
            to = entry.Version;
        }
        var changes = ids.Select(id => t.Rows.TryGetValue(id, out var row)
            ? new RowChange(ChangeOp.Update, Key(id), null, Row(row.Values))
            : new RowChange(ChangeOp.Delete, Key(id), null, null)).ToList();
        var hasMore = pending.Any(e => e.Version > to);
        return new ChangeBatch(changes, new Checkpoint(table.Method, CheckpointPhase.Incremental, to.ToString(CultureInfo.InvariantCulture)), hasMore);
    }

    private static ChangeBatch RowVersionChanges(InMemorySource.Table t, TableConfig table, long from, int maxRows)
    {
        var rows = t.Rows.Where(r => r.Value.RowVersion > from).OrderBy(r => r.Value.RowVersion).ToList();
        var taken = rows.Take(maxRows).ToList();
        var to = taken.Count == 0 ? from : taken[^1].Value.RowVersion;
        var changes = taken.Select(r => new RowChange(ChangeOp.Update, Key(r.Key), null, Row(r.Value.Values))).ToList();
        return new ChangeBatch(changes, new Checkpoint(table.Method, CheckpointPhase.Incremental, to.ToString(CultureInfo.InvariantCulture)), rows.Count > taken.Count);
    }

    public async IAsyncEnumerable<JsonObject> ReadKeysAsync(TableConfig table, TableSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        List<int> ids;
        lock (source.Gate) ids = source.Get(table.Table.Name).Rows.Keys.ToList();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Key(id);
        }
        await Task.CompletedTask;
    }

    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(FailWith is { } error
        ? new ConnectionTestResult { Succeeded = false, Error = error.Message }
        : new ConnectionTestResult { Succeeded = true, ServerVersion = "in-memory", Database = "memory", Login = "test", ChangeTrackingEnabled = true });

    public Task<SchemaBrowseResult> BrowseAsync(int maxTables, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        var names = source.TableNames();
        var tables = names.Take(maxTables).Select(name =>
        {
            var (method, watermark, note) = SourceTableInfo.Suggest(true, true, null, []);
            return new SourceTableInfo
            {
                Table = $"dbo.{name}", RowCount = source.Ids(name).Count, KeyColumns = ["id"], ChangeTrackingEnabled = true,
                SuggestedMethod = method, SuggestedWatermark = watermark, Note = note,
            };
        }).ToList();
        return Task.FromResult(new SchemaBrowseResult(tables, names.Count > maxTables));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ThrowIfFailing()
    {
        if (FailWith is not null) throw FailWith;
    }

    private static JsonObject Key(int id) => new() { ["id"] = id };

    private static JsonObject Row(Dictionary<string, object?> values) => ColumnValues.Row(values);
}
