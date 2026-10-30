using System.Net;
using System.Security.Cryptography;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>Alerts open when a rule fires, escalate, clear by themselves, and carry Operators' acknowledgements and notes.</summary>
public sealed class AlertTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record Collector(string Site, string AgentId, HttpClient Client);

    private async Task<(HttpClient Admin, Collector Collector)> SiteAsync()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = api.Console();
        var site = "alert" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
        await admin.CreateSiteAsync(site);
        await RunAsync();
        Assert.Empty(await OpenAsync(admin, site)); // a site being set up raises nothing
        var (agent, cert) = await api.EnrollAsync(admin, site);
        return (admin, new Collector(site, agent, api.Agent(cert)));
    }

    private async Task BeatAsync(Collector c, double lag = 30, TableState state = TableState.Streaming)
    {
        await c.Client.PostAsync($"agents/{c.AgentId}/heartbeat", new Heartbeat
        {
            AgentId = c.AgentId, SiteId = c.Site, AgentVersion = "1.0.0", SentUtc = api.Time.GetUtcNow(), ConfigVersion = 1,
            BufferBytes = 0, BufferCapBytes = 1000, EventHubReachable = true,
            Sources = [new SourceStatus { SourceId = "compuweigh", Connected = true }],
            Tables = [new TableStatus { SourceId = "compuweigh", Table = "dbo.Tickets", State = state, Method = CaptureMethod.RowVersion, LagSeconds = lag }],
        }).OkAsync();
        await RunAsync();
    }

    private Task RunAsync() => api.Services.GetRequiredService<AlertMonitor>().RunOnceAsync(CancellationToken.None);

    private static async Task<List<AlertDto>> OpenAsync(HttpClient client, string site, string state = "open") =>
        await client.GetAsync<List<AlertDto>>($"alerts?siteId={site}&state={state}");

    [SkippableFact]
    public async Task A_lagging_table_opens_one_alert_that_escalates_and_clears_by_itself()
    {
        var (admin, collector) = await SiteAsync();
        await BeatAsync(collector);
        Assert.Empty(await OpenAsync(admin, collector.Site));

        await BeatAsync(collector, lag: 16 * 60);
        var warning = Assert.Single(await OpenAsync(admin, collector.Site));
        Assert.Equal(("table_lag", AlertSeverity.Warning, "compuweigh", "dbo.Tickets"), (warning.Rule, warning.Severity, warning.SourceId, warning.Table));
        Assert.Equal("compuweigh/dbo.Tickets lag 16 min.", warning.Message);

        var operatorUser = api.Console("olive@mercerlandmark.com", Roles.Operator);
        var acked = await (await operatorUser.PostAsync($"alerts/{warning.Id}/acknowledge", new AlertNoteRequest("Scale PC rebooting"))).ReadAsync<AlertDto>();
        Assert.Equal("olive@mercerlandmark.com", acked.AcknowledgedBy);
        Assert.Equal(("acknowledged", "Scale PC rebooting"), (Assert.Single(acked.Notes).Kind, acked.Notes[0].Text));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await api.Console("vic@mercerlandmark.com", Roles.Viewer).PostAsync($"alerts/{warning.Id}/acknowledge", new AlertNoteRequest(null))).StatusCode);

        // Still lagging, further: the same alert, now critical, and the acknowledgement no longer holds.
        await BeatAsync(collector, lag: 61 * 60);
        var critical = Assert.Single(await OpenAsync(admin, collector.Site));
        Assert.Equal((warning.Id, AlertSeverity.Critical), (critical.Id, critical.Severity));
        Assert.NotNull(critical.EscalatedUtc);
        Assert.Null(critical.AcknowledgedUtc);
        await BeatAsync(collector, lag: 20 * 60); // easing back to a warning keeps it as it is
        Assert.Equal(critical.Id, Assert.Single(await OpenAsync(admin, collector.Site)).Id);

        var noted = await (await operatorUser.PostAsync($"alerts/{warning.Id}/notes", new AlertNoteRequest("Called the site"))).ReadAsync<AlertDto>();
        Assert.Equal(2, noted.Notes.Count);
        Assert.Contains("400", $"{(int)(await operatorUser.PostAsync($"alerts/{warning.Id}/notes", new AlertNoteRequest("  "))).StatusCode}");

        await BeatAsync(collector);
        Assert.Empty(await OpenAsync(admin, collector.Site));
        var resolved = Assert.Single(await OpenAsync(admin, collector.Site, "resolved"));
        Assert.Equal(warning.Id, resolved.Id);
        Assert.NotNull(resolved.ResolvedUtc);
        Assert.Equal(HttpStatusCode.BadRequest, (await operatorUser.PostAsync($"alerts/{warning.Id}/acknowledge", new AlertNoteRequest(null))).StatusCode);

        // It comes back: a new alert, not the old one reopened.
        await BeatAsync(collector, lag: 16 * 60);
        Assert.NotEqual(warning.Id, Assert.Single(await OpenAsync(admin, collector.Site)).Id);

        var audit = await admin.GetStringAsync("audit?take=1000");
        Assert.Contains($"alert {warning.Id} acknowledged", audit);
        Assert.Contains("Called the site", audit);
    }

    [SkippableFact]
    public async Task A_silent_collector_raises_a_critical_alert_shown_on_the_fleet_view()
    {
        var (admin, collector) = await SiteAsync();
        await BeatAsync(collector, state: TableState.Blocked);
        Assert.Equal("table_blocked", Assert.Single(await OpenAsync(admin, collector.Site)).Rule);

        api.Time.Advance(TimeSpan.FromMinutes(6));
        await RunAsync();
        var open = await OpenAsync(admin, collector.Site);
        Assert.Equal(["heartbeat_missing", "table_blocked"], open.Select(a => a.Rule)); // critical first
        Assert.Equal(AlertSeverity.Critical, open[0].Severity);
        Assert.Equal(2, (await admin.GetAsync<FleetStatus>("status")).Sites.Single(s => s.SiteId == collector.Site).OpenAlerts);

        await BeatAsync(collector);
        Assert.Empty(await OpenAsync(admin, collector.Site));
    }
}
