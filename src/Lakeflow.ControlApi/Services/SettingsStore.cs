using System.Text.Json;
using Lakeflow.ControlApi.Data;
using Lakeflow.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Services;

/// <summary>
/// Settings entered in the console, kept in <see cref="AppSetting"/> rows: a readable JSON part and a secret part
/// encrypted with the master key. Changes are audited by <see cref="LakeflowDb"/> (secrets as "(changed)").
/// </summary>
public sealed class SettingsStore(LakeflowDb db, ISecretProtector protector, TimeProvider time)
{
    public const string Server = "server";
    public const string Fabric = "fabric";
    public const string Ldap = "ldap";
    public const string WebCertificate = "web_certificate";

    public const string Snowflake = "snowflake";

    public const string NotifyEmail = "notifications.email";
    public const string NotifyTeams = "notifications.teams";
    public const string NotifySchedule = "notifications.schedule";
    public const string Updates = "updates";

    public static string EventHub(Division division) => $"eventhubs.{LakeNames.HubName(division)}";

    /// <summary>The hub's Listen-only connection string for the Snowflake forwarder (the key is the secret).</summary>
    public static string SnowflakeHub(Division division) => $"snowflake.listen.{LakeNames.HubName(division)}";

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class =>
        await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct) is { Json: { } json } ? EnvelopeJson.Deserialize<T>(json) : null;

    /// <summary>The setting and its decrypted secret, if any.</summary>
    public async Task<(T? Value, byte[]? Secret)> GetWithSecretAsync<T>(string key, CancellationToken ct) where T : class
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) return (null, null);
        return (row.Json is null ? null : EnvelopeJson.Deserialize<T>(row.Json), row.Secret is null ? null : protector.Unprotect(row.Secret));
    }

    public async Task<bool> HasSecretAsync(string key, CancellationToken ct) =>
        await db.Settings.AnyAsync(s => s.Key == key && s.Secret != null, ct);

    /// <summary>Saves the readable part; replaces the secret when <paramref name="secret"/> is given, keeps it otherwise.</summary>
    public async Task SetAsync<T>(string key, T? value, byte[]? secret, CancellationToken ct) where T : class
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = key });
        row.Json = value is null ? null : JsonSerializer.Serialize(value, EnvelopeJson.Options);
        if (secret is not null) row.Secret = protector.Protect(secret);
        row.UpdatedUtc = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        if (await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct) is { } row)
        {
            db.Settings.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }
}

/// <summary>Stored form of a division's hub (the Send policy key is the secret).</summary>
public sealed record EventHubSetting(string Namespace, string HubName, string PolicyName);

/// <summary>Stored form of the Fabric SQL endpoint (the client secret is the secret).</summary>
public sealed record FabricSetting(string Server, string Database, string ClientId);

/// <summary>Stored form of <see cref="SettingsStore.Server"/>.</summary>
public sealed record ServerSetting(string Host);

/// <summary>Stored form of the Snowflake destination (the private key PEM is the secret).</summary>
public sealed record SnowflakeSetting(bool Enabled, string Account, string User, string Role, string Warehouse, string Database, int SilverIntervalMinutes);

/// <summary>Stored form of the Graph email channel (the client secret is the secret).</summary>
public sealed record EmailNotifySetting(bool Enabled, string TenantId, string ClientId, string Sender, IReadOnlyList<string> Recipients,
    AlertSeverity MinimumSeverity);

/// <summary>Stored form of the Teams channel (the workflow URL is the secret).</summary>
public sealed record TeamsNotifySetting(bool Enabled, string? WebhookHost, AlertSeverity MinimumSeverity);

/// <summary>Stored form of <see cref="SettingsStore.Updates"/>: the version every collector should run, unless its site says otherwise.</summary>
public sealed record UpdateSetting(string? TargetVersion);
