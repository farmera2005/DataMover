using System.Net;
using System.Security.Cryptography;
using Lakeflow.Agent.Control;
using Lakeflow.Agent.Update;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lakeflow.Agent.Tests;

/// <summary>The agent's side of self-update: what it downloads, checks, waits for and asks the updater service to install.</summary>
public sealed class SelfUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakeflow-update-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _package = RandomNumberGenerator.GetBytes(4096);
    // 15:00 UTC on a winter day: 10:00 in New York.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero));
    private int _downloads;

    private sealed class FakePlatform(string updater) : IUpdatePlatform
    {
        public CollectorPlatform Kind { get; init; } = CollectorPlatform.WindowsService;
        public string? OwnPublisher { get; init; } = "CN=Mercer Landmark";
        public string? PackagePublisher { get; set; } = "CN=Mercer Landmark";
        public string? Publisher(string path) => PackagePublisher;
        public string UpdaterFolder => updater;
    }

    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    public SelfUpdateTests() => Directory.CreateDirectory(Path.Combine(_root, "updater"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Updates => Path.Combine(_root, "data", "updates");
    private string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private (SelfUpdate Update, FakePlatform Platform, ControlApiClient Client) Make(CollectorPlatform kind = CollectorPlatform.WindowsService,
        string? own = "CN=Mercer Landmark", byte[]? served = null)
    {
        var platform = new FakePlatform(Path.Combine(_root, "updater")) { Kind = kind, OwnPublisher = own };
        var http = new HttpClient(new Server(request =>
        {
            Assert.Equal("/agents/a1/packages/1.2.0", request.RequestUri!.AbsolutePath);
            _downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served ?? _package) };
        })) { BaseAddress = new Uri("https://api.test/") };
        return (new SelfUpdate(Path.Combine(_root, "data"), "1.1.0", platform, _time, NullLogger<SelfUpdate>.Instance), platform, new ControlApiClient(http, "a1"));
    }

    private CollectorConfig Config(string? target = "1.2.0", MaintenanceWindow? window = null, string? sha = null) => new()
    {
        AgentId = "a1",
        DesiredAgentVersion = target,
        Update = target is null ? null : new UpdateOffer(target, sha ?? Sha(_package), _package.Length, $"agents/a1/packages/{target}"),
        Sites = [new AgentConfig { AgentId = "a1", SiteId = "stmarys", ConfigVersion = 1, EventHubs = [], MaintenanceWindow = window, SiteTimeZone = "America/New_York" }],
    };

    private static readonly MaintenanceWindow Night = new(new TimeOnly(2, 0), new TimeOnly(4, 0));

    [Fact]
    public async Task On_target_or_without_one_it_reports_nothing()
    {
        var (update, _, client) = Make();
        await update.RunAsync(Config(target: null), client, default);
        Assert.Null(update.Status);
        await update.RunAsync(Config(target: "1.1.0"), client, default);
        Assert.Null(update.Status);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task Docker_and_other_collectors_say_how_to_update_them_by_hand()
    {
        var (docker, _, client) = Make(CollectorPlatform.Docker);
        await docker.RunAsync(Config(), client, default);
        Assert.Equal(UpdateState.Manual, docker.Status!.State);
        Assert.Contains("docker compose up -d --build", docker.Status.Detail);
        Assert.Equal("docker", docker.Platform);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task It_downloads_checks_and_waits_for_the_window_then_asks_the_updater_once()
    {
        var (update, _, client) = Make();
        await update.RunAsync(Config(window: Night), client, default);
        Assert.Equal(UpdateState.Waiting, update.Status!.State);
        Assert.Contains("02:00 to 04:00 America/New_York", update.Status.Detail);
        Assert.Equal(_package, File.ReadAllBytes(Path.Combine(Updates, "LakeflowAgent-1.2.0.msi")));
        Assert.False(File.Exists(Path.Combine(Updates, UpdateFiles.Request)));

        await update.RunAsync(Config(window: Night), client, default);
        Assert.Equal(1, _downloads); // already downloaded and checked

        _time.SetUtcNow(new DateTimeOffset(2026, 1, 16, 8, 0, 0, TimeSpan.Zero)); // 03:00 in New York
        await update.RunAsync(Config(window: Night), client, default);
        Assert.Equal(UpdateState.Installing, update.Status!.State);
        var request = UpdateFiles.Read<UpdateFiles.InstallRequest>(Path.Combine(Updates, UpdateFiles.Request))!;
        Assert.Equal(("1.2.0", Sha(_package)), (request.Version, request.Sha256));

        await update.RunAsync(Config(window: Night), client, default); // the updater has not taken it yet
        Assert.Equal(UpdateState.Installing, update.Status!.State);
        Assert.Equal(1, UpdateFiles.Read<UpdateFiles.AttemptLog>(Path.Combine(Updates, UpdateFiles.Attempts))!.Count);
    }

    [Fact]
    public async Task It_refuses_what_it_cannot_trust()
    {
        var (unsigned, _, client) = Make(own: null);
        await unsigned.RunAsync(Config(), client, default);
        Assert.Equal(UpdateState.Failed, unsigned.Status!.State);
        Assert.Contains("not signed", unsigned.Status.Detail);

        var (update, platform, client2) = Make();
        await update.RunAsync(Config(target: "1.0.9"), client2, default);
        Assert.Contains("older than the installed 1.1.0", update.Status!.Detail);

        await update.RunAsync(Config(sha: new string('0', 64)), client2, default);
        Assert.Contains("does not match the package's SHA-256", update.Status!.Detail);
        Assert.False(File.Exists(Path.Combine(Updates, "LakeflowAgent-1.2.0.msi")));

        platform.PackagePublisher = "CN=Someone Else";
        await update.RunAsync(Config(), client2, default);
        Assert.Equal(UpdateState.Failed, update.Status!.State);
        Assert.Contains("signed by CN=Someone Else, not by CN=Mercer Landmark", update.Status.Detail);
        Assert.False(File.Exists(Path.Combine(Updates, UpdateFiles.Request)));

        platform.PackagePublisher = null;
        await update.RunAsync(Config(), client2, default);
        Assert.Contains("is not signed", update.Status!.Detail);
    }

    [Fact]
    public async Task A_failed_install_is_retried_in_the_next_windows_then_given_up_after_three()
    {
        var (update, _, client) = Make();
        for (var attempt = 1; attempt <= SelfUpdate.MaxAttempts; attempt++)
        {
            await update.RunAsync(Config(), client, default); // no window: any time
            Assert.Equal(UpdateState.Installing, update.Status!.State);
            // The updater takes the request, msiexec fails.
            File.Delete(Path.Combine(Updates, UpdateFiles.Request));
            UpdateFiles.Write(Path.Combine(_root, "updater", UpdateFiles.Result),
                new UpdateFiles.InstallResult("1.2.0", Sha(_package), 1603, "msiexec failed (1603); see install-1.2.0.log.", _time.GetUtcNow()));
        }
        await update.RunAsync(Config(), client, default);
        Assert.Equal(UpdateState.Failed, update.Status!.State);
        Assert.Contains("Tried 3 times", update.Status.Detail);
        Assert.Contains("1603", update.Status.Detail);

        // A new package starts over.
        var newer = RandomNumberGenerator.GetBytes(100);
        var (again, _, client2) = Make(served: newer);
        await again.RunAsync(Config(sha: Sha(newer)), client2, default);
        Assert.Equal(UpdateState.Installing, again.Status!.State);
    }

    [Fact]
    public void Maintenance_windows_are_in_the_sites_time_zone_and_may_run_past_midnight()
    {
        var late = new MaintenanceWindow(new TimeOnly(23, 0), new TimeOnly(1, 0));
        var ny = "America/New_York";
        Assert.True(SelfUpdate.InWindow([(late, ny)], new DateTimeOffset(2026, 7, 1, 3, 30, 0, TimeSpan.Zero))); // 23:30 EDT
        Assert.True(SelfUpdate.InWindow([(late, ny)], new DateTimeOffset(2026, 7, 1, 4, 30, 0, TimeSpan.Zero))); // 00:30 EDT
        Assert.False(SelfUpdate.InWindow([(late, ny)], new DateTimeOffset(2026, 7, 1, 5, 30, 0, TimeSpan.Zero))); // 01:30 EDT
        Assert.True(SelfUpdate.InWindow([(null, ny)], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_installers_log_says_how_it_ended()
    {
        var log = Path.Combine(_root, "install.log");
        File.WriteAllText(log, "MSI (s) (A0:B4) [03:00:01:123]: Windows Installer installed the product. Product Name: Lakeflow Agent. Product Version: 1.2.0. Installation success or error status: 0.\r\n", System.Text.Encoding.Unicode);
        Assert.Equal(0, UpdateFiles.ExitCodeFromLog(log));
        File.WriteAllText(log, "... Installation success or error status: 1603.\r\n");
        Assert.Equal(1603, UpdateFiles.ExitCodeFromLog(log));
        Assert.Null(UpdateFiles.ExitCodeFromLog(Path.Combine(_root, "missing.log")));
    }
}
