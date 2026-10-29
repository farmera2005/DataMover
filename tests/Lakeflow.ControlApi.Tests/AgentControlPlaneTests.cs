using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lakeflow.Agent.Control;
using Lakeflow.Agent.Tests;
using Lakeflow.ControlApi.Security;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>The real agent (pipeline + control loop) against the real control API: the whole Phase 1 control path.</summary>
public sealed class AgentControlPlaneTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private bool _apiDown;

    /// <summary>The agent's HTTP stack, with a switch that cuts it off from the API.</summary>
    private HttpMessageHandler Handler(X509Certificate2? certificate, ServerTrust trust) =>
        new Switch(() => _apiDown) { InnerHandler = api.AgentHandler(certificate) };

    private sealed class Switch(Func<bool> down) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            down() ? throw new HttpRequestException("Simulated: no route to the control API") : base.SendAsync(request, cancellationToken);
    }

    [SkippableFact]
    public async Task Agent_enrolls_runs_on_pulled_config_reports_health_and_takes_credentials_and_commands()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = api.Console();
        var site = "e2e" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
        await admin.CreateSiteAsync(site);

        await using var h = await Harness.StartAsync();
        var source = h.Source("compuweigh", "Tickets");
        for (var id = 1; id <= 50; id++) source.Upsert("Tickets", id, new Dictionary<string, object?> { ["name"] = $"truck {id}" });
        var restarts = 0;
        using var control = new ControlLoop(h.Collector(), Handler, h.Time, NullLogger<ControlLoop>.Instance,
            Path.Combine(h.Directory, "logs"), () => restarts++);

        // Enroll with a token from the console, as the installer would.
        var token = await (await admin.PostAsync("enrollment-tokens", new EnrollmentTokenRequest(site, null))).ReadAsync<EnrollmentTokenResponse>();
        await control.EnrollAsync("https://localhost/", token.Token, null, "SCALE-PC", "1.0.0", CancellationToken.None);
        var agentId = control.Identity!.AgentId;
        Assert.StartsWith(site, agentId);

        // First config pull: the agent starts capturing what the console configured.
        await control.RunOnceAsync(CancellationToken.None);
        Assert.Equal(site, h.Pipeline.Config!.SiteId);
        Assert.Equal("grain", Assert.Single(h.Sender.Targets).HubName);
        await h.RunAsync(TimeSpan.FromSeconds(30));
        h.AssertSilverMatches("compuweigh", "Tickets");

        // Heartbeat makes the site visible in the fleet view.
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await control.RunOnceAsync(CancellationToken.None);
        var status = (await admin.GetAsync<FleetStatus>("status")).Sites.Single(s => s.SiteId == site);
        Assert.Equal(agentId, status.AgentId);
        Assert.NotNull(status.Heartbeat);
        Assert.Equal(TableState.Streaming, Assert.Single(status.Heartbeat!.Tables).State);
        Assert.Equal(Health.Green, status.Health);

        // The console seals the source password to the agent's published certificate; only the agent can open it.
        var published = await admin.GetAsync<AgentDto>($"agents/{agentId}");
        using (var cert = X509Certificate2.CreateFromPem(published.CertificatePem))
            await admin.PutAsync($"sites/{site}/sources/compuweigh/credential",
                new CredentialRequest(agentId, SealedSecret.Seal("Gr@in-2026", cert.GetRSAPublicKey()!, cert.Thumbprint))).OkAsync();
        h.Time.Advance(TimeSpan.FromSeconds(61));
        await control.RunOnceAsync(CancellationToken.None);
        Assert.Equal("Gr@in-2026", h.Store.GetCredential("compuweigh"));
        var sourceDto = Assert.Single(await admin.GetAsync<List<SourceDto>>($"sites/{site}/sources"));
        Assert.False(sourceDto.CredentialPending);
        Assert.NotNull(sourceDto.CredentialStoredUtc);

        // An operator pauses the table; the agent obeys and reports back.
        var operatorUser = api.Console("otto@mercerlandmark.com", Roles.Operator);
        var pause = await (await operatorUser.PostAsync($"sites/{site}/commands",
            new CommandRequest { Type = CommandType.PauseTable, SourceId = "compuweigh", Table = "dbo.Tickets" })).ReadAsync<CommandDto>();
        h.Time.Advance(TimeSpan.FromSeconds(61));
        await control.RunOnceAsync(CancellationToken.None);
        Assert.Equal(TableState.Paused, Assert.Single(h.Pipeline.BuildHeartbeat().Tables).State);
        var completed = (await admin.GetAsync<List<CommandDto>>($"sites/{site}/commands")).Single(c => c.CommandId == pause.CommandId);
        Assert.Equal("Table paused.", completed.Result);
        Assert.Equal("otto@mercerlandmark.com", completed.IssuedBy);

        // The control API goes away: capture and delivery carry on with the cached config; events wait locally.
        _apiDown = true;
        await admin.PostAsync($"sites/{site}/commands", new CommandRequest { Type = CommandType.ResumeTable, SourceId = "compuweigh", Table = "dbo.Tickets" }).OkAsync();
        for (var minute = 0; minute < 30; minute++)
        {
            h.Time.Advance(TimeSpan.FromMinutes(1));
            await control.RunOnceAsync(CancellationToken.None);
        }
        Assert.True(control.ConsecutiveFailures > 0);
        Assert.InRange(control.ConsecutiveFailures, 2, 10); // backing off, not hammering
        Assert.NotNull(h.Pipeline.Config);

        _apiDown = false;
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await control.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, control.ConsecutiveFailures);
        Assert.Equal(TableState.Streaming, Assert.Single(h.Pipeline.BuildHeartbeat().Tables).State);
        Assert.All(await admin.GetAsync<List<CommandDto>>($"sites/{site}/commands"), c => Assert.NotNull(c.CompletedUtc));

        // Restart is acknowledged before the service stops.
        var restart = await (await admin.PostAsync($"sites/{site}/commands", new CommandRequest { Type = CommandType.Restart })).ReadAsync<CommandDto>();
        h.Time.Advance(TimeSpan.FromSeconds(61));
        await control.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, restarts);
        Assert.Equal("Restarting.", (await admin.GetAsync<List<CommandDto>>($"sites/{site}/commands")).Single(c => c.CommandId == restart.CommandId).Result);
        // Delivered again (the agent never reported completion in time)? It is not run twice.
        await control.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, restarts);
    }

    [SkippableFact]
    public async Task Agent_renews_its_certificate_before_it_expires()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = api.Console();
        var site = "renew" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
        await admin.CreateSiteAsync(site);
        await using var h = await Harness.StartAsync();
        h.Source("compuweigh", "Tickets");
        using var control = new ControlLoop(h.Collector(), Handler, h.Time, NullLogger<ControlLoop>.Instance, h.Directory, () => { });
        var token = await (await admin.PostAsync("enrollment-tokens", new EnrollmentTokenRequest(site, null))).ReadAsync<EnrollmentTokenResponse>();
        await control.EnrollAsync("https://localhost/", token.Token, null, "SCALE-PC", "1.0.0", CancellationToken.None);
        var original = control.Identity!.Certificate.Thumbprint;

        h.Time.Advance(TimeSpan.FromDays(340)); // inside the 30-day renewal window (agent clock only)
        await control.RunOnceAsync(CancellationToken.None);

        Assert.NotEqual(original, control.Identity!.Certificate.Thumbprint);
        Assert.Equal(control.Identity.Certificate.Thumbprint, (await admin.GetAsync<AgentDto>($"agents/{control.Identity.AgentId}")).CertificateThumbprint);
        // The renewed identity is what a restarted agent loads.
        Assert.Equal(control.Identity.Certificate.Thumbprint, AgentIdentity.Load(h.Store)!.Certificate.Thumbprint);
    }
}
