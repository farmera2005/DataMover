using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Services;

/// <summary>Turns the stored site configuration into what an agent pulls, and what the console's edits are validated as.</summary>
public sealed class ConfigBuilder(LakeflowDb db, SettingsStore settings, TimeProvider time)
{
    /// <summary>How long after a console command the agent keeps polling quickly.</summary>
    public static readonly TimeSpan FastPollFor = TimeSpan.FromMinutes(10);

    public static SourceConfig ToConfig(Source source, IEnumerable<CapturedTable> tables) => new()
    {
        SourceId = source.SourceId,
        SourceSystem = source.SourceSystem,
        Division = source.Division,
        Engine = source.Engine,
        Paused = source.Paused,
        MaxRowsPerSecond = source.MaxRowsPerSecond,
        Connection = new ConnectionSettings
        {
            Host = source.Host,
            Port = source.Port,
            Instance = source.Instance,
            Database = source.DatabaseName,
            Auth = source.Auth,
            UserName = source.UserName,
            CredentialName = source.CredentialName,
            ConnectTimeoutSeconds = source.ConnectTimeoutSeconds,
            CommandTimeoutSeconds = source.CommandTimeoutSeconds,
            TrustServerCertificate = source.TrustServerCertificate,
        },
        Tables = tables.OrderBy(t => t.TableName).Select(ToConfig).ToList(),
    };

    public static TableConfig ToConfig(CapturedTable table) => new()
    {
        Table = TableRef.Parse(table.TableName),
        Method = table.Method,
        KeyColumns = string.IsNullOrEmpty(table.KeyColumns) ? null : table.KeyColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        WatermarkColumn = table.WatermarkColumn,
        LookbackSeconds = table.LookbackSeconds,
        PollIntervalSeconds = table.PollIntervalSeconds,
        LagWarningMinutes = table.LagWarningMinutes,
        LagCriticalMinutes = table.LagCriticalMinutes,
        Paused = table.Paused,
    };

    /// <summary>
    /// The config as it would be with the given sources, checked with the same rules the agent applies, so the console
    /// learns about a bad edit when it saves it rather than when the agent refuses it.
    /// </summary>
    public static IReadOnlyList<string> Validate(Site site, IReadOnlyList<SourceConfig> sources) =>
        ConfigValidator.Validate(new AgentConfig
        {
            AgentId = "validation",
            SiteId = site.SiteId,
            ConfigVersion = Math.Max(1, site.ConfigVersion),
            SiteTimeZone = site.TimeZone,
            Buffer = new BufferSettings { CapBytes = site.BufferCapBytes },
            EventHubs = sources.Select(s => s.Division).Distinct().Select(d => new EventHubTarget
            {
                Division = d, FullyQualifiedNamespace = "validation", HubName = "validation", SasToken = "-", SasExpiresUtc = DateTimeOffset.MaxValue,
            }).ToList(),
            Sources = sources,
        });

    public async Task<IReadOnlyList<SourceConfig>> SourcesAsync(string siteId, CancellationToken ct)
    {
        var sources = await db.Sources.Where(s => s.SiteId == siteId).OrderBy(s => s.SourceId).ToListAsync(ct);
        var tables = await db.Tables.Where(t => t.SiteId == siteId).ToListAsync(ct);
        return sources.Select(s => ToConfig(s, tables.Where(t => t.SourceId == s.SourceId))).ToList();
    }

    /// <summary>The divisions whose Event Hub is set up (Configuration, then Destinations), with the Send key.</summary>
    public async Task<Dictionary<Division, (EventHubSetting Hub, byte[] Key)>> HubsAsync(CancellationToken ct)
    {
        var hubs = new Dictionary<Division, (EventHubSetting, byte[])>();
        foreach (var division in Enum.GetValues<Division>())
            if (await settings.GetWithSecretAsync<EventHubSetting>(SettingsStore.EventHub(division), ct) is ({ } hub, { } key))
                hubs[division] = (hub, key);
        return hubs;
    }

