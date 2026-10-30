using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>Stands in for Entra ID, Graph and a Teams workflow, and records what Lakeflow sent.</summary>
public sealed class FakeNotificationHttp : HttpMessageHandler
{
    public sealed record Sent(HttpMethod Method, Uri Url, string Body, string? Authorization);

    public List<Sent> Requests { get; } = [];
    public Func<Sent, HttpResponseMessage?>? Respond { get; set; }

    public IEnumerable<JsonObject> Mail => Requests.Where(r => r.Url.AbsolutePath.EndsWith("/sendMail")).Select(r => (JsonObject)JsonNode.Parse(r.Body)!);
    public IEnumerable<string> MailSubjects => Mail.Select(m => m["message"]!["subject"]!.GetValue<string>());
    public IEnumerable<string> TeamsTexts => Requests.Where(r => r.Url.Host == "teams.example")
        .Select(r => string.Join(" | ", JsonNode.Parse(r.Body)!["attachments"]![0]!["content"]!["body"]!.AsArray().Select(b => b!["text"]!.GetValue<string>())));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var sent = new Sent(request.Method, request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct),
            request.Headers.Authorization?.ToString());
        lock (Requests) Requests.Add(sent);
        if (Respond?.Invoke(sent) is { } custom) return custom;
        if (sent.Url.Host == "login.microsoftonline.com")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"graph-token","expires_in":3599}""", Encoding.UTF8, "application/json") };
        return new HttpResponseMessage(HttpStatusCode.Accepted);
    }
}

public sealed class NotifyApiFactory : ApiFactory
{
    public FakeNotificationHttp Http { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddHttpClient("notifications").ConfigurePrimaryHttpMessageHandler(() => Http));
    }
}

/// <summary>Alert email (Graph) and Teams: what is sent when, quiet hours, harvest seasons, failures, and write-only secrets.</summary>
public sealed class NotificationTests : IAsyncLifetime
{
    private readonly NotifyApiFactory _api = new();
    private const string Secret = "graph-client-secret-value";
    private const string Workflow = "https://teams.example/workflows/abc/triggers/manual/paths/invoke?sig=teams-signature-value";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static async Task<string> ProblemsAsync(HttpResponseMessage response) => $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";

    private async Task<HttpClient> SetUpAsync(AlertSeverity teamsMinimum = AlertSeverity.Critical)
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = _api.Console();
        await admin.CreateSiteAsync("stmarys");
        await admin.PutAsync("settings/notifications/email", new EmailNotificationRequest(true, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Secret,
            "lakeflow-alerts@mercerlandmark.com", ["it@mercerlandmark.com", " ops@mercerlandmark.com "], AlertSeverity.Warning)).OkAsync();
        await admin.PutAsync("settings/notifications/teams", new TeamsNotificationRequest(true, Workflow, teamsMinimum)).OkAsync();
        return admin;
    }

    private static FleetHealth.Finding Lag(AlertSeverity severity, int minutes = 20) =>
        new(FleetHealth.Rules.TableLag, severity, "stmarys", "compuweigh", "dbo.Tickets", $"compuweigh/dbo.Tickets lag {minutes} min.");

    /// <summary>One monitor round with these findings standing for the fleet.</summary>
    private async Task RoundAsync(params FleetHealth.Finding[] findings)
    {
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AlertBook>().ApplyAsync(findings, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<Notifier>().RunAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Alerts_are_sent_once_when_raised_again_when_worse_and_when_cleared()
    {
        await SetUpAsync();
        await RoundAsync(Lag(AlertSeverity.Warning));
        var subject = Assert.Single(_api.Http.MailSubjects);
        Assert.Equal("[Lakeflow] Warning: Site stmarys: compuweigh/dbo.Tickets lag 20 min.", subject);
        var mail = Assert.Single(_api.Http.Mail);
        Assert.Equal(["it@mercerlandmark.com", "ops@mercerlandmark.com"],
            mail["message"]!["toRecipients"]!.AsArray().Select(r => r!["emailAddress"]!["address"]!.GetValue<string>()));
        var send = _api.Http.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/sendMail"));
        Assert.Equal("https://graph.microsoft.com/v1.0/users/lakeflow-alerts%40mercerlandmark.com/sendMail", send.Url.AbsoluteUri);
        Assert.Equal("Bearer graph-token", send.Authorization);
        Assert.Contains("client_secret=graph-client-secret-value", _api.Http.Requests.Single(r => r.Url.Host == "login.microsoftonline.com").Body);
        Assert.Empty(_api.Http.TeamsTexts); // Teams gets critical only

        await RoundAsync(Lag(AlertSeverity.Warning, 25));
        Assert.Single(_api.Http.MailSubjects); // nothing new

        await RoundAsync(Lag(AlertSeverity.Critical, 61));
        Assert.Equal("[Lakeflow] Now critical: Site stmarys: compuweigh/dbo.Tickets lag 61 min.", _api.Http.MailSubjects.Last());
        Assert.Contains("**Critical** · Site stmarys | compuweigh/dbo.Tickets lag 61 min.", Assert.Single(_api.Http.TeamsTexts));

        await RoundAsync();
        Assert.StartsWith("[Lakeflow] Cleared: Site stmarys", _api.Http.MailSubjects.Last());
        Assert.Contains("**Cleared**", _api.Http.TeamsTexts.Last());
        Assert.Equal(3, _api.Http.MailSubjects.Count());
        Assert.Single(_api.Http.Requests, r => r.Url.Host == "login.microsoftonline.com"); // the token is reused

        // Several at once go in one message.
        await RoundAsync(Lag(AlertSeverity.Critical, 70), new FleetHealth.Finding(FleetHealth.Rules.Buffer, AlertSeverity.Warning, "stmarys", null, null, "Local buffer 85% full."));
        Assert.Equal("[Lakeflow] 1 critical, 1 warning at Site stmarys", _api.Http.MailSubjects.Last());
    }

    [SkippableFact]
    public async Task Quiet_hours_hold_warnings_but_not_critical_alerts_and_harvest_season_lifts_them()
    {
        var admin = await SetUpAsync(teamsMinimum: AlertSeverity.Warning);
        var local = TimeZoneInfo.ConvertTime(_api.Time.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var quiet = new NotificationScheduleDto
        {
            QuietStart = local.AddHours(-1).ToString("HH:mm"), QuietEnd = local.AddHours(1).ToString("HH:mm"),
        };
        Assert.Contains("both a start and an end", await ProblemsAsync(await admin.PutAsync("settings/notifications/schedule", quiet with { QuietEnd = null })));
        Assert.Contains("09-15", await ProblemsAsync(await admin.PutAsync("settings/notifications/schedule", quiet with { HarvestSeasons = [new("Fall", "Sept 15", "11-30")] })));
        await admin.PutAsync("settings/notifications/schedule", quiet).OkAsync();

        await RoundAsync(Lag(AlertSeverity.Warning));
        Assert.Empty(_api.Http.MailSubjects);
        var buffer = new FleetHealth.Finding(FleetHealth.Rules.HeartbeatMissing, AlertSeverity.Critical, "stmarys", null, null, "No heartbeat for 6 min.");
        await RoundAsync(Lag(AlertSeverity.Warning), buffer);
        Assert.Equal("[Lakeflow] Critical: Site stmarys: No heartbeat for 6 min.", Assert.Single(_api.Http.MailSubjects));
        Assert.Single(_api.Http.TeamsTexts);

        // Harvest season: quiet hours do not apply, so the held warning goes.
        var today = DateOnly.FromDateTime(local.DateTime);
        await admin.PutAsync("settings/notifications/schedule", quiet with
        {
            HarvestSeasons = [new("Fall harvest", today.AddDays(-3).ToString("MM-dd"), today.AddDays(30).ToString("MM-dd"))],
        }).OkAsync();
        await RoundAsync(Lag(AlertSeverity.Warning), buffer);
        Assert.Equal("[Lakeflow] Warning: Site stmarys: compuweigh/dbo.Tickets lag 20 min.", _api.Http.MailSubjects.Last());
    }

    [SkippableFact]
    public async Task A_failing_channel_backs_off_says_why_and_catches_up_without_affecting_the_other()
    {
        var admin = await SetUpAsync(teamsMinimum: AlertSeverity.Warning);
        _api.Http.Respond = r => r.Url.AbsolutePath.EndsWith("/sendMail")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""") }
            : null;
        await RoundAsync(Lag(AlertSeverity.Warning));
        Assert.Single(_api.Http.TeamsTexts);
        var email = (await admin.GetAsync<NotificationSettingsDto>("settings/notifications")).Status.Single(s => s.Channel == "email");
        Assert.Contains("ErrorAccessDenied", email.LastError);
        Assert.Contains("Mail.Send", email.LastError);
        Assert.NotNull(email.NextAttemptUtc);

        var attempts = _api.Http.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/sendMail"));
        await RoundAsync(Lag(AlertSeverity.Warning));
        Assert.Equal(attempts, _api.Http.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/sendMail"))); // backing off

        _api.Http.Respond = null;
        _api.Time.Advance(TimeSpan.FromSeconds(31));
        await RoundAsync(Lag(AlertSeverity.Warning));
        Assert.Equal(attempts + 1, _api.Http.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/sendMail")));
        Assert.Single(_api.Http.TeamsTexts); // Teams already had it
        Assert.NotNull((await admin.GetAsync<NotificationSettingsDto>("settings/notifications")).Status.Single(s => s.Channel == "email").LastSentUtc);
    }

    [SkippableFact]
    public async Task Settings_are_validated_secrets_are_write_only_and_tests_explain_failures()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = _api.Console();
        var bad = await ProblemsAsync(await admin.PutAsync("settings/notifications/email",
            new EmailNotificationRequest(true, "tenant", "not-a-guid", null, "alerts", ["it@"], AlertSeverity.Warning)));
        foreach (var expected in new[] { "Tenant", "client) ID", "Send from", "it@ is not", "client secret" }) Assert.Contains(expected, bad);
        Assert.Contains("https://", await ProblemsAsync(await admin.PutAsync("settings/notifications/teams", new TeamsNotificationRequest(true, "http://teams.example/x", AlertSeverity.Warning))));

        admin = await SetUpAsync();
        var described = await admin.GetAsync<NotificationSettingsDto>("settings/notifications");
        Assert.True(described.Email.HasClientSecret);
        Assert.Equal(("teams.example", true), (described.Teams.WebhookHost, described.Teams.HasWebhook));
        // Saving again without the secret or URL keeps them.
        await admin.PutAsync("settings/notifications/teams", new TeamsNotificationRequest(true, null, AlertSeverity.Warning)).OkAsync();
        Assert.True((await admin.GetAsync<NotificationSettingsDto>("settings/notifications")).Teams.HasWebhook);
        var everything = await admin.GetStringAsync("settings/notifications") + await admin.GetStringAsync("audit?take=1000");
        foreach (var secret in new[] { Secret, "teams-signature-value", "workflows/abc" }) Assert.DoesNotContain(secret, everything);
        Assert.Contains("(changed)", everything);

        var ok = await (await admin.PostAsync("settings/notifications/email/test", new NotificationTestRequest("adam@mercerlandmark.com"))).ReadAsync<SettingsTestResult>();
        Assert.True(ok.Succeeded, ok.Message);
        Assert.Equal("[Lakeflow] Test alert email", _api.Http.MailSubjects.Last());
        Assert.True((await (await admin.PostAsync("settings/notifications/teams/test", new { })).ReadAsync<SettingsTestResult>()).Succeeded);

        _api.Http.Respond = r => r.Url.Host == "login.microsoftonline.com"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided.\r\nTrace ID: x"}""") }
            : null;
        await admin.PutAsync("settings/notifications/email", new EmailNotificationRequest(true, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "new-secret",
            "lakeflow-alerts@mercerlandmark.com", ["it@mercerlandmark.com"], AlertSeverity.Warning)).OkAsync();
        var refused = await (await admin.PostAsync("settings/notifications/email/test", new NotificationTestRequest(null))).ReadAsync<SettingsTestResult>();
        Assert.False(refused.Succeeded);
        Assert.Equal("Entra ID refused the app sign-in: AADSTS7000215: Invalid client secret provided.", refused.Message);
    }
}
