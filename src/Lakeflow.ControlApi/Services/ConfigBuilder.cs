using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lakeflow.ControlApi.Services;

/// <summary>Turns the stored site configuration into what an agent pulls, and what the console's edits are validated as.</summary>
public sealed class ConfigBuilder(LakeflowDb db, IKeyMaterial keys, IOptions<EventHubsOptions> hubs, TimeProvider time)
{
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

    /// <summary>
    /// The agent's config: settings, a SAS token per division hub (reissued once half its 24 hours have passed),
    /// commands not yet completed, and sealed credentials not yet acknowledged.
    /// </summary>
    public async Task<AgentConfig> ForAgentAsync(Agent agent, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var site = await db.Sites.SingleAsync(s => s.SiteId == agent.SiteId, ct);
        var sources = await SourcesAsync(site.SiteId, ct);

        if (agent.SasExpiresUtc is null || agent.SasExpiresUtc - now < SasTokens.Lifetime / 2)
            agent.SasExpiresUtc = DateTimeOffset.FromUnixTimeSeconds((now + SasTokens.Lifetime).ToUnixTimeSeconds());
        var expires = agent.SasExpiresUtc.Value;
        var options = hubs.Value;
        var targets = sources.Select(s => s.Division).Distinct().Order().Select(division =>
        {
            var (policy, key) = keys.HubSendKey(division);
            var hub = options.HubName(division);
            return new EventHubTarget
            {
                Division = division,
                FullyQualifiedNamespace = options.Namespace,
                HubName = hub,
                SasToken = SasTokens.Create(options.Namespace, hub, policy, key, expires),
                SasExpiresUtc = expires,
            };
        }).ToList();

        var commands = await db.Commands.Where(c => c.AgentId == agent.AgentId && c.CompletedUtc == null).OrderBy(c => c.IssuedUtc).ToListAsync(ct);
        foreach (var command in commands) command.DeliveredUtc ??= now;
        var credentials = await db.SealedCredentials.Where(c => c.AgentId == agent.AgentId).OrderBy(c => c.CreatedUtc).ToListAsync(ct);
        await db.SaveChangesAsync(ct);

        return new AgentConfig
        {
            AgentId = agent.AgentId,
            SiteId = site.SiteId,
            ConfigVersion = site.ConfigVersion,
            DesiredAgentVersion = site.DesiredAgentVersion,
            MaintenanceWindow = site.MaintenanceStart is { } start && site.MaintenanceEnd is { } end ? new MaintenanceWindow(start, end) : null,
            SiteTimeZone = site.TimeZone,
            EventHubs = targets,
            Buffer = new BufferSettings { CapBytes = site.BufferCapBytes },
            Sources = sources,
            Commands = commands.Select(c => new AgentCommand
            {
                CommandId = c.CommandId, Type = c.Type, SourceId = c.SourceId, Table = c.TableName, IssuedUtc = c.IssuedUtc, IssuedBy = c.IssuedBy,
            }).ToList(),
            Credentials = credentials.Select(c => new SealedCredential(c.Id, c.CredentialName, c.Sealed)).ToList(),
        };
    }
}
