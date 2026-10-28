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

    /// <summary>The marker that ends a snapshot; see <see cref="ChangeOp.SnapshotComplete"/>.</summary>
    public static RowChange SnapshotCompleted(long snapshotStartSeq) =>
        new(ChangeOp.SnapshotComplete, new JsonObject(), null, new JsonObject { [SnapshotStartSeq] = snapshotStartSeq });

    public ChangeEnvelope ToEnvelope(string siteId, string sourceId, string table, long seq, DateTimeOffset captureTs) => new()
    {
        SiteId = siteId,
        SourceId = sourceId,
        Table = table,
        Op = Op,
        Key = (JsonObject)Key.DeepClone(),
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
