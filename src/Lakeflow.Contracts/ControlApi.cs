namespace Lakeflow.Contracts;

// Shapes exchanged with the control API: agent enrollment, and what the console reads and writes.

public sealed record EnrollRequest(string Token, string CsrPem, string Hostname, string AgentVersion);

/// <param name="SiteId">The site an agent installed at a site serves; null for a central collector.</param>
public sealed record EnrollResponse(string AgentId, string? SiteId, string CertificatePem, string CaCertificatePem);

/// <summary>
/// A one-time token for a central collector (no <paramref name="SiteId"/>; it serves the sites assigned to it), or for
/// an agent installed at one site the centre cannot reach.
/// </summary>
public sealed record EnrollmentTokenRequest(string? SiteId, string? Name);

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
    /// <summary>The collector that reads this site's sources.</summary>
    public string? CollectorId { get; init; }
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

/// <summary>
/// A source password for the site's collector: sealed in the browser with <see cref="SealedSecret.Seal"/> (HTTPS), or
/// as <paramref name="Password"/>, which the API seals to the collector at once and never stores (a plain-HTTP page has
/// no WebCrypto).
/// </summary>
public sealed record CredentialRequest(string AgentId, string? Sealed, string? Password = null);

/// <param name="InstallCommand">What to run on the collector's host (or the site PC).</param>
/// <param name="ComposeFile">For a central collector: a compose file with everything filled in; save it as compose.yaml and run <c>docker compose up -d</c>.</param>
public sealed record EnrollmentTokenResponse(string Token, DateTimeOffset ExpiresUtc, string InstallCommand, string? ComposeFile = null);

/// <summary>A collector: central (serving the sites assigned to it) or installed at one site.</summary>
public sealed record AgentDto
{
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    /// <summary>Set for an agent installed at a site; null for a central collector.</summary>
    public string? SiteId { get; init; }
    public IReadOnlyList<string> Sites { get; init; } = [];
    public required string Hostname { get; init; }
    public string? AgentVersion { get; init; }
    public required DateTimeOffset EnrolledUtc { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
    public required DateTimeOffset CertificateExpiresUtc { get; init; }
    public required string CertificateThumbprint { get; init; }
    /// <summary>PEM certificate.</summary>
    public required string CertificatePem { get; init; }
    /// <summary>The certificate's RSA public key (SubjectPublicKeyInfo, base64): what the console seals passwords to.</summary>
    public required string PublicKeySpki { get; init; }
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
    public required string SiteId { get; init; }
    public required string AgentId { get; init; }
    public required CommandType Type { get; init; }
    public string? SourceId { get; init; }
    public string? Table { get; init; }
    public required string IssuedBy { get; init; }
    public required DateTimeOffset IssuedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public string? Result { get; init; }
    public bool? Succeeded { get; init; }
    /// <summary>Structured result: <see cref="ConnectionTestResult"/> or <see cref="SchemaBrowseResult"/> for the wizard commands.</summary>
    public System.Text.Json.Nodes.JsonObject? Details { get; init; }
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

public sealed record AgentEventDto(Guid EventId, string AgentId, string? SiteId, AgentEvent Event, DateTimeOffset ReceivedUtc);

/// <summary>Validation problems, returned with 400.</summary>
public sealed record ProblemList(IReadOnlyList<string> Errors);

/// <summary>A nightly source-versus-silver check, as the Fabric notebook recorded it.</summary>
public sealed record ReconciliationResult
{
    public required DateTimeOffset CheckedUtc { get; init; }
    public required string SiteId { get; init; }
    public required string SourceId { get; init; }
    public required string SourceSystem { get; init; }
    public required string Table { get; init; }
    public required string SilverTable { get; init; }
    public long AsOfSeq { get; init; }
    public long SourceRows { get; init; }
    public long SilverRows { get; init; }
    public string? SourceChecksum { get; init; }
    public string? SilverChecksum { get; init; }
    public long RowDiff { get; init; }
    public double MismatchPercent { get; init; }
    /// <summary><c>ok</c>, <c>within_tolerance</c> (0.1% or less) or <c>mismatch</c>.</summary>
    public required string Status { get; init; }
    /// <summary>Where silver was checked: <c>fabric</c> or <c>snowflake</c>.</summary>
    public string Destination { get; init; } = "fabric";
}
