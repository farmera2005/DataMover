using Lakeflow.ControlApi.Data;
using Lakeflow.Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Services;

/// <summary>The fleet view and every site's findings, from the stored heartbeats; used by <c>/status</c> and the alert monitor.</summary>
public sealed class FleetStatusService(LakeflowDb db, TimeProvider time, IReconciliationSource reconciliation, ConfigBuilder builder,
    ILogger<FleetStatusService> log)
{
    public sealed record Result(FleetStatus Fleet, IReadOnlyList<FleetHealth.Finding> Findings);

    /// <summary>The latest reconciliation results; the fleet view must not go down with the lakehouse.</summary>
    public async Task<IReadOnlyList<ReconciliationResult>> ReconciliationAsync(CancellationToken ct)
    {
        try
        {
            return await reconciliation.LatestAsync(null, ct);
        }
        catch (SqlException ex)
        {
            log.LogWarning("Reconciliation results unavailable: {Error}", ex.Message);
            return [];
        }
    }

    /// <param name="checks">Reconciliation results to use; null reads them now.</param>
    public async Task<Result> ComputeAsync(IReadOnlyList<ReconciliationResult>? checks, CancellationToken ct)
    {
        checks ??= await ReconciliationAsync(ct);
        var now = time.GetUtcNow();
        var sites = await db.Sites.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var agents = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.AgentId, ct);
        var beats = await db.SiteHeartbeats.AsNoTracking().ToDictionaryAsync(h => h.SiteId, ct);
        var tables = await db.Tables.AsNoTracking().ToListAsync(ct);
        var divisions = await db.Sources.Select(s => new { s.SiteId, s.Division }).Distinct().ToListAsync(ct);
        var openAlerts = await db.Alerts.Where(a => a.ResolvedUtc == null).GroupBy(a => a.SiteId)
            .Select(g => new { SiteId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.SiteId, g => g.Count, ct);
        var hubs = await builder.HubsAsync(ct);
        var statuses = new List<SiteStatus>();
        var all = new List<FleetHealth.Finding>();
        foreach (var site in sites)
        {
            var agent = site.AgentId is { } id ? agents.GetValueOrDefault(id) : null;
            if (agent?.RevokedUtc is not null) agent = null;
            // Only a heartbeat from the collector now serving the site counts.
            var beat = agent is not null && beats.TryGetValue(site.SiteId, out var b) && b.AgentId == agent.AgentId ? b : null;
            var heartbeat = beat is null ? null : EnvelopeJson.Deserialize<Heartbeat>(beat.Json);
            var findings = FleetHealth.Findings(site.SiteId, agent is not null, heartbeat, beat?.ReceivedUtc, now, (sourceId, table) =>
                tables.FirstOrDefault(t => t.SiteId == site.SiteId && t.SourceId == sourceId && t.TableName == table) is { } t
                    ? new FleetHealth.TableThresholds(t.LagWarningMinutes, t.LagCriticalMinutes)
                    : new FleetHealth.TableThresholds(15, 60),
                checks.Where(c => c.SiteId == site.SiteId)).ToList();
            // Without its Event Hub the site is not sent to its collector at all; say so first.
            foreach (var missing in divisions.Where(d => d.SiteId == site.SiteId && !hubs.ContainsKey(d.Division)).Select(d => d.Division).Reverse())
                findings.Insert(0, new FleetHealth.Finding(FleetHealth.Rules.HubMissing, AlertSeverity.Critical, site.SiteId, null, null,
                    $"The Event Hub for {LakeNames.HubName(missing)} is not set up yet (Configuration, then Destinations).", missing.ToString()));
            var (health, reasons) = FleetHealth.Summarize(findings);
            all.AddRange(findings);
            statuses.Add(new SiteStatus
            {
                SiteId = site.SiteId, Name = site.Name, Latitude = site.Latitude, Longitude = site.Longitude,
                Health = health, Reasons = reasons, AgentId = agent?.AgentId, AgentVersion = agent?.AgentVersion,
                LastHeartbeatUtc = beat?.ReceivedUtc,
                WorstLagSeconds = heartbeat?.Tables.Select(t => t.LagSeconds).DefaultIfEmpty(0).Max(),
                BufferBytes = heartbeat?.BufferBytes, BufferPercent = heartbeat?.BufferPercent, Heartbeat = heartbeat,
                OpenAlerts = openAlerts.GetValueOrDefault(site.SiteId),
                Divisions = divisions.Where(d => d.SiteId == site.SiteId).Select(d => d.Division).Distinct().Order().ToList(),
            });
        }
        var fleet = new FleetStatus(now, statuses.Count(s => s.Health == Health.Green), statuses.Count(s => s.Health == Health.Amber),
            statuses.Count(s => s.Health == Health.Red), statuses);
        return new Result(fleet, all);
    }
}
