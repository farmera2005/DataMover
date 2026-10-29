using Lakeflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Snowflake;

/// <summary>Everything the Snowflake destination needs, as entered in the console.</summary>
public sealed record SnowflakeRuntimeSettings(
    SnowflakeOptions Options, string PrivateKeyPem, IReadOnlyList<(Division Division, string ListenConnectionString)> Hubs,
    TimeSpan SilverInterval, string ConsumerGroup = "snowflake")
{
    /// <summary>Changes when anything that needs a restart changes.</summary>
    public string Fingerprint => string.Join("|", Options, PrivateKeyPem.GetHashCode(), SilverInterval, ConsumerGroup,
        string.Join(",", Hubs.Select(h => $"{h.Division}:{h.ListenConnectionString.GetHashCode()}")));
}

public sealed record SilverStatus(DateTimeOffset? LastRunUtc, DateTimeOffset? LastSuccessUtc, long LastRows, int LastTables,
    string? LastError, DateTimeOffset? NextRunUtc, DateTimeOffset? LastPurgeUtc);

/// <summary>
/// Runs the Snowflake destination while it is enabled: creates Lakeflow's tables, forwards every configured hub to
/// <c>BRONZE.EVENTS</c>, builds silver on a schedule, and purges old tombstones daily. Restarts itself when the
/// settings change; stops when they are removed or disabled. Collectors and Fabric are not affected either way.
/// </summary>
public sealed class SnowflakeRunner(Func<SnowflakeRuntimeSettings, IHubReader[]> readers, HttpClient http, TimeProvider time, ILoggerFactory loggers)
{
    private readonly ILogger _log = loggers.CreateLogger<SnowflakeRunner>();
    private CancellationTokenSource? _stop;
    private Task _running = Task.CompletedTask;
    private string? _fingerprint;

    public ForwarderStatus Forwarding { get; } = new();
    public SilverStatus Silver { get; private set; } = new(null, null, 0, 0, null, null, null);
    public string? SetupError { get; private set; }
    public bool Running => _stop is not null;

    /// <summary>Starts, restarts or stops to match the settings (null: off).</summary>
    public async Task ApplyAsync(SnowflakeRuntimeSettings? settings)
    {
        if (settings?.Fingerprint == _fingerprint) return;
        await StopAsync();
        _fingerprint = settings?.Fingerprint;
        if (settings is null) return;
        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        _running = Task.Run(() => RunAsync(settings, ct), ct);
    }

    public async Task StopAsync()
    {
        if (_stop is null) return;
        await _stop.CancelAsync();
        try { await _running; }
        catch (OperationCanceledException) { }
        _stop.Dispose();
        _stop = null;
        Forwarding.Clear();
    }

    private async Task RunAsync(SnowflakeRuntimeSettings settings, CancellationToken ct)
    {
        using var key = new KeyPair(settings.PrivateKeyPem);
        var auth = new SnowflakeAuth(settings.Options, key, time);
        var sql = new SqlApi(http, auth, time);
        // Lakeflow's own tables first; retried until Snowflake is reachable and the setup script has been run.
        for (var failures = 1; !ct.IsCancellationRequested; failures++)
        {
            try
            {
                await sql.ExecuteScriptAsync(SnowflakeSilverStore.BootstrapScript(), ct);
                SetupError = null;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetupError = ex.Message;
                _log.LogWarning("Snowflake setup not possible yet ({Failures}): {Error}", failures, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, 15 * failures)), time, ct);
            }
        }
        var ingest = new SnowpipeStreaming(http, auth, SnowflakeSilverStore.Bronze, "EVENTS", time);
        var hubReaders = readers(settings);
        try
        {
            var forwarders = hubReaders.Select(r => new HubForwarder(r, ingest, Forwarding, time, loggers.CreateLogger<HubForwarder>()).RunAsync(ct));
            await Task.WhenAll([.. forwarders, SilverLoopAsync(sql, settings.SilverInterval, ct)]);
        }
        finally
        {
            foreach (var reader in hubReaders) await reader.DisposeAsync();
        }
    }

    private async Task SilverLoopAsync(SqlApi sql, TimeSpan interval, CancellationToken ct)
    {
        var job = new SilverJob(new SnowflakeSilverStore(sql), loggers.CreateLogger<SilverJob>());
        var store = new SnowflakeSilverStore(sql);
        while (!ct.IsCancellationRequested)
        {
            var started = time.GetUtcNow();
            Silver = Silver with { LastRunUtc = started, NextRunUtc = started + interval };
            try
            {
                var result = await job.RunAsync(ct);
                Silver = Silver with { LastSuccessUtc = time.GetUtcNow(), LastRows = result.Rows, LastTables = result.Tables, LastError = null };
                if (Silver.LastPurgeUtc is null || time.GetUtcNow() - Silver.LastPurgeUtc > TimeSpan.FromDays(1))
                {
                    var purged = await store.PurgeTombstonesAsync(SilverJob.TombstoneDays, ct);
                    Silver = Silver with { LastPurgeUtc = time.GetUtcNow() };
                    if (purged > 0) _log.LogInformation("Snowflake silver: {Purged} old tombstones purged", purged);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Silver = Silver with { LastError = ex.Message };
                _log.LogWarning("Snowflake silver run failed (repeated next time): {Error}", ex.Message);
            }
            var wait = started + interval - time.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, time, ct);
        }
    }
}
