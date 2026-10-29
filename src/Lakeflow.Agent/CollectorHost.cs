using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent;

/// <summary>One site's pipeline and the store it owns.</summary>
public sealed record SiteParts(LocalStore Store, AgentPipeline Pipeline);

/// <summary>
/// A collector: one enrolled identity serving every site assigned to it. Each site has its own pipeline, store
/// (buffer, checkpoints, credentials) and loops, so a slow, failing or offline site never holds up another. A central
/// collector serves many sites; an agent installed at a site is the same thing serving one.
/// </summary>
public sealed class CollectorHost : IAsyncDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly Func<string, SiteParts> _openSite;
    private readonly bool _ownsSites;
    private readonly bool _runLoops;
    private readonly ILogger _log;
    private readonly Dictionary<string, (SiteParts Parts, CancellationTokenSource Stop, Task Loops)> _sites = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="identityStore">Holds the collector's identity, cached config and its own events.</param>
    /// <param name="openSite">Opens (or creates) a site's store and pipeline.</param>
    /// <param name="ownsSites">Dispose site stores and pipelines when a site is removed or the host stops.</param>
    /// <param name="runLoops">Run each site's capture and delivery loops in the background (off in step-by-step tests).</param>
    public CollectorHost(LocalStore identityStore, Func<string, SiteParts> openSite, ILogger<CollectorHost> log, bool ownsSites = true, bool runLoops = true)
    {
        (IdentityStore, _openSite, _log, _ownsSites, _runLoops) = (identityStore, openSite, log, ownsSites, runLoops);
    }

    public LocalStore IdentityStore { get; }
    public CollectorConfig? Config { get; private set; }

    /// <summary>The sites currently served, by site id.</summary>
    public IReadOnlyDictionary<string, SiteParts> Sites
    {
        get
        {
            lock (_sites) return _sites.ToDictionary(s => s.Key, s => s.Value.Parts);
        }
    }

    /// <summary>Starts the sites of the last config received, so a restart works while the API is down.</summary>
    public async Task<bool> LoadCachedAsync(CancellationToken ct)
    {
        if (IdentityStore.LoadCollectorConfig() is not { } cached) return false;
        await ApplyAsync(cached, cache: false, ct);
        return true;
    }

    /// <summary>Starts newly assigned sites, stops unassigned ones (their data stays on disk), and applies each site's config.</summary>
    public async Task ApplyAsync(CollectorConfig config, bool cache, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wanted = config.Sites.ToDictionary(s => s.SiteId, StringComparer.Ordinal);
            foreach (var gone in Sites.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                _log.LogInformation("Site {SiteId} is no longer assigned to this collector; stopping it (its buffer is kept)", gone);
                await StopSiteAsync(gone);
            }
            foreach (var (siteId, siteConfig) in wanted)
            {
                using var scope = SiteScope(siteId);
                if (!Sites.ContainsKey(siteId)) StartSite(siteId);
                if (!await Sites[siteId].Pipeline.ApplyConfigAsync(siteConfig))
                    _log.LogError("Config for site {SiteId} was refused; the site keeps its previous config", siteId);
            }
            Config = config;
            if (cache) IdentityStore.SaveCollectorConfig(config);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartSite(string siteId)
    {
        var parts = _openSite(siteId);
        var stop = new CancellationTokenSource();
        var loops = _runLoops
            ? Task.WhenAll(
                LoopAsync(siteId, "capture", parts.Pipeline.RunCaptureOnceAsync, stop.Token),
                LoopAsync(siteId, "delivery", async ct => await parts.Pipeline.RunDeliveryOnceAsync(ct), stop.Token))
            : Task.CompletedTask;
        lock (_sites) _sites[siteId] = (parts, stop, loops);
        _log.LogInformation("Serving site {SiteId}", siteId);
    }

    private async Task StopSiteAsync(string siteId)
    {
        (SiteParts Parts, CancellationTokenSource Stop, Task Loops) site;
        lock (_sites)
        {
            if (!_sites.Remove(siteId, out site)) return;
        }
        await site.Stop.CancelAsync();
        await site.Loops;
        site.Stop.Dispose();
        if (_ownsSites)
        {
            await site.Parts.Pipeline.DisposeAsync();
            site.Parts.Store.Dispose();
        }
    }

    /// <summary>One round of capture and delivery for every site (what the loops do each second); for tests.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var (siteId, parts) in Sites)
        {
            using var scope = SiteScope(siteId);
            await parts.Pipeline.RunCaptureOnceAsync(ct);
            await parts.Pipeline.RunDeliveryOnceAsync(ct);
        }
    }

    /// <summary>Puts <c>site_id</c> on every log line written inside it, by any component (the scope flows with the call).</summary>
    public IDisposable? SiteScope(string siteId) => _log.BeginScope(new Dictionary<string, object> { ["site_id"] = siteId });

    private async Task LoopAsync(string siteId, string name, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        using var scope = SiteScope(siteId);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await step(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Steps handle their own failures; reaching here is a bug, so log it and keep the site running.
                _log.LogError(ex, "Unexpected error in the {Loop} loop of site {SiteId}", name, siteId);
            }
            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var siteId in Sites.Keys.ToList()) await StopSiteAsync(siteId);
        _gate.Dispose();
    }
}
