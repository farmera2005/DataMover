using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Lakeflow.Contracts;

namespace Lakeflow.Agent.Control;

/// <summary>Creates the HTTP handler for calls to the control API, with or without the agent's client certificate.</summary>
public delegate HttpMessageHandler ControlApiHandlerFactory(X509Certificate2? clientCertificate);

/// <summary>The agent's calls to the control API: outbound HTTPS (TLS 1.2+) only, authenticated by client certificate.</summary>
public sealed class ControlApiClient(HttpClient http, string agentId) : IDisposable
{
    public static readonly ControlApiHandlerFactory DefaultHandler = certificate => new SocketsHttpHandler
    {
        SslOptions =
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificates = certificate is null ? null : new X509CertificateCollection { certificate },
        },
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };

    public string AgentId => agentId;

    public static HttpClient CreateHttp(string baseUrl, HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Enrolls with a one-time token. Throws <see cref="ControlApiException"/> with the API's reason if refused.</summary>
    public static async Task<EnrollResponse> EnrollAsync(HttpClient anonymous, EnrollRequest request, CancellationToken ct)
    {
        using var response = await anonymous.PostAsJsonAsync("agents/enroll", request, EnvelopeJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<EnrollResponse>(EnvelopeJson.Options, ct))!;
    }

    public async Task<CollectorConfig> GetConfigAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync($"agents/{agentId}/config", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CollectorConfig>(EnvelopeJson.Options, ct))!;
    }

    public async Task SendHeartbeatAsync(Heartbeat heartbeat, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"agents/{agentId}/heartbeat", heartbeat, EnvelopeJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task SendEventsAsync(IReadOnlyList<AgentEvent> events, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"agents/{agentId}/events", events, EnvelopeJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<string?> UploadLogsAsync(byte[] zip, Guid commandId, CancellationToken ct)
    {
        using var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new("application/zip");
        using var response = await http.PostAsync($"agents/{agentId}/logs?commandId={commandId}", content, ct);
        await EnsureAsync(response, ct);
        return response.Headers.Location?.ToString();
    }

    public async Task<EnrollResponse> RenewAsync(string csrPem, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"agents/{agentId}/renew", new RenewRequest(csrPem), EnvelopeJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<EnrollResponse>(EnvelopeJson.Options, ct))!;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ControlApiException(response.StatusCode, body.Length > 500 ? body[..500] : body);
    }

    public void Dispose() => http.Dispose();
}

public sealed class ControlApiException(HttpStatusCode status, string body) : Exception($"Control API answered {(int)status} {status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
}
