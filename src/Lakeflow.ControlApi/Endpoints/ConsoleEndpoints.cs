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
/// What the console calls, signed in (local account or directory). Viewers read; Operators also pause, resume and resnapshot;
/// Admins do everything else. Every write is audited by <see cref="LakeflowDb"/>.
/// </summary>
public static class ConsoleEndpoints
{
    private static readonly TimeSpan EnrollmentTokenLifetime = TimeSpan.FromHours(24);

    public static void MapConsoleEndpoints(this IEndpointRouteBuilder app)
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

        admin.MapPost("/enrollment-tokens", async Task<Results<Ok<EnrollmentTokenResponse>, NotFound, ValidationProblem>> (
            EnrollmentTokenRequest request, LakeflowDb db, IActor actor, TimeProvider time, SettingsStore settings, ServerOptions options,
            IKeyMaterial keys, CancellationToken ct) =>
        {
            if (request.SiteId is not null && await db.Sites.FindAsync([request.SiteId], ct) is null) return TypedResults.NotFound();
            if (request.SiteId is null && string.IsNullOrWhiteSpace(request.Name)) return Invalid(["A central collector needs a name (e.g. central-1)."]);
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var now = time.GetUtcNow();
            db.EnrollmentTokens.Add(new EnrollmentToken
            {
                Id = Guid.NewGuid(), SiteId = request.SiteId, Name = request.Name?.Trim(), TokenHash = AgentEndpoints.HashToken(token),
                CreatedUtc = now, CreatedBy = actor.Name, ExpiresUtc = now + EnrollmentTokenLifetime,
            });
            await db.SaveChangesAsync(ct);
            var host = (await settings.GetAsync<ServerSetting>(SettingsStore.Server, ct))?.Host ?? "localhost";
            var controlApi = $"https://{host}:{options.PublishedCollectorPort}/";
            var ca = LocalKeyMaterial.Fingerprint(keys.AgentCa);
            if (request.SiteId is not null)
                return TypedResults.Ok(new EnrollmentTokenResponse(token, now + EnrollmentTokenLifetime,
                    $"msiexec /i LakeflowAgent.msi /qn CONTROL_API=\"{controlApi}\" ENROLLMENT_TOKEN=\"{token}\" CA_SHA256=\"{ca}\""));
            return TypedResults.Ok(new EnrollmentTokenResponse(token, now + EnrollmentTokenLifetime,
                "docker compose --env-file lakeflow-collector.env up -d --build", CollectorEnvFile(request.Name!.Trim(), controlApi, token, ca)));
        });

        read.MapGet("/agents", async (LakeflowDb db, CancellationToken ct) =>
        {
            var assignments = await db.Sites.Where(s => s.AgentId != null).Select(s => new { s.SiteId, s.AgentId }).ToListAsync(ct);
            return (await db.Agents.OrderBy(a => a.Name).ThenByDescending(a => a.EnrolledUtc).ToListAsync(ct))
                .Select(a => ToDto(a, assignments.Where(x => x.AgentId == a.AgentId).Select(x => x.SiteId).Order().ToList()));
        });
        read.MapGet("/agents/{agentId}", async Task<Results<Ok<AgentDto>, NotFound>> (string agentId, LakeflowDb db, CancellationToken ct) =>
            await db.Agents.FindAsync([agentId], ct) is { } agent
                ? TypedResults.Ok(ToDto(agent, await db.Sites.Where(s => s.AgentId == agentId).Select(s => s.SiteId).OrderBy(x => x).ToListAsync(ct)))
                : TypedResults.NotFound());
        admin.MapPost("/agents/{agentId}/revoke", async Task<Results<NoContent, NotFound>> (string agentId, LakeflowDb db, IActor actor, TimeProvider time, CancellationToken ct) =>
        {
            if (await db.Agents.FindAsync([agentId], ct) is not { } agent) return TypedResults.NotFound();
            agent.RevokedUtc ??= time.GetUtcNow();
            agent.RevokedBy ??= actor.Name;
            db.SealedCredentials.RemoveRange(db.SealedCredentials.Where(c => c.AgentId == agentId));
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        });

        operate.MapPost("/sites/{siteId}/commands", PostCommandAsync);
        read.MapGet("/sites/{siteId}/commands/{commandId:guid}", async Task<Results<Ok<CommandDto>, NotFound>> (string siteId, Guid commandId, LakeflowDb db, CancellationToken ct) =>
            await db.Commands.FirstOrDefaultAsync(c => c.SiteId == siteId && c.CommandId == commandId, ct) is { } command
                ? TypedResults.Ok(ToDto(command))
                : TypedResults.NotFound());
        read.MapGet("/sites/{siteId}/commands", async (string siteId, LakeflowDb db, CancellationToken ct) =>
            (await db.Commands.Where(c => c.SiteId == siteId).OrderByDescending(c => c.IssuedUtc).Take(200).ToListAsync(ct)).Select(ToDto));

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

