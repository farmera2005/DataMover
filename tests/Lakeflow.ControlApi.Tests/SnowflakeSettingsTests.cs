using System.Net;
using Lakeflow.ControlApi.Data;
using Lakeflow.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>Configuration → Snowflake: the generated key pair stays on the server, and the forwarder only turns on when it can run.</summary>
public sealed class SnowflakeSettingsTests : IAsyncLifetime
{
    private readonly ApiFactory _api = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static async Task<string> ProblemsAsync(HttpResponseMessage response) => $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";

    private static SnowflakeSettingsRequest Settings(bool enabled = false) => new(enabled, "mercer-lake", "LAKEFLOW_FORWARDER", "LAKEFLOW_FORWARDER", "LAKEFLOW_WH", "LAKEFLOW", 5);

    private const string ListenKey = "c25vd2ZsYWtlLWxpc3Rlbi1rZXktZm9yLWdyYWlu";

    [SkippableFact]
    public async Task The_key_pair_is_generated_on_the_server_and_only_its_public_half_is_shown()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = _api.Console();
        var empty = await admin.GetAsync<SnowflakeSettingsDto>("settings/snowflake");
        Assert.Null(empty.PublicKey);
        Assert.Equal(Enum.GetValues<Division>().Length, empty.Hubs.Count); // every seeded hub is listed, none has a listen key
        Assert.All(empty.Hubs, h => Assert.False(h.HasListenKey));
        var untested = await (await admin.PostAsync("settings/snowflake/test", new { })).ReadAsync<SettingsTestResult>();
        Assert.False(untested.Succeeded);
        Assert.Contains("generate the key pair", untested.Message);

        await admin.PutAsync("settings/snowflake", Settings()).OkAsync();
        var first = await (await admin.PostAsync("settings/snowflake/key", new { })).ReadAsync<SnowflakeSettingsDto>();
        Assert.StartsWith("MII", first.PublicKey);
        Assert.StartsWith("SHA256:", first.PublicKeyFingerprint);
        Assert.Contains($"RSA_PUBLIC_KEY = '{first.PublicKey}'", first.SetupScript);
        Assert.Equal("mercer-lake", first.Account); // generating a key keeps the saved settings
        var second = await (await admin.PostAsync("settings/snowflake/key", new { })).ReadAsync<SnowflakeSettingsDto>();
        Assert.NotEqual(first.PublicKey, second.PublicKey);

        using var scope = _api.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<LakeflowDb>().Settings.AsNoTracking().SingleAsync(s => s.Key == "snowflake");
        Assert.NotNull(row.Secret);
        var everything = string.Join("\n", await admin.GetStringAsync("settings/snowflake"), await admin.GetStringAsync("audit?take=1000"),
            row.Json, System.Text.Encoding.Latin1.GetString(row.Secret!));
        Assert.DoesNotContain("PRIVATE KEY", everything);
        Assert.Contains("(changed)", everything);
    }

    [SkippableFact]
    public async Task Settings_are_validated_and_it_turns_on_only_with_a_key_and_a_listen_hub()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = _api.Console();
        Assert.Contains("not a URL", await ProblemsAsync(await admin.PutAsync("settings/snowflake", Settings() with { Account = "https://mercer-lake.snowflakecomputing.com" })));
        Assert.Contains("Warehouse", await ProblemsAsync(await admin.PutAsync("settings/snowflake", Settings() with { Warehouse = "WH; DROP" })));
        Assert.Contains("1 to 1440", await ProblemsAsync(await admin.PutAsync("settings/snowflake", Settings() with { SilverIntervalMinutes = 0 })));
        var noKey = await ProblemsAsync(await admin.PutAsync("settings/snowflake", Settings(enabled: true)));
        Assert.Contains("Generate the key pair", noKey);
        Assert.Contains("snowflake-listen", noKey);

        await admin.PostAsync("settings/snowflake/key", new { }).OkAsync();
        Assert.Contains("EntityPath", await ProblemsAsync(await admin.PutAsync("settings/snowflake/hubs/grain",
            new EventHubSettingsRequest($"Endpoint=sb://{ApiFactory.Namespace}/;SharedAccessKeyName=snowflake-listen;SharedAccessKey={ListenKey}"))));
        Assert.Contains("collectors send to", await ProblemsAsync(await admin.PutAsync("settings/snowflake/hubs/grain",
            new EventHubSettingsRequest($"Endpoint=sb://{ApiFactory.Namespace}/;SharedAccessKeyName=snowflake-listen;SharedAccessKey={ListenKey};EntityPath=feed-changes"))));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsync("settings/snowflake/hubs/nope",
            new EventHubSettingsRequest($"Endpoint=sb://{ApiFactory.Namespace}/;SharedAccessKeyName=snowflake-listen;SharedAccessKey={ListenKey};EntityPath=grain-changes"))).StatusCode);
        var withHub = await (await admin.PutAsync("settings/snowflake/hubs/grain", new EventHubSettingsRequest(
            $"Endpoint=sb://{ApiFactory.Namespace}/;SharedAccessKeyName=snowflake-listen;SharedAccessKey={ListenKey};EntityPath={LakeNames.HubName(Division.Grain)}"))).ReadAsync<SnowflakeSettingsDto>();
        Assert.True(withHub.Hubs.Single(h => h.Division == Division.Grain).HasListenKey);
        Assert.DoesNotContain(ListenKey, await admin.GetStringAsync("settings/snowflake") + await admin.GetStringAsync("audit?take=1000"));

        Assert.True((await (await admin.PutAsync("settings/snowflake", Settings(enabled: true))).ReadAsync<SnowflakeSettingsDto>()).Enabled);
        var status = await admin.GetAsync<SnowflakeStatusDto>("settings/snowflake/status");
        Assert.NotNull(status);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("settings/snowflake/hubs/grain")).StatusCode);
        Assert.False((await admin.GetAsync<SnowflakeSettingsDto>("settings/snowflake")).Hubs.Single(h => h.Division == Division.Grain).HasListenKey);
    }

    [SkippableFact]
    public async Task Only_admins_see_the_snowflake_settings()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var operatorUser = _api.Console("olive@mercerlandmark.com", Lakeflow.ControlApi.Security.Roles.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorUser.GetAsync("settings/snowflake")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorUser.GetAsync("settings/snowflake/status")).StatusCode);
    }
}
