using System.Net;
using System.Security.Cryptography;
using System.Text;
using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>Configuration → Updates: signed MSIs uploaded, a target version, and the offer collectors get and download.</summary>
public sealed class UpdateTests : IAsyncLifetime
{
    private readonly ApiFactory _api = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static async Task<string> ProblemsAsync(HttpResponseMessage response) => $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";

    /// <summary>Looks like a signed MSI to the server: the compound-file header and a DigitalSignature stream name.</summary>
    private static byte[] Msi(bool signed = true, int size = 200_000)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        // Straddles the server's 80 KB read chunks.
        if (signed) Encoding.Unicode.GetBytes("\u0005DigitalSignature").CopyTo(bytes, 81920 - 10);
        else Array.Clear(bytes, 8, size - 8);
        return bytes;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient admin, string? version, byte[] body) =>
        admin.PostAsync($"settings/updates/packages?version={version}&fileName=LakeflowAgent-{version}.msi", new ByteArrayContent(body));

    [SkippableFact]
    public async Task Uploads_are_checked_and_the_target_is_offered_to_collectors_to_download()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = _api.Console();
        var package = Msi();
        Assert.Contains("version", await ProblemsAsync(await UploadAsync(admin, "1.2", package)));
        Assert.Contains("not an MSI", await ProblemsAsync(await UploadAsync(admin, "1.2.0", Encoding.UTF8.GetBytes("MZ not an msi"))));
        Assert.Contains("not signed", await ProblemsAsync(await UploadAsync(admin, "1.2.0", Msi(signed: false))));
        var uploaded = await (await UploadAsync(admin, "1.2.0", package)).ReadAsync<UpdateSettingsDto>();
        var dto = Assert.Single(uploaded.Packages);
        Assert.Equal(("1.2.0", "LakeflowAgent-1.2.0.msi", Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(), (long)package.Length),
            (dto.Version, dto.FileName, dto.Sha256, dto.SizeBytes));
        Assert.Contains("already uploaded", await ProblemsAsync(await UploadAsync(admin, "1.2.0", package)));

        var site = "upd" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
        await admin.CreateSiteAsync(site);
        var (agent, cert) = await _api.EnrollAsync(admin, site);
        var collector = _api.Agent(cert);
        Assert.Null((await collector.GetAsync<CollectorConfig>($"agents/{agent}/config")).Update);
        Assert.Equal(HttpStatusCode.NotFound, (await collector.GetAsync($"agents/{agent}/packages/1.2.0")).StatusCode); // not its target

        Assert.Contains("Upload the 9.9.9 package first", await ProblemsAsync(await admin.PutAsync("settings/updates", new UpdateSettingsRequest("9.9.9"))));
        await admin.PutAsync("settings/updates", new UpdateSettingsRequest("1.2.0")).OkAsync();
        var config = await collector.GetAsync<CollectorConfig>($"agents/{agent}/config");
        Assert.Equal("1.2.0", config.DesiredAgentVersion);
        Assert.Equal(new UpdateOffer("1.2.0", dto.Sha256, package.Length, $"agents/{agent}/packages/1.2.0"), config.Update);
        Assert.Equal(package, await collector.GetByteArrayAsync(config.Update!.DownloadPath));
        Assert.Equal(HttpStatusCode.NotFound, (await collector.GetAsync($"agents/{agent}/packages/1.1.0")).StatusCode);
        // Console sessions cannot download from the collector routes.
        Assert.NotEqual(HttpStatusCode.OK, (await admin.GetAsync(config.Update.DownloadPath)).StatusCode);

        // A site's own target wins over the fleet's.
        await UploadAsync(admin, "1.3.0", Msi()).OkAsync();
        var siteDto = await admin.GetAsync<SiteDto>($"sites/{site}");
        Assert.Contains("upload that package first", await ProblemsAsync(await admin.PutAsync($"sites/{site}", siteDto with { DesiredAgentVersion = "7.0.0" })));
        await admin.PutAsync($"sites/{site}", siteDto with { DesiredAgentVersion = "1.3.0" }).OkAsync();
        Assert.Equal("1.3.0", (await collector.GetAsync<CollectorConfig>($"agents/{agent}/config")).Update!.Version);

        // The collector reports where it is; the console shows it.
        await collector.PostAsync($"agents/{agent}/heartbeat", new Heartbeat
        {
            AgentId = agent, SiteId = site, AgentVersion = "1.1.0", SentUtc = _api.Time.GetUtcNow(), ConfigVersion = 1, EventHubReachable = true,
            Platform = "windows-service", Update = new UpdateStatus(UpdateState.Waiting, "1.3.0", "Downloaded and checked; installs in the maintenance window."),
        }).OkAsync();
        var view = await admin.GetAsync<UpdateSettingsDto>("settings/updates");
        var row = view.Collectors.Single(c => c.AgentId == agent);
        Assert.Equal(("windows-service", "1.1.0", "1.3.0", UpdateState.Waiting), (row.Platform, row.Version, row.Target, row.Update!.State));
        Assert.Equal("1.2.0", view.TargetVersion);

        // Packages in use cannot be deleted.
        Assert.Contains("fleet's target", await ProblemsAsync(await admin.DeleteAsync("settings/updates/packages/1.2.0")));
        Assert.Contains("target of", await ProblemsAsync(await admin.DeleteAsync("settings/updates/packages/1.3.0")));
        await admin.PutAsync("settings/updates", new UpdateSettingsRequest(null)).OkAsync();
        Assert.DoesNotContain((await (await admin.DeleteAsync("settings/updates/packages/1.2.0")).ReadAsync<UpdateSettingsDto>()).Packages, p => p.Version == "1.2.0");
        Assert.False(File.Exists(Path.Combine(_api.DataDirectory, "packages", $"{dto.Sha256}.msi")));
        Assert.Contains("agent package 1.2.0", await admin.GetStringAsync("audit?take=1000"));
    }
}
