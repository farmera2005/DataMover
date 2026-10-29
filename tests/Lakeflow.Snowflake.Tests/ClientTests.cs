using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lakeflow.Snowflake.Tests;

public sealed class ClientTests
{
    private static readonly string Pem = KeyPair.GeneratePrivateKeyPem();
    private static readonly SnowflakeOptions Options = new() { Account = "mercer-lakeflow", User = "lakeflow_forwarder" };

    private static JsonElement Part(string jwt, int index) =>
        JsonDocument.Parse(Convert.FromBase64String(Pad(jwt.Split('.')[index].Replace('-', '+').Replace('_', '/')))).RootElement;

    private static string Pad(string s) => s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');

    [Fact]
    public void Key_pair_jwt_names_the_account_user_and_key_and_is_signed_by_it()
    {
        using var key = new KeyPair(Pem);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var jwt = key.CreateJwt(Options, now);
        var payload = Part(jwt, 1);
        Assert.Equal("MERCER-LAKEFLOW.LAKEFLOW_FORWARDER", payload.GetProperty("sub").GetString());
        Assert.Equal($"MERCER-LAKEFLOW.LAKEFLOW_FORWARDER.{key.Fingerprint}", payload.GetProperty("iss").GetString());
        Assert.Equal(1_800_000_000, payload.GetProperty("iat").GetInt64());
        Assert.Equal(1_800_000_000 + 59 * 60, payload.GetProperty("exp").GetInt64());
        Assert.Equal("RS256", Part(jwt, 0).GetProperty("alg").GetString());

        using var publicKey = RSA.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
        var parts = jwt.Split('.');
        Assert.True(publicKey.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Convert.FromBase64String(Pad(parts[2].Replace('-', '+').Replace('_', '/'))), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.StartsWith("SHA256:", key.Fingerprint);
        Assert.DoesNotContain("BEGIN", key.PublicKey);
    }

    [Theory]
    [InlineData("mercer-lakeflow", "https://mercer-lakeflow.snowflakecomputing.com/", "MERCER-LAKEFLOW")]
    [InlineData("Mercer_Lakeflow", "https://mercer-lakeflow.snowflakecomputing.com/", "MERCER_LAKEFLOW")]
    [InlineData("xy12345.east-us-2.azure", "https://xy12345.east-us-2.azure.snowflakecomputing.com/", "XY12345")]
    public void Account_identifiers_give_the_url_and_jwt_account(string account, string url, string jwtAccount)
    {
        var options = new SnowflakeOptions { Account = account };
        Assert.Equal(url, options.AccountUrl.ToString());
        Assert.Equal(jwtAccount, options.JwtAccount);
    }

    [Fact]
    public async Task Sql_api_signs_in_with_the_jwt_polls_long_statements_and_reads_every_partition()
    {
        var polls = 0;
        var http = new FakeHttp((request, body) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/statements" => FakeHttp.Json("""{"statementHandle":"h1"}""", HttpStatusCode.Accepted),
            "/api/v2/statements/h1" when request.RequestUri.Query.Contains("partition=1") => FakeHttp.Json("""{"data":[["3","c"]]}"""),
            "/api/v2/statements/h1" when ++polls < 2 => FakeHttp.Json("""{"statementHandle":"h1"}""", HttpStatusCode.Accepted),
            _ => FakeHttp.Json("""
                {"statementHandle":"h1","resultSetMetaData":{"rowType":[{"name":"N"},{"name":"V"}],"partitionInfo":[{},{}]},
                 "data":[["1","a"],["2",null]]}
                """),
        });
        using var key = new KeyPair(Pem);
        var time = TimeProvider.System; // the client waits a second between polls
        var sql = new SqlApi(new HttpClient(http), new SnowflakeAuth(Options, key, time), time);

        var result = await sql.QueryAsync("SELECT 1", CancellationToken.None);

        Assert.Equal(["N", "V"], result.Columns);
        Assert.Equal([["1", "a"], ["2", null], ["3", "c"]], result.Rows);
        var post = http.Requests[0];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.StartsWith("Bearer ", post.Headers["Authorization"]);
        Assert.Equal("KEYPAIR_JWT", post.Headers["X-Snowflake-Authorization-Token-Type"]);
        var body = JsonDocument.Parse(post.Body).RootElement;
        Assert.Equal("SELECT 1", body.GetProperty("statement").GetString());
        Assert.Equal("LAKEFLOW", body.GetProperty("database").GetString());
        Assert.Equal("LAKEFLOW_WH", body.GetProperty("warehouse").GetString());
        Assert.Equal("1", body.GetProperty("parameters").GetProperty("MULTI_STATEMENT_COUNT").GetString());
        Assert.Contains("requestId=", post.Uri.Query);
    }

    [Fact]
    public async Task Sql_api_errors_carry_snowflakes_message_and_transient_ones_are_retried_with_the_same_request_id()
    {
        var calls = 0;
        var http = new FakeHttp((request, _) => ++calls == 1
            ? FakeHttp.Json("""{"message":"warehouse busy"}""", HttpStatusCode.ServiceUnavailable)
            : FakeHttp.Json("""{"code":"002003","message":"SQL compilation error: Object 'X' does not exist"}""", HttpStatusCode.UnprocessableEntity));
        using var key = new KeyPair(Pem);
        var time = TimeProvider.System; // the client waits two seconds before retrying
        var sql = new SqlApi(new HttpClient(http), new SnowflakeAuth(Options, key, time), time);

        var error = await Assert.ThrowsAsync<SnowflakeException>(() => sql.QueryAsync("SELECT * FROM X", CancellationToken.None));
        Assert.Contains("Object 'X' does not exist", error.Message);
        Assert.False(error.Retryable);
        Assert.Equal(2, http.Requests.Count);
        var first = System.Web.HttpUtility.ParseQueryString(http.Requests[0].Uri.Query)["requestId"];
        Assert.Equal(first, System.Web.HttpUtility.ParseQueryString(http.Requests[1].Uri.Query)["requestId"]);
        Assert.Contains("retry=true", http.Requests[1].Uri.Query);
    }

    [Fact]
    public async Task Streaming_discovers_the_ingest_host_scopes_a_token_opens_channels_and_appends_with_offset_tokens()
    {
        var http = new FakeHttp((request, body) => (request.Method.Method, request.RequestUri!.Host, request.RequestUri.AbsolutePath) switch
        {
            ("GET", "mercer-lakeflow.snowflakecomputing.com", "/v2/streaming/hostname") => FakeHttp.Json("ingest-abc.snowflakecomputing.com"),
            ("POST", "mercer-lakeflow.snowflakecomputing.com", "/oauth/token") => FakeHttp.Json("scoped-token-1"),
            ("PUT", "ingest-abc.snowflakecomputing.com", _) => FakeHttp.Json("""{"next_continuation_token":"c1","channel_status":{"last_committed_offset_token":"41"}}"""),
            ("POST", "ingest-abc.snowflakecomputing.com", _) => FakeHttp.Json("""{"next_continuation_token":"c2"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var key = new KeyPair(Pem);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var streaming = new SnowpipeStreaming(new HttpClient(http), new SnowflakeAuth(Options, key, time), "BRONZE", "EVENTS", time);

        var session = await streaming.OpenChannelAsync("LAKEFLOW_GRAIN_0", CancellationToken.None);
        Assert.Equal(("c1", 41L), (session.ContinuationToken, session.CommittedOffset));
        var next = await streaming.AppendAsync(session, Encoding.UTF8.GetBytes("{\"A\":1}\n"), 57, CancellationToken.None);
        Assert.Equal("c2", next);

        var token = http.Requests.Single(r => r.Uri.AbsolutePath == "/oauth/token");
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", token.Body);
        Assert.Contains("scope=ingest-abc.snowflakecomputing.com", token.Body);
        var open = http.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/v2/streaming/databases/LAKEFLOW/schemas/BRONZE/pipes/EVENTS-STREAMING/channels/LAKEFLOW_GRAIN_0", open.Uri.AbsolutePath);
        Assert.Equal("Bearer scoped-token-1", open.Headers["Authorization"]);
        Assert.Equal("OAUTH", open.Headers["X-Snowflake-Authorization-Token-Type"]);
        var append = http.Requests.Last();
        Assert.Equal("/v2/streaming/data/databases/LAKEFLOW/schemas/BRONZE/pipes/EVENTS-STREAMING/channels/LAKEFLOW_GRAIN_0/rows", append.Uri.AbsolutePath);
        Assert.Contains("continuationToken=c1", append.Uri.Query);
        Assert.Contains("offsetToken=57", append.Uri.Query);
        Assert.Equal("application/x-ndjson", append.Headers["Content-Type"]);
        // The host and token are reused.
        Assert.Single(http.Requests, r => r.Uri.AbsolutePath == "/v2/streaming/hostname");
    }

    [Fact]
    public void Bronze_rows_keep_values_exact_and_land_bad_events_with_their_body()
    {
        var envelope = """
            {"envelope_version":1,"site_id":"stmarys","source_id":"compuweigh","source_system":"compuweigh","table":"dbo.Tickets",
             "op":"update","key":{"id":7},"key_id":"{\"id\":7}","before":null,
             "after":{"id":7,"qty":123456789012.34560,"name":"x","ok":true,"gone":null,"tags":[1,2]},
             "source_ts":null,"capture_ts":"2026-10-28T12:00:00+00:00","seq":12345678901234,"partition_key":"stmarys|dbo.Tickets"}
            """;
        var row = JsonDocument.Parse(BronzeRows.Row("grain", "3", new HubEvent(99, DateTimeOffset.Parse("2026-10-28T12:00:01Z"), Encoding.UTF8.GetBytes(envelope)))).RootElement;
        Assert.Equal("grain", row.GetProperty("DIVISION").GetString());
        Assert.Equal("3", row.GetProperty("PARTITION_ID").GetString());
        Assert.Equal(99, row.GetProperty("EH_SEQUENCE").GetInt64());
        Assert.Equal("dbo.Tickets", row.GetProperty("TABLE_NAME").GetString());
        Assert.Equal("12345678901234", row.GetProperty("SEQ").GetRawText());
        var values = row.GetProperty("AFTER_VALUES");
        Assert.Equal("123456789012.34560", values.GetProperty("qty").GetString());
        Assert.Equal("true", values.GetProperty("ok").GetString());
        Assert.Equal(JsonValueKind.Null, values.GetProperty("gone").ValueKind);
        Assert.Equal("[1,2]", values.GetProperty("tags").GetString());
        Assert.Contains("123456789012.34560", row.GetProperty("AFTER_JSON").GetString());
        Assert.False(row.TryGetProperty("BEFORE_JSON", out _));
        Assert.Equal(BronzeRows.Columns.Order(), BronzeRows.Columns.Distinct().Order());

        var bad = JsonDocument.Parse(BronzeRows.Row("grain", "3", new HubEvent(100, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes("not json")))).RootElement;
        Assert.Equal("not json", bad.GetProperty("RAW_BODY").GetString());
        Assert.True(bad.TryGetProperty("PARSE_ERROR", out _));
        Assert.False(bad.TryGetProperty("OP", out _));
    }

    [Fact]
    public void Ndjson_requests_stay_under_the_limit_and_keep_order()
    {
        var rows = Enumerable.Range(0, 10).Select(i => ((long)i, Encoding.UTF8.GetBytes(new string('x', 99)))).ToList();
        var chunks = Ndjson.Chunks(rows, maxBytes: 350).ToList();
        Assert.All(chunks, c => Assert.True(c.Bytes.Length <= 350));
        Assert.Equal(10, chunks.Sum(c => c.Rows));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (long)i), chunks.SelectMany(c => Enumerable.Range((int)c.FirstSequence, c.Rows).Select(i => (long)i)));
        Assert.Equal(9, chunks[^1].LastSequence);
        Assert.Equal('\n', (char)chunks[0].Bytes[^1]);
    }
}
