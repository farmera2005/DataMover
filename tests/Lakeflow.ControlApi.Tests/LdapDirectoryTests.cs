using Lakeflow.ControlApi.Security;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>Directory sign-in's safety rules, without a directory; a real one runs with LDAP_TEST_URL (see docs/console.md).</summary>
public sealed class LdapDirectoryTests
{
    private static readonly LdapSettingsDto Settings = new()
    {
        Enabled = true, Url = "ldaps://127.0.0.1:1", BindDn = "CN=svc", UserBaseDn = "DC=x", AdminGroupDn = "CN=admins,DC=x",
    };

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("CORP\\alice", "alice")]
    [InlineData("  bob  ", "bob")]
    public void Account_names_drop_the_domain(string typed, string account) => Assert.Equal(account, LdapDirectory.AccountName(typed));

    [Fact]
    public void Filter_values_are_escaped_so_a_name_cannot_change_the_filter() =>
        Assert.Equal(@"\2a\29\28uid=\2a\5c\00\28", LdapDirectory.Escape("*)(uid=*\\\0("));

    [Fact]
    public async Task Empty_passwords_and_unencrypted_connections_are_never_tried()
    {
        var directory = new LdapDirectory(NullLogger<LdapDirectory>.Instance);
        Assert.Equal("Enter your password.", (await directory.AuthenticateAsync(Settings, "svc-pw", "alice", "", default)).Message);
        Assert.Contains("service account has no password", (await directory.AuthenticateAsync(Settings, "", "alice", "pw", default)).Message);
        Assert.Contains("never sent unencrypted", (await directory.AuthenticateAsync(Settings with { Url = "ldap://127.0.0.1:1" }, "svc-pw", "alice", "pw", default)).Message);
        var unreachable = await directory.AuthenticateAsync(Settings, "svc-pw", "alice", "pw", default);
        Assert.False(unreachable.Succeeded);
        Assert.Contains("Could not reach the directory server", unreachable.Message);
    }
}
