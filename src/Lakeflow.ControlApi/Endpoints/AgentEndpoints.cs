using System.Security.Cryptography;
using System.Text;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Endpoints;

/// <summary>What agents call. Agents only ever connect out to these; nothing connects into a site.</summary>
public static class AgentEndpoints
{
    public const long MaxLogBundleBytes = 25L * 1024 * 1024;

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/agents/enroll", EnrollAsync).AllowAnonymous().RequireRateLimiting("enroll");

        var agent = app.MapGroup("/agents/{agentId}").RequireAuthorization(Policies.Agent);
        agent.MapPost("/renew", RenewAsync);
        agent.MapGet("/config", ConfigAsync);
        agent.MapPost("/heartbeat", HeartbeatAsync);
        agent.MapPost("/events", EventsAsync);
        agent.MapPost("/logs", LogsAsync).DisableAntiforgery();
    }

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>
    /// Exchanges a one-time enrollment token (24 h, single use) and a CSR for a client certificate. Enrolling a new
    /// agent for a site retires the site's previous agent, so two machines never capture the same site.
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

        var agentId = $"{token.SiteId}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;
        try
        {
            certificate = certificates.Issue(request.CsrPem, agentId);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: 400);
        }

        foreach (var previous in await db.Agents.Where(a => a.SiteId == token.SiteId && a.RevokedUtc == null).ToListAsync(ct))
        {
            previous.RevokedUtc = now;
            previous.RevokedBy = $"replaced by {agentId}";
        }
        db.Agents.Add(new Agent
        {
            AgentId = agentId,
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
        log.LogInformation("Agent {AgentId} enrolled for site {SiteId} from {Hostname}", agentId, token.SiteId, request.Hostname);
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

    private static async Task<Ok<AgentConfig>> ConfigAsync(string agentId, LakeflowDb db, ConfigBuilder builder, CancellationToken ct)
    {
        var agent = await db.Agents.SingleAsync(a => a.AgentId == agentId, ct);
        return TypedResults.Ok(await builder.ForAgentAsync(agent, ct));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> HeartbeatAsync(
        string agentId, Heartbeat heartbeat, LakeflowDb db, TimeProvider time, CancellationToken ct)
    {
        if (heartbeat.AgentId != agentId) return TypedResults.Problem("agent_id does not match the certificate.", statusCode: 400);
        var agent = await db.Agents.SingleAsync(a => a.AgentId == agentId, ct);
        agent.LastHeartbeatUtc = time.GetUtcNow();
        agent.LastHeartbeatJson = EnvelopeJson.Serialize(heartbeat with { SiteId = agent.SiteId });
        agent.AgentVersion = heartbeat.AgentVersion;
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Stores events (idempotent by event id), completes commands and retires delivered credentials.</summary>
    private static async Task<NoContent> EventsAsync(string agentId, List<AgentEvent> events, LakeflowDb db, TimeProvider time, CancellationToken ct)
    {
        var agent = await db.Agents.SingleAsync(a => a.AgentId == agentId, ct);
        var now = time.GetUtcNow();
        var ids = events.Select(e => e.EventId).ToList();
        var known = await db.AgentEvents.Where(e => ids.Contains(e.EventId)).Select(e => e.EventId).ToListAsync(ct);
        foreach (var e in events.Where(e => !known.Contains(e.EventId)).DistinctBy(e => e.EventId))
        {
            db.AgentEvents.Add(new AgentEventRow
            {
                EventId = e.EventId, AgentId = agentId, SiteId = agent.SiteId, Kind = e.Kind, Severity = e.Severity,
                OccurredUtc = e.OccurredUtc, ReceivedUtc = now, Json = EnvelopeJson.Serialize(e),
            });
            if (e is { Kind: AgentEventKind.CommandResult, CommandId: { } commandId }
                && await db.Commands.FirstOrDefaultAsync(c => c.CommandId == commandId && c.AgentId == agentId, ct) is { } command)
            {
                command.CompletedUtc = e.OccurredUtc;
                command.Result = e.Message.Length > 2000 ? e.Message[..2000] : e.Message;
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

    /// <summary>A zip of the agent's recent logs, sent in answer to a "collect logs" command.</summary>
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
