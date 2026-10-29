using System.Text.Json.Nodes;

namespace Lakeflow.Contracts;

public enum CommandType
{
    ResnapshotTable, PauseTable, ResumeTable, PauseSource, ResumeSource, Restart, CollectLogs,
    /// <summary>Connect to the source and report what the login can do (setup wizard). Result details: <see cref="ConnectionTestResult"/>.</summary>
    TestConnection,
    /// <summary>List the tables the login can read, with what each supports (setup wizard). Result details: <see cref="SchemaBrowseResult"/>.</summary>
    BrowseSchema,
}

/// <summary>Queued by the console, delivered on the agent's next config poll (sites never accept inbound connections).</summary>
public sealed record AgentCommand
{
    public required Guid CommandId { get; init; }
    public required CommandType Type { get; init; }
    public string? SourceId { get; init; }
    /// <summary>Qualified table name for table commands.</summary>
    public string? Table { get; init; }
    public required DateTimeOffset IssuedUtc { get; init; }
    public required string IssuedBy { get; init; }
}

public enum TableState { Snapshotting, Streaming, Paused, Blocked, Failing }

public sealed record TableStatus
{
    public required string SourceId { get; init; }
    public required string Table { get; init; }
    public required TableState State { get; init; }
    public required CaptureMethod Method { get; init; }
    /// <summary>Now minus the capture time of the oldest change not yet acknowledged by Event Hubs; 0 when caught up.</summary>
    public double LagSeconds { get; init; }
    public double RowsPerMinute { get; init; }
    public DateTimeOffset? LastCaptureUtc { get; init; }
    public string? Checkpoint { get; init; }
    public string? LastError { get; init; }
}

public sealed record SourceStatus
{
    public required string SourceId { get; init; }
    public bool Connected { get; init; }
    public int ConsecutiveFailures { get; init; }
    public string? LastError { get; init; }
}

/// <summary>Sent every 30 s.</summary>
public sealed record Heartbeat
{
    public required string AgentId { get; init; }
    public required string SiteId { get; init; }
    public required string AgentVersion { get; init; }
    public required DateTimeOffset SentUtc { get; init; }
    public required long ConfigVersion { get; init; }
    public long BufferBytes { get; init; }
    public long BufferCapBytes { get; init; }
    public long BufferedChanges { get; init; }
    public DateTimeOffset? OldestBufferedUtc { get; init; }
    public bool EventHubReachable { get; init; }
    public IReadOnlyList<SourceStatus> Sources { get; init; } = [];
    public IReadOnlyList<TableStatus> Tables { get; init; } = [];
    public string? LastError { get; init; }

    public double BufferPercent => BufferCapBytes <= 0 ? 0 : 100.0 * BufferBytes / BufferCapBytes;
}

public enum EventSeverity { Info, Warning, Error, Critical }

public enum AgentEventKind { Error, Audit, CommandResult, SchemaChange, Snapshot, Update, CredentialStored }

/// <summary>Error and audit events posted to <c>POST /agents/{id}/events</c>.</summary>
public sealed record AgentEvent
{
    public required Guid EventId { get; init; }
    public required DateTimeOffset OccurredUtc { get; init; }
    public required AgentEventKind Kind { get; init; }
    public required EventSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? SourceId { get; init; }
    public string? Table { get; init; }
    public Guid? CommandId { get; init; }
    public JsonObject? Details { get; init; }
}
