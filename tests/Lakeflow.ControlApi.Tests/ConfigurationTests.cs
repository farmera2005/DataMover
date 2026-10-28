using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

public sealed class ConfigurationTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private HttpClient Admin()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        return api.Console();
    }

    private static string Site() => "site" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant();

    private async Task<string> ProblemsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [SkippableFact]
    public async Task Agent_config_carries_the_sites_sources_tables_and_send_only_tokens_for_its_hubs()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        await admin.PutAsync($"sites/{site}/sources/kahler", new SourceDto
        {
            SourceId = "kahler", SourceSystem = "kahler", Division = Division.Agronomy, Engine = SourceEngine.SqlServer,
            Connection = new ConnectionSettings { Host = "10.1.2.4", Instance = "KAHLER", UserName = "ro", CredentialName = "kahler" },
        }).OkAsync();
        var (agent, cert) = await api.EnrollAsync(admin, site);

        var config = await api.Agent(cert).GetAsync<AgentConfig>($"agents/{agent}/config");

        Assert.Equal(agent, config.AgentId);
        Assert.Equal(site, config.SiteId);
        Assert.Empty(ConfigValidator.Validate(config));
        Assert.Equal(["compuweigh", "kahler"], config.Sources.Select(s => s.SourceId));
        var table = Assert.Single(config.Sources[0].Tables);
        Assert.Equal(new TableRef("dbo", "Tickets"), table.Table);
        Assert.Equal(CaptureMethod.RowVersion, table.Method);
        Assert.Equal([Division.Grain, Division.Agronomy], config.EventHubs.Select(h => h.Division));
        foreach (var hub in config.EventHubs)
        {
            Assert.Equal("lakeflow-test.servicebus.windows.net", hub.FullyQualifiedNamespace);
            Assert.Equal(LakeNames.HubName(hub.Division), hub.HubName);
            Assert.Contains("skn=agent-send", hub.SasToken);
            Assert.True(SasTokens.IsValid(hub.SasToken, hub.FullyQualifiedNamespace, hub.HubName, api.Keys.HubSendKey(hub.Division).Key, api.Time.GetUtcNow()));
            Assert.False(SasTokens.IsValid(hub.SasToken, hub.FullyQualifiedNamespace, "feed", api.Keys.HubSendKey(hub.Division).Key, api.Time.GetUtcNow()));
            Assert.InRange(hub.SasExpiresUtc - api.Time.GetUtcNow(), TimeSpan.FromHours(23.9), TimeSpan.FromHours(24));
        }
        Assert.DoesNotContain("password", JsonSerializer.Serialize(config, EnvelopeJson.Options), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Sas_tokens_are_reused_for_half_their_life_then_rotated()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var (agent, cert) = await api.EnrollAsync(admin, site);
        var client = api.Agent(cert);

        var first = (await client.GetAsync<AgentConfig>($"agents/{agent}/config")).EventHubs[0];
        api.Time.Advance(TimeSpan.FromHours(11));
        var same = (await client.GetAsync<AgentConfig>($"agents/{agent}/config")).EventHubs[0];
        Assert.Equal(first.SasToken, same.SasToken);
        api.Time.Advance(TimeSpan.FromHours(2));
        var rotated = (await client.GetAsync<AgentConfig>($"agents/{agent}/config")).EventHubs[0];
        Assert.NotEqual(first.SasToken, rotated.SasToken);
        Assert.True(rotated.SasExpiresUtc > first.SasExpiresUtc);
    }

    [SkippableFact]
    public async Task Edits_are_validated_with_the_agents_own_rules()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);

        Assert.Contains("Binlog capture is not available for SqlServer",
            await ProblemsAsync(await admin.PutAsync($"sites/{site}/sources/compuweigh/tables/dbo.Bins", new TableDto { Table = "dbo.Bins", Method = CaptureMethod.Binlog })));
        Assert.Contains("timestamp polling needs a watermark_column",
            await ProblemsAsync(await admin.PutAsync($"sites/{site}/sources/compuweigh/tables/dbo.Bins", new TableDto { Table = "dbo.Bins", Method = CaptureMethod.TimestampPolling })));
        Assert.Contains("credential_name is required", await ProblemsAsync(await admin.PutAsync($"sites/{site}/sources/wem", new SourceDto
        {
            SourceId = "wem", SourceSystem = "wem", Division = Division.Feed, Engine = SourceEngine.SqlServer, Connection = new ConnectionSettings { Host = "h" },
        })));
        Assert.Contains("must match the URL", await ProblemsAsync(await admin.PutAsync($"sites/{site}/sources/compuweigh/tables/dbo.Bins", new TableDto { Table = "dbo.Other", Method = CaptureMethod.RowVersion })));
        Assert.Contains("not a known time zone", await ProblemsAsync(await admin.PutAsync($"sites/{site}", new SiteDto { SiteId = site, Name = "x", TimeZone = "Mars/Olympus" })));
        Assert.Contains("site_id", await ProblemsAsync(await admin.PutAsync("sites/Bad%20Id", new SiteDto { SiteId = "Bad Id", Name = "x" })));

        // Nothing invalid was saved.
        Assert.Single(await admin.GetAsync<List<TableDto>>($"sites/{site}/sources/compuweigh/tables"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("sites/Bad%20Id")).StatusCode);
    }

    [SkippableFact]
    public async Task Every_change_is_audited_with_who_and_what_and_moves_the_config_version()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var version = (await admin.GetAsync<SiteDto>($"sites/{site}")).ConfigVersion;

        var editor = api.Console("eve@mercerlandmark.com", Roles.Admin);
        await editor.PutAsync($"sites/{site}/sources/compuweigh/tables/dbo.Tickets",
            new TableDto { Table = "dbo.Tickets", Method = CaptureMethod.RowVersion, LagWarningMinutes = 20, LagCriticalMinutes = 90 }).OkAsync();
        Assert.Equal(version + 1, (await admin.GetAsync<SiteDto>($"sites/{site}")).ConfigVersion);

        var audit = await admin.GetAsync<List<AuditEntryDto>>("audit?take=1000");
        var entry = audit.First(a => a.Entity == $"table {site}/compuweigh/dbo.Tickets" && a.Action == "updated");
        Assert.Equal("eve@mercerlandmark.com", entry.Actor);
        Assert.Contains("\"LagWarningMinutes\":15", entry.Before);
        Assert.Contains("\"LagWarningMinutes\":20", entry.After);
        Assert.Contains(audit, a => a.Entity == $"site {site}" && a.Action == "created");

        // Saving the same values again changes nothing and is not audited.
        var count = audit.Count;
        await editor.PutAsync($"sites/{site}/sources/compuweigh/tables/dbo.Tickets",
            new TableDto { Table = "dbo.Tickets", Method = CaptureMethod.RowVersion, LagWarningMinutes = 20, LagCriticalMinutes = 90 }).OkAsync();
        Assert.Equal(version + 1, (await admin.GetAsync<SiteDto>($"sites/{site}")).ConfigVersion);
        Assert.Equal(count, (await admin.GetAsync<List<AuditEntryDto>>("audit?take=1000")).Count);
    }

    [SkippableFact]
    public async Task Enrollment_tokens_and_sealed_credentials_never_reach_the_audit_log()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var (agent, cert) = await api.EnrollAsync(admin, site);
        var sealedValue = SealedSecret.Seal("s3cret-pw", cert.GetRSAPublicKey()!, cert.Thumbprint);
        await admin.PutAsync($"sites/{site}/sources/compuweigh/credential", new CredentialRequest(agent, sealedValue)).OkAsync();

        var audit = JsonSerializer.Serialize(await admin.GetAsync<List<AuditEntryDto>>("audit?take=1000"), EnvelopeJson.Options);
        Assert.Contains($"credential {site}/compuweigh", audit);
        Assert.Contains($"enrollment token for {site}", audit);
        Assert.DoesNotContain(sealedValue[..40], audit);
        Assert.DoesNotContain("TokenHash", audit);
        Assert.DoesNotContain("s3cret", audit);
    }

    [SkippableFact]
    public async Task Credentials_must_be_sealed_to_the_sites_current_agent_and_only_the_agent_opens_them()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var (agent, cert) = await api.EnrollAsync(admin, site);
        var url = $"sites/{site}/sources/compuweigh/credential";

        Assert.Contains("plain passwords are refused", await ProblemsAsync(await admin.PutAsync(url, new CredentialRequest(agent, "hunter2"))));
        using var otherKey = RSA.Create(2048);
        Assert.Contains("different certificate", await ProblemsAsync(await admin.PutAsync(url, new CredentialRequest(agent, SealedSecret.Seal("pw", otherKey, "OTHER")))));
        Assert.Contains("not this site's active agent", await ProblemsAsync(await admin.PutAsync(url, new CredentialRequest("someone-else", SealedSecret.Seal("pw", cert.GetRSAPublicKey()!, cert.Thumbprint)))));

        // The console seals with the certificate the API publishes for the agent.
        var published = await admin.GetAsync<AgentDto>($"agents/{agent}");
        using var publicCert = X509Certificate2.CreateFromPem(published.CertificatePem);
        await admin.PutAsync(url, new CredentialRequest(agent, SealedSecret.Seal("pw-123", publicCert.GetRSAPublicKey()!, publicCert.Thumbprint))).OkAsync();
        Assert.True((await admin.GetAsync<List<SourceDto>>($"sites/{site}/sources")).Single().CredentialPending);

        var config = await api.Agent(cert).GetAsync<AgentConfig>($"agents/{agent}/config");
        var credential = Assert.Single(config.Credentials);
        Assert.Equal("compuweigh", credential.Name);
        Assert.Equal("pw-123", SealedSecret.Open(credential.Sealed, cert.GetRSAPrivateKey()!, cert.Thumbprint));

        // Once the agent confirms, the sealed copy is gone from the API.
        await api.Agent(cert).PostAsync($"agents/{agent}/events", new[]
        {
            new AgentEvent
            {
                EventId = Guid.NewGuid(), OccurredUtc = api.Time.GetUtcNow(), Kind = AgentEventKind.CredentialStored, Severity = EventSeverity.Info,
                Message = "stored", Details = new System.Text.Json.Nodes.JsonObject { ["credential_id"] = credential.Id.ToString() },
            },
        }).OkAsync();
        Assert.Empty((await api.Agent(cert).GetAsync<AgentConfig>($"agents/{agent}/config")).Credentials);
        var source = (await admin.GetAsync<List<SourceDto>>($"sites/{site}/sources")).Single();
        Assert.False(source.CredentialPending);
        Assert.NotNull(source.CredentialStoredUtc);
        using var scope = api.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<LakeflowDb>().SealedCredentials.AnyAsync(c => c.AgentId == agent));
    }

    [SkippableFact]
    public async Task Commands_are_delivered_until_the_agent_reports_a_result()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var (agent, cert) = await api.EnrollAsync(admin, site);

        Assert.Contains("Unknown table", await ProblemsAsync(await admin.PostAsync($"agents/{agent}/commands",
            new CommandRequest { Type = CommandType.ResnapshotTable, SourceId = "compuweigh", Table = "dbo.Nope" })));
        Assert.Contains("needs table", await ProblemsAsync(await admin.PostAsync($"agents/{agent}/commands",
            new CommandRequest { Type = CommandType.ResnapshotTable, SourceId = "compuweigh" })));
        var command = await (await admin.PostAsync($"agents/{agent}/commands",
            new CommandRequest { Type = CommandType.ResnapshotTable, SourceId = "compuweigh", Table = "dbo.Tickets" })).ReadAsync<CommandDto>();

        var delivered = Assert.Single((await api.Agent(cert).GetAsync<AgentConfig>($"agents/{agent}/config")).Commands);
        Assert.Equal(command.CommandId, delivered.CommandId);
        Assert.Equal("adam@mercerlandmark.com", delivered.IssuedBy);
        Assert.Single((await api.Agent(cert).GetAsync<AgentConfig>($"agents/{agent}/config")).Commands); // still pending

        var result = new AgentEvent
        {
            EventId = Guid.NewGuid(), OccurredUtc = api.Time.GetUtcNow(), Kind = AgentEventKind.CommandResult, Severity = EventSeverity.Info,
            CommandId = command.CommandId, Message = "Resnapshot queued.",
        };
        await api.Agent(cert).PostAsync($"agents/{agent}/events", new[] { result }).OkAsync();
        await api.Agent(cert).PostAsync($"agents/{agent}/events", new[] { result }).OkAsync(); // a retried post is harmless

        Assert.Empty((await api.Agent(cert).GetAsync<AgentConfig>($"agents/{agent}/config")).Commands);
        var done = Assert.Single(await admin.GetAsync<List<CommandDto>>($"agents/{agent}/commands"));
        Assert.Equal("Resnapshot queued.", done.Result);
        Assert.Single(await admin.GetAsync<List<AgentEventDto>>($"events?siteId={site}"));
    }

    [SkippableFact]
    public async Task Fleet_status_turns_amber_and_red_by_the_alert_thresholds()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        SiteStatus Status(FleetStatus fleet) => fleet.Sites.Single(s => s.SiteId == site);

        var noAgent = Status(await admin.GetAsync<FleetStatus>("status"));
        Assert.Equal(Health.Red, noAgent.Health);
        Assert.Contains("No agent enrolled.", noAgent.Reasons);

        var (agent, cert) = await api.EnrollAsync(admin, site);
        Heartbeat Beat(double lag = 30, long buffer = 0, int failures = 0, TableState state = TableState.Streaming) => new()
        {
            AgentId = agent, SiteId = site, AgentVersion = "1.0.0", SentUtc = api.Time.GetUtcNow(), ConfigVersion = 1,
            BufferBytes = buffer, BufferCapBytes = 1000, EventHubReachable = true,
            Sources = [new SourceStatus { SourceId = "compuweigh", Connected = failures == 0, ConsecutiveFailures = failures, LastError = failures > 0 ? "timeout" : null }],
            Tables = [new TableStatus { SourceId = "compuweigh", Table = "dbo.Tickets", State = state, Method = CaptureMethod.RowVersion, LagSeconds = lag }],
        };
        async Task<SiteStatus> AfterAsync(Heartbeat beat)
        {
            await api.Agent(cert).PostAsync($"agents/{agent}/heartbeat", beat).OkAsync();
            return Status(await admin.GetAsync<FleetStatus>("status"));
        }

        var green = await AfterAsync(Beat());
        Assert.Equal(Health.Green, green.Health);
        Assert.Equal("1.0.0", green.AgentVersion);
        Assert.Equal(30, green.WorstLagSeconds);
        Assert.Equal(Health.Amber, (await AfterAsync(Beat(lag: 16 * 60))).Health);
        Assert.Equal(Health.Red, (await AfterAsync(Beat(lag: 61 * 60))).Health);
        Assert.Equal(Health.Amber, (await AfterAsync(Beat(buffer: 850))).Health);
        Assert.Equal(Health.Amber, (await AfterAsync(Beat(state: TableState.Blocked))).Health);
        var failing = await AfterAsync(Beat(failures: 3));
        Assert.Equal(Health.Red, failing.Health);
        Assert.Contains(failing.Reasons, r => r.Contains("connection failing"));

        await AfterAsync(Beat());
        api.Time.Advance(TimeSpan.FromMinutes(6));
        var silent = Status(await admin.GetAsync<FleetStatus>("status"));
        Assert.Equal(Health.Red, silent.Health);
        Assert.Contains(silent.Reasons, r => r.StartsWith("No heartbeat for 6 min"));
    }

    [SkippableFact]
    public async Task Log_bundles_are_uploaded_by_the_agent_and_downloaded_by_admins_only()
    {
        var admin = Admin();
        var site = Site();
        await admin.CreateSiteAsync(site);
        var (agent, cert) = await api.EnrollAsync(admin, site);

        var zip = RandomNumberGenerator.GetBytes(1000);
        using var upload = await api.Agent(cert).PostAsync($"agents/{agent}/logs", new ByteArrayContent(zip));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var location = upload.Headers.Location!.ToString().TrimStart('/');

        Assert.Equal(zip, await admin.GetByteArrayAsync(location));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Console("v@x", Roles.Viewer).GetAsync(location)).StatusCode);
    }

    [SkippableFact]
    public async Task Retention_keeps_a_year_of_audit()
    {
        Admin();
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LakeflowDb>();
        var now = api.Time.GetUtcNow();
        db.AuditEntries.AddRange(
            new AuditEntry { AtUtc = now.AddDays(-400), Actor = "old", Action = "updated", Entity = "retention-test-old" },
            new AuditEntry { AtUtc = now.AddDays(-300), Actor = "recent", Action = "updated", Entity = "retention-test-recent" });
        await db.SaveChangesAsync();

        await ActivatorUtilities.CreateInstance<Retention>(api.Services).RunOnceAsync(CancellationToken.None);

        var left = await db.AuditEntries.Where(a => a.Entity.StartsWith("retention-test")).Select(a => a.Entity).ToListAsync();
        Assert.Equal(["retention-test-recent"], left);
    }
}
