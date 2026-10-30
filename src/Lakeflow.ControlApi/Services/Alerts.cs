using Lakeflow.ControlApi.Data;
using Lakeflow.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Services;

/// <summary>
/// Turns the fleet's findings into alerts: a new problem opens one, a problem that is still there keeps it up to date
/// (going from warning to critical escalates it and clears any acknowledgement), and a problem that is gone resolves it.
/// Sites still being set up raise nothing.
/// </summary>
public sealed class AlertBook(LakeflowDb db, TimeProvider time)
{
    public sealed record Changes(IReadOnlyList<Alert> Opened, IReadOnlyList<Alert> Escalated, IReadOnlyList<Alert> Resolved);

    public async Task<Changes> ApplyAsync(IEnumerable<FleetHealth.Finding> findings, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var current = new Dictionary<string, FleetHealth.Finding>();
        foreach (var finding in findings.Where(f => f.Severity is not null))
        {
            // Two findings with one key (two lag rows for one table): keep the worse.
            if (!current.TryGetValue(finding.Key, out var seen) || finding.Severity > seen.Severity) current[finding.Key] = finding;
        }
        var open = await db.Alerts.Where(a => a.ResolvedUtc == null).ToDictionaryAsync(a => a.Key, ct);
        var (opened, escalated, resolved) = (new List<Alert>(), new List<Alert>(), new List<Alert>());
        foreach (var (key, finding) in current)
        {
            var severity = finding.Severity!.Value;
            if (open.TryGetValue(key, out var alert))
            {
                if (severity > alert.Severity)
                {
                    (alert.EscalatedUtc, alert.AcknowledgedUtc, alert.AcknowledgedBy) = (now, null, null);
                    escalated.Add(alert);
                }
                (alert.Severity, alert.Message, alert.LastSeenUtc) = (severity, Truncate(finding.Message), now);
                continue;
            }
            alert = new Alert
            {
                Id = Guid.NewGuid(), Key = key, Rule = finding.Rule, Severity = severity, SiteId = finding.SiteId, SourceId = finding.SourceId,
                TableName = finding.Table, Message = Truncate(finding.Message), OpenedUtc = now, LastSeenUtc = now,
            };
            db.Alerts.Add(alert);
            opened.Add(alert);
        }
        foreach (var alert in open.Values.Where(a => !current.ContainsKey(a.Key)))
        {
            alert.ResolvedUtc = now;
            resolved.Add(alert);
        }
        await db.SaveChangesAsync(ct);
        return new Changes(opened, escalated, resolved);
    }

    private static string Truncate(string message) => message.Length <= 2000 ? message : message[..1997] + "...";
}

/// <summary>
/// Evaluates the alert rules every 30 seconds (reconciliation results, which change nightly, every 10 minutes), then sends
/// what is due by email and Teams.
/// </summary>
public sealed class AlertMonitor(IServiceScopeFactory scopes, TimeProvider time, IConfiguration configuration, ILogger<AlertMonitor> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(10);

    private IReadOnlyList<ReconciliationResult>? _checks;
    private DateTimeOffset _checksRead;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var fleet = scope.ServiceProvider.GetRequiredService<FleetStatusService>();
        if (_checks is null || time.GetUtcNow() - _checksRead > ReconciliationInterval)
            (_checks, _checksRead) = (await fleet.ReconciliationAsync(ct), time.GetUtcNow());
        var result = await fleet.ComputeAsync(_checks, ct);
        var changes = await scope.ServiceProvider.GetRequiredService<AlertBook>().ApplyAsync(result.Findings, ct);
        foreach (var alert in changes.Opened.Concat(changes.Escalated))
            log.LogWarning("Alert {Severity} at {Site}: {Message}", alert.Severity, alert.SiteId, alert.Message);
        foreach (var alert in changes.Resolved)
            log.LogInformation("Alert resolved at {Site}: {Message}", alert.SiteId, alert.Message);
        // Email and Teams: whatever is not sent yet (each channel's failures are its own).
        await scope.ServiceProvider.GetRequiredService<Notifier>().RunAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Alerts:Enabled", true)) return;
        // Give collectors a moment to report after the server starts.
        try { await Task.Delay(Interval, time, stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { log.LogError(ex, "Alert evaluation failed"); }
            try { await Task.Delay(Interval, time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
