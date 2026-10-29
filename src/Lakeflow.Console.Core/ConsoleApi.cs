using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core;

/// <summary>The API refused a request; <see cref="Errors"/> holds what to show the user.</summary>
public sealed class ConsoleApiException(HttpStatusCode status, IReadOnlyList<string> errors)
    : Exception(string.Join(" ", errors))
{
    public HttpStatusCode Status { get; } = status;
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Adds the signed-in user's token to every call.</summary>
public sealed class BearerTokenHandler(IAccessTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Every console call to the control API.</summary>
public sealed class ConsoleApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = EnvelopeJson.Options;

    public static ConsoleApi Create(Uri baseUrl, IAccessTokenProvider tokens, HttpMessageHandler? inner = null) =>
        new(new HttpClient(new BearerTokenHandler(tokens) { InnerHandler = inner ?? new HttpClientHandler() })
        {
            BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(60),
        });

    public Task<FleetStatus> GetStatusAsync(CancellationToken ct = default) => GetAsync<FleetStatus>("status", ct);
    public Task<List<SiteDto>> GetSitesAsync(CancellationToken ct = default) => GetAsync<List<SiteDto>>("sites", ct);
    public Task<SiteDto> GetSiteAsync(string siteId, CancellationToken ct = default) => GetAsync<SiteDto>($"sites/{E(siteId)}", ct);
    public Task<SiteDto> PutSiteAsync(SiteDto site, CancellationToken ct = default) => PutAsync<SiteDto, SiteDto>($"sites/{E(site.SiteId)}", site, ct);

    public Task<List<SourceDto>> GetSourcesAsync(string siteId, CancellationToken ct = default) => GetAsync<List<SourceDto>>($"sites/{E(siteId)}/sources", ct);
    public Task<SourceDto> PutSourceAsync(string siteId, SourceDto source, CancellationToken ct = default) =>
        PutAsync<SourceDto, SourceDto>($"sites/{E(siteId)}/sources/{E(source.SourceId)}", source, ct);
    public Task DeleteSourceAsync(string siteId, string sourceId, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"sites/{E(siteId)}/sources/{E(sourceId)}", null, ct);
    public Task PutCredentialAsync(string siteId, string sourceId, CredentialRequest credential, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"sites/{E(siteId)}/sources/{E(sourceId)}/credential", JsonContent.Create(credential, options: Json), ct);

    public Task<List<TableDto>> GetTablesAsync(string siteId, string sourceId, CancellationToken ct = default) =>
        GetAsync<List<TableDto>>($"sites/{E(siteId)}/sources/{E(sourceId)}/tables", ct);
    public Task<TableDto> PutTableAsync(string siteId, string sourceId, TableDto table, CancellationToken ct = default) =>
        PutAsync<TableDto, TableDto>($"sites/{E(siteId)}/sources/{E(sourceId)}/tables/{E(table.Table)}", table, ct);

    /// <summary>A token for a central collector (no site) or for an agent installed at one site.</summary>
    public async Task<EnrollmentTokenResponse> CreateEnrollmentTokenAsync(EnrollmentTokenRequest request, CancellationToken ct = default) =>
        await ReadAsync<EnrollmentTokenResponse>(await http.PostAsJsonAsync("enrollment-tokens", request, Json, ct), ct);

    public Task<AgentDto> GetAgentAsync(string agentId, CancellationToken ct = default) => GetAsync<AgentDto>($"agents/{E(agentId)}", ct);

    public Task<List<AgentDto>> GetAgentsAsync(CancellationToken ct = default) => GetAsync<List<AgentDto>>("agents", ct);
    public Task RevokeAgentAsync(string agentId, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"agents/{E(agentId)}/revoke", JsonContent.Create(new { }), ct);

    /// <summary>Commands go to the site; the API hands them to the collector serving it.</summary>
    public async Task<CommandDto> PostCommandAsync(string siteId, CommandRequest command, CancellationToken ct = default) =>
        await ReadAsync<CommandDto>(await http.PostAsJsonAsync($"sites/{E(siteId)}/commands", command, Json, ct), ct);
    public Task<CommandDto> GetCommandAsync(string siteId, Guid commandId, CancellationToken ct = default) =>
        GetAsync<CommandDto>($"sites/{E(siteId)}/commands/{commandId}", ct);
    public Task<List<CommandDto>> GetCommandsAsync(string siteId, CancellationToken ct = default) => GetAsync<List<CommandDto>>($"sites/{E(siteId)}/commands", ct);

    public Task<List<AgentEventDto>> GetEventsAsync(string? siteId, int take = 50, CancellationToken ct = default) =>
        GetAsync<List<AgentEventDto>>($"events?take={take}" + (siteId is null ? "" : $"&siteId={E(siteId)}"), ct);
    public Task<List<AuditEntryDto>> GetAuditAsync(int take = 100, CancellationToken ct = default) => GetAsync<List<AuditEntryDto>>($"audit?take={take}", ct);
    public Task<List<ReconciliationResult>> GetReconciliationAsync(string? siteId, CancellationToken ct = default) =>
        GetAsync<List<ReconciliationResult>>("reconciliation" + (siteId is null ? "" : $"?siteId={E(siteId)}"), ct);

    public async Task<byte[]> DownloadAsync(string relativeUrl, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(relativeUrl.TrimStart('/'), ct);
        await EnsureAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Queues a command and waits for the collector's result (collectors pull commands, every 10 s while one waits).
    /// Returns the command as it stands when it completed or when <paramref name="timeout"/> ran out.
    /// </summary>
    public async Task<CommandDto> RunCommandAsync(string siteId, CommandRequest command, TimeSpan timeout, TimeSpan pollEvery, CancellationToken ct = default)
    {
        var issued = await PostCommandAsync(siteId, command, ct);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var current = await GetCommandAsync(siteId, issued.CommandId, ct);
            if (current.CompletedUtc is not null || DateTime.UtcNow >= deadline) return current;
            await Task.Delay(pollEvery, ct);
        }
    }

    private static string E(string part) => Uri.EscapeDataString(part);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct) => await ReadAsync<T>(await http.GetAsync(url, ct), ct);

    private async Task<TOut> PutAsync<TIn, TOut>(string url, TIn body, CancellationToken ct) =>
        await ReadAsync<TOut>(await http.PutAsJsonAsync(url, body, Json, ct), ct);

    private async Task SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var response = await http.SendAsync(new HttpRequestMessage(method, url) { Content = content }, ct);
        await EnsureAsync(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            await EnsureAsync(response, ct);
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
    }

    /// <summary>Turns validation problems and status codes into messages a person can act on.</summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var errors = new List<string>();
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("errors", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object)
                    foreach (var field in e.EnumerateObject()) errors.AddRange(field.Value.EnumerateArray().Select(x => x.GetString()!));
                else if (e.ValueKind == JsonValueKind.Array) errors.AddRange(e.EnumerateArray().Select(x => x.GetString()!));
            }
            else if (json.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                errors.Add(detail.GetString()!);
        }
        catch (JsonException) { }
        if (errors.Count == 0)
            errors.Add(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your sign-in has expired; sign in again.",
                HttpStatusCode.Forbidden => "Your role does not allow this (ask an Admin for the Operator or Admin role).",
                HttpStatusCode.NotFound => "Not found; it may have been removed. Refresh and try again.",
                _ => $"The control API answered {(int)response.StatusCode} {response.ReasonPhrase}.",
            });
        throw new ConsoleApiException(response.StatusCode, errors);
    }
}
