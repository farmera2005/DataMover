using Lakeflow.Console.Core.ViewModels;
using Lakeflow.ControlApi.Tests;
using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Console.Core.Tests;

/// <summary>The first-run wizard and the Configuration pages' view models, on a fresh install.</summary>
public sealed class SetupWizardTests : IAsyncLifetime
{
    private readonly ApiFactory _api = new() { SeedEventHubs = false };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [SkippableFact]
    public async Task The_wizard_creates_the_administrator_a_hub_and_the_first_site()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var browser = ConsoleTests.Browser(_api);
        var shell = new ShellViewModel(browser);
        await shell.InitializeCommand.ExecuteAsync(null);
        Assert.Equal(ShellState.NeedsSetup, shell.State);

        var wizard = new SetupViewModel(browser) { Password = "a long enough password", ConfirmPassword = "a different password!" };
        await wizard.CreateAdministratorCommand.ExecuteAsync(null);
        Assert.Contains("not the same", wizard.Error);
        wizard.ConfirmPassword = "a long enough password";
        await wizard.CreateAdministratorCommand.ExecuteAsync(null);
        Assert.Null(wizard.Error);
        Assert.Equal(SetupStep.Destinations, wizard.Step);
        Assert.Equal(("admin", ConsoleRole.Admin), (wizard.Administrator!.UserName, wizard.Administrator.Role));
        Assert.Equal("", wizard.Password);

        wizard.ConnectionString = "not a connection string";
        await wizard.SaveHubCommand.ExecuteAsync(null);
        Assert.Contains("not an Event Hubs connection string", wizard.Error);
        wizard.ConnectionString = "Endpoint=sb://mercer.servicebus.windows.net/;SharedAccessKeyName=send;SharedAccessKey=abc;EntityPath=grain";
        await wizard.SaveHubCommand.ExecuteAsync(null);
        Assert.Equal("mercer.servicebus.windows.net", Assert.Single(wizard.Hubs).Namespace);
        wizard.Next();

        (wizard.SiteId, wizard.SiteName) = ("StMarys", "St. Marys");
        await wizard.CreateSiteCommand.ExecuteAsync(null);
        Assert.Null(wizard.Error);
        Assert.Equal(SetupStep.Done, wizard.Step);
        Assert.Equal("stmarys", wizard.CreatedSiteId);
        Assert.Contains("built-in collector has not checked in yet", wizard.Message);

