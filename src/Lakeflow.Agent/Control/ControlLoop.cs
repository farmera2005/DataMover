using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent.Control;

/// <summary>
/// The collector's side of the control plane: pulls the config of every assigned site (with commands and sealed
/// credentials) every 60 s, sends each site's heartbeat every 30 s, posts events, and renews the certificate. While the
/// API is unreachable the collector keeps running on its cached config and retries with back-off.
/// </summary>
public sealed class ControlLoop(
    CollectorHost host, ControlApiHandlerFactory handlers, TimeProvider time, ILogger<ControlLoop> log,
    string logDirectory, Action requestRestart) : IDisposable
{
    private LocalStore store => host.IdentityStore;

    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    private const long MaxLogBundle = 20L * 1024 * 1024;

    private AgentIdentity? _identity;
    private ControlApiClient? _client;
    private DateTimeOffset _nextConfig = DateTimeOffset.MinValue;
    private DateTimeOffset _nextHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRenew = DateTimeOffset.MinValue;

    public int ConsecutiveFailures { get; private set; }
    public string? LastError { get; private set; }
    public bool Enrolled => Identity is not null;

    public AgentIdentity? Identity
    {
        get
        {
            if (_identity is null && AgentIdentity.Load(store) is { } loaded) Use(loaded);
            return _identity;
        }
    }

    private void Use(AgentIdentity identity)
    {
        _client?.Dispose();
        _identity?.Dispose();
        _identity = identity;
        _client = new ControlApiClient(ControlApiClient.CreateHttp(identity.ControlApi, handlers(identity.Certificate)), identity.AgentId);
    }

    /// <summary>Enrolls with a one-time token from the console (passed to the installer).</summary>
    public async Task EnrollAsync(string controlApi, string token, string hostname, string agentVersion, CancellationToken ct)
    {
        var (key, csr) = AgentIdentity.NewRequest(hostname);
        using (key)
        {
            using var anonymous = ControlApiClient.CreateHttp(controlApi, handlers(null));
            var response = await ControlApiClient.EnrollAsync(anonymous, new EnrollRequest(token, csr, hostname, agentVersion), ct);
            Use(AgentIdentity.Save(store, key, response.CertificatePem, controlApi));
            log.LogInformation("Enrolled as {AgentId} ({Scope})", response.AgentId, response.SiteId is null ? "central collector" : $"site {response.SiteId}");
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (Identity is null || _client is not { } client) return;
        var now = time.GetUtcNow();
        if (now >= _nextConfig)
        {
            try
            {
                var config = await client.GetConfigAsync(ct);
                await host.ApplyAsync(config, cache: true, ct);
                foreach (var siteConfig in config.Sites)
                {
                    if (!host.Sites.TryGetValue(siteConfig.SiteId, out var site) || site.Pipeline.Config?.ConfigVersion != siteConfig.ConfigVersion)
                        continue; // refused config: leave its credentials and commands for when it is fixed
                    HandleCredentials(site, siteConfig, now);
                    await HandleCommandsAsync(client, site, siteConfig, ct);
                }
                _nextConfig = now + TimeSpan.FromSeconds(config.ConfigPollSeconds);
                if (ConsecutiveFailures > 0) log.LogInformation("Control API reachable again");
                ConsecutiveFailures = 0;
                LastError = null;
            }
            catch (Exception ex) when (ex is HttpRequestException or ControlApiException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                ConsecutiveFailures++;
                LastError = ex.Message;
                var delay = Backoff.Delay(ConsecutiveFailures, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
                _nextConfig = now + delay;
                log.LogWarning("Config pull failed ({Failures} in a row), running on cached config; retrying in {Delay}: {Error}",
                    ConsecutiveFailures, delay, ex.Message);
            }
        }

        if (now >= _nextHeartbeat && host.Config is { } current)
        {
            _nextHeartbeat = now + TimeSpan.FromSeconds(current.HeartbeatSeconds);
            foreach (var site in host.Sites.Values.Where(s => s.Pipeline.Config is not null))
                await TryAsync("heartbeat", () => client.SendHeartbeatAsync(site.Pipeline.BuildHeartbeat(), ct));
        }

        await FlushEventsAsync(client, ct);

        if (Identity!.Certificate.NotAfter.ToUniversalTime() - now.UtcDateTime < RenewBefore && now >= _nextRenew)
        {
            _nextRenew = now + TimeSpan.FromHours(6);
            await TryAsync("certificate renewal", () => RenewAsync(client, ct));
        }
    }

    /// <summary>Posts pending events, each site's stamped with its site; they stay in their store until the API has them.</summary>
    public async Task FlushEventsAsync(ControlApiClient client, CancellationToken ct)
    {
        var stores = new List<(string? SiteId, LocalStore Store)> { (null, store) };
        stores.AddRange(host.Sites.Where(s => !ReferenceEquals(s.Value.Store, store)).Select(s => ((string?)s.Key, s.Value.Store)));
        // A collector that is also its only site's store (an agent at a site): its events belong to that site.
        var shared = host.Sites.FirstOrDefault(s => ReferenceEquals(s.Value.Store, store)).Key;
        if (shared is not null) stores[0] = (shared, store);
        foreach (var (siteId, source) in stores)
        {
            for (var round = 0; round < 10; round++)
            {
                var pending = source.PeekEvents(100);
                if (pending.Count == 0) break;
                var events = pending.Select(p => p.Event with { SiteId = p.Event.SiteId ?? siteId }).ToList();
                if (!await TryAsync("events", () => client.SendEventsAsync(events, ct))) return;
                source.RemoveEvents(pending.Select(p => p.Id));
            }
        }
    }

    private void HandleCredentials(SiteParts site, AgentConfig config, DateTimeOffset now)
    {
        foreach (var credential in config.Credentials)
        {
            using var key = Identity!.Certificate.GetRSAPrivateKey()!;
            try
            {
                site.Store.SetCredential(credential.Name, SealedSecret.Open(credential.Sealed, key, Identity.Certificate.Thumbprint));
                if (!site.Store.MarkHandled("credential", credential.Id, now)) continue;
                log.LogInformation("Stored credential {Name} for site {SiteId}", credential.Name, config.SiteId);
                site.Store.AddEvent(new AgentEvent
                {
                    EventId = Guid.NewGuid(), OccurredUtc = now, Kind = AgentEventKind.CredentialStored, Severity = EventSeverity.Info,
                    SiteId = config.SiteId, Message = $"Credential {credential.Name} stored.",
                    Details = new JsonObject { ["credential_id"] = credential.Id.ToString() },
                });
                // A source that failed for want of its password can try again straight away.
                foreach (var source in config.Sources.Where(s => s.Connection.CredentialName == credential.Name))
                    site.Pipeline.RetrySourceSoon(source.SourceId);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                if (!site.Store.MarkHandled("credential", credential.Id, now)) continue;
                log.LogError("Credential {Name} could not be opened: {Error}", credential.Name, ex.Message);
                site.Store.AddEvent(new AgentEvent
                {
                    EventId = Guid.NewGuid(), OccurredUtc = now, Kind = AgentEventKind.Error, Severity = EventSeverity.Error, SiteId = config.SiteId,
                    Message = $"Credential {credential.Name} could not be opened ({ex.Message}); enter it again in the console.",
                });
            }
        }
    }

    /// <summary>Each command runs at most once, even if its result is lost and it is delivered again.</summary>
    private async Task HandleCommandsAsync(ControlApiClient client, SiteParts site, AgentConfig config, CancellationToken ct)
    {
        foreach (var command in config.Commands)
        {
            if (!site.Store.MarkHandled("command", command.CommandId, time.GetUtcNow())) continue;
            switch (command.Type)
            {
                case CommandType.Restart:
                    Result(site.Store, config.SiteId, command, EventSeverity.Info, "Restarting.");
                    await FlushEventsAsync(client, ct);
                    log.LogWarning("Restart requested by {User}", command.IssuedBy);
                    requestRestart();
                    return;
                case CommandType.CollectLogs:
                    try
                    {
                        var location = await client.UploadLogsAsync(ZipLogs(), command.CommandId, ct);
                        Result(site.Store, config.SiteId, command, EventSeverity.Info, $"Logs uploaded: {location}");
                    }
                    catch (Exception ex) when (ex is HttpRequestException or ControlApiException or IOException)
                    {
                        Result(site.Store, config.SiteId, command, EventSeverity.Warning, $"Log upload failed: {ex.Message}");
                    }
                    break;
                default:
                    await site.Pipeline.ExecuteAsync(command);
                    break;
            }
        }
    }

    private void Result(LocalStore target, string siteId, AgentCommand command, EventSeverity severity, string message) => target.AddEvent(new AgentEvent
    {
        EventId = Guid.NewGuid(), OccurredUtc = time.GetUtcNow(), Kind = AgentEventKind.CommandResult, Severity = severity,
        SiteId = siteId, CommandId = command.CommandId, Message = message,
    });

    /// <summary>The newest log files that fit in 20 MB.</summary>
    internal byte[] ZipLogs()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            long total = 0;
            var files = Directory.Exists(logDirectory)
                ? new DirectoryInfo(logDirectory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc)
                : Enumerable.Empty<FileInfo>();
            foreach (var file in files)
            {
                if (total + file.Length > MaxLogBundle) break;
                total += file.Length;
                var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
                using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }
        return buffer.ToArray();
    }

    private async Task RenewAsync(ControlApiClient client, CancellationToken ct)
    {
        var identity = Identity!;
        var (key, csr) = AgentIdentity.NewRequest(identity.AgentId);
        using (key)
        {
            var response = await client.RenewAsync(csr, ct);
            Use(AgentIdentity.Save(store, key, response.CertificatePem, identity.ControlApi));
            log.LogInformation("Certificate renewed; valid until {NotAfter:u}", Identity!.Certificate.NotAfter.ToUniversalTime());
        }
    }

    private async Task<bool> TryAsync(string what, Func<Task> call)
    {
        try
        {
            await call();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or ControlApiException or TaskCanceledException)
        {
            log.LogWarning("Sending {What} to the control API failed: {Error}", what, ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _identity?.Dispose();
    }
}
