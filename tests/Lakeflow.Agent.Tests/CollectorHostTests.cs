using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;
using Lakeflow.Testing.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lakeflow.Agent.Tests;

/// <summary>The collector host as the program runs it: a store directory and background loops per site.</summary>
public sealed class CollectorHostTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lakeflow-collector-" + Guid.NewGuid().ToString("N"));
    private readonly FileKeyProtector _protector;
    private readonly Dictionary<string, InMemorySource> _sources = [];
    private readonly Dictionary<string, FakeSender> _senders = [];

    public CollectorHostTests() => _protector = new FileKeyProtector(Path.Combine(_dir, "secrets", "master.key"));

    private sealed class Factory(InMemorySource source) : Capture.IConnectorFactory
    {
        public ISourceConnector Create(SourceConfig config) => new InMemoryConnector(source);
    }

    private CollectorHost NewHost() => new(LocalStore.Open(Path.Combine(_dir, "collector"), _protector), siteId =>
    {
        var store = LocalStore.Open(Path.Combine(_dir, "sites", siteId), _protector);
        var sender = _senders[siteId] = new FakeSender();
        return new SiteParts(store, new AgentPipeline(store, new Factory(_sources[siteId]), sender, TimeProvider.System, NullLoggerFactory.Instance, "1.0.0"));
    }, NullLogger<CollectorHost>.Instance);

    private static CollectorConfig Config(params string[] sites) => new()
    {
        AgentId = "collector-1",
        Sites = sites.Select(site => Harness.Config(Harness.SourceConfig("cw", CaptureMethod.ChangeTracking, "Tickets")) with { SiteId = site }).ToList(),
    };

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "timed out");
    }

    [Fact]
    public async Task Sites_run_in_their_own_stores_and_loops_and_come_back_after_a_restart()
    {
        foreach (var site in new[] { "north", "south" })
        {
            _sources[site] = new InMemorySource();
            _sources[site].CreateTable("Tickets");
            for (var id = 1; id <= 5; id++) _sources[site].Upsert("Tickets", id, new Dictionary<string, object?> { ["name"] = $"{site} {id}" });
        }

        var host = NewHost();
        await host.ApplyAsync(Config("north", "south"), cache: true, CancellationToken.None);
        await WaitAsync(() => _senders["north"].Snapshot().Count(e => e.Op == ChangeOp.Snapshot) == 5
                              && _senders["south"].Snapshot().Count(e => e.Op == ChangeOp.Snapshot) == 5);
        Assert.All(_senders["north"].Snapshot(), e => Assert.Equal("north", e.SiteId));
        Assert.True(File.Exists(Path.Combine(_dir, "sites", "north", "buffer.db")));
        Assert.True(File.Exists(Path.Combine(_dir, "sites", "south", "buffer.db")));

        // South is unassigned: its loops stop, its data stays on disk.
        await host.ApplyAsync(Config("north"), cache: true, CancellationToken.None);
        Assert.Equal(["north"], host.Sites.Keys);
        Assert.True(File.Exists(Path.Combine(_dir, "sites", "south", "buffer.db")));
        await host.DisposeAsync();
        host.IdentityStore.Dispose();

        // Restart with the API down: the cached config brings north back, and it carries on from its checkpoint.
        _sources["north"].Upsert("Tickets", 6, new Dictionary<string, object?> { ["name"] = "after restart" });
        var restarted = NewHost();
        Assert.True(await restarted.LoadCachedAsync(CancellationToken.None));
        Assert.Equal(["north"], restarted.Sites.Keys);
        await WaitAsync(() => _senders["north"].Snapshot().Any(e => e.Key["id"]?.GetValue<int>() == 6));
        Assert.DoesNotContain(_senders["north"].Snapshot(), e => e.Op == ChangeOp.Snapshot); // no second snapshot
        await restarted.DisposeAsync();
        restarted.IdentityStore.Dispose();
    }

    [Fact]
    public void Key_file_is_created_owner_only_and_reused()
    {
        var keyFile = Path.Combine(_dir, "secrets", "master.key");
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
        var sealedValue = _protector.Protect("secret"u8.ToArray());
        Assert.Equal("secret"u8.ToArray(), new FileKeyProtector(keyFile).Unprotect(sealedValue));
        var other = new FileKeyProtector(Path.Combine(_dir, "other.key"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => other.Unprotect(sealedValue));
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    /// <summary>The installer's ACLs name the service by this SID before the service exists; it must be Windows' own.</summary>
    [Fact]
    public void Service_sid_is_computed_like_windows_does() =>
        Assert.Equal("S-1-5-80-2136591765-628530825-2363202261-254100150-1450642212",
            Lakeflow.Agent.Control.EnrollmentFile.ServiceSid(Lakeflow.Agent.Control.EnrollmentFile.ServiceName));
}
