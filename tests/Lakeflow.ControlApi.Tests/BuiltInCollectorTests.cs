using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Lakeflow.Agent.Control;
using Lakeflow.Agent.Tests;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>
/// The stack's own collector enrolls with no one copying a token, and every collector trusts only this stack's CA on
/// the collectors' port (by the SHA-256 that comes with its token), never anything else.
/// </summary>
public sealed class BuiltInCollectorTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [SkippableFact]
    public async Task The_built_in_collector_enrolls_from_the_shared_file_which_then_goes_away()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        api.Console(); // starts the app
        var file = Path.Combine(api.DataDirectory, "bootstrap", "enroll.json");
        var builtIn = ActivatorUtilities.CreateInstance<BuiltInCollector>(api.Services);
        await builtIn.EnsureAsync(file, CancellationToken.None);
        var written = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement;
        Assert.Equal("https://api:8444/", written.GetProperty("control_api").GetString());
        Assert.Equal(LocalKeyMaterial.Fingerprint(api.Keys.AgentCa), written.GetProperty("ca_sha256").GetString());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        // Unchanged while still valid.
        var token = written.GetProperty("token").GetString()!;
        await builtIn.EnsureAsync(file, CancellationToken.None);
        Assert.Equal(token, JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement.GetProperty("token").GetString());

        await using var h = await Harness.StartAsync();
        using var control = new ControlLoop(h.Collector(), (c, _) => api.AgentHandler(c), h.Time, NullLogger<ControlLoop>.Instance, h.Directory, () => { });
        await control.EnrollAsync("https://localhost/", token, LocalKeyMaterial.Fingerprint(api.Keys.AgentCa), "lakeflow-collector", "1.0.0", CancellationToken.None);
        Assert.StartsWith("collector-", control.Identity!.AgentId);
        Assert.Equal(api.Keys.AgentCa.Thumbprint, control.Identity.Ca!.Thumbprint);

        var agents = await api.Console().GetAsync<List<AgentDto>>("agents");
        Assert.Contains(agents, a => a.Name == BuiltInCollector.Name && a.AgentId == control.Identity.AgentId);
        await builtIn.EnsureAsync(file, CancellationToken.None);
        Assert.False(File.Exists(file));
    }

    [SkippableFact]
    public async Task A_collector_refuses_a_control_API_whose_CA_does_not_match_the_token()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = api.Console();
        var token = await (await admin.PostAsync("enrollment-tokens", new EnrollmentTokenRequest(null, "pinned"))).ReadAsync<EnrollmentTokenResponse>();
        await using var h = await Harness.StartAsync();
        using var control = new ControlLoop(h.Collector(), (c, _) => api.AgentHandler(c), h.Time, NullLogger<ControlLoop>.Instance, h.Directory, () => { });
        var wrong = new string('a', 64);
        var refused = await Assert.ThrowsAsync<ControlApiException>(() =>
            control.EnrollAsync("https://localhost/", token.Token, wrong, "x", "1.0.0", CancellationToken.None));
        Assert.Contains("does not match", refused.Message);
        Assert.False(control.Enrolled);
    }

    /// <summary>Real TLS, as on the collectors' port: only certificates from the CA the collector holds are accepted.</summary>
    [Fact]
    public async Task Server_trust_accepts_only_certificates_from_the_collectors_CA()
    {
        using var keys = new TestCa();
        var certificates = new ServerCertificates(keys, new FakeTimeProvider(DateTimeOffset.UtcNow));
        certificates.Load("lakeflow.corp.local", null);
        using var otherCa = new TestCa();
        var other = new ServerCertificates(otherCa, new FakeTimeProvider(DateTimeOffset.UtcNow));
        other.Load("lakeflow.corp.local", null);

        Assert.True(await HandshakeAsync(certificates.CollectorContext, new ServerTrust(keys.AgentCa)));
        // Another CA's certificate, whatever its name, is refused.
        Assert.False(await HandshakeAsync(other.CollectorContext, new ServerTrust(keys.AgentCa)));
        // Without the CA the system's trust applies, which does not know it.
        Assert.False(await HandshakeAsync(certificates.CollectorContext, ServerTrust.System));
    }

    [SkippableFact]
    public async Task The_CA_is_public_on_the_collectors_port_and_matches_its_fingerprint()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        using var http = new HttpClient(api.AgentHandler(null)) { BaseAddress = new Uri("https://localhost/") };
        using var ca = await ControlApiClient.GetCaAsync(http, CancellationToken.None);
        Assert.Equal(LocalKeyMaterial.Fingerprint(api.Keys.AgentCa), ServerTrust.Fingerprint(ca));
        Assert.False(ca.HasPrivateKey);
    }

    private static async Task<bool> HandshakeAsync(SslStreamCertificateContext serverCertificate, ServerTrust trust)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(socket.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = serverCertificate });
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
            }
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = new SslStream(client.GetStream());
        try
        {
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "api", RemoteCertificateValidationCallback = trust.Validator() ?? ((_, _, _, errors) => errors == SslPolicyErrors.None),
            });
            return true;
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
            return false;
        }
        finally
        {
            client.Close();
            await serverTask;
        }
    }

    private sealed class TestCa : IKeyMaterial, IDisposable
    {
        public TestCa()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN=Test CA {Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(1));
            AgentCa = new X509Certificate2(ca.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.EphemeralKeySet);
        }

        public X509Certificate2 AgentCa { get; }

        public void Dispose() => AgentCa.Dispose();
    }
}
