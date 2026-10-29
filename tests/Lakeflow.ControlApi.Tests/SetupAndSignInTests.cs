using System.Net;
using System.Net.Http.Json;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>First-run setup (and its window), sign-in, user management and the write-only settings, each on a fresh install.</summary>
public sealed class SetupAndSignInTests : IAsyncLifetime
{
    private readonly ApiFactory _api = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static void SkipWithoutDatabase() => Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);

    private static async Task<string> ProblemsAsync(HttpResponseMessage response) => $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";

    [SkippableFact]
    public async Task Setup_creates_the_first_administrator_once_and_signs_them_in()
    {
        SkipWithoutDatabase();
        var browser = _api.Anonymous();
        var status = await browser.GetAsync<SetupStatus>("auth/setup");
        Assert.True(status.NeedsSetup);
        Assert.True(status.WindowOpen);

        Assert.Contains("at least 12 characters", await ProblemsAsync(await browser.PostAsync("auth/setup", new SetupRequest("admin", "short", null))));
        var me = await (await browser.PostAsync("auth/setup", new SetupRequest(" Admin ", "a long enough password", "Adam"))).ReadAsync<Me>();
        Assert.Equal(("admin", ConsoleRole.Admin, "local"), (me.UserName, me.Role, me.AuthSource));
        // Signed in by the same response.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("sites")).StatusCode);
        Assert.False((await browser.GetAsync<SetupStatus>("auth/setup")).NeedsSetup);

        // Only once: nobody else can claim the install afterwards.
        var other = _api.Anonymous();
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsync("auth/setup", new SetupRequest("mallory", "another long password", null))).StatusCode);

        // The browser's host name became the server's name (certificates, install files).
        var server = await browser.GetAsync<ServerSettingsDto>("settings/server");
        Assert.Equal("localhost", server.Host);
        Assert.Equal("https://localhost:8444/", server.CollectorUrl);

        using var scope = _api.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<LakeflowDb>().AuditEntries.Where(a => a.Entity == "user admin").SingleAsync();
        Assert.Equal("setup (admin)", audit.Actor);
        Assert.Contains("\"PasswordHash\":\"(changed)\"", audit.After);
        Assert.DoesNotContain("a long enough password", audit.After);
    }

    [SkippableFact]
    public async Task Setup_is_refused_once_its_hour_has_passed()
    {
        SkipWithoutDatabase();
        var browser = _api.Anonymous();
        await browser.GetAsync("auth/setup"); // starts the app (and the window)
        _api.Time.Advance(TimeSpan.FromMinutes(61));
        var status = await browser.GetAsync<SetupStatus>("auth/setup");
        Assert.True(status.NeedsSetup);
        Assert.False(status.WindowOpen);
        var refused = await browser.PostAsync("auth/setup", new SetupRequest("admin", "a long enough password", null));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Restart the api container", await refused.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Sign_in_checks_the_password_and_the_account_and_signs_out_everywhere_on_changes()
    {
        SkipWithoutDatabase();
        var admin = _api.Console("boss", Roles.Admin);
        await admin.PostAsync("users", new UserRequest("Val", "Val Viewer", ConsoleRole.Viewer, false, "val's long password")).OkAsync();
        var users = await admin.GetAsync<List<UserDto>>("users");
        var val = users.Single(u => u.UserName == "val");

        var browser = _api.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("auth/sign-in", new SignInRequest("val", "wrong password!!"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("auth/sign-in", new SignInRequest("nobody", "val's long password"))).StatusCode);
        var me = await (await browser.PostAsync("auth/sign-in", new SignInRequest("VAL", "val's long password"))).ReadAsync<Me>();
        Assert.Equal(ConsoleRole.Viewer, me.Role);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("status")).StatusCode);

        // Disabling the account ends the session at once.
        await admin.PutAsync($"users/{val.Id}", new UserRequest("val", "Val Viewer", ConsoleRole.Viewer, true, null)).OkAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("status")).StatusCode);
        var disabled = await _api.Anonymous().PostAsync("auth/sign-in", new SignInRequest("val", "val's long password"));
        Assert.Contains("disabled", await disabled.Content.ReadAsStringAsync());

        // Signing out ends the session.
        var boss = _api.Console("boss", Roles.Admin);
        await boss.PostAsync("auth/sign-out", new { }).OkAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await boss.GetAsync("status")).StatusCode);
    }

    [SkippableFact]
    public async Task The_last_local_administrator_cannot_be_removed_or_demoted()
    {
        SkipWithoutDatabase();
        var admin = _api.Console("only-admin", Roles.Admin);
        var me = (await admin.GetAsync<List<UserDto>>("users")).Single(u => u.UserName == "only-admin");
        Assert.Contains("last active local administrator",
            await ProblemsAsync(await admin.PutAsync($"users/{me.Id}", new UserRequest("only-admin", null, ConsoleRole.Viewer, false, null))));
        Assert.Contains("own account", await ProblemsAsync(await admin.DeleteAsync($"users/{me.Id}")));

        await admin.PostAsync("users", new UserRequest("second", null, ConsoleRole.Admin, false, "second admin password")).OkAsync();
        await admin.PutAsync($"users/{me.Id}", new UserRequest("only-admin", null, ConsoleRole.Operator, false, null)).OkAsync();
    }

    [SkippableFact]
    public async Task Changing_your_password_needs_the_current_one()
    {
        SkipWithoutDatabase();
        var user = _api.Console("pat", Roles.Operator);
        Assert.Contains("not correct", await ProblemsAsync(await user.PostAsJsonAsync("auth/password", new { current_password = "nope", new_password = "a brand new password" })));
        await user.PostAsJsonAsync("auth/password", new { current_password = ApiFactory.TestPassword, new_password = "a brand new password" }).OkAsync();
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("auth/me")).StatusCode); // still signed in on this browser
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.Anonymous().PostAsync("auth/sign-in", new SignInRequest("pat", ApiFactory.TestPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.Anonymous().PostAsync("auth/sign-in", new SignInRequest("pat", "a brand new password"))).StatusCode);
    }

    [SkippableFact]
    public async Task Settings_secrets_are_write_only_encrypted_and_audited_as_changed()
    {
        SkipWithoutDatabase();
        var admin = _api.Console();
        const string key = "c2VjcmV0LWtleS1mb3ItdGhlLWdyYWluLWh1Yg==";
        var bad = await admin.PutAsync("settings/event-hubs/grain", new EventHubSettingsRequest("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=x"));
        Assert.Contains("EntityPath", await ProblemsAsync(bad));
        var hub = await (await admin.PutAsync("settings/event-hubs/grain", new EventHubSettingsRequest(
            $"Endpoint=sb://mercer-lake.servicebus.windows.net/;SharedAccessKeyName=lakeflow-send;SharedAccessKey={key};EntityPath=grain-changes"))).ReadAsync<EventHubSettingsDto>();
        Assert.Equal(("mercer-lake.servicebus.windows.net", "grain-changes", "lakeflow-send", true), (hub.Namespace, hub.HubName, hub.PolicyName, hub.HasKey));

        await admin.PutAsync("settings/fabric", new FabricSettingsRequest("x.datawarehouse.fabric.microsoft.com", "silver", Guid.NewGuid().ToString(), "fabric-client-secret")).OkAsync();
        var ldap = new LdapSettingsDto
        {
            Enabled = true, Url = "ldaps://dc1.corp.local:636", BindDn = "CN=svc,DC=corp,DC=local", UserBaseDn = "DC=corp,DC=local",
            AdminGroupDn = "CN=Lakeflow Admins,DC=corp,DC=local",
        };
        Assert.Contains("anonymous bind", await ProblemsAsync(await admin.PutAsync("settings/ldap", new LdapSettingsRequest(ldap, ""))));
        Assert.Contains("StartTLS", await ProblemsAsync(await admin.PutAsync("settings/ldap", new LdapSettingsRequest(ldap with { Url = "ldap://dc1:389" }, "pw"))));
        await admin.PutAsync("settings/ldap", new LdapSettingsRequest(ldap, "ldap-bind-password")).OkAsync();
        // Saving again without a password keeps the stored one.
        Assert.True((await (await admin.PutAsync("settings/ldap", new LdapSettingsRequest(ldap, null))).ReadAsync<LdapSettingsDto>()).HasBindPassword);

        var everything = string.Join("\n", await admin.GetStringAsync("settings/event-hubs"), await admin.GetStringAsync("settings/fabric"),
            await admin.GetStringAsync("settings/ldap"), await admin.GetStringAsync("audit?take=1000"));
        foreach (var secret in new[] { key, "fabric-client-secret", "ldap-bind-password" }) Assert.DoesNotContain(secret, everything);
        Assert.Contains("(changed)", everything);

        // At rest the secrets are encrypted with the master key, which is not in the database.
        using var scope = _api.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<LakeflowDb>().Settings.AsNoTracking().ToListAsync();
        var raw = string.Join("", rows.Select(r => r.Json + (r.Secret is null ? "" : System.Text.Encoding.Latin1.GetString(r.Secret))));
        foreach (var secret in new[] { key, "fabric-client-secret", "ldap-bind-password" }) Assert.DoesNotContain(secret, raw);
        Assert.True(File.Exists(Path.Combine(_api.DataDirectory, "master.key")));

        // Collectors get SAS tokens signed with the stored key, for the hub named in the connection string.
        var site = "hubs" + Guid.NewGuid().ToString("N")[..6];
        await admin.CreateSiteAsync(site);
        var (agent, cert) = await _api.EnrollAsync(admin, site);
        var target = Assert.Single((await _api.Agent(cert).SiteConfigAsync(agent)).EventHubs);
        Assert.Equal(("mercer-lake.servicebus.windows.net", "grain-changes"), (target.FullyQualifiedNamespace, target.HubName));
        Assert.True(SasTokens.IsValid(target.SasToken, target.FullyQualifiedNamespace, target.HubName, System.Text.Encoding.UTF8.GetBytes(key), _api.Time.GetUtcNow()));
    }

    [SkippableFact]
    public async Task A_site_whose_event_hub_is_not_set_up_waits_and_says_why()
    {
        SkipWithoutDatabase();
        var admin = _api.Console();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("settings/event-hubs/feed")).StatusCode);
        var site = "nohub" + Guid.NewGuid().ToString("N")[..6];
        await admin.CreateSiteAsync(site, Division.Feed);
        var (agent, cert) = await _api.EnrollAsync(admin, site);
        Assert.Empty((await _api.Agent(cert).GetAsync<CollectorConfig>($"agents/{agent}/config")).Sites);
        var status = (await admin.GetAsync<FleetStatus>("status")).Sites.Single(s => s.SiteId == site);
        Assert.Equal(Health.Red, status.Health);
        Assert.StartsWith("The Event Hub for feed is not set up yet", status.Reasons[0]);
    }
}
