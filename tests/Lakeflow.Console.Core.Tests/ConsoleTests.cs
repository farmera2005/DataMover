using System.Security.Cryptography;
using Lakeflow.Agent.Control;
using Lakeflow.Agent.Tests;
using Lakeflow.Console.Core;
using Lakeflow.Console.Core.ViewModels;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Tests;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lakeflow.Console.Core.Tests;

/// <summary>
/// The console's view models against the real control API (in-process, PostgreSQL in Docker) and a real collector
/// answering commands: what the web pages bind to, tested end to end.
/// </summary>
public sealed class ConsoleTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    /// <summary>A browser: the API client with a cookie jar, not signed in.</summary>
    internal static ConsoleApi Browser(ApiFactory api) =>
        ConsoleApi.Create(new Uri("https://localhost/"), new CookieContainerHandler { InnerHandler = api.Server.CreateHandler() });

    /// <summary>Signed in as a local account with the given role (Admin by default).</summary>
    private ConsoleApi Console(string user = "adam@mercerlandmark.com", params string[] roles)
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        api.Console(user, roles); // creates the account
        var console = Browser(api);
        console.SignInAsync(new SignInRequest(user, ApiFactory.TestPassword)).GetAwaiter().GetResult();
        return console;
    }

    private static SignedInUser User(params string[] roles) => new("someone@mercerlandmark.com", null,
        roles.Contains(Roles.Admin) ? ConsoleRole.Admin : roles.Contains(Roles.Operator) ? ConsoleRole.Operator : ConsoleRole.Viewer, "local");

    private static string NewSite() => "con" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant();

    /// <summary>An enrolled agent with in-memory sources, pumped in the background like the real service.</summary>
    private sealed class RunningAgent : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _pump = Task.CompletedTask;
        public Harness Harness { get; private set; } = null!;
        public ControlLoop Control { get; private set; } = null!;

        public static async Task<RunningAgent> StartAsync(ApiFactory api, ConsoleApi console, string siteId, Action<Harness> sources)
        {
            var agent = new RunningAgent { Harness = await Harness.StartAsync() };
            sources(agent.Harness);
            agent.Control = new ControlLoop(agent.Harness.Collector(), (c, _) => api.AgentHandler(c), agent.Harness.Time,
                NullLogger<ControlLoop>.Instance, agent.Harness.Directory, () => { });
            var token = await console.CreateEnrollmentTokenAsync(new EnrollmentTokenRequest(siteId, null));
            await agent.Control.EnrollAsync("https://localhost/", token.Token, null, "SCALE-PC", "1.0.0", CancellationToken.None);
            agent._pump = Task.Run(async () =>
            {
                while (!agent._stop.IsCancellationRequested)
                {
                    await agent.Control.RunOnceAsync(CancellationToken.None);
                    await agent.Harness.StepAsync();
                    agent.Harness.Time.Advance(TimeSpan.FromSeconds(11)); // past the 10 s fast poll each time
                    await Task.Delay(20);
                }
            });
            return agent;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _pump;
            Control.Dispose();
            await Harness.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Shell_signs_in_keeps_the_session_and_signs_out()
    {
        Console("otto@mercerlandmark.com", Roles.Operator);
        var browser = Browser(api);
        var shell = new ShellViewModel(browser);
        await shell.InitializeCommand.ExecuteAsync(null);
        Assert.Equal(ShellState.SignedOut, shell.State);

        (shell.UserName, shell.Password) = ("otto@mercerlandmark.com", "not the password!");
        await shell.SignInCommand.ExecuteAsync(null);
        Assert.False(shell.IsSignedIn);
        Assert.Contains("not correct", shell.Error);
        Assert.Equal("", shell.Password);

        (shell.UserName, shell.Password) = ("otto@mercerlandmark.com", ApiFactory.TestPassword);
        await shell.SignInCommand.ExecuteAsync(null);
        Assert.Equal(ShellState.SignedIn, shell.State);
        Assert.Equal("otto@mercerlandmark.com", shell.User!.Name);
        Assert.Equal(ConsoleRole.Operator, shell.User.Role);
        Assert.True(shell.User.CanOperate);
        Assert.False(shell.User.CanAdminister);

        // A reload finds the session (the cookie), then signing out ends it.
        var reloaded = new ShellViewModel(browser);
        await reloaded.InitializeCommand.ExecuteAsync(null);
        Assert.Equal(ShellState.SignedIn, reloaded.State);
        await reloaded.SignOutCommand.ExecuteAsync(null);
        var after = new ShellViewModel(browser);
        await after.InitializeCommand.ExecuteAsync(null);
        Assert.Equal(ShellState.SignedOut, after.State);
    }

    [SkippableFact]
    public async Task Fleet_lists_sites_worst_first_and_filters()
    {
        var admin = Console();
        var (healthy, quiet) = (NewSite(), NewSite());
        foreach (var site in new[] { healthy, quiet })
            await admin.PutSiteAsync(new SiteDto { SiteId = site, Name = site == healthy ? "Healthy Elevator" : "Quiet Elevator" });
        await using var agent = await RunningAgent.StartAsync(api, admin, healthy, h => h.Source("x"));
        // Wait for the heartbeat to arrive.
        for (var i = 0; i < 200 && (await admin.GetStatusAsync()).Sites.Single(s => s.SiteId == healthy).Heartbeat is null; i++) await Task.Delay(25);

        var fleet = new FleetViewModel(Console("v@x", Roles.Viewer));
        await fleet.RefreshCommand.ExecuteAsync(null);

        var rows = fleet.Sites.Where(s => s.SiteId == healthy || s.SiteId == quiet).ToList();
        Assert.Equal([quiet, healthy], rows.Select(r => r.SiteId)); // red (no agent) above green
        Assert.Equal(Health.Red, rows[0].Health);
        Assert.Equal("No collector assigned.", rows[0].Reason);
        Assert.Equal(Health.Green, rows[1].Health);
        Assert.Equal("1.0.0", rows[1].AgentVersion);
        Assert.True(fleet.Red >= 1 && fleet.Green >= 1);

        fleet.Filter = "healthy";
        Assert.Contains(fleet.Sites, s => s.SiteId == healthy);
        Assert.DoesNotContain(fleet.Sites, s => s.SiteId == quiet);
        fleet.Filter = "";
        fleet.HealthFilter = Health.Red;
        Assert.All(fleet.Sites, s => Assert.Equal(Health.Red, s.Health));
    }

    [SkippableFact]
    public async Task Setup_wizard_tests_the_connection_through_the_agent_browses_tables_and_starts_capture()
    {
        var admin = Console();
        var site = NewSite();
        var sites = new SitesViewModel(admin) { SiteId = site, SiteName = "St. Marys Grain" };
        await sites.SaveSiteCommand.ExecuteAsync(null);
        Assert.Null(sites.Error);
        await using var agent = await RunningAgent.StartAsync(api, admin, site, h =>
        {
            var source = h.Source("compuweigh", "Tickets", "Bins");
            for (var id = 1; id <= 25; id++) source.Upsert("Tickets", id, new Dictionary<string, object?> { ["name"] = $"t{id}" });
        });

        var wizard = new SourceWizardViewModel(admin, new DotNetSecretSealer(), site, commandTimeout: TimeSpan.FromSeconds(30), pollEvery: TimeSpan.FromMilliseconds(50));
        wizard.Next();
        Assert.Contains(wizard.Errors, e => e.StartsWith("Source id"));
        (wizard.SourceId, wizard.SourceSystem, wizard.Engine) = ("compuweigh", "compuweigh", SourceEngine.Db2i);
        wizard.Next();
        Assert.Contains(wizard.Errors, e => e.Contains("later phase"));
        wizard.Engine = SourceEngine.SqlServer;
        wizard.Next();
        Assert.Equal(WizardStep.Connection, wizard.Step);

        (wizard.Host, wizard.Database, wizard.UserName, wizard.Password) = ("10.1.2.3", "GMS", "lakeflow_ro", "Gr@in-2026");
        await wizard.TestConnectionCommand.ExecuteAsync(null);

        Assert.Empty(wizard.Errors);
        Assert.True(wizard.TestResult!.Succeeded);
        Assert.Equal("", wizard.Password); // sealed and dropped
        Assert.Equal("Gr@in-2026", agent.Harness.Store.GetCredential("compuweigh")); // only the agent can open it
        Assert.Equal(WizardStep.Tables, wizard.Step);
        Assert.Equal(["dbo.Bins", "dbo.Tickets"], wizard.Tables.Select(t => t.Table).Order());
        var tickets = wizard.Tables.Single(t => t.Table == "dbo.Tickets");
        Assert.Equal("25", tickets.Rows);
        Assert.Equal(CaptureMethod.ChangeTracking, tickets.Method);
        // The source is saved paused while the wizard runs: nothing captured yet.
        Assert.True((await admin.GetSourcesAsync(site)).Single().Paused);
        Assert.Empty(agent.Harness.Sender.Snapshot());

        await wizard.FinishCommand.ExecuteAsync(null);
        Assert.Contains(wizard.Errors, e => e.Contains("at least one table"));
        tickets.Selected = true;
        await wizard.FinishCommand.ExecuteAsync(null);
        Assert.Empty(wizard.Errors);
        Assert.Equal(WizardStep.Done, wizard.Step);
        Assert.False((await admin.GetSourcesAsync(site)).Single().Paused);

        // The agent picks up the new config and snapshots the table.
        for (var i = 0; i < 400 && agent.Harness.Sender.Snapshot().Count(e => e.Op == ChangeOp.Snapshot) < 25; i++) await Task.Delay(25);
        var received = agent.Harness.Sender.Snapshot();
        Assert.Equal(25, received.Count(e => e.Op == ChangeOp.Snapshot));
        Assert.All(received, e => Assert.Equal("dbo.Tickets", e.Table));
    }

    [SkippableFact]
    public async Task Setup_wizard_reports_a_failed_connection_and_a_site_without_an_agent()
    {
        var admin = Console();
        var lonely = NewSite();
        await admin.PutSiteAsync(new SiteDto { SiteId = lonely, Name = "No agent yet" });
        var noAgent = new SourceWizardViewModel(admin, new DotNetSecretSealer(), lonely) { SourceId = "cw", SourceSystem = "compuweigh", Host = "h", UserName = "u" };
        noAgent.Next();
        await noAgent.TestConnectionCommand.ExecuteAsync(null);
        Assert.Contains(noAgent.Errors, e => e.Contains("No collector is assigned"));

        var site = NewSite();
        await admin.PutSiteAsync(new SiteDto { SiteId = site, Name = "Unreachable source" });
        await using var agent = await RunningAgent.StartAsync(api, admin, site, h => h.Source("cw", "Tickets"));
        agent.Harness.Connectors["cw"] = new Testing.InMemory.InMemoryConnector(agent.Harness.Sources["cw"]) { FailWith = new TimeoutException("server unreachable") };
        var wizard = new SourceWizardViewModel(admin, new DotNetSecretSealer(), site, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50))
            { SourceId = "cw", SourceSystem = "compuweigh", Host = "10.9.9.9", UserName = "u", Password = "p" };
        wizard.Next();
        await wizard.TestConnectionCommand.ExecuteAsync(null);

        Assert.False(wizard.TestResult!.Succeeded);
        Assert.Contains(wizard.Errors, e => e.Contains("server unreachable"));
        Assert.Equal(WizardStep.Connection, wizard.Step);
    }

    [SkippableFact]
    public async Task Site_detail_shows_tables_and_offers_actions_by_role()
    {
        var admin = Console();
        var site = NewSite();
        await admin.PutSiteAsync(new SiteDto { SiteId = site, Name = "Detail" });
        await admin.PutSourceAsync(site, new SourceDto
        {
            SourceId = "cw", SourceSystem = "compuweigh", Division = Division.Grain, Engine = SourceEngine.SqlServer,
            Connection = new ConnectionSettings { Host = "h", UserName = "u", CredentialName = "cw" },
        });
        await admin.PutTableAsync(site, "cw", new TableDto { Table = "dbo.Tickets", Method = CaptureMethod.ChangeTracking });
        await using var agent = await RunningAgent.StartAsync(api, admin, site, h =>
            h.Source("cw", "Tickets").Upsert("Tickets", 1, new Dictionary<string, object?> { ["name"] = "a" }));
        for (var i = 0; i < 400 && (await admin.GetStatusAsync()).Sites.Single(s => s.SiteId == site).Heartbeat?.Tables.FirstOrDefault()?.State != TableState.Streaming; i++)
            await Task.Delay(25);

        var viewer = new SiteDetailViewModel(Console("v@x", Roles.Viewer), User(Roles.Viewer), site);
        await viewer.LoadCommand.ExecuteAsync(null);
        Assert.Null(viewer.Error);
        var row = Assert.Single(viewer.Tables);
        Assert.Equal(("cw", "dbo.Tickets", "Streaming", "Change Tracking"), (row.SourceId, row.Table, row.State, row.Method));
        Assert.NotNull(viewer.Agent);
        Assert.False(viewer.CanOperate);
        Assert.False(viewer.CollectLogsCommand.CanExecute(null));
        await viewer.PauseTableCommand.ExecuteAsync(row);
        Assert.Contains("role does not allow", viewer.Error);

        var operatorView = new SiteDetailViewModel(Console("o@x", Roles.Operator), User(Roles.Operator), site);
        await operatorView.LoadCommand.ExecuteAsync(null);
        Assert.True(operatorView.CanOperate);
        await operatorView.PauseTableCommand.ExecuteAsync(operatorView.Tables[0]);
        Assert.Null(operatorView.Error);
        for (var i = 0; i < 400 && (await admin.GetStatusAsync()).Sites.Single(s => s.SiteId == site).Heartbeat?.Tables[0].State != TableState.Paused; i++)
            await Task.Delay(25);
        await operatorView.LoadCommand.ExecuteAsync(null);
        Assert.True(operatorView.Tables[0].IsPaused);
        Assert.Contains(operatorView.Commands, c => c.Type == CommandType.PauseTable && c.Result == "Table paused.");
        Assert.Contains(operatorView.Events, e => e.Event.Kind == AgentEventKind.Snapshot);

        var adminView = new SiteDetailViewModel(admin, User(Roles.Admin), site);
        await adminView.LoadCommand.ExecuteAsync(null);
        Assert.True(adminView.CollectLogsCommand.CanExecute(null));
        await adminView.CollectLogsCommand.ExecuteAsync(null);
        CommandDto? logs = null;
        for (var i = 0; i < 400 && logs?.CompletedUtc is null; i++)
        {
            await Task.Delay(25);
            logs = (await admin.GetCommandsAsync(site)).First(c => c.Type == CommandType.CollectLogs);
        }
        var zip = await adminView.DownloadLogsAsync(logs!);
        Assert.NotNull(zip);
        Assert.Equal((byte)'P', zip![0]); // a zip ("PK")
    }

    [Fact]
    public void Formats_read_well()
    {
        Assert.Equal("45 s", Format.Lag(45));
        Assert.Equal("12 min", Format.Lag(720));
        Assert.Equal("3.0 h", Format.Lag(3 * 3600));
        Assert.Equal("2.0 days", Format.Lag(2 * 86400));
        Assert.Equal("1.5 MB", Format.Bytes(1536 * 1024));
        Assert.Equal("—", Format.Lag(null));
    }
}