    /// <summary>
    /// The settings a collector on another server needs, as an env file for <c>deploy/collector/compose.yaml</c>: it
    /// enrolls on first start and trusts only this stack's CA (by its SHA-256).
    /// </summary>
    public static string CollectorEnvFile(string name, string controlApi, string token, string caSha256) => $"""
        # Lakeflow collector "{name}". Single use, valid 24 hours. In deploy/collector on the collector's server run:
        #   docker compose --env-file lakeflow-collector.env up -d --build
        LAKEFLOW_CONTROL_API={controlApi}
        LAKEFLOW_ENROLLMENT_TOKEN={token}
        LAKEFLOW_CA_SHA256={caSha256}

        """;

    private static async Task<Ok<FleetStatus>> StatusAsync(LakeflowDb db, TimeProvider time, IReconciliationSource reconciliation,
        ConfigBuilder builder, ILogger<FleetStatus> log, CancellationToken ct)
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
        var agents = await db.Agents.ToDictionaryAsync(a => a.AgentId, ct);
        var beats = await db.SiteHeartbeats.ToDictionaryAsync(h => h.SiteId, ct);
        var tables = await db.Tables.ToListAsync(ct);
        var divisions = await db.Sources.Select(s => new { s.SiteId, s.Division }).Distinct().ToListAsync(ct);
        var hubs = await builder.HubsAsync(ct);
        var result = new List<SiteStatus>();
        foreach (var site in sites)
        {
            var agent = site.AgentId is { } id ? agents.GetValueOrDefault(id) : null;
            if (agent?.RevokedUtc is not null) agent = null;
            // Only a heartbeat from the collector now serving the site counts.
            var beat = agent is not null && beats.TryGetValue(site.SiteId, out var b) && b.AgentId == agent.AgentId ? b : null;
            var heartbeat = beat is null ? null : EnvelopeJson.Deserialize<Heartbeat>(beat.Json);
            var (health, reasons) = FleetHealth.Evaluate(agent is not null, heartbeat, beat?.ReceivedUtc, now, (sourceId, table) =>
                tables.FirstOrDefault(t => t.SiteId == site.SiteId && t.SourceId == sourceId && t.TableName == table) is { } t
                    ? new FleetHealth.TableThresholds(t.LagWarningMinutes, t.LagCriticalMinutes)
                    : new FleetHealth.TableThresholds(15, 60),
                checks.Where(c => c.SiteId == site.SiteId));
            var missing = divisions.Where(d => d.SiteId == site.SiteId && !hubs.ContainsKey(d.Division)).Select(d => d.Division).ToList();
            if (missing.Count > 0)
            {
                // Without its Event Hub the site is not sent to its collector at all; say so first.
                health = Health.Red;
                reasons = [$"The Event Hub for {string.Join(", ", missing.Select(m => LakeNames.HubName(m)))} is not set up yet (Configuration, then Destinations).", .. reasons];
            }
            result.Add(new SiteStatus
            {
                SiteId = site.SiteId, Name = site.Name, Latitude = site.Latitude, Longitude = site.Longitude,
                Health = health, Reasons = reasons, AgentId = agent?.AgentId, AgentVersion = agent?.AgentVersion,
                LastHeartbeatUtc = beat?.ReceivedUtc,
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
        if (dto.CollectorId != site.AgentId)
        {
            if (dto.CollectorId is not null && await db.Agents.FirstOrDefaultAsync(a => a.AgentId == dto.CollectorId && a.RevokedUtc == null, ct) is null)
                errors.Add($"Collector {dto.CollectorId} is not an active collector.");
            else
            {
                // Passwords were sealed to the old collector's key; the new one cannot open them. Re-enter them.
                db.SealedCredentials.RemoveRange(db.SealedCredentials.Where(c => c.SiteId == siteId));
                foreach (var source in await db.Sources.Where(x => x.SiteId == siteId).ToListAsync(ct)) source.CredentialStoredUtc = null;
                site.AgentId = dto.CollectorId;
            }
        }
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
            return TypedResults.Conflict(new ProblemList(["The site has its own enrolled agent; that agent's history is kept, so the site cannot be deleted. Unassign or revoke instead."]));
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
    /// Stores a password sealed to the site's current collector certificate: sealed in the browser (the API cannot open
    /// it; values sealed to another certificate are refused), or sealed here at once when the page had no WebCrypto
    /// (plain HTTP). Either way only the sealed value is stored.
    /// </summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> PutCredentialAsync(
        string siteId, string sourceId, CredentialRequest request, LakeflowDb db, IActor actor, TimeProvider time, CancellationToken ct)
    {
        if (await db.Sources.FindAsync([siteId, sourceId], ct) is not { } source) return TypedResults.NotFound();
        if (string.IsNullOrEmpty(source.CredentialName)) return Invalid(["The source has no credential_name to fill."]);
        var site = await db.Sites.SingleAsync(s => s.SiteId == siteId, ct);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.AgentId == request.AgentId && a.AgentId == site.AgentId && a.RevokedUtc == null, ct);
        if (agent is null) return Invalid(["That is not the collector serving this site; reload the site and try again."]);
        string sealedValue;
        if (!string.IsNullOrEmpty(request.Password) && request.Sealed is null)
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(agent.CertificatePem);
            using var publicKey = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPublicKey(certificate)!;
            sealedValue = SealedSecret.Seal(request.Password, publicKey, agent.CertificateThumbprint);
        }
        else
        {
            if (request.Sealed is null || !SealedSecret.IsWellFormed(request.Sealed)) return Invalid(["Send the password sealed to the collector's certificate, or as password."]);
            if (!string.Equals(SealedSecret.KeyId(request.Sealed), agent.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
                return Invalid(["The credential was sealed to a different certificate; reload the collector and seal again."]);
            sealedValue = request.Sealed;
        }

        db.SealedCredentials.RemoveRange(db.SealedCredentials.Where(c => c.AgentId == agent.AgentId && c.SourceId == sourceId));
        db.SealedCredentials.Add(new SealedCredentialRow
        {
            Id = Guid.NewGuid(), AgentId = agent.AgentId, SiteId = siteId, SourceId = sourceId, CredentialName = source.CredentialName,
            Sealed = sealedValue, CreatedUtc = time.GetUtcNow(), CreatedBy = actor.Name,
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CommandDto>, NotFound, ForbidHttpResult, ValidationProblem>> PostCommandAsync(
        string siteId, CommandRequest request, HttpContext http, LakeflowDb db, IActor actor, TimeProvider time, CancellationToken ct)
    {
        if (await db.Sites.FindAsync([siteId], ct) is not { } site) return TypedResults.NotFound();
        if (site.AgentId is null || await db.Agents.FindAsync([site.AgentId], ct) is not { RevokedUtc: null } agent)
            return Invalid(["No collector is assigned to this site."]);
        var agentId = agent.AgentId;
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
            AgentId = agentId, SiteId = siteId, ConfigVersion = 1, EventHubs = [], Commands = [command],
        }).Where(e => e.StartsWith("command")).ToList();
        if (request.SourceId is not null && !await db.Sources.AnyAsync(s => s.SiteId == siteId && s.SourceId == request.SourceId, ct))
            errors.Add($"Unknown source {request.SourceId}.");
        if (request.Table is not null && !await db.Tables.AnyAsync(t => t.SiteId == siteId && t.SourceId == request.SourceId && t.TableName == request.Table, ct))
            errors.Add($"Unknown table {request.Table}.");
        if (errors.Count > 0) return Invalid(errors);

        var row = new Command
        {
            CommandId = command.CommandId, SiteId = siteId, AgentId = agentId, Type = command.Type, SourceId = command.SourceId, TableName = command.Table,
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
        BufferCapBytes = s.BufferCapBytes, CollectorId = s.AgentId, ConfigVersion = s.ConfigVersion,
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

    private static AgentDto ToDto(Agent a, IReadOnlyList<string> sites)
    {
        using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(a.CertificatePem);
        return new AgentDto
        {
            AgentId = a.AgentId, Name = a.Name, SiteId = a.SiteId, Sites = sites, Hostname = a.Hostname, AgentVersion = a.AgentVersion,
            EnrolledUtc = a.EnrolledUtc, LastSeenUtc = a.LastSeenUtc, CertificateExpiresUtc = a.CertificateExpiresUtc,
            CertificateThumbprint = a.CertificateThumbprint, CertificatePem = a.CertificatePem,
            PublicKeySpki = Convert.ToBase64String(certificate.PublicKey.ExportSubjectPublicKeyInfo()), Revoked = a.RevokedUtc is not null,
        };
    }

    private static CommandDto ToDto(Command c) => new()
    {
        CommandId = c.CommandId, SiteId = c.SiteId, AgentId = c.AgentId, Type = c.Type, SourceId = c.SourceId, Table = c.TableName,
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
