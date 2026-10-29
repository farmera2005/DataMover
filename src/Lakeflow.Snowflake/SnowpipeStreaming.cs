using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lakeflow.Snowflake;

/// <summary>An open channel: where the next append continues, and the last offset Snowflake has committed.</summary>
public sealed record ChannelSession(string Channel, string ContinuationToken, long? CommittedOffset);

/// <summary>Streams rows into one pipe, a channel per Event Hubs partition, with offset tokens for exactly-once.</summary>
public interface IStreamingIngest
{
    /// <summary>Opens (or reopens) a channel; returns the last committed offset token so a restart resumes after it.</summary>
    Task<ChannelSession> OpenChannelAsync(string channel, CancellationToken ct);

    /// <summary>Appends NDJSON rows; <paramref name="offsetToken"/> is the last row's position. Returns the next continuation token.</summary>
    Task<string> AppendAsync(ChannelSession session, ReadOnlyMemory<byte> ndjson, long offsetToken, CancellationToken ct);
}

/// <summary>
/// Snowpipe Streaming, high-performance architecture, over its REST API (Snowflake has no .NET SDK). Flow: discover
/// the account's ingest host, exchange the key-pair JWT for a token scoped to it, then open channels and append NDJSON
/// rows with continuation and offset tokens. Writes go to the table's default pipe (<c>&lt;TABLE&gt;-STREAMING</c>).
/// The paths follow Snowflake's REST API reference; the gated integration test (SNOWFLAKE_TEST_*) exercises them.
/// </summary>
public sealed class SnowpipeStreaming(HttpClient http, SnowflakeAuth auth, string schema, string table, TimeProvider time) : IStreamingIngest
{
    public string Pipe => $"{table}-STREAMING";

    private string? _ingestHost;
    private string? _scopedToken;
    private DateTimeOffset _tokenExpires;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ChannelSession> OpenChannelAsync(string channel, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, $"v2/streaming/databases/{E(auth.Options.Database)}/schemas/{E(schema)}/pipes/{E(Pipe)}/channels/{E(channel)}",
            () => new StringContent("{}", Encoding.UTF8, "application/json"), ct);
        var json = await response.Content.ReadFromJsonAsync(ct);
        var continuation = Find(json, "next_continuation_token", "continuation_token")
            ?? throw new SnowflakeException($"Opening channel {channel}: no continuation token in the answer.", 200, false);
        var committed = Find(json, "last_committed_offset_token", "committed_offset_token", "offset_token");
        return new ChannelSession(channel, continuation, long.TryParse(committed, out var offset) ? offset : null);
    }

    public async Task<string> AppendAsync(ChannelSession session, ReadOnlyMemory<byte> ndjson, long offsetToken, CancellationToken ct)
    {
        var path = $"v2/streaming/data/databases/{E(auth.Options.Database)}/schemas/{E(schema)}/pipes/{E(Pipe)}/channels/{E(session.Channel)}/rows"
            + $"?continuationToken={E(session.ContinuationToken)}&offsetToken={offsetToken}";
        using var response = await SendAsync(HttpMethod.Post, path, () =>
        {
            var content = new ReadOnlyMemoryContent(ndjson);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return content;
        }, ct);
        var json = await response.Content.ReadFromJsonAsync(ct);
        return Find(json, "next_continuation_token", "continuation_token")
            ?? throw new SnowflakeException("Appending rows: no continuation token in the answer.", 200, false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Func<HttpContent> content, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (host, token) = await TokenAsync(refresh: attempt > 1, ct);
            var request = new HttpRequestMessage(method, new Uri($"https://{host}/{path}")) { Content = content() };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Snowflake-Authorization-Token-Type", "OAUTH");
            var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
            {
                response.Dispose();
                continue; // the scoped token expired: get a new one once
            }
            if (!response.IsSuccessStatusCode)
            {
                using (response) await SnowflakeAuth.ThrowAsync(response, $"Snowpipe Streaming {method} {path.Split('?')[0]}", ct);
            }
            return response;
        }
    }

    /// <summary>The ingest host and a token scoped to it (about an hour), renewed ten minutes before it ends.</summary>
    private async Task<(string Host, string Token)> TokenAsync(bool refresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_ingestHost is null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(auth.Options.AccountUrl, "v2/streaming/hostname"));
                auth.Authorize(request);
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) await SnowflakeAuth.ThrowAsync(response, "Snowpipe Streaming host discovery", ct);
                _ingestHost = Unquote(await response.Content.ReadAsStringAsync(ct)).Replace('_', '-');
            }
            if (refresh || _scopedToken is null || time.GetUtcNow() >= _tokenExpires)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(auth.Options.AccountUrl, "oauth/token"))
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                        ["scope"] = _ingestHost,
                        ["assertion"] = auth.Jwt(),
                    }),
                };
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) await SnowflakeAuth.ThrowAsync(response, "Snowpipe Streaming token", ct);
                _scopedToken = Unquote(await response.Content.ReadAsStringAsync(ct));
                _tokenExpires = time.GetUtcNow().AddMinutes(50);
            }
            return (_ingestHost, _scopedToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string E(string part) => Uri.EscapeDataString(part);

    private static string Unquote(string text)
    {
        text = text.Trim();
        if (text.StartsWith('{'))
        {
            var json = JsonNode.Parse(text);
            return Find(json, "hostname", "token", "access_token") ?? text;
        }
        return text.Trim('"');
    }

    /// <summary>The first string (or number) property with one of the names, anywhere in the answer.</summary>
    internal static string? Find(JsonNode? node, params string[] names)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in names)
                    if (obj[name] is JsonValue value && value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
                        return value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
                foreach (var (_, child) in obj)
                    if (Find(child, names) is { } found) return found;
                return null;
            case JsonArray array:
                foreach (var child in array)
                    if (Find(child, names) is { } found) return found;
                return null;
            default:
                return null;
        }
    }
}

internal static class ContentExtensions
{
    public static async Task<JsonNode?> ReadFromJsonAsync(this HttpContent content, CancellationToken ct)
    {
        var text = await content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }
}
