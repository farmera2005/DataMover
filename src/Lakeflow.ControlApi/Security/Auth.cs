using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Lakeflow.ControlApi.Data;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Security;

public static class Roles
{
    // Entra ID app roles; assign groups to them in the enterprise application so roles follow group membership.
    public const string Admin = "Lakeflow.Admin";
    public const string Operator = "Lakeflow.Operator";
    public const string Viewer = "Lakeflow.Viewer";
}

public static class Policies
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";
    public const string Agent = "agent";
}

public static class AgentClaims
{
    public const string AgentId = "lakeflow_agent_id";
}

public sealed class EntraOptions
{
    /// <summary>Directory (tenant) id.</summary>
    public string TenantId { get; set; } = "";
    /// <summary>The API's application id URI or client id, as tokens carry it in <c>aud</c>.</summary>
    public string Audience { get; set; } = "";
}

public static class AuthSetup
{
    /// <summary>Header App Service puts the client certificate in (base64 DER) once TLS is terminated.</summary>
    public const string ClientCertificateHeader = "X-ARR-ClientCert";

    public static void AddLakeflowAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var entra = configuration.GetSection("Entra").Get<EntraOptions>() ?? new EntraOptions();
        services.AddCertificateForwarding(o => o.CertificateHeader = ClientCertificateHeader);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.Authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
                o.Audience = entra.Audience;
                o.MapInboundClaims = false;
                o.TokenValidationParameters.RoleClaimType = "roles";
                o.TokenValidationParameters.NameClaimType = "preferred_username";
            })
            .AddCertificate(o =>
            {
                o.AllowedCertificateTypes = CertificateTypes.Chained;
                o.ChainTrustValidationMode = X509ChainTrustMode.CustomRootTrust;
                o.RevocationMode = X509RevocationMode.NoCheck; // revocation is the agents table, checked below
                o.ValidateCertificateUse = true;
                o.Events = new CertificateAuthenticationEvents { OnCertificateValidated = ValidateAgentAsync };
            });
        services.AddOptions<CertificateAuthenticationOptions>(CertificateAuthenticationDefaults.AuthenticationScheme)
            .Configure<IKeyMaterial>((o, keys) => o.CustomTrustStore = [keys.AgentCa]);

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Viewer, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireRole(Roles.Admin, Roles.Operator, Roles.Viewer))
            .AddPolicy(Policies.Operator, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireRole(Roles.Admin, Roles.Operator))
            .AddPolicy(Policies.Admin, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireRole(Roles.Admin))
            .AddPolicy(Policies.Agent, p => p.AddAuthenticationSchemes(CertificateAuthenticationDefaults.AuthenticationScheme)
                .RequireClaim(AgentClaims.AgentId)
                .AddRequirements(new SameAgentRequirement()));
        services.AddSingleton<IAuthorizationHandler, SameAgentHandler>();
    }

    /// <summary>The certificate chains to our CA; it must also be an enrolled, unrevoked agent's current (or just-renewed) certificate.</summary>
    private static async Task ValidateAgentAsync(CertificateValidatedContext context)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<LakeflowDb>();
        var time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        var thumbprint = context.ClientCertificate.Thumbprint;
        var agent = await db.Agents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CertificateThumbprint == thumbprint || a.PreviousThumbprint == thumbprint);
        if (agent is null || agent.RevokedUtc is not null || context.ClientCertificate.NotAfter.ToUniversalTime() < time.GetUtcNow().UtcDateTime)
        {
            context.Fail("Unknown, revoked or expired agent certificate.");
            return;
        }
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AgentClaims.AgentId, agent.AgentId), new Claim(ClaimTypes.Name, agent.AgentId)],
            context.Scheme.Name));
        context.Success();
    }
}

/// <summary>An agent may only use its own routes: <c>/agents/{id}</c> must be the certificate's agent.</summary>
public sealed class SameAgentRequirement : IAuthorizationRequirement;

public sealed class SameAgentHandler(IHttpContextAccessor http) : AuthorizationHandler<SameAgentRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SameAgentRequirement requirement)
    {
        var routeId = http.HttpContext?.Request.RouteValues["agentId"] as string;
        if (routeId is not null && routeId == context.User.FindFirstValue(AgentClaims.AgentId)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>The audit actor: console user's Entra name, or the agent id.</summary>
public sealed class HttpActor(IHttpContextAccessor http) : IActor
{
    public string Name
    {
        get
        {
            var user = http.HttpContext?.User;
            return user?.FindFirstValue(AgentClaims.AgentId) is { } agent ? $"agent:{agent}"
                : user?.FindFirstValue("preferred_username") ?? user?.FindFirstValue("name") ?? user?.FindFirstValue("oid") ?? "system";
        }
    }
}
