using System.Security.Cryptography;
using System.Text;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Endpoints;

/// <summary>
/// What collectors call. A collector (central, or installed at one site) only ever connects out to these, and may only
/// report on the sites assigned to it.
/// </summary>
public static class AgentEndpoints
{
    public const long MaxLogBundleBytes = 25L * 1024 * 1024;

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/agents/enroll", EnrollAsync).AllowAnonymous().RequireRateLimiting("enroll").WithMetadata(new CollectorRoute());
        // Public: a new collector downloads it and checks it against the SHA-256 that came with its token.
        app.MapGet("/agents/ca", (IKeyMaterial keys) => Results.Text(keys.AgentCa.ExportCertificatePem(), "application/x-pem-file"))
            .AllowAnonymous().WithMetadata(new CollectorRoute());

        var agent = app.MapGroup("/agents/{agentId}").RequireAuthorization(Policies.Agent).WithMetadata(new CollectorRoute());
        agent.MapPost("/renew", RenewAsync);
        agent.MapGet("/config", ConfigAsync);
        agent.MapPost("/heartbeat", HeartbeatAsync);
        agent.MapPost("/events", EventsAsync);
        agent.MapPost("/logs", LogsAsync).DisableAntiforgery();
    }

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>
    /// Exchanges a one-time enrollment token (24 h, single use) and a CSR for a client certificate. A site token makes an
    /// agent for that site and assigns the site to it (retiring the site's previous site agent); a collector token makes
    /// a central collector, to which sites are then assigned in the console.
    /// </summary>
    private static async Task<Results<Ok<EnrollResponse>, ProblemHttpResult>> EnrollAsync(
        EnrollRequest request, LakeflowDb db, AgentCertificates certificates, TimeProvider time, ILogger<EnrollRequestLog> log, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var hash = HashToken(request.Token ?? "");
        var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.UsedUtc is not null || token.ExpiresUtc <= now)
        {
            log.LogWarning("Enrollment refused from {Hostname}: token unknown, used or expired", request.Hostname);
            return TypedResults.Problem("The enrollment token is not valid. Generate a new one in the console.", statusCode: 401);
        }
        if (string.IsNullOrWhiteSpace(request.Hostname) || request.Hostname.Length > 256)
            return TypedResults.Problem("hostname is required.", statusCode: 400);

        var prefix = token.SiteId ?? "collector";
        var agentId = $"{prefix}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;
        try
        {
            certificate = certificates.Issue(request.CsrPem, agentId);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: 400);
        }

        if (token.SiteId is { } siteId)
        {
            foreach (var previous in await db.Agents.Where(a => a.SiteId == siteId && a.RevokedUtc == null).ToListAsync(ct))
            {
                previous.RevokedUtc = now;
                previous.RevokedBy = $"replaced by {agentId}";
            }
            if (await db.Sites.FindAsync([siteId], ct) is { } site) site.AgentId = agentId;
        }
        db.Agents.Add(new Agent
        {
            AgentId = agentId,
            Name = token.Name ?? token.SiteId ?? agentId,
            SiteId = token.SiteId,
            Hostname = request.Hostname,
            AgentVersion = request.AgentVersion,
            EnrolledUtc = now,
            CertificateThumbprint = certificate.Thumbprint,
            CertificatePem = AgentCertificates.Pem(certificate),
            CertificateExpiresUtc = certificate.NotAfter.ToUniversalTime(),
        });
        token.UsedUtc = now;
        token.UsedByAgentId = agentId;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Collector {AgentId} enrolled ({Scope}) from {Hostname}", agentId, token.SiteId ?? "central", request.Hostname);
        return TypedResults.Ok(new EnrollResponse(agentId, token.SiteId, AgentCertificates.Pem(certificate), AgentCertificates.Pem(certificates.Ca)));
    }

    /// <summary>A new certificate for a new key, before the current one expires. The old one keeps working until it does.</summary>
    private static async Task<Results<Ok<EnrollResponse>, ProblemHttpResult>> RenewAsync(
        string agentId, RenewRequest request, LakeflowDb db, AgentCertificates certificates, CancellationToken ct)
    {
        var agent = await db.Agents.SingleAsync(a => a.AgentId == agentId, ct);
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;
        try
        {
            certificate = certificates.Issue(request.CsrPem, agentId);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: 400);
        }
        agent.PreviousThumbprint = agent.CertificateThumbprint;
        agent.CertificateThumbprint = certificate.Thumbprint;
        agent.CertificatePem = AgentCertificates.Pem(certificate);
        agent.CertificateExpiresUtc = certificate.NotAfter.ToUniversalTime();
        // Credentials sealed to the old key that are still waiting would never open; the console must re-enter them.
        db.SealedCredentials.RemoveRange(db.SealedCredentials.Where(c => c.AgentId == agentId));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new EnrollResponse(agentId, agent.SiteId, AgentCertificates.Pem(certificate), AgentCertificates.Pem(certificates.Ca)));
    }

    private static async Task<Ok<CollectorConfig>> ConfigAsync(string agentId, LakeflowDb db, ConfigBuilder builder, TimeProvider time, CancellationToken ct)
    {
        var agent = await db.Agents.SingleAsync(a => a.AgentId == agentId, ct);
        agent.LastSeenUtc = time.GetUtcNow();
        return TypedResults.Ok(await builder.ForCollectorAsync(agent, ct));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> HeartbeatAsync(
        string agentId, Heartbeat heartbeat, LakeflowDb db, TimeProvider time, CancellationToken ct)
    {
        if (heartbeat.AgentId != agentId) return TypedResults.Problem("agent_id does not match the certificate.", statusCode: 400);
        if (!await db.Sites.AnyAsync(s => s.SiteId == heartbeat.SiteId && s.AgentId == agentId, ct))
            return TypedResults.Problem($"Site {heartbeat.SiteId} is not assigned to this collector.", statusCode: 403);
        var now = time.GetUtcNow();
        var agent = await db.Agents.SingleAsync(a => a.AgentId == agentId, ct);
        agent.LastSeenUtc = now;
        agent.AgentVersion = heartbeat.AgentVersion;
        var row = await db.SiteHeartbeats.FindAsync([heartbeat.SiteId], ct);
        if (row is null) db.SiteHeartbeats.Add(row = new SiteHeartbeat { SiteId = heartbeat.SiteId, AgentId = agentId, Json = "" });
        row.AgentId = agentId;
        row.ReceivedUtc = now;
        row.Json = EnvelopeJson.Serialize(heartbeat);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Stores events (idempotent by event id), completes commands and retires delivered credentials.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> EventsAsync(
        string agentId, List<AgentEvent> events, LakeflowDb db, TimeProvider time, CancellationToken ct)
    {
        var served = await db.Sites.Where(s => s.AgentId == agentId).Select(s => s.SiteId).ToListAsync(ct);
        var foreign = events.FirstOrDefault(e => e.SiteId is not null && !served.Contains(e.SiteId));
        if (foreign is not null) return TypedResults.Problem($"Site {foreign.SiteId} is not assigned to this collector.", statusCode: 403);
        var now = time.GetUtcNow();
        var ids = events.Select(e => e.EventId).ToList();
        var known = await db.AgentEvents.Where(e => ids.Contains(e.EventId)).Select(e => e.EventId).ToListAsync(ct);
        foreach (var e in events.Where(e => !known.Contains(e.EventId)).DistinctBy(e => e.EventId))
        {
            db.AgentEvents.Add(new AgentEventRow
            {
                EventId = e.EventId, AgentId = agentId, SiteId = e.SiteId, Kind = e.Kind, Severity = e.Severity,
                OccurredUtc = e.OccurredUtc, ReceivedUtc = now, Json = EnvelopeJson.Serialize(e),
            });
            if (e is { Kind: AgentEventKind.CommandResult, CommandId: { } commandId }
                && await db.Commands.FirstOrDefaultAsync(c => c.CommandId == commandId && c.AgentId == agentId, ct) is { } command)
            {
                command.CompletedUtc = e.OccurredUtc;
                command.Result = e.Message.Length > 2000 ? e.Message[..2000] : e.Message;
                command.Succeeded = e.Severity == EventSeverity.Info;
                command.ResultDetails = e.Details?.ToJsonString();
            }
            if (e.Kind == AgentEventKind.CredentialStored
                && Guid.TryParse(e.Details?["credential_id"]?.GetValue<string>(), out var credentialId)
                && await db.SealedCredentials.FirstOrDefaultAsync(c => c.Id == credentialId && c.AgentId == agentId, ct) is { } credential)
            {
                db.SealedCredentials.Remove(credential);
                var source = await db.Sources.FindAsync([credential.SiteId, credential.SourceId], ct);
                if (source is not null) source.CredentialStoredUtc = e.OccurredUtc;
            }
        }
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>A zip of the collector's recent logs, sent in answer to a "collect logs" command.</summary>
    private static async Task<Results<Created, ProblemHttpResult>> LogsAsync(
        string agentId, Guid? commandId, HttpRequest request, LakeflowDb db, TimeProvider time, CancellationToken ct)
    {
        if (request.ContentLength is null or > MaxLogBundleBytes)
            return TypedResults.Problem($"Log bundles must declare a length of at most {MaxLogBundleBytes} bytes.", statusCode: 413);
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxLogBundleBytes) return TypedResults.Problem("Log bundle too large.", statusCode: 413);
        var bundle = new LogBundle { Id = Guid.NewGuid(), AgentId = agentId, CommandId = commandId, ReceivedUtc = time.GetUtcNow(), Zip = buffer.ToArray() };
        db.LogBundles.Add(bundle);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/logs/{bundle.Id}");
    }

    /// <summary>Logger category for enrollment.</summary>
    public sealed class EnrollRequestLog;
}
