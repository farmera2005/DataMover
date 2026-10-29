using System.Text;
using Lakeflow.Contracts;
using Lakeflow.Snowflake;
using Microsoft.Data.SqlClient;

namespace Lakeflow.ControlApi.Services;

/// <summary>Reads the Snowflake settings entered in the console into what the runner needs (null: off or incomplete).</summary>
public sealed class SnowflakeSettingsReader(SettingsStore settings)
{
    public async Task<(SnowflakeSetting? Setting, string? PrivateKeyPem)> GetAsync(CancellationToken ct)
    {
        var (setting, secret) = await settings.GetWithSecretAsync<SnowflakeSetting>(SettingsStore.Snowflake, ct);
        return (setting, secret is null ? null : Encoding.UTF8.GetString(secret));
    }

    public static SnowflakeOptions Options(SnowflakeSetting s) => new()
    {
        Account = s.Account, User = s.User, Role = s.Role, Warehouse = s.Warehouse, Database = s.Database,
    };

    public async Task<SnowflakeRuntimeSettings?> RuntimeAsync(CancellationToken ct)
    {
        var (setting, pem) = await GetAsync(ct);
        if (setting is not { Enabled: true } || pem is null || string.IsNullOrWhiteSpace(setting.Account)) return null;
        var hubs = new List<(Division, string)>();
        foreach (var division in Enum.GetValues<Division>())
            if (await settings.GetWithSecretAsync<EventHubSetting>(SettingsStore.SnowflakeHub(division), ct) is ({ } hub, { } key))
                hubs.Add((division, $"Endpoint=sb://{hub.Namespace}/;SharedAccessKeyName={hub.PolicyName};SharedAccessKey={Encoding.UTF8.GetString(key)};EntityPath={hub.HubName}"));
        return hubs.Count == 0 ? null : new SnowflakeRuntimeSettings(Options(setting), pem, hubs, TimeSpan.FromMinutes(setting.SilverIntervalMinutes));
    }
}

/// <summary>Keeps the Snowflake runner in step with the console's settings (checked every 30 s, and right after a save).</summary>
public sealed class SnowflakeHost(IServiceScopeFactory scopes, SnowflakeRunner runner, ILogger<SnowflakeHost> log) : BackgroundService
{
    private readonly SemaphoreSlim _changed = new(0, 1);

    /// <summary>Called after the settings change, so the runner restarts at once.</summary>
    public void Changed()
    {
        if (_changed.CurrentCount == 0) _changed.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await runner.ApplyAsync(await scope.ServiceProvider.GetRequiredService<SnowflakeSettingsReader>().RuntimeAsync(stoppingToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Could not apply the Snowflake settings: {Error}", ex.Message);
            }
            try { await _changed.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        await runner.StopAsync();
    }
}

/// <summary>Latest reconciliation results from Snowflake's <c>SILVER.LAKEFLOW_RECONCILIATION</c> (none when not set up).</summary>
public sealed class SnowflakeReconciliation(SnowflakeSettingsReader reader, IHttpClientFactory http, TimeProvider time) : IReconciliationSource
{
    public async Task<IReadOnlyList<ReconciliationResult>> LatestAsync(string? siteId, CancellationToken ct)
    {
        var (setting, pem) = await reader.GetAsync(ct);
        if (setting is not { Enabled: true } || pem is null) return [];
        using var key = new KeyPair(pem);
        var sql = new SqlApi(http.CreateClient("snowflake"), new SnowflakeAuth(SnowflakeSettingsReader.Options(setting), key, time), time);
        SqlResult result;
        try
        {
            result = await sql.QueryAsync($"""
                SELECT TO_VARCHAR(CHECKED_AT, 'YYYY-MM-DD"T"HH24:MI:SS"Z"'), SITE_ID, SOURCE_ID, SOURCE_SYSTEM, TABLE_NAME, SILVER_TABLE, AS_OF_SEQ,
                       SOURCE_ROWS, SILVER_ROWS, SOURCE_CHECKSUM, SILVER_CHECKSUM, ROW_DIFF, MISMATCH_PCT, STATUS
                FROM {SnowflakeSilverStore.Reconciliation}
                WHERE CHECKED_AT >= DATEADD(day, -3, SYSDATE()) {(siteId is null ? "" : $"AND SITE_ID = {Sql.Literal(siteId)}")}
                QUALIFY ROW_NUMBER() OVER (PARTITION BY SITE_ID, SOURCE_ID, TABLE_NAME ORDER BY CHECKED_AT DESC) = 1
                ORDER BY SITE_ID, SOURCE_ID, TABLE_NAME
                """, ct);
        }
        catch (SnowflakeException ex) when (ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return []; // not set up yet
        }
        var n = System.Globalization.CultureInfo.InvariantCulture;
        return result.Rows.Select(r => new ReconciliationResult
        {
            CheckedUtc = DateTimeOffset.Parse(r[0]!, n), SiteId = r[1]!, SourceId = r[2]!, SourceSystem = r[3]!, Table = r[4]!, SilverTable = r[5]!,
            AsOfSeq = long.Parse(r[6]!, n), SourceRows = long.Parse(r[7]!, n), SilverRows = long.Parse(r[8]!, n), SourceChecksum = r[9],
            SilverChecksum = r[10], RowDiff = long.Parse(r[11]!, n), MismatchPercent = double.Parse(r[12]!, n), Status = r[13]!, Destination = "snowflake",
        }).ToList();
    }
}

/// <summary>Fabric's and Snowflake's results together; one destination being down never hides the other's.</summary>
public sealed class AllReconciliation(SettingsReconciliation fabric, SnowflakeReconciliation snowflake, ILogger<AllReconciliation> log) : IReconciliationSource
{
    public async Task<IReadOnlyList<ReconciliationResult>> LatestAsync(string? siteId, CancellationToken ct)
    {
        var results = new List<ReconciliationResult>();
        foreach (var (name, source) in new (string, IReconciliationSource)[] { ("Fabric", fabric), ("Snowflake", snowflake) })
        {
            try
            {
                results.AddRange(await source.LatestAsync(siteId, ct));
            }
            catch (Exception ex) when (ex is SqlException or SnowflakeException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning("{Destination} reconciliation results unavailable: {Error}", name, ex.Message);
            }
        }
        return results;
    }
}
