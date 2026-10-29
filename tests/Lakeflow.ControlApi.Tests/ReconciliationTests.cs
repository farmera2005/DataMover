using System.Security.Cryptography;
using Lakeflow.Contracts;
using Lakeflow.ControlApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>
/// The API reads the notebook's reconciliation table over the Fabric SQL endpoint; here the same table lives in the
/// test SQL Server (both speak T-SQL over TDS).
/// </summary>
public sealed class ReconciliationTests : IAsyncLifetime
{
    private static readonly string? Master = Environment.GetEnvironmentVariable("MSSQL_TEST_CONNECTION");

    /// <summary>The API with its reconciliation source pointed at a database in the test SQL Server.</summary>
    private sealed class FabricApiFactory : ApiFactory
    {
        public string SqlDatabase { get; } = "lakeflow_fabric_" + RandomNumberGenerator.GetHexString(8).ToLowerInvariant();

        public string SqlConnectionString => new SqlConnectionStringBuilder(Master) { InitialCatalog = SqlDatabase }.ConnectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddScoped<IReconciliationSource>(_ => new SqlEndpointReconciliation(SqlConnectionString)));
        }
    }

    private readonly FabricApiFactory _api = new();

    public async Task InitializeAsync()
    {
        if (Master is not { Length: > 0 }) return;
        await using var connection = new SqlConnection(Master);
        await connection.OpenAsync();
        await new SqlCommand($"CREATE DATABASE [{_api.SqlDatabase}]", connection).ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        if (Master is not { Length: > 0 }) return;
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(Master);
        await connection.OpenAsync();
        await new SqlCommand($"ALTER DATABASE [{_api.SqlDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_api.SqlDatabase}]", connection).ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Latest_results_show_in_the_console_and_a_mismatch_turns_the_site_amber()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        Skip.If(Master is not { Length: > 0 }, "Set MSSQL_TEST_CONNECTION: the Fabric SQL endpoint is stood in for by SQL Server.");
        var admin = _api.Console();
        var site = "rec" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
        await admin.CreateSiteAsync(site);
        var (agent, cert) = await _api.EnrollAsync(admin, site);
        await _api.Agent(cert).PostAsync($"agents/{agent}/heartbeat", new Heartbeat
        {
            AgentId = agent, SiteId = site, AgentVersion = "1.0.0", SentUtc = _api.Time.GetUtcNow(), ConfigVersion = 1, EventHubReachable = true,
        }).OkAsync();
        Assert.Equal(Health.Green, (await admin.GetAsync<FleetStatus>("status")).Sites.Single(s => s.SiteId == site).Health);

        // Before the notebook's first nightly run the results table does not exist yet: nothing to report, not an error.
        Assert.Empty(await admin.GetAsync<List<ReconciliationResult>>($"reconciliation?siteId={site}"));

        await using (var connection = new SqlConnection(_api.SqlConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand($"""
                IF OBJECT_ID('dbo.lakeflow_reconciliation') IS NULL
                CREATE TABLE dbo.lakeflow_reconciliation (checked_at datetime2, site_id varchar(64), source_id varchar(64),
                    source_system varchar(64), table_name varchar(260), silver_table varchar(300), as_of_seq bigint, capture_ts datetime2,
                    source_rows bigint, silver_rows bigint, source_checksum varchar(30), silver_checksum varchar(30), row_diff bigint,
                    mismatch_pct float, status varchar(20));
                INSERT dbo.lakeflow_reconciliation VALUES
                    (DATEADD(hour, -30, SYSUTCDATETIME()), '{site}', 'compuweigh', 'compuweigh', 'dbo.Tickets', 'silver.compuweigh_tickets', 10, NULL, 100, 100, '1', '1', 0, 0, 'ok'),
                    (DATEADD(hour, -6, SYSUTCDATETIME()), '{site}', 'compuweigh', 'compuweigh', 'dbo.Tickets', 'silver.compuweigh_tickets', 90, NULL, 1000, 990, '5', '6', -10, 1.0, 'mismatch'),
                    (DATEADD(day, -10, SYSUTCDATETIME()), '{site}', 'compuweigh', 'compuweigh', 'dbo.Old', 'silver.compuweigh_old', 1, NULL, 1, 2, '1', '2', 1, 100, 'mismatch');
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        var latest = Assert.Single(await admin.GetAsync<List<ReconciliationResult>>($"reconciliation?siteId={site}"));
        Assert.Equal(("dbo.Tickets", "mismatch", 1000L, 990L), (latest.Table, latest.Status, latest.SourceRows, latest.SilverRows));
        var status = (await admin.GetAsync<FleetStatus>("status")).Sites.Single(s => s.SiteId == site);
        Assert.Equal(Health.Amber, status.Health);
        Assert.Contains(status.Reasons, r => r.Contains("reconciliation: source 1000 rows, silver 990"));
    }
}
