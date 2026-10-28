using System.Text.Json.Nodes;

namespace Lakeflow.Contracts;

/// <summary>A row change as a connector reads it, before the agent stamps site, sequence and capture time.</summary>
public sealed record RowChange(
    ChangeOp Op,
    JsonObject Key,
    JsonObject? Before,
    JsonObject? After,
    DateTimeOffset? SourceTs = null)
{
    public const string SnapshotStartSeq = "snapshot_start_seq";
    public const string RowCount = "row_count";
    public const string KeyChecksumField = "key_checksum";

    /// <summary>The marker that ends a snapshot; see <see cref="ChangeOp.SnapshotComplete"/>.</summary>
    public static RowChange SnapshotCompleted(long snapshotStartSeq) =>
        new(ChangeOp.SnapshotComplete, new JsonObject(), null, new JsonObject { [SnapshotStartSeq] = snapshotStartSeq });

    /// <summary>The marker carrying the source's nightly count and key checksum; see <see cref="ChangeOp.Reconcile"/>.</summary>
    public static RowChange Reconciled(long rowCount, ulong keyChecksum) =>
        new(ChangeOp.Reconcile, new JsonObject(), null, new JsonObject
        {
            [RowCount] = rowCount,
            // As text: an unsigned 64-bit value does not fit every JSON reader's integer type.
            [KeyChecksumField] = keyChecksum.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

    public ChangeEnvelope ToEnvelope(string siteId, string sourceId, string sourceSystem, string table, long seq, DateTimeOffset captureTs) => new()
    {
        SiteId = siteId,
        SourceId = sourceId,
        SourceSystem = sourceSystem,
        Table = table,
        Op = Op,
        Key = (JsonObject)Key.DeepClone(),
        KeyId = ColumnValues.KeyText(Key),
        Before = (JsonObject?)Before?.DeepClone(),
        After = (JsonObject?)After?.DeepClone(),
        SourceTs = SourceTs,
        CaptureTs = captureTs,
        Seq = seq,
    };
}

/// <summary>
/// Changes read in one call plus the checkpoint to store once they are safely buffered.
/// The agent writes the rows and <see cref="Next"/> in the same local transaction.
/// </summary>
public sealed record ChangeBatch(IReadOnlyList<RowChange> Changes, Checkpoint Next, bool HasMore);