        // The reloaded console is signed in as the new administrator.
        var reloaded = new ShellViewModel(browser);
        await reloaded.InitializeCommand.ExecuteAsync(null);
        Assert.Equal(ShellState.SignedIn, reloaded.State);
        Assert.True(reloaded.User!.CanAdminister);
    }

    [SkippableFact]
    public async Task Configuration_pages_save_users_destinations_sign_in_and_server_settings()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        _api.Console("admin");
        var browser = ConsoleTests.Browser(_api);
        await browser.SignInAsync(new SignInRequest("admin", ApiFactory.TestPassword));

        var users = new UsersViewModel(browser);
        await users.LoadCommand.ExecuteAsync(null);
        users.New();
        (users.UserName, users.Role, users.Password) = ("vic", ConsoleRole.Viewer, "vic's long password");
        await users.SaveCommand.ExecuteAsync(null);
        Assert.Null(users.Error);
        Assert.Contains(users.Users, u => u.UserName == "vic" && u.Role == ConsoleRole.Viewer);

        var destinations = new DestinationsViewModel(browser);
        await destinations.LoadCommand.ExecuteAsync(null);
        Assert.Empty(destinations.Hubs);
        (destinations.Division, destinations.ConnectionString) = (Division.Feed,
            "Endpoint=sb://mercer.servicebus.windows.net/;SharedAccessKeyName=send;SharedAccessKey=abc;EntityPath=feed");
        await destinations.SaveHubCommand.ExecuteAsync(null);
        Assert.Equal(Division.Feed, Assert.Single(destinations.Hubs).Division);
        (destinations.FabricServer, destinations.FabricDatabase, destinations.FabricClientId, destinations.FabricClientSecret) =
            ("x.datawarehouse.fabric.microsoft.com", "silver", Guid.NewGuid().ToString(), "s3cret");
        await destinations.SaveFabricCommand.ExecuteAsync(null);
        Assert.True(destinations.FabricHasSecret);
        Assert.Equal("", destinations.FabricClientSecret);

        var snowflake = new SnowflakeViewModel(browser);
        await snowflake.LoadCommand.ExecuteAsync(null);
        Assert.Null(snowflake.Settings.PublicKey);
        snowflake.Settings = snowflake.Settings with { Account = "mercer-lake" };
        await snowflake.SaveCommand.ExecuteAsync(null);
        await snowflake.GenerateKeyCommand.ExecuteAsync(null);
        Assert.Contains("RSA_PUBLIC_KEY", snowflake.Settings.SetupScript);
        await snowflake.SetEnabledAsync(true, default);
        Assert.Contains("snowflake-listen", snowflake.Error); // no hub's listen key yet
        Assert.False(snowflake.Settings.Enabled);

        var alerts = new AlertsViewModel(browser, canOperate: true);
        await alerts.RefreshCommand.ExecuteAsync(null);
        Assert.Null(alerts.Error);
        Assert.Empty(alerts.Alerts); // nothing has heartbeated, and sites being set up raise nothing
        alerts.State = "all";
        await alerts.RefreshCommand.ExecuteAsync(null);
        Assert.Null(alerts.Error);

        var notifications = new NotificationsViewModel(browser);
        await notifications.LoadCommand.ExecuteAsync(null);
        Assert.Equal("America/New_York", notifications.Schedule.TimeZone);
        notifications.Schedule = notifications.Schedule with { QuietStart = "22:00" };
        await notifications.SaveScheduleCommand.ExecuteAsync(null);
        Assert.Contains("both a start and an end", notifications.Error);
        notifications.Schedule = notifications.Schedule with { QuietEnd = "06:00" };
        notifications.AddSeason();
        await notifications.SaveScheduleCommand.ExecuteAsync(null);
        Assert.Null(notifications.Error);
        Assert.Equal("Fall harvest", Assert.Single(notifications.Seasons).Name);
        Assert.Equal(["a@x.com", "b@x.com", "c@x.com"], NotificationsViewModel.SplitRecipients("a@x.com\nb@x.com; c@x.com,"));
        notifications.Email = notifications.Email with { Enabled = true };
        await notifications.SaveEmailCommand.ExecuteAsync(null);
        Assert.Contains("recipient", notifications.Error);

        var updates = new UpdatesViewModel(browser);
        await updates.LoadCommand.ExecuteAsync(null);
        Assert.Null(updates.Error);
        Assert.Empty(updates.Packages);
        Assert.Equal("1.4.0", UpdatesViewModel.VersionFromFileName("LakeflowAgent-1.4.0.msi"));
        Assert.Null(UpdatesViewModel.VersionFromFileName("LakeflowAgent.msi"));
        updates.UploadVersion = "1.4.0";
        await updates.UploadAsync(new MemoryStream("not an msi"u8.ToArray()), "LakeflowAgent-1.4.0.msi", default);
        Assert.Contains("not an MSI", updates.Error);

        var signIn = new SignInSettingsViewModel(browser);
        await signIn.LoadCommand.ExecuteAsync(null);
        Assert.False(signIn.Settings.Enabled);
        signIn.Settings = signIn.Settings with { Enabled = true, Url = "ldaps://127.0.0.1:1", BindDn = "CN=svc", UserBaseDn = "DC=x", ViewerGroupDn = "CN=v" };
        signIn.BindPassword = "bind";
        (signIn.TestUserName, signIn.TestPassword) = ("someone", "pw");
        await signIn.TestCommand.ExecuteAsync(null);
        Assert.False(signIn.TestResult!.Succeeded);
        Assert.Contains("Could not reach", signIn.TestResult.Message);
        await signIn.SaveCommand.ExecuteAsync(null);
        Assert.True(signIn.Settings.HasBindPassword);

        var server = new ServerSettingsViewModel(browser);
        await server.LoadCommand.ExecuteAsync(null);
        server.Host = "lakeflow.corp.local";
        await server.SaveHostCommand.ExecuteAsync(null);
        Assert.Contains("lakeflow.corp.local", server.Server!.WebCertificate.Names);
        Assert.Equal("https://lakeflow.corp.local:8444/", server.Server.CollectorUrl);
        Assert.False(server.Server.WebCertificate.Uploaded);
    }
}
