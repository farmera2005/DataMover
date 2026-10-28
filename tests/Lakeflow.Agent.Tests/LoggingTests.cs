using Lakeflow.Contracts;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Xunit;

namespace Lakeflow.Agent.Tests;

public class LoggingTests
{
    private sealed class Collect : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    [Fact]
    public async Task Every_line_carries_site_source_and_table()
    {
        var sink = new Collect();
        var logger = new LoggerConfiguration().MinimumLevel.Debug().Enrich.FromLogContext()
            .Enrich.With(new AgentContextEnricher(() => "stmarys")).WriteTo.Sink(sink).CreateLogger();
        using var loggers = new SerilogLoggerFactory(logger);
        await using var harness = await StartWithLoggers(loggers);
        var source = harness.Source("cw", "Tickets");
        source.Upsert("Tickets", 1, new Dictionary<string, object?> { ["name"] = "a" });
        Assert.True(await harness.Pipeline.ApplyConfigAsync(Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets"))));
        await harness.RunAsync(TimeSpan.FromMinutes(1));

        Assert.NotEmpty(sink.Events);
        Assert.All(sink.Events, e =>
        {
            Assert.True(e.Properties.ContainsKey("site_id"));
            Assert.True(e.Properties.ContainsKey("source_id"));
            Assert.True(e.Properties.ContainsKey("table"));
        });
        var snapshot = sink.Events.First(e => e.MessageTemplate.Text.StartsWith("Snapshot starting"));
        Assert.Equal("\"cw\"", snapshot.Properties["source_id"].ToString());
        Assert.Equal("\"dbo.Tickets\"", snapshot.Properties["table"].ToString());
        Assert.Equal("\"stmarys\"", snapshot.Properties["site_id"].ToString());
        var applied = sink.Events.First(e => e.MessageTemplate.Text.StartsWith("Applied config"));
        Assert.Equal("\"-\"", applied.Properties["table"].ToString());
    }

    private static async Task<Harness> StartWithLoggers(Microsoft.Extensions.Logging.ILoggerFactory loggers)
    {
        var harness = new Harness { Loggers = loggers };
        await harness.ReopenAsync();
        return harness;
    }
}
