using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>
/// The real API in-process on a fresh PostgreSQL database (POSTGRES_TEST_CONNECTION), with simulated time and its own
/// data directory (master key, CA). Console clients are real local accounts signed in with the session cookie; collector
/// calls carry their client certificate on the TLS connection, as Kestrel presents it. Every division's Event Hub is
/// set up, with known keys (<see cref="HubKey"/>).
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string Namespace = "lakeflow-test.servicebus.windows.net";
    public const string TestPassword = "correct horse battery staple";

    public static string? SkipReason => Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION") is { Length: > 0 }
        ? null : "Set POSTGRES_TEST_CONNECTION to run control API tests (see README: PostgreSQL in Docker).";

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    /// <summary>Tests enroll many agents from one address; the production default is 10.</summary>
    public int EnrollPerMinute { get; init; } = 1000;
    public int SignInPerMinute { get; init; } = 100000;
    /// <summary>Set up every division's hub at start (as an administrator would in Configuration).</summary>
    public bool SeedEventHubs { get; init; } = true;
    public string DatabaseName { get; } = $"lakeflow_api_{DateTime.UtcNow:yyyyMMddHHmmss}_{RandomNumberGenerator.GetHexString(4).ToLowerInvariant()}";
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "lakeflow-api-tests", Guid.NewGuid().ToString("N"));

    public IKeyMaterial Keys => Services.GetRequiredService<IKeyMaterial>();

    public static byte[] HubKey(Division division) => Encoding.UTF8.GetBytes($"test-send-key-{LakeNames.HubName(division)}");

    public string ConnectionString => new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION"))
    {
        Database = DatabaseName,
    }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Lakeflow", ConnectionString);
        builder.UseSetting("Server:DataDirectory", DataDirectory);
        builder.UseSetting("Listen:Enabled", "false");
        builder.UseSetting("RateLimits:EnrollPerMinute", EnrollPerMinute.ToString());
        builder.UseSetting("RateLimits:SignInPerMinute", SignInPerMinute.ToString());
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (SeedEventHubs)
        {
            using var scope = host.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
            foreach (var division in Enum.GetValues<Division>())
                settings.SetAsync(SettingsStore.EventHub(division), new EventHubSetting(Namespace, LakeNames.HubName(division), "lakeflow-send"),
                    HubKey(division), CancellationToken.None).GetAwaiter().GetResult();
        }
        return host;
    }

    /// <summary>Creates (or updates) a local account directly in the database.</summary>
    public async Task<Guid> EnsureUserAsync(string user, ConsoleRole role, bool disabled = false, string password = TestPassword)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LakeflowDb>();
        var name = Accounts.Normalize(user);
        var row = await db.Users.FirstOrDefaultAsync(u => u.UserName == name);
        if (row is null) db.Users.Add(row = new User { Id = Guid.NewGuid(), UserName = name, CreatedUtc = Time.GetUtcNow() });
        row.Role = role;
        row.Disabled = disabled;
        Accounts.SetPassword(row, password);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>A console client signed in (session cookie) as a local account with the given role (Admin by default).</summary>
    public HttpClient Console(string user = "adam@mercerlandmark.com", params string[] roles)
    {
        var role = roles.Contains(Roles.Admin) || roles.Length == 0 ? ConsoleRole.Admin
            : roles.Contains(Roles.Operator) ? ConsoleRole.Operator : ConsoleRole.Viewer;
        EnsureUserAsync(user, role).GetAwaiter().GetResult();
        var client = Anonymous();
        var response = client.PostAsJsonAsync("auth/sign-in", new SignInRequest(user, TestPassword), EnvelopeJson.Options).GetAwaiter().GetResult();
        Assert.True(response.IsSuccessStatusCode, $"sign-in as {user}: {(int)response.StatusCode}");
        return client;
    }

    /// <summary>Not signed in; sends the console's request header like the real console.</summary>
    public HttpClient Anonymous()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/"), HandleCookies = true });
        client.DefaultRequestHeaders.Add(AuthSetup.RequestHeader, "1");
        return client;
    }

    /// <summary>The handler a collector uses: its client certificate on the connection, as Kestrel presents a TLS client certificate.</summary>
    public HttpMessageHandler AgentHandler(X509Certificate2? certificate) =>
        Server.CreateHandler(context => context.Connection.ClientCertificate = certificate);

    public HttpClient Agent(X509Certificate2 certificate) =>
        new(AgentHandler(certificate)) { BaseAddress = new Uri("https://localhost/") };

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
        if (SkipReason is not null) return;
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", connection);
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
