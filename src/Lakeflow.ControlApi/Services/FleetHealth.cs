using Lakeflow.Contracts;

namespace Lakeflow.ControlApi.Services;

/// <summary>
/// Green/amber/red for a site from its latest heartbeat, using the spec's alert thresholds. One place, so the fleet
/// view and (later) email/Teams alerts agree.
/// </summary>
public static class FleetHealth
{
    public static readonly TimeSpan HeartbeatMissing = TimeSpan.FromMinutes(5);
    public const int SourceFailuresCritical = 3;
    public const double BufferWarningPercent = 80;

    public sealed record TableThresholds(int WarningMinutes, int CriticalMinutes);

    public static (Health Health, IReadOnlyList<string> Reasons) Evaluate(
        bool hasAgent, Heartbeat? heartbeat, DateTimeOffset? lastHeartbeatUtc, DateTimeOffset now,
        Func<string, string, TableThresholds> thresholds, IEnumerable<ReconciliationResult>? reconciliation = null)
    {
        var red = new List<string>();
        var amber = new List<string>();
        if (!hasAgent) red.Add("No agent enrolled.");
        else if (heartbeat is null || lastHeartbeatUtc is null) red.Add("No heartbeat received yet.");
        else
        {
            var silent = now - lastHeartbeatUtc.Value;
            if (silent > HeartbeatMissing) red.Add($"No heartbeat for {Minutes(silent)}.");
            foreach (var source in heartbeat.Sources.Where(s => s.ConsecutiveFailures >= SourceFailuresCritical))
                red.Add($"Source {source.SourceId} connection failing ({source.ConsecutiveFailures} attempts): {source.LastError}");
            foreach (var table in heartbeat.Tables)
            {
                var limit = thresholds(table.SourceId, table.Table);
                var lag = TimeSpan.FromSeconds(table.LagSeconds);
                if (table.State == TableState.Paused) continue;
                if (lag.TotalMinutes > limit.CriticalMinutes) red.Add($"{table.SourceId}/{table.Table} lag {Minutes(lag)}.");
                else if (lag.TotalMinutes > limit.WarningMinutes) amber.Add($"{table.SourceId}/{table.Table} lag {Minutes(lag)}.");
                if (table.State == TableState.Blocked) amber.Add($"{table.SourceId}/{table.Table} blocked: {table.LastError}");
                else if (table.State == TableState.Failing) amber.Add($"{table.SourceId}/{table.Table} failing: {table.LastError}");
            }
            if (heartbeat.BufferPercent > BufferWarningPercent) amber.Add($"Local buffer {heartbeat.BufferPercent:F0}% full.");
            if (!heartbeat.EventHubReachable) amber.Add($"Event Hubs unreachable: {heartbeat.LastError}");
        }
        // Spec: a row-count mismatch over 0.1% in the nightly reconciliation is a warning.
        foreach (var check in reconciliation ?? [])
            if (check.Status == "mismatch")
                amber.Add($"{check.SourceId}/{check.Table} reconciliation: source {check.SourceRows} rows, silver {check.SilverRows} ({check.MismatchPercent:F2}%).");
        var health = red.Count > 0 ? Health.Red : amber.Count > 0 ? Health.Amber : Health.Green;
        return (health, [.. red, .. amber]);
    }

    private static string Minutes(TimeSpan span) => span.TotalMinutes < 120 ? $"{span.TotalMinutes:F0} min" : $"{span.TotalHours:F1} h";
}
