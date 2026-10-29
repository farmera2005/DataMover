using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lakeflow.ControlApi.Security;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

public sealed class EnrollmentAndAuthTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private HttpClient Admin()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        return api.Console();
    }

    // The certificate handler answers a refused certificate with 403 rather than 401; either way the agent is out.
    private static void Refused(HttpStatusCode status) =>
        Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"expected the certificate to be refused, got {(int)status}");

    private static string Site() => "site" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant();

    [SkippableFact]
    public async Task Enrollment_issues_a_client_certificate_from_our_CA_and_the_token_works_once()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var token = await (await admin.PostAsync("enrollment-tokens", new EnrollmentTokenRequest(site, null))).ReadAsync<EnrollmentTokenResponse>();
        Assert.Contains($"ENROLLMENT_TOKEN=\"{token.Token}\"", token.InstallCommand);
        Assert.Equal(api.Time.GetUtcNow().AddHours(24), token.ExpiresUtc);

        var key = RSA.Create(3072);
        var csr = new CertificateRequest("CN=whatever-the-agent-asks", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        var enrolled = await (await api.Anonymous().PostAsync("agents/enroll", new EnrollRequest(token.Token, csr, "SCALE-PC", "1.0.0"))).ReadAsync<EnrollResponse>();

        Assert.StartsWith(site + "-", enrolled.AgentId);
        using var certificate = X509Certificate2.CreateFromPem(enrolled.CertificatePem);
        Assert.Equal(enrolled.AgentId, certificate.GetNameInfo(X509NameType.SimpleName, false));
        Assert.Equal(api.Keys.AgentCa.Subject, certificate.Issuer);
        Assert.Contains(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.2");
        Assert.True(certificate.GetRSAPublicKey()!.ExportSubjectPublicKeyInfo().SequenceEqual(key.ExportSubjectPublicKeyInfo()));
        Assert.DoesNotContain("PRIVATE KEY", enrolled.CertificatePem);

        var reuse = await api.Anonymous().PostAsync("agents/enroll", new EnrollRequest(token.Token, csr, "OTHER-PC", "1.0.0"));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [SkippableFact]
    public async Task Expired_or_unknown_tokens_and_bad_requests_are_refused()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var token = await (await admin.PostAsync("enrollment-tokens", new EnrollmentTokenRequest(site, null))).ReadAsync<EnrollmentTokenResponse>();
        var csr = new CertificateRequest("CN=x", RSA.Create(2048), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous().PostAsync("agents/enroll", new EnrollRequest("made-up", csr, "PC", "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Anonymous().PostAsync("agents/enroll", new EnrollRequest(token.Token, "not a csr", "PC", "1"))).StatusCode);
        var weak = new CertificateRequest("CN=x", RSA.Create(1024), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Anonymous().PostAsync("agents/enroll", new EnrollRequest(token.Token, weak, "PC", "1"))).StatusCode);

        api.Time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous().PostAsync("agents/enroll", new EnrollRequest(token.Token, csr, "PC", "1"))).StatusCode);
    }

    [SkippableFact]
    public async Task Agents_reach_only_their_own_routes_with_a_valid_unrevoked_certificate()
    {
        var admin = Admin();
        var (siteA, siteB) = (Site(), Site());
        await admin.CreateSiteAsync(siteA);
        await admin.CreateSiteAsync(siteB);
        var (agentA, certA) = await api.EnrollAsync(admin, siteA);
        var (agentB, _) = await api.EnrollAsync(admin, siteB);

        Assert.Equal(HttpStatusCode.OK, (await api.Agent(certA).GetAsync($"agents/{agentA}/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Agent(certA).GetAsync($"agents/{agentB}/config")).StatusCode);
        Refused((await api.Anonymous().GetAsync($"agents/{agentA}/config")).StatusCode);

        // Same subject, not issued by our CA.
        using var forgedKey = RSA.Create(2048);
        var forgedRequest = new CertificateRequest($"CN={agentA}, O=Lakeflow Agent", forgedKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        forgedRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], true));
        using var forged = forgedRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Refused((await api.Agent(forged).GetAsync($"agents/{agentA}/config")).StatusCode);

        // A console token is not an agent identity.
        Refused((await admin.GetAsync($"agents/{agentA}/config")).StatusCode);

        await admin.PostAsync($"agents/{agentA}/revoke", new { }).OkAsync();
        Refused((await api.Agent(certA).GetAsync($"agents/{agentA}/config")).StatusCode);
    }

    [SkippableFact]
    public async Task Enrolling_a_replacement_agent_retires_the_old_one()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var (oldAgent, oldCert) = await api.EnrollAsync(admin, site);
        var (newAgent, newCert) = await api.EnrollAsync(admin, site);

        Refused((await api.Agent(oldCert).GetAsync($"agents/{oldAgent}/config")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Agent(newCert).GetAsync($"agents/{newAgent}/config")).StatusCode);
        var agents = await admin.GetAsync<List<AgentDto>>("agents");
        Assert.True(agents.Single(a => a.AgentId == oldAgent).Revoked);
    }

    [SkippableFact]
    public async Task Renewal_issues_a_new_certificate_and_keeps_the_old_one_working_until_it_expires()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var (agent, cert) = await api.EnrollAsync(admin, site);
        var key = RSA.Create(3072);
        var csr = new CertificateRequest("CN=x", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();

        var renewed = await (await api.Agent(cert).PostAsync($"agents/{agent}/renew", new RenewRequest(csr))).ReadAsync<EnrollResponse>();
        using var fresh = X509Certificate2.CreateFromPem(renewed.CertificatePem).CopyWithPrivateKey(key);

        Assert.NotEqual(cert.Thumbprint, fresh.Thumbprint);
        Assert.Equal(HttpStatusCode.OK, (await api.Agent(fresh).GetAsync($"agents/{agent}/config")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Agent(cert).GetAsync($"agents/{agent}/config")).StatusCode);
        Assert.Equal(fresh.Thumbprint, (await admin.GetAsync<AgentDto>($"agents/{agent}")).CertificateThumbprint);
    }

    [SkippableFact]
    public async Task Console_roles_decide_what_each_user_may_do()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var (agent, _) = await api.EnrollAsync(admin, site);
        var viewer = api.Console("val@mercerlandmark.com", Roles.Viewer);
        var operatorUser = api.Console("otto@mercerlandmark.com", Roles.Operator);
        var pause = new CommandRequest { Type = CommandType.PauseTable, SourceId = "compuweigh", Table = "dbo.Tickets" };

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"sites/{site}/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync($"sites/{site}", new SiteDto { SiteId = site, Name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"sites/{site}/commands", pause)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await operatorUser.PostAsync($"sites/{site}/commands", pause)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorUser.PostAsync($"sites/{site}/commands", new CommandRequest { Type = CommandType.Restart })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorUser.PostAsync("enrollment-tokens", new EnrollmentTokenRequest(site, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorUser.PostAsync($"agents/{agent}/revoke", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous().GetAsync("status")).StatusCode);

        // A made-up session cookie is not a session.
        var forged = api.Anonymous();
        forged.DefaultRequestHeaders.Add("Cookie", $"{AuthSetup.CookieName}=CfDJ8made-up");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("status")).StatusCode);

        // Demoting a user ends their session's extra rights at once (new security stamp).
        await api.EnsureUserAsync("otto@mercerlandmark.com", ConsoleRole.Viewer);
        Assert.Equal(HttpStatusCode.Unauthorized, (await operatorUser.PostAsync($"sites/{site}/commands", pause)).StatusCode);
    }

    /// <summary>What a Viewer may call (reads), written out here so a route given the wrong policy is caught.</summary>
    private static readonly HashSet<string> ViewerRoutes =
    [
        "GET /status", "GET /reconciliation", "GET /sites", "GET /sites/{siteId}", "GET /sites/{siteId}/sources",
        "GET /sites/{siteId}/sources/{sourceId}/tables", "GET /agents", "GET /agents/{agentId}", "GET /sites/{siteId}/commands/{commandId:guid}",
        "GET /sites/{siteId}/commands", "GET /events", "GET /audit", "GET /auth/me", "POST /auth/password", "GET /settings/server/ca.crt",
    ];

    /// <summary>What an Operator may call on top (the API itself refuses Admin-only command types).</summary>
    private static readonly HashSet<string> OperatorRoutes = ["POST /sites/{siteId}/commands"];

    private static readonly HashSet<string> OpenRoutes =
        ["GET /health", "GET /", "GET /auth/setup", "POST /auth/setup", "POST /auth/sign-in", "POST /auth/sign-out", "POST /agents/enroll", "GET /agents/ca", "GET /console/{*path:nonfile}", "HEAD /console/{*path:nonfile}"];

    /// <summary>
    /// Walks every route the app maps (so a new one cannot be forgotten): anonymous callers get 401 everywhere but the
    /// open routes, collector routes refuse console sessions, and Viewers and Operators get 403 on everything not listed
    /// above for them; everything else is for Admins.
    /// </summary>
    [SkippableFact]
    public async Task Every_route_is_walked_for_its_role()
    {
        var admin = Admin();
        var viewer = api.Console("walk-viewer@mercerlandmark.com", Roles.Viewer);
        var operatorUser = api.Console("walk-operator@mercerlandmark.com", Roles.Operator);
        var anonymous = api.Anonymous();
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var walked = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var template = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            var url = System.Text.RegularExpressions.Regex.Replace(template, @"\{(\w+)(:guid)?[^}]*\}", m => m.Groups[2].Success ? Guid.NewGuid().ToString() : "x" + m.Groups[1].Value)
                .TrimStart('/');
            var isAgent = endpoint.Metadata.GetOrderedMetadata<AuthorizeAttribute>().Any(a => a.Policy == Policies.Agent);
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                var route = $"{method} {template}";
                walked.Add(route);
                HttpRequestMessage Request() => new(new HttpMethod(method), url) { Content = method == "GET" ? null : new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
                if (OpenRoutes.Contains(route)) continue;
                if (isAgent)
                {
                    Refused((await anonymous.SendAsync(Request())).StatusCode);
                    Refused((await admin.SendAsync(Request())).StatusCode);
                    continue;
                }
                Assert.True((await anonymous.SendAsync(Request())).StatusCode == HttpStatusCode.Unauthorized, $"{route} anonymous");
                var asViewer = (await viewer.SendAsync(Request())).StatusCode;
                var asOperator = (await operatorUser.SendAsync(Request())).StatusCode;
                Assert.True(ViewerRoutes.Contains(route) ? asViewer is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) : asViewer == HttpStatusCode.Forbidden,
                    $"{route} as Viewer answered {(int)asViewer}");
                var operatorMay = ViewerRoutes.Contains(route) || OperatorRoutes.Contains(route);
                Assert.True(operatorMay ? asOperator is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) : asOperator == HttpStatusCode.Forbidden,
                    $"{route} as Operator answered {(int)asOperator}");
            }
        }
        // The lists name only routes that exist.
        Assert.Empty(ViewerRoutes.Concat(OperatorRoutes).Concat(OpenRoutes).Except(walked));
        Assert.True(walked.Count > 40, $"only {walked.Count} routes walked");
    }

    [SkippableFact]
    public async Task Changes_without_the_console_header_are_refused()
    {
        var admin = Admin();
        admin.DefaultRequestHeaders.Remove(AuthSetup.RequestHeader);
        var response = await admin.PutAsync("sites/csrf", new SiteDto { SiteId = "csrf", Name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("sites")).StatusCode);
    }

    [SkippableFact]
    public async Task Every_console_route_requires_sign_in()
    {
        Admin();
        var anonymous = api.Anonymous();
        foreach (var (method, url) in new[]
        {
            ("GET", "status"), ("GET", "sites"), ("PUT", "sites/x"), ("DELETE", "sites/x"), ("GET", "sites/x/sources"),
            ("PUT", "sites/x/sources/y"), ("PUT", "sites/x/sources/y/credential"), ("PUT", "sites/x/sources/y/tables/dbo.t"),
            ("POST", "enrollment-tokens"), ("GET", "agents"), ("POST", "agents/a/revoke"), ("POST", "sites/x/commands"),
            ("GET", "events"), ("GET", "audit"), ("GET", $"logs/{Guid.NewGuid()}"), ("GET", "users"), ("POST", "users"),
            ("GET", "settings/ldap"), ("PUT", "settings/event-hubs/grain"), ("GET", "settings/server"), ("GET", "auth/me"),
        })
        {
            var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") });
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {url} answered {(int)response.StatusCode}");
        }
    }
}

/// <summary>Its own app instance, with the production limit.</summary>
public sealed class EnrollmentRateLimitTests : IAsyncLifetime
{
    private readonly ApiFactory _api = new() { EnrollPerMinute = 10 };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [SkippableFact]
    public async Task Enrollment_is_rate_limited()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
            codes.Add((await _api.Anonymous().PostAsync("agents/enroll", new EnrollRequest("guess-" + i, "x", "PC", "1"))).StatusCode);
        Assert.Equal(10, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }
}
