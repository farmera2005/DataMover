using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Lakeflow.Agent;

/// <summary>
/// Every log line carries <c>site_id</c>, <c>source_id</c> and <c>table</c> ("-" where they do not apply): site from
/// the current config, source and table from the logging scope the source workers open.
/// </summary>
public sealed class AgentContextEnricher(Func<string?> siteId) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        logEvent.AddPropertyIfAbsent(factory.CreateProperty("site_id", siteId() ?? "-"));
        logEvent.AddPropertyIfAbsent(factory.CreateProperty("source_id", "-"));
        logEvent.AddPropertyIfAbsent(factory.CreateProperty("table", "-"));
    }
}

public static class AgentLogging
{
    /// <summary>Compact JSON, one file per day, 14 days kept (Application Insights forwarding is added with the control API).</summary>
    public static LoggerConfiguration Configure(LoggerConfiguration logger, string logDirectory, Func<string?> siteId) => logger
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Azure", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.With(new AgentContextEnricher(siteId))
        .WriteTo.File(new CompactJsonFormatter(), Path.Combine(logDirectory, "agent-.log"),
            rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 100L * 1024 * 1024,
            rollOnFileSizeLimit: true, shared: false)
        .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u3} [{site_id}/{source_id}/{table}] {Message:lj}{NewLine}{Exception}");
}
