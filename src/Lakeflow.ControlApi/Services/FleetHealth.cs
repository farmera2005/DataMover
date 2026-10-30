using Lakeflow.Contracts;

namespace Lakeflow.ControlApi.Services;

/// <summary>
/// What is wrong with a site, from its latest heartbeat, using the spec's alert thresholds. One place, so the fleet view
/// (green/amber/red) and the alerts (<see cref="AlertBook"/>) agree.
/// </summary>
public static class FleetHealth
{
    public static readonly TimeSpan HeartbeatMissing = TimeSpan.FromMinutes(5);
    public const int SourceFailuresCritical = 3;
    public const double BufferWarningPercent = 80;

    public sealed record TableThresholds(int WarningMinutes, int CriticalMinutes);

    /// <summary>
    /// One thing wrong. <see cref="Severity"/> null means a site still being set up (no collector, no heartbeat yet): red
    /// in the fleet view, but not an alert. <see cref="Key"/> identifies it across evaluations, so it stays one alert.
    /// </summary>
    public sealed record Finding(string Rule, AlertSeverity? Severity, string SiteId, string? SourceId, string? Table, string Message, string? Extra = null)
    {
        public string Key => string.Join('|', Rule, SiteId, SourceId, Table, Extra);
    }

    public static class Rules
    {
        public const string Setup = "setup";
        public const string HeartbeatMissing = "heartbeat_missing";
        public const string SourceFailing = "source_failing";
        public const string TableLag = "table_lag";
        public const string TableBlocked = "table_blocked";
        public const string TableFailing = "table_failing";
        public const string Buffer = "buffer";
        public const string EventHubs = "event_hubs";
        public const string Reconciliation = "reconciliation";
        public const string HubMissing = "hub_missing";
    }

    public static IReadOnlyList<Finding> Findings(string siteId, bool hasAgent, Heartbeat? heartbeat, DateTimeOffset? lastHeartbeatUtc,
        DateTimeOffset now, Func<string, string, TableThresholds> thresholds, IEnumerable<ReconciliationResult>? reconciliation = null)
    {
        var found = new List<Finding>();
        void Add(string rule, AlertSeverity? severity, string message, string? source = null, string? table = null, string? extra = null) =>
            found.Add(new Finding(rule, severity, siteId, source, table, message, extra));

        if (!hasAgent) Add(Rules.Setup, null, "No collector assigned.");
        else if (heartbeat is null || lastHeartbeatUtc is null) Add(Rules.Setup, null, "No heartbeat received yet.");
        else
        {
            var silent = now - lastHeartbeatUtc.Value;
            if (silent > HeartbeatMissing) Add(Rules.HeartbeatMissing, AlertSeverity.Critical, $"No heartbeat for {Minutes(silent)}.");
            foreach (var source in heartbeat.Sources.Where(s => s.ConsecutiveFailures >= SourceFailuresCritical))
                Add(Rules.SourceFailing, AlertSeverity.Critical,
                    $"Source {source.SourceId} connection failing ({source.ConsecutiveFailures} attempts): {source.LastError}", source.SourceId);
            foreach (var table in heartbeat.Tables)
            {
                var limit = thresholds(table.SourceId, table.Table);
                var lag = TimeSpan.FromSeconds(table.LagSeconds);
                if (table.State == TableState.Paused) continue;
                if (lag.TotalMinutes > limit.CriticalMinutes)
                    Add(Rules.TableLag, AlertSeverity.Critical, $"{table.SourceId}/{table.Table} lag {Minutes(lag)}.", table.SourceId, table.Table);
                else if (lag.TotalMinutes > limit.WarningMinutes)
                    Add(Rules.TableLag, AlertSeverity.Warning, $"{table.SourceId}/{table.Table} lag {Minutes(lag)}.", table.SourceId, table.Table);
                if (table.State == TableState.Blocked)
                    Add(Rules.TableBlocked, AlertSeverity.Warning, $"{table.SourceId}/{table.Table} blocked: {table.LastError}", table.SourceId, table.Table);
                else if (table.State == TableState.Failing)
                    Add(Rules.TableFailing, AlertSeverity.Warning, $"{table.SourceId}/{table.Table} failing: {table.LastError}", table.SourceId, table.Table);
            }
            if (heartbeat.BufferPercent > BufferWarningPercent)
                Add(Rules.Buffer, AlertSeverity.Warning, $"Local buffer {heartbeat.BufferPercent:F0}% full.");
            if (!heartbeat.EventHubReachable) Add(Rules.EventHubs, AlertSeverity.Warning, $"Event Hubs unreachable: {heartbeat.LastError}");
        }
        // Spec: a row-count mismatch over 0.1% in the nightly reconciliation is a warning.
        foreach (var check in reconciliation ?? [])
            if (check.Status == "mismatch")
                Add(Rules.Reconciliation, AlertSeverity.Warning,
                    $"{check.SourceId}/{check.Table} reconciliation{(check.Destination == "fabric" ? "" : $" ({check.Destination})")}: source {check.SourceRows} rows, silver {check.SilverRows} ({check.MismatchPercent:F2}%).",
                    check.SourceId, check.Table, check.Destination);
        return found;
    }

    /// <summary>Green, amber (only warnings) or red (anything critical, or still being set up); reasons worst first.</summary>
    public static (Health Health, IReadOnlyList<string> Reasons) Summarize(IReadOnlyList<Finding> findings)
    {
        var red = findings.Where(f => f.Severity is null or AlertSeverity.Critical).Select(f => f.Message).ToList();
        var amber = findings.Where(f => f.Severity == AlertSeverity.Warning).Select(f => f.Message).ToList();
        return (red.Count > 0 ? Health.Red : amber.Count > 0 ? Health.Amber : Health.Green, [.. red, .. amber]);
    }

    private static string Minutes(TimeSpan span) => span.TotalMinutes < 120 ? $"{span.TotalMinutes:F0} min" : $"{span.TotalHours:F1} h";
}
