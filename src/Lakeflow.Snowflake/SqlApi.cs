using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lakeflow.Snowflake;

/// <summary>Rows from a statement, every value as Snowflake's text (or null).</summary>
public sealed record SqlResult(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows)
{
    public static readonly SqlResult Empty = new([], []);

    public string? Scalar => Rows.Count > 0 && Rows[0].Length > 0 ? Rows[0][0] : null;
}

/// <summary>
/// Runs statements through Snowflake's SQL API (<c>POST /api/v2/statements</c>), signed in with the key-pair JWT, so
/// no driver is needed. Each request is its own session: work that spans requests is written to be idempotent rather
/// than wrapped in a transaction (see <see cref="SilverJob"/>). A request is retried with the same request id
/// (<c>retry=true</c>), so Snowflake runs it at most once.
/// </summary>
public sealed class SqlApi(HttpClient http, SnowflakeAuth auth, TimeProvider time)
{
    public const int TimeoutSeconds = 600;

    public async Task<SqlResult> QueryAsync(string statement, CancellationToken ct) => await RunAsync(statement, 1, ct);

    /// <summary>Several statements separated by <c>;</c>, run in order in one session; stops at the first error.</summary>
    public async Task ExecuteScriptAsync(string script, CancellationToken ct) => await RunAsync(script, 0, ct);

    private async Task<SqlResult> RunAsync(string statement, int statementCount, CancellationToken ct)
    {
        var options = auth.Options;
        var body = new JsonObject
        {
            ["statement"] = statement,
            ["timeout"] = TimeoutSeconds,
            ["database"] = options.Database,
            ["warehouse"] = options.Warehouse,
            ["role"] = options.Role,
            ["parameters"] = new JsonObject
            {
                ["MULTI_STATEMENT_COUNT"] = statementCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["TIMEZONE"] = "UTC",
                ["QUERY_TAG"] = "lakeflow",
            },
        };
        var requestId = Guid.NewGuid();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var url = new Uri(options.AccountUrl, $"api/v2/statements?requestId={requestId}" + (attempt > 1 ? "&retry=true" : ""));
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
                auth.Authorize(request);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await http.SendAsync(request, ct);
                return await ReadAsync(response, ct);
            }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested
                && (ex is HttpRequestException or TaskCanceledException || ex is SnowflakeException { Retryable: true }))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), time, ct);
            }
        }
    }

    private async Task<SqlResult> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // 202: still running; poll its handle.
        while (response.StatusCode == HttpStatusCode.Accepted)
        {
            var handle = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["statementHandle"]?.GetValue<string>()
                ?? throw new SnowflakeException("Snowflake accepted the statement without a handle.", 202, true);
            await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
            response.Dispose();
            response = await GetAsync($"api/v2/statements/{handle}", ct);
        }
        if (!response.IsSuccessStatusCode) await SnowflakeAuth.ThrowAsync(response, "Snowflake SQL", ct);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? [];
        var columns = json["resultSetMetaData"]?["rowType"]?.AsArray().Select(c => c?["name"]?.GetValue<string>() ?? "").ToList() ?? [];
        var rows = new List<string?[]>();
        AddRows(json, rows);
        var partitions = json["resultSetMetaData"]?["partitionInfo"]?.AsArray().Count ?? 1;
        var handleText = json["statementHandle"]?.GetValue<string>();
        for (var p = 1; p < partitions && handleText is not null; p++)
        {
            using var part = await GetAsync($"api/v2/statements/{handleText}?partition={p}", ct);
            if (!part.IsSuccessStatusCode) await SnowflakeAuth.ThrowAsync(part, "Snowflake SQL", ct);
            AddRows(await part.Content.ReadFromJsonAsync<JsonObject>(ct) ?? [], rows);
        }
        return new SqlResult(columns, rows);
    }

    private static void AddRows(JsonObject json, List<string?[]> rows)
    {
        foreach (var row in json["data"]?.AsArray() ?? [])
            rows.Add(row!.AsArray().Select(v => v?.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v?.ToJsonString()).ToArray());
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(auth.Options.AccountUrl, path));
        auth.Authorize(request);
        request.Headers.Accept.ParseAdd("application/json");
        return await http.SendAsync(request, ct);
    }
}

/// <summary>SQL text helpers: literals and identifiers are always escaped, never interpolated raw.</summary>
public static class Sql
{
    /// <summary>A string literal (Snowflake treats backslash as an escape inside quotes, so it is doubled too).</summary>
    public static string Literal(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";

    /// <summary>A quoted (case-sensitive) identifier.</summary>
    public static string Ident(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
