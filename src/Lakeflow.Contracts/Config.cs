namespace Lakeflow.Contracts;

public enum Division { Grain, Feed, Agronomy, Erp, Other }

public enum SourceEngine { SqlServer, Db2i, MySql, Postgres, Access, Files }

public enum CaptureMethod
{
    /// <summary>SQL Server Change Tracking (default for SQL Server once the vendor approves enabling it).</summary>
    ChangeTracking,
    /// <summary>SQL Server CDC, where the vendor allows it.</summary>
    Cdc,
    /// <summary>Poll a rowversion/timestamp column (SQL Server fallback; nothing to enable on the source).</summary>
    RowVersion,
    /// <summary>Poll a date/time watermark column with a look-back (generic fallback).</summary>
    TimestampPolling,
    /// <summary>Db2 for i journal reading (DISPLAY_JOURNAL).</summary>
    Journal,
    /// <summary>MySQL row-format binlog.</summary>
    Binlog,
    /// <summary>PostgreSQL logical replication (pgoutput).</summary>
    LogicalReplication,
    /// <summary>Poll every row by key and diff hashes (Access).</summary>
    KeyHashDiff,
    /// <summary>Folder watch with file hash and append offsets.</summary>
    FileWatch,
}

public enum AuthMethod { SqlLogin, Windows }

/// <summary>Everything an agent needs, pulled from the control API every 60 s and cached locally.</summary>
public sealed record AgentConfig
{
    public required string AgentId { get; init; }
    public required string SiteId { get; init; }
    /// <summary>Increases with every audited config change.</summary>
    public required long ConfigVersion { get; init; }
    /// <summary>Version the console wants this agent on; null keeps the current one.</summary>
    public string? DesiredAgentVersion { get; init; }
    public MaintenanceWindow? MaintenanceWindow { get; init; }
    public required EventHubTarget EventHub { get; init; }
    public BufferSettings Buffer { get; init; } = new();
    /// <summary>The site's time zone, for the maintenance window and the nightly delete reconciliation.</summary>
    public string SiteTimeZone { get; init; } = "America/New_York";
    /// <summary>Local time each night when polling tables are checked for deleted keys.</summary>
    public TimeOnly DeleteReconcileAt { get; init; } = new(2, 30);
    public int ConfigPollSeconds { get; init; } = 60;
    public int HeartbeatSeconds { get; init; } = 30;
    public IReadOnlyList<SourceConfig> Sources { get; init; } = [];
    /// <summary>Commands queued since the agent last acknowledged; delivered on the config poll.</summary>
    public IReadOnlyList<AgentCommand> Commands { get; init; } = [];
}

/// <summary>Daily window, in <see cref="AgentConfig.SiteTimeZone"/>, when the agent may self-update.</summary>
public sealed record MaintenanceWindow(TimeOnly Start, TimeOnly End);

/// <summary>
/// Where an agent sends change batches. The SAS token is Send-only, scoped to this hub and this agent,
/// and rotated by the control API every 24 hours; the agent never stores it unprotected.
/// </summary>
public sealed record EventHubTarget
{
    /// <summary>e.g. <c>lakeflow-prod.servicebus.windows.net</c>.</summary>
    public required string FullyQualifiedNamespace { get; init; }
    public required string HubName { get; init; }
    public required string SasToken { get; init; }
    public required DateTimeOffset SasExpiresUtc { get; init; }

    public override string ToString() => $"{FullyQualifiedNamespace}/{HubName} (SAS expires {SasExpiresUtc:u})";
}

public sealed record BufferSettings
{
    public const long DefaultCapBytes = 10L * 1024 * 1024 * 1024;

    public long CapBytes { get; init; } = DefaultCapBytes;
    public int AlertPercent { get; init; } = 80;
    /// <summary>Most envelopes sent in one Event Hubs batch (also bounded by the 1 MB batch size).</summary>
    public int SendBatchMaxEvents { get; init; } = 500;
}

/// <summary>One database (or folder) at a site.</summary>
public sealed record SourceConfig
{
    /// <summary>Unique per site, e.g. <c>compuweigh</c>.</summary>
    public required string SourceId { get; init; }
    /// <summary>The site system, shared across sites; names the bronze/silver tables, e.g. <c>compuweigh</c>.</summary>
    public required string SourceSystem { get; init; }
    public required Division Division { get; init; }
    public required SourceEngine Engine { get; init; }
    public required ConnectionSettings Connection { get; init; }
    public bool Paused { get; init; }
    /// <summary>Most rows per second read from this source, so site systems (scales especially) are never slowed.</summary>
    public int MaxRowsPerSecond { get; init; } = 2000;
    public IReadOnlyList<TableConfig> Tables { get; init; } = [];
}

/// <summary>
/// Non-secret connection details. The password is never part of config: <see cref="CredentialName"/> names a
/// credential held on the agent, encrypted with DPAPI (machine scope).
/// </summary>
public sealed record ConnectionSettings
{
    /// <summary>Host name or IP; for files, the folder or share path.</summary>
    public required string Host { get; init; }
    public int? Port { get; init; }
    public string? Instance { get; init; }
    public string? Database { get; init; }
    public AuthMethod Auth { get; init; } = AuthMethod.SqlLogin;
    public string? UserName { get; init; }
    public string? CredentialName { get; init; }
    public int ConnectTimeoutSeconds { get; init; } = 5;
    public int CommandTimeoutSeconds { get; init; } = 15;
    public bool TrustServerCertificate { get; init; }
}

public sealed record TableConfig
{
    public required TableRef Table { get; init; }
    public required CaptureMethod Method { get; init; }
    /// <summary>Overrides the key for tables without a primary key; otherwise the source key is used.</summary>
    public IReadOnlyList<string>? KeyColumns { get; init; }
    /// <summary>Column polled by <see cref="CaptureMethod.TimestampPolling"/> or <see cref="CaptureMethod.RowVersion"/>.</summary>
    public string? WatermarkColumn { get; init; }
    /// <summary>Re-read this far behind the watermark to catch late commits (timestamp polling).</summary>
    public int LookbackSeconds { get; init; } = 300;
    public int PollIntervalSeconds { get; init; } = 30;
    public int LagWarningMinutes { get; init; } = 15;
    public int LagCriticalMinutes { get; init; } = 60;
    public bool Paused { get; init; }
}
