using System.Text.Json.Nodes;

namespace Lakeflow.Contracts;

/// <summary>What a connector can do; the agent and the contract tests adapt to it.</summary>
/// <param name="NativeDeletes">
/// True when <see cref="ISourceConnector.ReadChangesAsync"/> reports deletes itself (CDC-style methods).
/// When false the connector must also implement <see cref="IKeyReader"/> so deletes are found by key reconciliation.
/// </param>
/// <param name="ProvidesBefore">True when update/delete changes carry the before image.</param>
public sealed record ConnectorCapabilities(bool NativeDeletes, bool ProvidesBefore);

/// <summary>
/// The one interface every source connector implements. Connectors only read: they open read-only
/// connections and never issue writes, DDL or lock-heavy queries against a source.
/// A connector instance serves one source; the agent never runs two calls on it at once.
/// </summary>
public interface ISourceConnector : IAsyncDisposable
{
    SourceEngine Engine { get; }

    ConnectorCapabilities GetCapabilities(TableConfig table);

    /// <summary>Current columns and key of a table.</summary>
    Task<TableSchema> GetSchemaAsync(TableRef table, CancellationToken cancellationToken);

    /// <summary>
    /// Copies the table in key order, in batches of at most <paramref name="batchSize"/> rows, all as
    /// <see cref="ChangeOp.Snapshot"/>. Pass the last stored checkpoint to resume an unfinished snapshot,
    /// or null to start one. The last batch's checkpoint is <see cref="CheckpointPhase.Incremental"/>,
    /// positioned so that changes made while the snapshot ran are returned by <see cref="ReadChangesAsync"/>.
    /// </summary>
    IAsyncEnumerable<ChangeBatch> SnapshotAsync(
        TableConfig table, TableSchema schema, Checkpoint? resumeFrom, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Changes after <paramref name="checkpoint"/>, at most <paramref name="maxRows"/> (a connector may exceed it
    /// slightly to avoid splitting one source transaction). Delivery is at-least-once: a change may be
    /// returned again after a restart, never skipped.
    /// </summary>
    Task<ChangeBatch> ReadChangesAsync(
        TableConfig table, TableSchema schema, Checkpoint checkpoint, int maxRows, CancellationToken cancellationToken);
}

/// <summary>Lists every key of a table, for nightly delete detection on sources without native deletes.</summary>
public interface IKeyReader
{
    IAsyncEnumerable<JsonObject> ReadKeysAsync(TableConfig table, TableSchema schema, CancellationToken cancellationToken);
}

/// <summary>The source no longer supports the stored checkpoint (e.g. Change Tracking retention passed it); resnapshot.</summary>
public sealed class CheckpointExpiredException(string message) : Exception(message);
