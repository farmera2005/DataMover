using System.Text.RegularExpressions;

namespace Lakeflow.Contracts;

/// <summary>
/// Rules a config must meet before the control API saves it or an agent applies it.
/// Returns every problem rather than stopping at the first, so the console can show them together.
/// </summary>
public static partial class ConfigValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdPattern();

    private static readonly Dictionary<SourceEngine, CaptureMethod[]> AllowedMethods = new()
    {
        [SourceEngine.SqlServer] = [CaptureMethod.ChangeTracking, CaptureMethod.Cdc, CaptureMethod.RowVersion, CaptureMethod.TimestampPolling],
        [SourceEngine.Db2i] = [CaptureMethod.Journal, CaptureMethod.TimestampPolling],
        [SourceEngine.MySql] = [CaptureMethod.Binlog, CaptureMethod.TimestampPolling],
        [SourceEngine.Postgres] = [CaptureMethod.LogicalReplication, CaptureMethod.TimestampPolling],
        [SourceEngine.Access] = [CaptureMethod.KeyHashDiff],
        [SourceEngine.Files] = [CaptureMethod.FileWatch],
    };

    public static bool IsAllowed(SourceEngine engine, CaptureMethod method) => AllowedMethods[engine].Contains(method);

    public static IReadOnlyList<string> Validate(AgentConfig config)
    {
        var errors = new List<string>();
        CheckId(errors, "agent_id", config.AgentId);
        CheckId(errors, "site_id", config.SiteId);
        if (config.ConfigVersion < 1) errors.Add("config_version must be 1 or more.");
        if (config.ConfigPollSeconds is < 10 or > 3600) errors.Add("config_poll_seconds must be 10–3600.");
        if (config.HeartbeatSeconds is < 5 or > 300) errors.Add("heartbeat_seconds must be 5–300.");
        foreach (var hub in config.EventHubs)
        {
            if (string.IsNullOrWhiteSpace(hub.FullyQualifiedNamespace)) errors.Add($"event_hubs {hub.Division}: namespace is required.");
            if (string.IsNullOrWhiteSpace(hub.HubName)) errors.Add($"event_hubs {hub.Division}: hub_name is required.");
        }
        if (config.EventHubs.GroupBy(h => h.Division).Any(g => g.Count() > 1)) errors.Add("event_hubs lists a division twice.");
        if (config.Buffer.CapBytes < 10L * 1024 * 1024) errors.Add("buffer cap_bytes must be at least 10 MB.");
        if (config.Buffer.AlertPercent is < 1 or > 99) errors.Add("buffer alert_percent must be 1–99.");
        if (config.Buffer.SendBatchMaxEvents is < 1 or > 5000) errors.Add("buffer send_batch_max_events must be 1–5000.");

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in config.Sources)
        {
            var where = $"source '{source.SourceId}'";
            CheckId(errors, "source_id", source.SourceId);
            CheckId(errors, $"{where} source_system", source.SourceSystem);
            if (!sourceIds.Add(source.SourceId)) errors.Add($"{where} is listed twice.");
            if (config.HubFor(source.Division) is null) errors.Add($"{where}: no event hub for division {source.Division}.");
            if (string.IsNullOrWhiteSpace(source.Connection.Host)) errors.Add($"{where}: host is required.");
            if (source.Connection.Port is < 1 or > 65535) errors.Add($"{where}: port must be 1–65535.");
            if (source.Engine != SourceEngine.Files && source.Engine != SourceEngine.Access
                && string.IsNullOrWhiteSpace(source.Connection.CredentialName))
                errors.Add($"{where}: credential_name is required.");
            if (source.Connection.ConnectTimeoutSeconds is < 1 or > 60) errors.Add($"{where}: connect_timeout_seconds must be 1–60.");
            if (source.Connection.CommandTimeoutSeconds is < 1 or > 300) errors.Add($"{where}: command_timeout_seconds must be 1–300.");
            if (source.MaxRowsPerSecond < 1) errors.Add($"{where}: max_rows_per_second must be positive.");

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in source.Tables)
            {
                var t = $"{where} table '{table.Table}'";
                if (string.IsNullOrWhiteSpace(table.Table.Name)) errors.Add($"{where}: a table has no name.");
                if (!tables.Add(table.Table.QualifiedName)) errors.Add($"{t} is listed twice.");
                if (!IsAllowed(source.Engine, table.Method)) errors.Add($"{t}: {table.Method} capture is not available for {source.Engine}.");
                if (table.Method == CaptureMethod.TimestampPolling && string.IsNullOrWhiteSpace(table.WatermarkColumn))
                    errors.Add($"{t}: timestamp polling needs a watermark_column.");
                if (table.KeyColumns is { Count: 0 }) errors.Add($"{t}: key_columns, when given, cannot be empty.");
                if (table.PollIntervalSeconds is < 5 or > 3600) errors.Add($"{t}: poll_interval_seconds must be 5–3600.");
                if (table.LookbackSeconds is < 0 or > 86400) errors.Add($"{t}: lookback_seconds must be 0–86400.");
                if (table.LagWarningMinutes < 1) errors.Add($"{t}: lag_warning_minutes must be positive.");
                if (table.LagCriticalMinutes <= table.LagWarningMinutes) errors.Add($"{t}: lag_critical_minutes must exceed lag_warning_minutes.");
            }
        }

        foreach (var command in config.Commands)
        {
            var tableCommand = command.Type is CommandType.ResnapshotTable or CommandType.PauseTable or CommandType.ResumeTable;
            var sourceCommand = tableCommand || command.Type is CommandType.PauseSource or CommandType.ResumeSource;
            if (sourceCommand && string.IsNullOrEmpty(command.SourceId)) errors.Add($"command {command.CommandId}: {command.Type} needs source_id.");
            if (tableCommand && string.IsNullOrEmpty(command.Table)) errors.Add($"command {command.CommandId}: {command.Type} needs table.");
        }
        return errors;
    }

    private static void CheckId(List<string> errors, string field, string? value)
    {
        if (value is null || !IdPattern().IsMatch(value))
            errors.Add($"{field} '{value}' must be 1–64 lower-case letters, digits, '-' or '_', starting with a letter or digit.");
    }
}
