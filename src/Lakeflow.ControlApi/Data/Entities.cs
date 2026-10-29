using Lakeflow.Contracts;

namespace Lakeflow.ControlApi.Data;

/// <summary>Marks entities whose changes are written to the audit log.</summary>
public interface IAudited
{
    string AuditName { get; }
}

/// <summary>Entities that are part of a site's agent config: changing one bumps <see cref="Site.ConfigVersion"/>.</summary>
public interface ISiteConfig : IAudited
{
    string SiteId { get; }
}

public sealed class Site : ISiteConfig
{
    public required string SiteId { get; set; }
    public required string Name { get; set; }
    public string TimeZone { get; set; } = "America/New_York";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public TimeOnly? MaintenanceStart { get; set; }
    public TimeOnly? MaintenanceEnd { get; set; }
    public string? DesiredAgentVersion { get; set; }
    public long BufferCapBytes { get; set; } = BufferSettings.DefaultCapBytes;
    /// <summary>The collector that reads this site.</summary>
    public string? AgentId { get; set; }
    public long ConfigVersion { get; set; } = 1;
    public string AuditName => $"site {SiteId}";
}

public sealed class Source : ISiteConfig
{
    public required string SiteId { get; set; }
    public required string SourceId { get; set; }
    public required string SourceSystem { get; set; }
    public Division Division { get; set; }
    public SourceEngine Engine { get; set; }
    public required string Host { get; set; }
    public int? Port { get; set; }
    public string? Instance { get; set; }
    public string? DatabaseName { get; set; }
    public AuthMethod Auth { get; set; }
    public string? UserName { get; set; }
    public string? CredentialName { get; set; }
    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int CommandTimeoutSeconds { get; set; } = 15;
    public bool TrustServerCertificate { get; set; }
    public bool Paused { get; set; }
    public int MaxRowsPerSecond { get; set; } = 2000;
    public DateTimeOffset? CredentialStoredUtc { get; set; }
    public string AuditName => $"source {SiteId}/{SourceId}";
}

public sealed class CapturedTable : ISiteConfig
{
    public required string SiteId { get; set; }
    public required string SourceId { get; set; }
    /// <summary>Qualified name, e.g. <c>dbo.Tickets</c>.</summary>
    public required string TableName { get; set; }
    public CaptureMethod Method { get; set; }
    /// <summary>Comma-separated, or null for the primary key.</summary>
    public string? KeyColumns { get; set; }
    public string? WatermarkColumn { get; set; }
    public int LookbackSeconds { get; set; } = 300;
    public int PollIntervalSeconds { get; set; } = 30;
    public int LagWarningMinutes { get; set; } = 15;
    public int LagCriticalMinutes { get; set; } = 60;
    public bool Paused { get; set; }
    public string AuditName => $"table {SiteId}/{SourceId}/{TableName}";
}

public sealed class EnrollmentToken : IAudited
{
    public Guid Id { get; set; }
    /// <summary>Null for a central collector.</summary>
    public string? SiteId { get; set; }
    public string? Name { get; set; }
    /// <summary>SHA-256 of the token; the token itself is shown once and never stored.</summary>
    public required byte[] TokenHash { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? UsedUtc { get; set; }
    public string? UsedByAgentId { get; set; }
    public string AuditName => SiteId is null ? $"enrollment token for collector {Name}" : $"enrollment token for {SiteId}";
}

/// <summary>A collector: central (serving the sites assigned to it) or installed at one site (<see cref="SiteId"/>).</summary>
public sealed class Agent : IAudited
{
    public required string AgentId { get; set; }
    public required string Name { get; set; }
    public string? SiteId { get; set; }
    public required string Hostname { get; set; }
    public string? AgentVersion { get; set; }
    public DateTimeOffset EnrolledUtc { get; set; }
    public required string CertificateThumbprint { get; set; }
    public required string CertificatePem { get; set; }
    public DateTimeOffset CertificateExpiresUtc { get; set; }
    /// <summary>The certificate replaced by the last renewal, accepted until it expires so a renewal cannot strand the agent.</summary>
    public string? PreviousThumbprint { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    /// <summary>Expiry of the SAS tokens last handed out; tokens are reissued when less than half their life remains.</summary>
    public DateTimeOffset? SasExpiresUtc { get; set; }
    public string AuditName => $"agent {AgentId}";
}

/// <summary>The latest heartbeat for a site, from the collector serving it.</summary>
public sealed class SiteHeartbeat
{
    public required string SiteId { get; set; }
    public required string AgentId { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }
    public required string Json { get; set; }
}

public sealed class SealedCredentialRow : IAudited
{
    public Guid Id { get; set; }
    public required string AgentId { get; set; }
    public required string SiteId { get; set; }
    public required string SourceId { get; set; }
    public required string CredentialName { get; set; }
    /// <summary>Only the agent's private key opens it; deleted once the agent confirms it stored the password.</summary>
    public required string Sealed { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public required string CreatedBy { get; set; }
    public string AuditName => $"credential {SiteId}/{SourceId}";
}

public sealed class Command : IAudited
{
    public Guid CommandId { get; set; }
    public required string SiteId { get; set; }
    public required string AgentId { get; set; }
    public CommandType Type { get; set; }
    public string? SourceId { get; set; }
    public string? TableName { get; set; }
    public required string IssuedBy { get; set; }
    public DateTimeOffset IssuedUtc { get; set; }
    public DateTimeOffset? DeliveredUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? Result { get; set; }
    public bool? Succeeded { get; set; }
    /// <summary>Structured result (connection test, table list), JSON.</summary>
    public string? ResultDetails { get; set; }
    public string AuditName => $"command {Type} for {SiteId}";
}

public sealed class AgentEventRow
{
    public Guid EventId { get; set; }
    public required string AgentId { get; set; }
    public string? SiteId { get; set; }
    public AgentEventKind Kind { get; set; }
    public EventSeverity Severity { get; set; }
    public DateTimeOffset OccurredUtc { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }
    public required string Json { get; set; }
}

public sealed class LogBundle
{
    public Guid Id { get; set; }
    public required string AgentId { get; set; }
    public Guid? CommandId { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }
    public required byte[] Zip { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public required string Actor { get; set; }
    /// <summary>created, updated, deleted.</summary>
    public required string Action { get; set; }
    public required string Entity { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
}
