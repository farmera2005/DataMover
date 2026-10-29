using System.Net;
using System.Security.Cryptography;
using Lakeflow.Agent;
using Lakeflow.Agent.Control;
using Lakeflow.Agent.Tests;
using Lakeflow.ControlApi.Security;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>One central collector serving several sites, each isolated in its own pipeline and store.</summary>
public sealed class CollectorTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed class Central : IAsyncDisposable
    {
        public required Harness Identity { get; init; }
        public required Dictionary<string, Harness> Sites { get; init; }
        public required CollectorHost Host { get; init; }
        public required ControlLoop Control { get; init; }

        /// <summary>Control exchange plus every site's capture and delivery, as the running collector does.</summary>
        public async Task RunAsync(TimeSpan duration, TimeSpan? step = null)
        {
            var tick = step ?? TimeSpan.FromSeconds(11);
            for (var t = TimeSpan.Zero; t <= duration; t += tick)
            {
                await Control.RunOnceAsync(CancellationToken.None);
                await Host.RunOnceAsync(CancellationToken.None);
                Identity.Time.Advance(tick);
                foreach (var site in Sites.Values) site.Time.Advance(tick);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Control.Dispose();
            await Host.DisposeAsync();
            foreach (var site in Sites.Values) await site.DisposeAsync();
            await Identity.DisposeAsync();
        }
    }

    private async Task<Central> StartAsync(HttpClient admin, string name, params string[] sites)
    {
        var identity = await Harness.StartAsync();
        var harnesses = new Dictionary<string, Harness>();
        foreach (var site in sites)
        {
            var h = await Harness.StartAsync();
            var source = h.Source("compuweigh", "Tickets");
            for (var id = 1; id <= 10; id++) source.Upsert("Tickets", id, new Dictionary<string, object?> { ["name"] = $"{site} truck {id}" });
            harnesses[site] = h;
        }
        var host = new CollectorHost(identity.Store, site => new SiteParts(harnesses[site].Store, harnesses[site].Pipeline),
            NullLogger<CollectorHost>.Instance, ownsSites: false, runLoops: false);
        var control = new ControlLoop(host, api.AgentHandler, identity.Time, NullLogger<ControlLoop>.Instance, identity.Directory, () => { });
        var token = await (await admin.PostAsync("enrollment-tokens", new EnrollmentTokenRequest(null, name))).ReadAsync<EnrollmentTokenResponse>();
        Assert.Contains("docker compose up -d", token.InstallCommand);
        await control.EnrollAsync("https://localhost/", token.Token, "COLLECTOR-1", "1.0.0", CancellationToken.None);
        return new Central { Identity = identity, Sites = harnesses, Host = host, Control = control };
    }

    private static string NewSite() => "cs" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant();

    private static async Task AssignAsync(HttpClient admin, string site, string? collector)
    {
        var current = await admin.GetAsync<SiteDto>($"sites/{site}");
        await admin.PutAsync($"sites/{site}", current with { CollectorId = collector }).OkAsync();
    }

    [SkippableFact]
    public async Task One_collector_serves_several_sites_each_in_isolation()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = api.Console();
        var (a, b) = (NewSite(), NewSite());
        await admin.CreateSiteAsync(a);
        await admin.CreateSiteAsync(b);
        await using var central = await StartAsync(admin, "central-1", a, b);
        var collectorId = central.Control.Identity!.AgentId;
        Assert.StartsWith("collector-", collectorId);

        // Not assigned yet: the collector serves nothing.
        await central.RunAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(central.Host.Sites);

        await AssignAsync(admin, a, collectorId);
        await AssignAsync(admin, b, collectorId);
        await central.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(new[] { a, b }.Order(), central.Host.Sites.Keys.Order());
        central.Sites[a].AssertSilverMatches("compuweigh", "Tickets");
        central.Sites[b].AssertSilverMatches("compuweigh", "Tickets");
        Assert.All(central.Sites[a].Sender.Received, e => Assert.Equal(a, e.SiteId));

        var fleet = await admin.GetAsync<FleetStatus>("status");
        foreach (var site in new[] { a, b })
        {
            var status = fleet.Sites.Single(s => s.SiteId == site);
            Assert.Equal(Health.Green, status.Health);
            Assert.Equal(collectorId, status.AgentId);
        }
        var collector = await admin.GetAsync<AgentDto>($"agents/{collectorId}");
        Assert.Null(collector.SiteId);
        Assert.Equal("central-1", collector.Name);
        Assert.Equal(new[] { a, b }.Order(), collector.Sites.Order());

        // A password for site A reaches site A's store only.
        await admin.PutAsync($"sites/{a}/sources/compuweigh/credential",
            new CredentialRequest(collectorId, SealedSecret.Seal("pw-A", RSA.Create().Also(k => k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(collector.PublicKeySpki), out _)), collector.CertificateThumbprint))).OkAsync();
        await central.RunAsync(TimeSpan.FromSeconds(70)); // next config pull (every 60 s when nothing is waiting)
        Assert.Equal("pw-A", central.Sites[a].Store.GetCredential("compuweigh"));
        Assert.Null(central.Sites[b].Store.GetCredential("compuweigh"));

        // A command for site B pauses B's table, not A's.
        await (await admin.PostAsync($"sites/{b}/commands", new CommandRequest { Type = CommandType.PauseTable, SourceId = "compuweigh", Table = "dbo.Tickets" })).ReadAsync<CommandDto>();
        await central.RunAsync(TimeSpan.FromSeconds(70));
        Assert.Equal(TableState.Paused, central.Sites[b].Pipeline.BuildHeartbeat().Tables.Single().State);
        Assert.Equal(TableState.Streaming, central.Sites[a].Pipeline.BuildHeartbeat().Tables.Single().State);
        Assert.Equal("Table paused.", (await admin.GetAsync<List<CommandDto>>($"sites/{b}/commands")).Single().Result);

        // Site A's database goes away: A backs off and turns red; B is untouched.
        central.Sites[a].Connectors["compuweigh"].FailWith = new TimeoutException("site A unreachable");
        central.Sites[b].Sources["compuweigh"].Upsert("Tickets", 99, new Dictionary<string, object?> { ["name"] = "new at B" });
        await admin.PostAsync($"sites/{b}/commands", new CommandRequest { Type = CommandType.ResumeTable, SourceId = "compuweigh", Table = "dbo.Tickets" }).OkAsync();
        await central.RunAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
        fleet = await admin.GetAsync<FleetStatus>("status");
        Assert.Equal(Health.Red, fleet.Sites.Single(s => s.SiteId == a).Health);
        Assert.Equal(Health.Green, fleet.Sites.Single(s => s.SiteId == b).Health);
        central.Sites[b].AssertSilverMatches("compuweigh", "Tickets");

        // Unassigned: the collector stops serving B and the API no longer takes its reports for B.
        await AssignAsync(admin, b, null);
        await central.RunAsync(TimeSpan.FromSeconds(70));
        Assert.Equal([a], central.Host.Sites.Keys);
        var heartbeat = central.Sites[b].Pipeline.BuildHeartbeat();
        var refused = await api.Agent(central.Control.Identity!.Certificate).PostAsync($"agents/{collectorId}/heartbeat", heartbeat);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("No collector assigned.", (await admin.GetAsync<FleetStatus>("status")).Sites.Single(s => s.SiteId == b).Reasons);
    }

    [SkippableFact]
    public async Task Moving_a_site_to_another_collector_drops_passwords_sealed_to_the_old_one()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = api.Console();
        var site = NewSite();
        await admin.CreateSiteAsync(site);
        await using var first = await StartAsync(admin, "central-a", site);
        await using var second = await StartAsync(admin, "central-b", site);
        var (one, two) = (first.Control.Identity!.AgentId, second.Control.Identity!.AgentId);
        await AssignAsync(admin, site, one);
        var collector = await admin.GetAsync<AgentDto>($"agents/{one}");
        using var key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(collector.PublicKeySpki), out _);
        await admin.PutAsync($"sites/{site}/sources/compuweigh/credential", new CredentialRequest(one, SealedSecret.Seal("pw", key, collector.CertificateThumbprint))).OkAsync();
        Assert.True((await admin.GetAsync<List<SourceDto>>($"sites/{site}/sources")).Single().CredentialPending);

        await AssignAsync(admin, site, two);
        var source = (await admin.GetAsync<List<SourceDto>>($"sites/{site}/sources")).Single();
        Assert.False(source.CredentialPending);
        Assert.Null(source.CredentialStoredUtc);
        Assert.Contains("not the collector serving this site", await (await admin.PutAsync($"sites/{site}/sources/compuweigh/credential",
            new CredentialRequest(one, SealedSecret.Seal("pw", key, collector.CertificateThumbprint)))).Content.ReadAsStringAsync());

        var unknown = await admin.PutAsync($"sites/{site}", (await admin.GetAsync<SiteDto>($"sites/{site}")) with { CollectorId = "collector-nope" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(two, (await admin.GetAsync<SiteDto>($"sites/{site}")).CollectorId);
    }
}

internal static class Fluent
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
