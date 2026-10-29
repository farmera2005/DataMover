using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Lakeflow.ControlApi.Data;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Security;

public static class Roles
{
    public const string Admin = "Lakeflow.Admin";
    public const string Operator = "Lakeflow.Operator";
    public const string Viewer = "Lakeflow.Viewer";

    public static string For(ConsoleRole role) => role switch
    {
        ConsoleRole.Admin => Admin,
        ConsoleRole.Operator => Operator,
        _ => Viewer,
    };
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

public static class UserClaims
{
    public const string UserId = "lakeflow_user_id";
    public const string Stamp = "lakeflow_stamp";
}

public static class AuthSetup
{
    public const string CookieName = "lakeflow_session";

    /// <summary>
    /// Header the console sends on every request that changes something. A cross-site form cannot set it, which (with
    /// the SameSite=Strict cookie) keeps another site from acting as a signed-in user.
    /// </summary>
    public const string RequestHeader = "X-Lakeflow-Request";

    public static void AddLakeflowAuth(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                // The console works over plain HTTP too; over HTTPS the cookie is Secure.
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                o.Events = new CookieAuthenticationEvents
                {
                    // An API: answer with status codes, never redirect.
                    OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
                    OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
                    OnValidatePrincipal = ValidateUserAsync,
                };
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

        const string cookie = CookieAuthenticationDefaults.AuthenticationScheme;
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Viewer, p => p.AddAuthenticationSchemes(cookie).RequireRole(Roles.Admin, Roles.Operator, Roles.Viewer))
            .AddPolicy(Policies.Operator, p => p.AddAuthenticationSchemes(cookie).RequireRole(Roles.Admin, Roles.Operator))
            .AddPolicy(Policies.Admin, p => p.AddAuthenticationSchemes(cookie).RequireRole(Roles.Admin))
            .AddPolicy(Policies.Agent, p => p.AddAuthenticationSchemes(CertificateAuthenticationDefaults.AuthenticationScheme)
                .RequireClaim(AgentClaims.AgentId)
                .AddRequirements(new SameAgentRequirement()));
        services.AddSingleton<IAuthorizationHandler, SameAgentHandler>();
    }

    public static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity(
    [
        new Claim(UserClaims.UserId, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.UserName),
        new Claim(ClaimTypes.Role, Roles.For(user.Role)),
        new Claim(UserClaims.Stamp, user.SecurityStamp),
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>
    /// Every request re-checks the user: a disabled or deleted account, or a changed password or role (new security
    /// stamp), ends the session at once.
    /// </summary>
    private static async Task ValidateUserAsync(CookieValidatePrincipalContext context)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<LakeflowDb>();
        var id = Guid.TryParse(context.Principal?.FindFirstValue(UserClaims.UserId), out var g) ? g : Guid.Empty;
        var stamp = context.Principal?.FindFirstValue(UserClaims.Stamp);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (user is null || user.Disabled || user.SecurityStamp != stamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    /// <summary>The certificate chains to our CA; it must also be an enrolled, unrevoked collector's current (or just-renewed) certificate.</summary>
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

/// <summary>The audit actor: the console user's name, or the collector id.</summary>
public sealed class HttpActor(IHttpContextAccessor http) : IActor
{
    private string? _override;

    public string Name
    {
        get
        {
            if (_override is not null) return _override;
            var user = http.HttpContext?.User;
            return user?.FindFirstValue(AgentClaims.AgentId) is { } agent ? $"agent:{agent}"
                : user?.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.Name) ?? "system" : "system";
        }
    }

    /// <summary>For actions before anyone is signed in (setup, sign-in itself).</summary>
    public void ActAs(string name) => _override = name;
}
