using System.Net;

namespace Lakeflow.Snowflake.Tests;

/// <summary>Answers requests from a script and records them.</summary>
public sealed class FakeHttp(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        lock (Requests) Requests.Add((request.Method, request.RequestUri!, headers, body));
        return answer(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
