using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Testing;

/// <summary>
/// The contract every connector must pass: snapshot, incremental, delete, restart-resume and schema drift.
/// Subclass it once per connector + capture method and supply a test bed.
/// </summary>
public abstract class ConnectorContractTests : IAsyncLifetime
{
    protected IContractTestBed Bed { get; private set; } = null!;
    private readonly List<ISourceConnector> _connectors = [];
    private static int _tableCounter;

    protected abstract Task<IContractTestBed> CreateBedAsync();

    /// <summary>Why this bed cannot run here (e.g. no test database configured); the tests are skipped.</summary>
    protected virtual string? SkipReason => null;

    public async Task InitializeAsync()
    {
        if (SkipReason is null) Bed = await CreateBedAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var connector in _connectors) await connector.DisposeAsync();
        if (Bed is not null) await Bed.DisposeAsync();
    }

    private ISourceConnector Connect()
    {
        var connector = Bed.CreateConnector();
        _connectors.Add(connector);
        return connector;
    }

    private async Task<(TableConfig Table, ExpectedTable Expected)> NewTableAsync()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var table = await Bed.CreateTableAsync($"contract_{Interlocked.Increment(ref _tableCounter)}_{Guid.NewGuid():N}"[..30]);
        return (table, new ExpectedTable(Bed, table));
    }

    /// <summary>Runs a whole snapshot, applying each batch; returns the final checkpoint.</summary>
    private static async Task<Checkpoint> SnapshotAllAsync(ISourceConnector connector, TableConfig table, Replica replica,
        int batchSize = 10, Checkpoint? resumeFrom = null)
    {
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);
        Checkpoint? last = resumeFrom;
        await foreach (var batch in connector.SnapshotAsync(table, schema, resumeFrom, batchSize, CancellationToken.None))
        {
            Assert.All(batch.Changes, c => Assert.Equal(ChangeOp.Snapshot, c.Op));
            Assert.True(batch.Changes.Count <= batchSize, $"snapshot batch of {batch.Changes.Count} exceeds {batchSize}");
            replica.Apply(batch.Changes);
            last = batch.Next;
        }
        Assert.NotNull(last);
        Assert.Equal(CheckpointPhase.Incremental, last!.Phase);
        Assert.Equal(table.Method, last.Method);
        return last;
    }

    /// <summary>Reads changes until none are left, applying them; finds deletes by key reconciliation if needed.</summary>
    private async Task<Checkpoint> CatchUpAsync(ISourceConnector connector, TableConfig table, Checkpoint checkpoint, Replica replica)
    {
        await Bed.SettleAsync();
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);
        for (var i = 0; ; i++)
        {
            Assert.True(i < 1000, "ReadChanges never reported the end of the changes");
            var batch = await connector.ReadChangesAsync(table, schema, checkpoint, 7, CancellationToken.None);
            Assert.All(batch.Changes, c => Assert.NotEqual(ChangeOp.Snapshot, c.Op));
            Assert.All(batch.Changes.Where(c => c.Op != ChangeOp.Delete), c => Assert.NotNull(c.After));
            replica.Apply(batch.Changes);
            Assert.Equal(CheckpointPhase.Incremental, batch.Next.Phase);
            checkpoint = batch.Next;
            if (!batch.HasMore) break;
        }
        if (!connector.GetCapabilities(table).NativeDeletes) await ReconcileDeletesAsync(connector, table, schema, replica);
        return checkpoint;
    }

    private static async Task ReconcileDeletesAsync(ISourceConnector connector, TableConfig table, TableSchema schema, Replica replica)
    {
        var reader = connector as IKeyReader
            ?? throw new Xunit.Sdk.XunitException($"{connector.GetType().Name} has no native deletes, so it must implement IKeyReader.");
        var present = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in reader.ReadKeysAsync(table, schema, CancellationToken.None))
            present.Add(ColumnValues.KeyText(key));
        foreach (var gone in replica.Keys.Where(k => !present.Contains(ColumnValues.KeyText(k))).ToList())
            replica.Apply(new RowChange(ChangeOp.Delete, gone, null, null));
    }

    [SkippableFact]
    public async Task Schema_reports_columns_and_primary_key()
    {
        var (table, _) = await NewTableAsync();
        var schema = await Connect().GetSchemaAsync(table.Table, CancellationToken.None);

        Assert.Equal(["id"], schema.KeyColumns.Select(k => k.ToLowerInvariant()));
        Assert.Equal(["id", "name", "qty"], schema.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name.ToLowerInvariant()).Take(3));
        Assert.False(schema.Find("id")!.Nullable);
        Assert.True(schema.Find("name")!.Nullable);
    }

    [SkippableFact]
    public async Task Snapshot_copies_every_row_in_bounded_batches()
    {
        var (table, expected) = await NewTableAsync();
        for (var id = 1; id <= 25; id++) await expected.InsertAsync(id, $"row {id}", id * 1.25m);
        await expected.InsertAsync(26, null, null);

        var replica = new Replica();
        await SnapshotAllAsync(Connect(), table, replica);

        expected.AssertMatches(replica);
    }

    [SkippableFact]
    public async Task Snapshot_of_empty_table_ends_ready_for_changes()
    {
        var (table, expected) = await NewTableAsync();
        var connector = Connect();
        var replica = new Replica();
        var checkpoint = await SnapshotAllAsync(connector, table, replica);
        Assert.Equal(0, replica.Count);

        await expected.InsertAsync(1, "first", 1m);
        await CatchUpAsync(connector, table, checkpoint, replica);
        expected.AssertMatches(replica);
    }

    [SkippableFact]
    public async Task Incremental_captures_inserts_and_updates()
    {
        var (table, expected) = await NewTableAsync();
        for (var id = 1; id <= 5; id++) await expected.InsertAsync(id, $"row {id}", id);
        var connector = Connect();
        var replica = new Replica();
        var checkpoint = await SnapshotAllAsync(connector, table, replica);

        for (var id = 6; id <= 20; id++) await expected.InsertAsync(id, $"new {id}", id + 0.5m);
        await expected.UpdateAsync(2, "changed", 99.1234m);
        await expected.UpdateAsync(7, null, null);
        await expected.UpdateAsync(7, "changed twice", 7.7m);
        checkpoint = await CatchUpAsync(connector, table, checkpoint, replica);
        expected.AssertMatches(replica);

        // Nothing changed since: any rows returned again are repeats and change nothing.
        await Bed.SettleAsync();
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);
        var again = await connector.ReadChangesAsync(table, schema, checkpoint, 100, CancellationToken.None);
        Assert.False(again.HasMore);
        Assert.All(again.Changes, c => Assert.NotEqual(ChangeOp.Delete, c.Op));
        replica.Apply(again.Changes); // look-back methods may repeat recent rows; that must be harmless
        expected.AssertMatches(replica);
    }

    [SkippableFact]
    public async Task Deletes_are_captured()
    {
        var (table, expected) = await NewTableAsync();
        for (var id = 1; id <= 10; id++) await expected.InsertAsync(id, $"row {id}", id);
        var connector = Connect();
        var replica = new Replica();
        var checkpoint = await SnapshotAllAsync(connector, table, replica);

        await expected.DeleteAsync(3);
        await expected.DeleteAsync(10);
        await expected.InsertAsync(11, "inserted then deleted", 1m);
        await expected.DeleteAsync(11);
        await CatchUpAsync(connector, table, checkpoint, replica);

        expected.AssertMatches(replica);
        Assert.Null(replica.Get(3));
    }

    [SkippableFact]
    public async Task Changes_made_during_a_snapshot_are_not_lost()
    {
        var (table, expected) = await NewTableAsync();
        for (var id = 1; id <= 30; id++) await expected.InsertAsync(id, $"row {id}", id);
        var connector = Connect();
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);
        var replica = new Replica();

        Checkpoint? last = null;
        var batches = 0;
        await foreach (var batch in connector.SnapshotAsync(table, schema, null, 10, CancellationToken.None))
        {
            replica.Apply(batch.Changes);
            last = batch.Next;
            if (++batches == 1)
            {
                await expected.UpdateAsync(2, "already copied, then changed", 2.5m);
                await expected.UpdateAsync(25, "not copied yet, changed", 25.5m);
                await expected.DeleteAsync(5);
                await expected.DeleteAsync(28);
                await expected.InsertAsync(31, "inserted mid-snapshot", 31m);
            }
        }
        await CatchUpAsync(connector, table, last!, replica);
        expected.AssertMatches(replica);
    }

    [SkippableFact]
    public async Task Interrupted_snapshot_resumes_from_its_checkpoint()
    {
        var (table, expected) = await NewTableAsync();
        for (var id = 1; id <= 35; id++) await expected.InsertAsync(id, $"row {id}", id);
        var replica = new Replica();

        var first = Connect();
        var schema = await first.GetSchemaAsync(table.Table, CancellationToken.None);
        Checkpoint? stored = null;
        await foreach (var batch in first.SnapshotAsync(table, schema, null, 10, CancellationToken.None))
        {
            replica.Apply(batch.Changes);
            stored = batch.Next;
            if (replica.Count >= 20) break; // agent stopped after two batches
        }
        await first.DisposeAsync();
        Assert.Equal(CheckpointPhase.Snapshot, stored!.Phase);
        Assert.NotNull(stored.SnapshotAfterKey);

        await expected.UpdateAsync(1, "changed while stopped", 1.5m);
        var second = Connect();
        var resumedRows = new Replica();
        var checkpoint = await SnapshotAllAsync(second, table, resumedRows, resumeFrom: stored);
        Assert.InRange(resumedRows.Count, 15, 16); // the rest of the table, perhaps re-reading the boundary row
        foreach (var row in resumedRows.Rows) replica.Apply(new RowChange(ChangeOp.Snapshot, ColumnValues.KeyOf(row, ["id"]), null, row));

        await CatchUpAsync(second, table, checkpoint, replica);
        expected.AssertMatches(replica);
    }

    [SkippableFact]
    public async Task Restarted_connector_resumes_incremental_capture_from_its_checkpoint()
    {
        var (table, expected) = await NewTableAsync();
        for (var id = 1; id <= 5; id++) await expected.InsertAsync(id, $"row {id}", id);
        var replica = new Replica();
        var first = Connect();
        var checkpoint = await SnapshotAllAsync(first, table, replica);
        await expected.InsertAsync(6, "before restart", 6m);
        checkpoint = await CatchUpAsync(first, table, checkpoint, replica);
        await first.DisposeAsync();

        // Round-trip the checkpoint through JSON as the agent's store does.
        checkpoint = EnvelopeJson.Deserialize<Checkpoint>(EnvelopeJson.Serialize(checkpoint));

        await expected.InsertAsync(7, "after restart", 7m);
        await expected.UpdateAsync(1, "updated after restart", 1.1m);
        await expected.DeleteAsync(2);

        var second = Connect();
        var schema = await second.GetSchemaAsync(table.Table, CancellationToken.None);
        await Bed.SettleAsync();
        var resumed = await second.ReadChangesAsync(table, schema, checkpoint, 100, CancellationToken.None);
        // At-least-once: earlier changes may repeat, but the new ones must all be there.
        var keys = resumed.Changes.Select(c => c.Key["id"]!.GetValue<int>()).ToHashSet();
        Assert.Contains(7, keys);
        Assert.Contains(1, keys);

        await CatchUpAsync(second, table, checkpoint, replica);
        expected.AssertMatches(replica);
    }

    [SkippableFact]
    public async Task Retrying_from_an_older_checkpoint_returns_the_changes_again()
    {
        // The agent stores a checkpoint only after the batch is buffered; if buffering fails it retries from the
        // old checkpoint on the same connector, and nothing may be held back as "already sent".
        var (table, expected) = await NewTableAsync();
        for (var id = 1; id <= 3; id++) await expected.InsertAsync(id, $"row {id}", id);
        var connector = Connect();
        var replica = new Replica();
        var checkpoint = await SnapshotAllAsync(connector, table, replica);
        await expected.InsertAsync(4, "new", 4m);
        await expected.UpdateAsync(1, "changed", 1.5m);
        await Bed.SettleAsync();
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);

        // Look-back methods may also repeat unchanged rows; rows 1 and 4 must be there both times.
        var lost = await connector.ReadChangesAsync(table, schema, checkpoint, 100, CancellationToken.None);
        Assert.Subset(lost.Changes.Select(c => c.Key["id"]!.GetValue<int>()).ToHashSet(), new HashSet<int> { 1, 4 });
        var retried = await connector.ReadChangesAsync(table, schema, checkpoint, 100, CancellationToken.None);
        Assert.Subset(retried.Changes.Select(c => c.Key["id"]!.GetValue<int>()).ToHashSet(), new HashSet<int> { 1, 4 });

        replica.Apply(retried.Changes);
        await CatchUpAsync(connector, table, retried.Next, replica);
        expected.AssertMatches(replica);
    }

    [SkippableFact]
    public async Task Every_key_is_listed_exactly_once_for_reconciliation()
    {
        var (table, expected) = await NewTableAsync();
        for (var id = 1; id <= 25; id++) await expected.InsertAsync(id, $"row {id}", id);
        await expected.DeleteAsync(7);
        var connector = Connect();
        var reader = Assert.IsAssignableFrom<IKeyReader>(connector);
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);

        var keys = new List<string>();
        await foreach (var key in reader.ReadKeysAsync(table, schema, CancellationToken.None)) keys.Add(ColumnValues.KeyText(key));

        Assert.Equal(24, keys.Count);
        Assert.Equal(24, keys.Distinct().Count());
        Assert.DoesNotContain("{\"id\":7}", keys);
        Assert.Contains("{\"id\":25}", keys);
    }

    [SkippableFact]
    public async Task Added_column_is_captured_automatically()
    {
        var (table, expected) = await NewTableAsync();
        for (var id = 1; id <= 3; id++) await expected.InsertAsync(id, $"row {id}", id);
        var connector = Connect();
        var replica = new Replica();
        var checkpoint = await SnapshotAllAsync(connector, table, replica);
        var before = await connector.GetSchemaAsync(table.Table, CancellationToken.None);

        await Bed.AddColumnAsync(table, "note");
        var after = await connector.GetSchemaAsync(table.Table, CancellationToken.None);
        var diff = SchemaComparer.Compare(before, after);
        Assert.Equal(SchemaChangeKind.Additive, diff.Kind);
        Assert.Equal("note", Assert.Single(diff.Added).Name, ignoreCase: true);

        await expected.InsertAsync(4, "has a note", 4m);
        await expected.SetTextAsync(4, "note", "hello");
        await expected.SetTextAsync(1, "note", "later");
        await CatchUpAsync(connector, table, checkpoint, replica);
        expected.AssertMatches(replica);
    }

    [SkippableFact]
    public async Task Dropped_or_retyped_column_is_a_breaking_change()
    {
        var (table, expected) = await NewTableAsync();
        await expected.InsertAsync(1, "row", 1m);
        await Bed.AddColumnAsync(table, "note");
        var connector = Connect();
        var original = await connector.GetSchemaAsync(table.Table, CancellationToken.None);

        await Bed.DropColumnAsync(table, "note");
        var dropped = SchemaComparer.Compare(original, await connector.GetSchemaAsync(table.Table, CancellationToken.None));
        Assert.Equal(SchemaChangeKind.Breaking, dropped.Kind);
        Assert.Equal("note", Assert.Single(dropped.Dropped).Name, ignoreCase: true);

        await Bed.RetypeQtyAsync(table);
        var retyped = SchemaComparer.Compare(original, await connector.GetSchemaAsync(table.Table, CancellationToken.None));
        Assert.Equal(SchemaChangeKind.Breaking, retyped.Kind);
        Assert.Contains(retyped.Retyped, r => r.Before.Name.Equals("qty", StringComparison.OrdinalIgnoreCase));
    }
}
