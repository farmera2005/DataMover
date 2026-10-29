using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lakeflow.ControlApi.Security;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>
/// The real API in-process on a fresh SQL Server database (MSSQL_TEST_CONNECTION), with simulated time, a test CA,
/// and Entra ID replaced by a local signing key, so console calls carry real, validated JWTs with app roles.
/// Agent calls carry their client certificate the way App Service forwards it.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://login.test/lakeflow/v2.0";
    public const string Audience = "api://lakeflow-control-test";

    public static string? SkipReason => Environment.GetEnvironmentVariable("MSSQL_TEST_CONNECTION") is { Length: > 0 }
        ? null : "Set MSSQL_TEST_CONNECTION to run control API tests (scripts/test-sqlserver.sh).";

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    /// <summary>Tests enroll many agents from one address; the production default is 10.</summary>
    public int EnrollPerMinute { get; init; } = 1000;
    public DevelopmentKeyMaterial Keys { get; } = new();
    public RsaSecurityKey SigningKey { get; } = new(RSA.Create(2048)) { KeyId = "test" };
    public string DatabaseName { get; } = $"lakeflow_api_{DateTime.UtcNow:yyyyMMddHHmmss}_{RandomNumberGenerator.GetHexString(4).ToLowerInvariant()}";

    public string ConnectionStringForTests => ConnectionString;

    private string ConnectionString => new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MSSQL_TEST_CONNECTION"))
    {
        InitialCatalog = DatabaseName,
    }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Lakeflow", ConnectionString);
        builder.UseSetting("EventHubs:Namespace", "lakeflow-test.servicebus.windows.net");
        builder.UseSetting("PublicBaseUrl", "https://lakeflow.test");
        builder.UseSetting("Entra:TenantId", "test-tenant");
        builder.UseSetting("Entra:Audience", Audience);
        builder.UseSetting("RateLimits:EnrollPerMinute", EnrollPerMinute.ToString());
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IKeyMaterial>(Keys);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Authority = null;
                o.Configuration = new OpenIdConnectConfiguration();
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidAudience = Audience,
                    IssuerSigningKey = SigningKey,
                    RoleClaimType = "roles",
                    NameClaimType = "preferred_username",
                };
            });
        });
    }

    public string Token(string user, string[] roles, string audience = Audience, SecurityKey? key = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object> { ["preferred_username"] = user, ["roles"] = roles, ["oid"] = Guid.NewGuid().ToString() },
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });

    /// <summary>A console client signed in as <paramref name="user"/> with the given app roles.</summary>
    public HttpClient Console(string user = "adam@mercerlandmark.com", params string[] roles)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Token(user, roles.Length == 0 ? [Roles.Admin] : roles));
        return client;
    }

    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") });

    /// <summary>The handler an agent uses: TLS client certificate, as App Service forwards it.</summary>
    public HttpMessageHandler AgentHandler(X509Certificate2? certificate) =>
        new CertificateHeaderHandler(certificate) { InnerHandler = Server.CreateHandler() };

    public HttpClient Agent(X509Certificate2 certificate) =>
        new(AgentHandler(certificate)) { BaseAddress = new Uri("https://localhost/") };

    private sealed class CertificateHeaderHandler(X509Certificate2? certificate) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (certificate is not null) request.Headers.Add(AuthSetup.ClientCertificateHeader, Convert.ToBase64String(certificate.RawData));
            return base.SendAsync(request, cancellationToken);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (SkipReason is not null) return;
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(Environment.GetEnvironmentVariable("MSSQL_TEST_CONNECTION"));
        await connection.OpenAsync();
        await using var command = new SqlCommand($"IF DB_ID('{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}] END", connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Console calls the tests make over and over.</summary>
public static class ApiCalls
{
    public static async Task<HttpResponseMessage> PutAsync<T>(this HttpClient client, string url, T body) =>
        await client.PutAsJsonAsync(url, body, EnvelopeJson.Options);

    public static async Task<HttpResponseMessage> PostAsync<T>(this HttpClient client, string url, T body) =>
        await client.PostAsJsonAsync(url, body, EnvelopeJson.Options);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(EnvelopeJson.Options))!;
    }

    public static async Task<T> GetAsync<T>(this HttpClient client, string url) => await (await client.GetAsync(url)).ReadAsync<T>();

    /// <summary>The config of the one site a site agent serves.</summary>
    public static async Task<AgentConfig> SiteConfigAsync(this HttpClient agent, string agentId) =>
        Assert.Single((await agent.GetAsync<CollectorConfig>($"agents/{agentId}/config")).Sites);

    public static async Task OkAsync(this Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>A site with one CompuWeigh source and one table.</summary>
    public static async Task CreateSiteAsync(this HttpClient admin, string siteId, Division division = Division.Grain)
    {
        await admin.PutAsync($"sites/{siteId}", new SiteDto { SiteId = siteId, Name = $"Site {siteId}", Latitude = 40.5, Longitude = -84.6 }).OkAsync();
        await admin.PutAsync($"sites/{siteId}/sources/compuweigh", new SourceDto
        {
            SourceId = "compuweigh", SourceSystem = "compuweigh", Division = division, Engine = SourceEngine.SqlServer,
            Connection = new ConnectionSettings { Host = "10.1.2.3", Port = 1433, Database = "GMS", UserName = "lakeflow_ro", CredentialName = "compuweigh" },
        }).OkAsync();
        await admin.PutAsync($"sites/{siteId}/sources/compuweigh/tables/dbo.Tickets",
            new TableDto { Table = "dbo.Tickets", Method = CaptureMethod.RowVersion }).OkAsync();
    }

    /// <summary>Enrolls an agent for the site the way the agent does; returns its id and certificate with key.</summary>
    public static async Task<(string AgentId, X509Certificate2 Certificate)> EnrollAsync(this ApiFactory api, HttpClient admin, string siteId)
    {
        var token = await (await admin.PostAsync("enrollment-tokens", new EnrollmentTokenRequest(siteId, null))).ReadAsync<EnrollmentTokenResponse>();
        var key = RSA.Create(3072); // kept alive: the returned certificate uses it
        var csr = new CertificateRequest("CN=scale-pc", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        var enrolled = await (await api.Anonymous().PostAsync("agents/enroll", new EnrollRequest(token.Token, csr, "SCALE-PC", "1.0.0")))
            .ReadAsync<EnrollResponse>();
        using var publicOnly = X509Certificate2.CreateFromPem(enrolled.CertificatePem);
        return (enrolled.AgentId, publicOnly.CopyWithPrivateKey(key));
    }
}
