using System.Text.Json.Nodes;

namespace Lakeflow.Contracts;

/// <summary>What happened to a row.</summary>
public enum ChangeOp
{
    Insert,
    Update,
    Delete,
    /// <summary>Row copied by an initial snapshot or resnapshot (treated as an upsert downstream).</summary>
    Snapshot,
    /// <summary>
    /// Control marker sent when a snapshot finishes. <c>key</c> is empty and <c>after</c> holds
    /// <c>snapshot_start_seq</c>: every row that still exists was sent with that seq or later, so silver deletes
    /// the site's rows of this table whose latest seq is lower (rows deleted while the table was not tracked).
    /// </summary>
    SnapshotComplete,
    /// <summary>
    /// Control marker sent nightly after a catch-up read. <c>after</c> holds <c>row_count</c> and <c>key_checksum</c>
    /// (see <see cref="KeyChecksum"/>) of the source table; silver compares them with its own rows at this seq.
    /// </summary>
    Reconcile,
}

/// <summary>
/// One row change as it travels from an agent to Event Hubs and lands in bronze.
/// One envelope per row change; serialized with <see cref="EnvelopeJson"/>.
/// </summary>
public sealed record ChangeEnvelope
{
    /// <summary>Bumped only for incompatible changes to the envelope itself.</summary>
    public const int CurrentVersion = 1;

    public int EnvelopeVersion { get; init; } = CurrentVersion;
    public required string SiteId { get; init; }
    public required string SourceId { get; init; }
    /// <summary>The site system, shared across sites (names the bronze and silver tables).</summary>
    public required string SourceSystem { get; init; }
    /// <summary>Source-qualified table name, e.g. <c>dbo.Tickets</c>.</summary>
    public required string Table { get; init; }
    public required ChangeOp Op { get; init; }
    /// <summary>Primary-key column values; always present (empty for control markers).</summary>
    public required JsonObject Key { get; init; }
    /// <summary>
    /// The key as one canonical string (<see cref="ColumnValues.KeyText"/>): what silver matches rows on and
    /// checksums, independent of how the key object is re-encoded on the way.
    /// </summary>
    public required string KeyId { get; init; }
    /// <summary>Row before the change, when the capture method provides it.</summary>
    public JsonObject? Before { get; init; }
    /// <summary>Row after the change; null for deletes.</summary>
    public JsonObject? After { get; init; }
    /// <summary>When the change happened at the source, when the capture method knows it.</summary>
    public DateTimeOffset? SourceTs { get; init; }
    /// <summary>When the agent read the change.</summary>
    public required DateTimeOffset CaptureTs { get; init; }
    /// <summary>Monotonic per site + source + table, never reset (not even by a resnapshot).</summary>
    public required long Seq { get; init; }

    /// <summary>Event Hubs partition key: keeps changes to one table in order.</summary>
    public string PartitionKey => LakeNames.PartitionKey(SiteId, Table);
}
