using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lakeflow.Snowflake.Tests;

/// <summary>
/// Against a real Snowflake account, when one is configured (CI secrets or a developer's trial account):
/// SNOWFLAKE_TEST_ACCOUNT, SNOWFLAKE_TEST_USER, SNOWFLAKE_TEST_ROLE, SNOWFLAKE_TEST_WAREHOUSE, SNOWFLAKE_TEST_DATABASE
/// (a database for tests only: its BRONZE and SILVER schemas are dropped) and SNOWFLAKE_TEST_PRIVATE_KEY (PEM, the
/// user's key pair). Streams the collector fixture through Snowpipe Streaming and checks silver against the source.
/// </summary>
public sealed class SnowflakeIntegrationTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static readonly string? SkipReason = Env("SNOWFLAKE_TEST_ACCOUNT") is null || Env("SNOWFLAKE_TEST_PRIVATE_KEY") is null
        ? "Set SNOWFLAKE_TEST_ACCOUNT, _USER, _ROLE, _WAREHOUSE, _DATABASE and _PRIVATE_KEY to test against Snowflake." : null;

    [SkippableFact]
    public async Task The_fixture_streams_into_bronze_and_ends_in_silver_as_the_source_rows()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var options = new SnowflakeOptions
        {
            Account = Env("SNOWFLAKE_TEST_ACCOUNT")!, User = Env("SNOWFLAKE_TEST_USER") ?? "LAKEFLOW_FORWARDER",
            Role = Env("SNOWFLAKE_TEST_ROLE") ?? "LAKEFLOW_FORWARDER", Warehouse = Env("SNOWFLAKE_TEST_WAREHOUSE") ?? "LAKEFLOW_WH",
            Database = Env("SNOWFLAKE_TEST_DATABASE") ?? "LAKEFLOW_TEST",
        };
        using var key = new KeyPair(Env("SNOWFLAKE_TEST_PRIVATE_KEY")!.Replace("\\n", "\n"));
        var auth = new SnowflakeAuth(options, key, TimeProvider.System);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(11) };
        var sql = new SqlApi(http, auth, TimeProvider.System);
        await sql.ExecuteScriptAsync("DROP SCHEMA IF EXISTS SILVER; DROP SCHEMA IF EXISTS BRONZE", CancellationToken.None);
        await sql.ExecuteScriptAsync(SnowflakeSilverStore.BootstrapScript(), CancellationToken.None);

        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "agent_run.json")))!.AsObject();
        var envelopes = json["envelopes"]!.AsArray().Select(e => e!.ToJsonString()).ToList();
        var streaming = new SnowpipeStreaming(http, auth, "BRONZE", "EVENTS", TimeProvider.System);
        var session = await streaming.OpenChannelAsync("LAKEFLOW_TEST_0", CancellationToken.None);
        var rows = envelopes.Select((e, i) => ((long)i, BronzeRows.Row("grain", "0", new HubEvent(i, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes(e))))).ToList();
        foreach (var chunk in Ndjson.Chunks(rows))
            session = session with { ContinuationToken = await streaming.AppendAsync(session, chunk.Bytes, chunk.LastSequence, CancellationToken.None) };

        // Rows become visible once committed (seconds).
        for (var i = 0; i < 60; i++)
        {
            if ((await sql.QueryAsync("SELECT COUNT(*) FROM BRONZE.EVENTS", CancellationToken.None)).Scalar == envelopes.Count.ToString()) break;
            await Task.Delay(2000);
        }
        Assert.Equal(envelopes.Count.ToString(), (await sql.QueryAsync("SELECT COUNT(*) FROM BRONZE.EVENTS", CancellationToken.None)).Scalar);
        Assert.Equal((envelopes.Count - 1).ToString(), (await streaming.OpenChannelAsync("LAKEFLOW_TEST_0", CancellationToken.None)).CommittedOffset?.ToString());

        var result = await new SilverJob(new SnowflakeSilverStore(sql), NullLogger.Instance).RunAsync(CancellationToken.None);
        Assert.Equal("ok", Assert.Single(result.Reconciliations).Status);

        var expected = json["expected"]!.AsObject();
        var silver = await sql.QueryAsync("""SELECT "_KEY_ID", "name", "qty" FROM SILVER.COMPUWEIGH_TICKETS WHERE NOT "_DELETED" """, CancellationToken.None);
        Assert.Equal(expected.Count, silver.Rows.Count);
        foreach (var row in silver.Rows)
        {
            var source = expected[row[0]!]!.AsObject();
            Assert.Equal(source["name"]!.GetValue<string>(), row[1]);
            Assert.Equal(source["qty"]!.ToJsonString(), row[2]); // exact digits
        }
        // Running again with nothing new changes nothing.
        Assert.Equal(0, (await new SilverJob(new SnowflakeSilverStore(sql), NullLogger.Instance).RunAsync(CancellationToken.None)).Tables);
        var check = await sql.QueryAsync("SELECT STATUS FROM SILVER.LAKEFLOW_RECONCILIATION", CancellationToken.None);
        Assert.Equal("ok", Assert.Single(check.Rows)[0]);
    }
}
