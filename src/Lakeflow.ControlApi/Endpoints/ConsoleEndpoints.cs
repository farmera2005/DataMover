using System.Security.Cryptography;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Endpoints;

/// <summary>
/// What the console calls, signed in with Entra ID. Viewers read; Operators also pause, resume and resnapshot;
/// Admins do everything else. Every write is audited by <see cref="LakeflowDb"/>.
/// </summary>
public static class ConsoleEndpoints
{
    private static readonly TimeSpan EnrollmentTokenLifetime = TimeSpan.FromHours(24);

    public static void MapConsoleEndpoints(this IEndpointRouteBuilder app, string publicBaseUrl)
    {
        var read = app.MapGroup("").RequireAuthorization(Policies.Viewer);
        var admin = app.MapGroup("").RequireAuthorization(Policies.Admin);
        var operate = app.MapGroup("").RequireAuthorization(Policies.Operator);

        read.MapGet("/status", StatusAsync);
        read.MapGet("/reconciliation", async (string? siteId, IReconciliationSource source, CancellationToken ct) =>
            await source.LatestAsync(siteId, ct));
        read.MapGet("/sites", async (LakeflowDb db, CancellationToken ct) =>
            (await db.Sites.OrderBy(s => s.SiteId).ToListAsync(ct)).Select(ToDto));
        read.MapGet("/sites/{siteId}", async Task<Results<Ok<SiteDto>, NotFound>> (string siteId, LakeflowDb db, CancellationToken ct) =>
            await db.Sites.FindAsync([siteId], ct) is { } site ? TypedResults.Ok(ToDto(site)) : TypedResults.NotFound());
        admin.MapPut("/sites/{siteId}", PutSiteAsync);
        admin.MapDelete("/sites/{siteId}", DeleteSiteAsync);

        read.MapGet("/sites/{siteId}/sources", async (string siteId, LakeflowDb db, CancellationToken ct) =>
        {
            var pending = await db.SealedCredentials.Where(c => c.SiteId == siteId).Select(c => c.SourceId).ToListAsync(ct);
            return (await db.Sources.Where(s => s.SiteId == siteId).OrderBy(s => s.SourceId).ToListAsync(ct))
                .Select(s => ToDto(s, pending.Contains(s.SourceId)));
        });
        admin.MapPut("/sites/{siteId}/sources/{sourceId}", PutSourceAsync);
        admin.MapDelete("/sites/{siteId}/sources/{sourceId}", async Task<Results<NoContent, NotFound>> (string siteId, string sourceId, LakeflowDb db, CancellationToken ct) =>
        {
            if (await db.Sources.FindAsync([siteId, sourceId], ct) is not { } source) return TypedResults.NotFound();
            db.Sources.Remove(source);
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        });
        admin.MapPut("/sites/{siteId}/sources/{sourceId}/credential", PutCredentialAsync);

        read.MapGet("/sites/{siteId}/sources/{sourceId}/tables", async (string siteId, string sourceId, LakeflowDb db, CancellationToken ct) =>
            (await db.Tables.Where(t => t.SiteId == siteId && t.SourceId == sourceId).OrderBy(t => t.TableName).ToListAsync(ct)).Select(ToDto));
        admin.MapPut("/sites/{siteId}/sources/{sourceId}/tables/{table}", PutTableAsync);
        admin.MapDelete("/sites/{siteId}/sources/{sourceId}/tables/{table}", async Task<Results<NoContent, NotFound>> (string siteId, string sourceId, string table, LakeflowDb db, CancellationToken ct) =>
        {
            if (await db.Tables.FindAsync([siteId, sourceId, table], ct) is not { } row) return TypedResults.NotFound();
            db.Tables.Remove(row);
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        });

        admin.MapPost("/sites/{siteId}/enrollment-tokens", async Task<Results<Ok<EnrollmentTokenResponse>, NotFound>> (
            string siteId, LakeflowDb db, IActor actor, TimeProvider time, CancellationToken ct) =>
        {
            if (await db.Sites.FindAsync([siteId], ct) is null) return TypedResults.NotFound();
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var now = time.GetUtcNow();
            db.EnrollmentTokens.Add(new EnrollmentToken
            {
                Id = Guid.NewGuid(), SiteId = siteId, TokenHash = AgentEndpoints.HashToken(token), CreatedUtc = now,
                CreatedBy = actor.Name, ExpiresUtc = now + EnrollmentTokenLifetime,
            });
            await db.SaveChangesAsync(ct);
            var install = $"msiexec /i LakeflowAgent.msi /qn CONTROL_API=\"{publicBaseUrl}\" ENROLLMENT_TOKEN=\"{token}\"";
            return TypedResults.Ok(new EnrollmentTokenResponse(token, now + EnrollmentTokenLifetime, install));
        });

        read.MapGet("/agents", async (LakeflowDb db, CancellationToken ct) =>
            (await db.Agents.OrderBy(a => a.SiteId).ThenByDescending(a => a.EnrolledUtc).ToListAsync(ct)).Select(ToDto));
        read.MapGet("/agents/{agentId}", async Task<Results<Ok<AgentDto>, NotFound>> (string agentId, LakeflowDb db, CancellationToken ct) =>
            await db.Agents.FindAsync([agentId], ct) is { } agent ? TypedResults.Ok(ToDto(agent)) : TypedResults.NotFound());
        admin.MapPost("/agents/{agentId}/revoke", async Task<Results<NoContent, NotFound>> (string agentId, LakeflowDb db, IActor actor, TimeProvider time, CancellationToken ct) =>
        {
            if (await db.Agents.FindAsync([agentId], ct) is not { } agent) return TypedResults.NotFound();
            agent.RevokedUtc ??= time.GetUtcNow();
            agent.RevokedBy ??= actor.Name;
            db.SealedCredentials.RemoveRange(db.SealedCredentials.Where(c => c.AgentId == agentId));
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        });

        operate.MapPost("/agents/{agentId}/commands", PostCommandAsync);
        read.MapGet("/agents/{agentId}/commands/{commandId:guid}", async Task<Results<Ok<CommandDto>, NotFound>> (string agentId, Guid commandId, LakeflowDb db, CancellationToken ct) =>
            await db.Commands.FirstOrDefaultAsync(c => c.AgentId == agentId && c.CommandId == commandId, ct) is { } command
                ? TypedResults.Ok(ToDto(command))
                : TypedResults.NotFound());
        read.MapGet("/agents/{agentId}/commands", async (string agentId, LakeflowDb db, CancellationToken ct) =>
            (await db.Commands.Where(c => c.AgentId == agentId).OrderByDescending(c => c.IssuedUtc).Take(200).ToListAsync(ct)).Select(ToDto));

        read.MapGet("/events", async (string? siteId, int? take, LakeflowDb db, CancellationToken ct) =>
        {
            var query = db.AgentEvents.AsQueryable();
            if (siteId is not null) query = query.Where(e => e.SiteId == siteId);
            var rows = await query.OrderByDescending(e => e.ReceivedUtc).Take(Math.Clamp(take ?? 100, 1, 1000)).ToListAsync(ct);
            return rows.Select(r => new AgentEventDto(r.EventId, r.AgentId, r.SiteId, EnvelopeJson.Deserialize<AgentEvent>(r.Json), r.ReceivedUtc));
        });
        read.MapGet("/audit", async (DateTimeOffset? before, int? take, LakeflowDb db, CancellationToken ct) =>
        {
            var query = db.AuditEntries.AsQueryable();
            if (before is not null) query = query.Where(a => a.AtUtc < before);
            return (await query.OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 100, 1, 1000)).ToListAsync(ct))
                .Select(a => new AuditEntryDto(a.Id, a.AtUtc, a.Actor, a.Action, a.Entity, a.Before, a.After));
        });
        admin.MapGet("/logs/{id:guid}", async Task<Results<FileContentHttpResult, NotFound>> (Guid id, LakeflowDb db, CancellationToken ct) =>
            await db.LogBundles.FindAsync([id], ct) is { } bundle
                ? TypedResults.File(bundle.Zip, "application/zip", $"{bundle.AgentId}-{bundle.ReceivedUtc:yyyyMMdd-HHmmss}.zip")
                : TypedResults.NotFound());
    }

    private static async Task<Ok<FleetStatus>> StatusAsync(LakeflowDb db, TimeProvider time, IReconciliationSource reconciliation,
        ILogger<FleetStatus> log, CancellationToken ct)
    {
        IReadOnlyList<ReconciliationResult> checks = [];
        try
        {
            checks = await reconciliation.LatestAsync(null, ct);
        }
        catch (SqlException ex)
        {
            // The fleet view must not go down with the lakehouse.
            log.LogWarning("Reconciliation results unavailable: {Error}", ex.Message);
        }
        var now = time.GetUtcNow();
        var sites = await db.Sites.OrderBy(s => s.Name).ToListAsync(ct);
        var agents = await db.Agents.Where(a => a.RevokedUtc == null).ToListAsync(ct);
        var tables = await db.Tables.ToListAsync(ct);
        var result = new List<SiteStatus>();
        foreach (var site in sites)
        {
            var agent = agents.Where(a => a.SiteId == site.SiteId).MaxBy(a => a.EnrolledUtc);
            var heartbeat = agent?.LastHeartbeatJson is { } json ? EnvelopeJson.Deserialize<Heartbeat>(json) : null;
            var (health, reasons) = FleetHealth.Evaluate(agent is not null, heartbeat, agent?.LastHeartbeatUtc, now, (sourceId, table) =>
                tables.FirstOrDefault(t => t.SiteId == site.SiteId && t.SourceId == sourceId && t.TableName == table) is { } t
                    ? new FleetHealth.TableThresholds(t.LagWarningMinutes, t.LagCriticalMinutes)
                    : new FleetHealth.TableThresholds(15, 60),
                checks.Where(c => c.SiteId == site.SiteId));
            result.Add(new SiteStatus
            {
                SiteId = site.SiteId, Name = site.Name, Latitude = site.Latitude, Longitude = site.Longitude,
                Health = health, Reasons = reasons, AgentId = agent?.AgentId, AgentVersion = agent?.AgentVersion,
                LastHeartbeatUtc = agent?.LastHeartbeatUtc,
                WorstLagSeconds = heartbeat?.Tables.Select(t => t.LagSeconds).DefaultIfEmpty(0).Max(),
                BufferBytes = heartbeat?.BufferBytes, BufferPercent = heartbeat?.BufferPercent, Heartbeat = heartbeat,
            });
        }
        return TypedResults.Ok(new FleetStatus(now, result.Count(s => s.Health == Health.Green), result.Count(s => s.Health == Health.Amber),
            result.Count(s => s.Health == Health.Red), result));
    }

    private static async Task<Results<Ok<SiteDto>, ValidationProblem>> PutSiteAsync(string siteId, SiteDto dto, LakeflowDb db, CancellationToken ct)
    {
        var site = await db.Sites.FindAsync([siteId], ct);
        if (site is null) db.Sites.Add(site = new Site { SiteId = siteId, Name = dto.Name });
        site.Name = dto.Name;
        site.TimeZone = dto.TimeZone;
        site.Latitude = dto.Latitude;
        site.Longitude = dto.Longitude;
        site.MaintenanceStart = dto.MaintenanceStart;
        site.MaintenanceEnd = dto.MaintenanceEnd;
        site.DesiredAgentVersion = dto.DesiredAgentVersion;
        site.BufferCapBytes = dto.BufferCapBytes;

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("name is required.");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(dto.TimeZone, out _)) errors.Add($"time_zone '{dto.TimeZone}' is not a known time zone.");
        if ((dto.MaintenanceStart is null) != (dto.MaintenanceEnd is null)) errors.Add("maintenance_start and maintenance_end go together.");
        errors.AddRange(ConfigBuilder.Validate(site, await new ConfigBuilderSources(db).ForSiteAsync(siteId, ct)));
        if (errors.Count > 0) return Invalid(errors);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(site));
    }

    private static async Task<Results<NoContent, NotFound, Conflict<ProblemList>>> DeleteSiteAsync(string siteId, LakeflowDb db, CancellationToken ct)
    {
        if (await db.Sites.FindAsync([siteId], ct) is not { } site) return TypedResults.NotFound();
        if (await db.Agents.AnyAsync(a => a.SiteId == siteId, ct))
            return TypedResults.Conflict(new ProblemList(["The site has enrolled agents; revoke them first. Agent history is kept for the audit log."]));
        db.Sites.Remove(site);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<SourceDto>, NotFound, ValidationProblem>> PutSourceAsync(
        string siteId, string sourceId, SourceDto dto, LakeflowDb db, CancellationToken ct)
    {
        if (await db.Sites.FindAsync([siteId], ct) is not { } site) return TypedResults.NotFound();
        if (dto.SourceId != sourceId) return Invalid(["source_id in the body must match the URL."]);
        var source = await db.Sources.FindAsync([siteId, sourceId], ct);
        if (source is null) db.Sources.Add(source = new Source { SiteId = siteId, SourceId = sourceId, SourceSystem = dto.SourceSystem, Host = dto.Connection.Host });
        source.SourceSystem = dto.SourceSystem;
        source.Division = dto.Division;
        source.Engine = dto.Engine;
        source.Paused = dto.Paused;
        source.MaxRowsPerSecond = dto.MaxRowsPerSecond;
        var c = dto.Connection;
        source.Host = c.Host;
        source.Port = c.Port;
        source.Instance = c.Instance;
        source.DatabaseName = c.Database;
        source.Auth = c.Auth;
        source.UserName = c.UserName;
        source.CredentialName = c.CredentialName;
        source.ConnectTimeoutSeconds = c.ConnectTimeoutSeconds;
        source.CommandTimeoutSeconds = c.CommandTimeoutSeconds;
        source.TrustServerCertificate = c.TrustServerCertificate;

        var errors = ConfigBuilder.Validate(site, await new ConfigBuilderSources(db).ForSiteAsync(siteId, ct));
        if (errors.Count > 0) return Invalid(errors);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(source, false));
    }

    private static async Task<Results<Ok<TableDto>, NotFound, ValidationProblem>> PutTableAsync(
        string siteId, string sourceId, string table, TableDto dto, LakeflowDb db, CancellationToken ct)
    {
        if (await db.Sites.FindAsync([siteId], ct) is not { } site || await db.Sources.FindAsync([siteId, sourceId], ct) is null)
            return TypedResults.NotFound();
        if (!string.Equals(dto.Table, table, StringComparison.Ordinal)) return Invalid(["table in the body must match the URL."]);
        var row = await db.Tables.FindAsync([siteId, sourceId, table], ct);
        if (row is null) db.Tables.Add(row = new CapturedTable { SiteId = siteId, SourceId = sourceId, TableName = table });
        row.Method = dto.Method;
        row.KeyColumns = dto.KeyColumns is null ? null : string.Join(",", dto.KeyColumns);
        row.WatermarkColumn = dto.WatermarkColumn;
        row.LookbackSeconds = dto.LookbackSeconds;
        row.PollIntervalSeconds = dto.PollIntervalSeconds;
        row.LagWarningMinutes = dto.LagWarningMinutes;
        row.LagCriticalMinutes = dto.LagCriticalMinutes;
        row.Paused = dto.Paused;

        var errors = ConfigBuilder.Validate(site, await new ConfigBuilderSources(db).ForSiteAsync(siteId, ct));
        if (errors.Count > 0) return Invalid(errors);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(row));
    }

    /// <summary>
    /// Stores a password the console sealed to the site's current agent certificate. The API cannot open it, refuses
    /// anything that is not a sealed value, and refuses values sealed to another certificate.
    /// </summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> PutCredentialAsync(
        string siteId, string sourceId, CredentialRequest request, LakeflowDb db, IActor actor, TimeProvider time, CancellationToken ct)
    {
        if (await db.Sources.FindAsync([siteId, sourceId], ct) is not { } source) return TypedResults.NotFound();
        if (string.IsNullOrEmpty(source.CredentialName)) return Invalid(["The source has no credential_name to fill."]);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.AgentId == request.AgentId && a.SiteId == siteId && a.RevokedUtc == null, ct);
        if (agent is null) return Invalid(["The agent is not this site's active agent."]);
        if (!SealedSecret.IsWellFormed(request.Sealed)) return Invalid(["The credential must be sealed to the agent's certificate; plain passwords are refused."]);
        if (!string.Equals(SealedSecret.KeyId(request.Sealed), agent.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            return Invalid(["The credential was sealed to a different certificate; reload the agent and seal again."]);

        db.SealedCredentials.RemoveRange(db.SealedCredentials.Where(c => c.AgentId == agent.AgentId && c.SourceId == sourceId));
        db.SealedCredentials.Add(new SealedCredentialRow
        {
            Id = Guid.NewGuid(), AgentId = agent.AgentId, SiteId = siteId, SourceId = sourceId, CredentialName = source.CredentialName,
            Sealed = request.Sealed, CreatedUtc = time.GetUtcNow(), CreatedBy = actor.Name,
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CommandDto>, NotFound, ForbidHttpResult, ValidationProblem>> PostCommandAsync(
        string agentId, CommandRequest request, HttpContext http, LakeflowDb db, IActor actor, TimeProvider time, CancellationToken ct)
    {
        if (await db.Agents.FindAsync([agentId], ct) is not { } agent || agent.RevokedUtc is not null) return TypedResults.NotFound();
        // Operators pause, resume and resnapshot; restarting, pulling logs and source setup are for Admins.
        if (request.Type is CommandType.Restart or CommandType.CollectLogs or CommandType.TestConnection or CommandType.BrowseSchema
            && !http.User.IsInRole(Roles.Admin)) return TypedResults.Forbid();
        var command = new AgentCommand
        {
            CommandId = Guid.NewGuid(), Type = request.Type, SourceId = request.SourceId, Table = request.Table,
            IssuedUtc = time.GetUtcNow(), IssuedBy = actor.Name,
        };
        var errors = ConfigValidator.Validate(new AgentConfig
        {
            AgentId = agentId, SiteId = agent.SiteId, ConfigVersion = 1, EventHubs = [], Commands = [command],
        }).Where(e => e.StartsWith("command")).ToList();
        if (request.SourceId is not null && !await db.Sources.AnyAsync(s => s.SiteId == agent.SiteId && s.SourceId == request.SourceId, ct))
            errors.Add($"Unknown source {request.SourceId}.");
        if (request.Table is not null && !await db.Tables.AnyAsync(t => t.SiteId == agent.SiteId && t.SourceId == request.SourceId && t.TableName == request.Table, ct))
            errors.Add($"Unknown table {request.Table}.");
        if (errors.Count > 0) return Invalid(errors);

        var row = new Command
        {
            CommandId = command.CommandId, AgentId = agentId, Type = command.Type, SourceId = command.SourceId, TableName = command.Table,
            IssuedBy = command.IssuedBy, IssuedUtc = command.IssuedUtc,
        };
        db.Commands.Add(row);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(row));
    }

    private static ValidationProblem Invalid(IEnumerable<string> errors) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["config"] = errors.ToArray() });

    private static SiteDto ToDto(Site s) => new()
    {
        SiteId = s.SiteId, Name = s.Name, TimeZone = s.TimeZone, Latitude = s.Latitude, Longitude = s.Longitude,
        MaintenanceStart = s.MaintenanceStart, MaintenanceEnd = s.MaintenanceEnd, DesiredAgentVersion = s.DesiredAgentVersion,
        BufferCapBytes = s.BufferCapBytes, ConfigVersion = s.ConfigVersion,
    };

    private static SourceDto ToDto(Source s, bool credentialPending) => new()
    {
        SourceId = s.SourceId, SourceSystem = s.SourceSystem, Division = s.Division, Engine = s.Engine, Paused = s.Paused,
        MaxRowsPerSecond = s.MaxRowsPerSecond, Connection = ConfigBuilder.ToConfig(s, []).Connection,
        CredentialPending = credentialPending, CredentialStoredUtc = s.CredentialStoredUtc,
    };

    private static TableDto ToDto(CapturedTable t)
    {
        var config = ConfigBuilder.ToConfig(t);
        return new TableDto
        {
            Table = t.TableName, Method = t.Method, KeyColumns = config.KeyColumns, WatermarkColumn = t.WatermarkColumn,
            LookbackSeconds = t.LookbackSeconds, PollIntervalSeconds = t.PollIntervalSeconds, LagWarningMinutes = t.LagWarningMinutes,
            LagCriticalMinutes = t.LagCriticalMinutes, Paused = t.Paused,
        };
    }

    private static AgentDto ToDto(Agent a) => new()
    {
        AgentId = a.AgentId, SiteId = a.SiteId, Hostname = a.Hostname, AgentVersion = a.AgentVersion, EnrolledUtc = a.EnrolledUtc,
        LastHeartbeatUtc = a.LastHeartbeatUtc, CertificateExpiresUtc = a.CertificateExpiresUtc, CertificateThumbprint = a.CertificateThumbprint,
        CertificatePem = a.CertificatePem, Revoked = a.RevokedUtc is not null,
    };

    private static CommandDto ToDto(Command c) => new()
    {
        CommandId = c.CommandId, AgentId = c.AgentId, Type = c.Type, SourceId = c.SourceId, Table = c.TableName,
        IssuedBy = c.IssuedBy, IssuedUtc = c.IssuedUtc, CompletedUtc = c.CompletedUtc, Result = c.Result, Succeeded = c.Succeeded,
        Details = c.ResultDetails is null ? null : System.Text.Json.Nodes.JsonNode.Parse(c.ResultDetails)?.AsObject(),
    };
}

/// <summary>The site's sources and tables as they stand in this unit of work, pending edits included.</summary>
internal sealed class ConfigBuilderSources(LakeflowDb db)
{
    public async Task<IReadOnlyList<SourceConfig>> ForSiteAsync(string siteId, CancellationToken ct)
    {
        await db.Sources.Where(s => s.SiteId == siteId).LoadAsync(ct);
        await db.Tables.Where(t => t.SiteId == siteId).LoadAsync(ct);
        var live = (EntityState s) => s is not EntityState.Deleted and not EntityState.Detached;
        var sources = db.ChangeTracker.Entries<Source>().Where(e => e.Entity.SiteId == siteId && live(e.State)).Select(e => e.Entity);
        var tables = db.ChangeTracker.Entries<CapturedTable>().Where(e => e.Entity.SiteId == siteId && live(e.State)).Select(e => e.Entity).ToList();
        return sources.OrderBy(s => s.SourceId).Select(s => ConfigBuilder.ToConfig(s, tables.Where(t => t.SourceId == s.SourceId))).ToList();
    }
}
