using System.Text.Json.Nodes;

namespace Lakeflow.Contracts;

public enum CheckpointPhase
{
    Snapshot,
    Incremental,
}

/// <summary>
/// Where capture of one table stands. Stored by the agent per table so a restart resumes exactly where it stopped.
/// </summary>
/// <param name="Method">Capture method that produced this checkpoint; a different method means a resnapshot.</param>
/// <param name="Phase">Snapshotting or streaming changes.</param>
/// <param name="Position">
/// Connector-defined incremental position (Change Tracking version, LSN, binlog position, file offset...).
/// During a snapshot it is the position taken when the snapshot started, so changes made while
/// copying are read afterwards.
/// </param>
/// <param name="SnapshotAfterKey">Last key copied by an unfinished snapshot; null before the first batch.</param>
public sealed record Checkpoint(
    CaptureMethod Method,
    CheckpointPhase Phase,
    string Position,
    JsonObject? SnapshotAfterKey = null)
{
    public bool IsSnapshotComplete => Phase == CheckpointPhase.Incremental;
}
