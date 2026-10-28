namespace Lakeflow.Contracts;

// Shapes exchanged with the control API: agent enrollment, and what the console reads and writes.

public sealed record EnrollRequest(string Token, string CsrPem, string Hostname, string AgentVersion);

public sealed record EnrollResponse(string AgentId, string SiteId, string CertificatePem, string CaCertificatePem);

public sealed record RenewRequest(string CsrPem);

public sealed record SiteDto
{
    public required string SiteId { get; init; }
    public required string Name { get; init; }
    public string TimeZone { get; init; } = "America/New_York";
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public TimeOnly? MaintenanceStart { get; init; }
    public TimeOnly? MaintenanceEnd { get; init; }
    public string? DesiredAgentVersion { get; init; }
    public long BufferCapBytes { get; init; } = BufferSettings.DefaultCapBytes;
    /// <summary>Read only.</summary>
    public long ConfigVersion { get; init; }
}

public sealed record SourceDto
{
    public required string SourceId { get; init; }
    public required string SourceSystem { get; init; }
    public required Division Division { get; init; }
    public required SourceEngine Engine { get; init; }
    public required ConnectionSettings Connection { get; init; }
    public bool Paused { get; init; }
    public int MaxRowsPerSecond { get; init; } = 2000;
    /// <summary>Read only: a sealed credential is waiting for the agent to pick it up.</summary>
    public bool CredentialPending { get; init; }
    /// <summary>Read only: when the agent last confirmed it stored this source's credential.</summary>
    public DateTimeOffset? CredentialStoredUtc { get; init; }
}

/// <summary>A table to capture; the table name is in the route.</summary>
public sealed record TableDto
{
    public required string Table { get; init; }
    public required CaptureMethod Method { get; init; }
    public IReadOnlyList<string>? KeyColumns { get; init; }
    public string? WatermarkColumn { get; init; }
    public int LookbackSeconds { get; init; } = 300;
    public int PollIntervalSeconds { get; init; } = 30;
    public int LagWarningMinutes { get; init; } = 15;
    public int LagCriticalMinutes { get; init; } = 60;
    public bool Paused { get; init; }
}

/// <summary>A source password sealed in the console with <see cref="SealedSecret.Seal"/> for the site's agent.</summary>
public sealed record CredentialRequest(string AgentId, string Sealed);

public sealed record EnrollmentTokenResponse(string Token, DateTimeOffset ExpiresUtc, string InstallCommand);

public sealed record AgentDto
{
    public required string AgentId { get; init; }
    public required string SiteId { get; init; }
    public required string Hostname { get; init; }
    public string? AgentVersion { get; init; }
    public required DateTimeOffset EnrolledUtc { get; init; }
    public DateTimeOffset? LastHeartbeatUtc { get; init; }
    public required DateTimeOffset CertificateExpiresUtc { get; init; }
    public required string CertificateThumbprint { get; init; }
    /// <summary>PEM certificate; the console seals source passwords to its public key.</summary>
    public required string CertificatePem { get; init; }
    public bool Revoked { get; init; }
}

public sealed record CommandRequest
{
    public required CommandType Type { get; init; }
    public string? SourceId { get; init; }
    public string? Table { get; init; }
}

public sealed record CommandDto
{
    public required Guid CommandId { get; init; }
    public required string AgentId { get; init; }
    public required CommandType Type { get; init; }
    public string? SourceId { get; init; }
    public string? Table { get; init; }
    public required string IssuedBy { get; init; }
    public required DateTimeOffset IssuedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public string? Result { get; init; }
}

public enum Health { Green, Amber, Red }

/// <summary>One row of the fleet overview.</summary>
public sealed record SiteStatus
{
    public required string SiteId { get; init; }
    public required string Name { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public required Health Health { get; init; }
    /// <summary>Why the site is not green, worst first.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public string? AgentId { get; init; }
    public string? AgentVersion { get; init; }
    public DateTimeOffset? LastHeartbeatUtc { get; init; }
    public double? WorstLagSeconds { get; init; }
    public long? BufferBytes { get; init; }
    public double? BufferPercent { get; init; }
    /// <summary>The latest heartbeat, for site and table detail.</summary>
    public Heartbeat? Heartbeat { get; init; }
}

public sealed record FleetStatus(DateTimeOffset AsOfUtc, int Green, int Amber, int Red, IReadOnlyList<SiteStatus> Sites);

public sealed record AuditEntryDto(long Id, DateTimeOffset AtUtc, string Actor, string Action, string Entity, string? Before, string? After);

public sealed record AgentEventDto(Guid EventId, string AgentId, string SiteId, AgentEvent Event, DateTimeOffset ReceivedUtc);

/// <summary>Validation problems, returned with 400.</summary>
public sealed record ProblemList(IReadOnlyList<string> Errors);