    /// <summary>
    /// A collector's config: for each site assigned to it, the settings, a SAS token per division hub (reissued once
    /// half its 24 hours have passed), commands not yet completed, and sealed credentials not yet acknowledged. A site
    /// whose division hub is not set up yet is left out (the collector waits; nothing is lost) and the fleet view says why.
    /// </summary>
    public async Task<CollectorConfig> ForCollectorAsync(Agent agent, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var sites = await db.Sites.Where(s => s.AgentId == agent.AgentId).OrderBy(s => s.SiteId).ToListAsync(ct);
        var siteIds = sites.Select(s => s.SiteId).ToList();
        var commands = await db.Commands.Where(c => c.AgentId == agent.AgentId && c.CompletedUtc == null && siteIds.Contains(c.SiteId))
            .OrderBy(c => c.IssuedUtc).ToListAsync(ct);
        foreach (var command in commands) command.DeliveredUtc ??= now;
        var credentials = await db.SealedCredentials.Where(c => c.AgentId == agent.AgentId).OrderBy(c => c.CreatedUtc).ToListAsync(ct);

        if (agent.SasExpiresUtc is null || agent.SasExpiresUtc - now < SasTokens.Lifetime / 2)
            agent.SasExpiresUtc = DateTimeOffset.FromUnixTimeSeconds((now + SasTokens.Lifetime).ToUnixTimeSeconds());
        var expires = agent.SasExpiresUtc.Value;
        var hubs = await HubsAsync(ct);
        var tokens = new Dictionary<Division, EventHubTarget>();
        EventHubTarget Hub(Division division)
        {
            if (tokens.TryGetValue(division, out var target)) return target;
            var (hub, key) = hubs[division];
            return tokens[division] = new EventHubTarget
            {
                Division = division,
                FullyQualifiedNamespace = hub.Namespace,
                HubName = hub.HubName,
                SasToken = SasTokens.Create(hub.Namespace, hub.HubName, hub.PolicyName, key, expires),
                SasExpiresUtc = expires,
            };
        }

        var configs = new List<AgentConfig>();
        foreach (var site in sites)
        {
            var sources = await SourcesAsync(site.SiteId, ct);
            if (sources.Any(s => !hubs.ContainsKey(s.Division))) continue;
            configs.Add(new AgentConfig
            {
                AgentId = agent.AgentId,
                SiteId = site.SiteId,
                ConfigVersion = site.ConfigVersion,
                DesiredAgentVersion = site.DesiredAgentVersion,
                MaintenanceWindow = site.MaintenanceStart is { } start && site.MaintenanceEnd is { } end ? new MaintenanceWindow(start, end) : null,
                SiteTimeZone = site.TimeZone,
                EventHubs = sources.Select(s => s.Division).Distinct().Order().Select(Hub).ToList(),
                Buffer = new BufferSettings { CapBytes = site.BufferCapBytes },
                Sources = sources,
                Commands = commands.Where(c => c.SiteId == site.SiteId).Select(c => new AgentCommand
                {
                    CommandId = c.CommandId, Type = c.Type, SourceId = c.SourceId, Table = c.TableName, IssuedUtc = c.IssuedUtc, IssuedBy = c.IssuedBy,
                }).ToList(),
                Credentials = credentials.Where(c => c.SiteId == site.SiteId)
                    .Select(c => new SealedCredential(c.Id, c.CredentialName, c.Sealed)).ToList(),
            });
        }
        await db.SaveChangesAsync(ct);

        return new CollectorConfig
        {
            AgentId = agent.AgentId,
            // Someone is waiting on a command (e.g. the setup wizard): poll every 10 s instead of 60 s for a while.
            ConfigPollSeconds = commands.Any(c => now - c.IssuedUtc < FastPollFor) ? 10 : 60,
            DesiredAgentVersion = sites.Select(s => s.DesiredAgentVersion).FirstOrDefault(v => v is not null),
            Sites = configs,
        };
    }
}
