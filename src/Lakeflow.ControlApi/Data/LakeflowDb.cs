using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Lakeflow.ControlApi.Data;

/// <summary>Who is acting: a console user or a collector (from its certificate).</summary>
public interface IActor
{
    string Name { get; }
}

/// <summary>
/// Control-plane database (PostgreSQL). Every change to an audited entity is written to <see cref="AuditEntry"/> in the
/// same transaction (who, what, before, after), with secret columns left out or shown as "(changed)"; every change to a site's config bumps
/// that site's <see cref="Site.ConfigVersion"/>.
/// </summary>
public sealed class LakeflowDb(DbContextOptions<LakeflowDb> options, IActor actor, TimeProvider time) : DbContext(options)
{
    private static readonly HashSet<string> NeverAudited = [nameof(EnrollmentToken.TokenHash), nameof(SealedCredentialRow.Sealed), nameof(Command.ResultDetails),
        nameof(Agent.LastSeenUtc), nameof(Agent.SasExpiresUtc), nameof(Agent.CertificatePem), nameof(User.SecurityStamp), nameof(User.LastSignInUtc),
        nameof(AppSetting.UpdatedUtc)];

    /// <summary>Recorded only as "(changed)" when they change.</summary>
    private static readonly HashSet<string> Secrets = [nameof(User.PasswordHash), nameof(AppSetting.Secret)];

    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<CapturedTable> Tables => Set<CapturedTable>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<SiteHeartbeat> SiteHeartbeats => Set<SiteHeartbeat>();
    public DbSet<SealedCredentialRow> SealedCredentials => Set<SealedCredentialRow>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<AgentEventRow> AgentEvents => Set<AgentEventRow>();
    public DbSet<LogBundle> LogBundles => Set<LogBundle>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AlertNote> AlertNotes => Set<AlertNote>();
    public DbSet<AlertDelivery> AlertDeliveries => Set<AlertDelivery>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Site>(e =>
        {
            e.HasKey(x => x.SiteId);
            e.Property(x => x.SiteId).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.DesiredAgentVersion).HasMaxLength(32);
            e.Property(x => x.AgentId).HasMaxLength(64);
            e.HasIndex(x => x.AgentId);
            e.Ignore(x => x.AuditName);
        });
        model.Entity<Source>(e =>
        {
            e.HasKey(x => new { x.SiteId, x.SourceId });
            e.Property(x => x.SiteId).HasMaxLength(64);
            e.Property(x => x.SourceId).HasMaxLength(64);
            e.Property(x => x.SourceSystem).HasMaxLength(64);
            e.Property(x => x.Division).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Engine).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Auth).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Host).HasMaxLength(260);
            e.Property(x => x.Instance).HasMaxLength(128);
            e.Property(x => x.DatabaseName).HasMaxLength(128);
            e.Property(x => x.UserName).HasMaxLength(128);
            e.Property(x => x.CredentialName).HasMaxLength(64);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.AuditName);
        });
        model.Entity<CapturedTable>(e =>
        {
            e.HasKey(x => new { x.SiteId, x.SourceId, x.TableName });
            e.Property(x => x.SiteId).HasMaxLength(64);
            e.Property(x => x.SourceId).HasMaxLength(64);
            e.Property(x => x.TableName).HasMaxLength(260);
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.KeyColumns).HasMaxLength(1000);
            e.Property(x => x.WatermarkColumn).HasMaxLength(128);
            e.HasOne<Source>().WithMany().HasForeignKey(x => new { x.SiteId, x.SourceId }).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.AuditName);
        });
        model.Entity<EnrollmentToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SiteId).HasMaxLength(64);
            e.Property(x => x.TokenHash).HasMaxLength(32);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.UsedByAgentId).HasMaxLength(64);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.AuditName);
        });
        model.Entity<Agent>(e =>
        {
            e.HasKey(x => x.AgentId);
            e.Property(x => x.AgentId).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.SiteId).HasMaxLength(64);
            e.Property(x => x.Hostname).HasMaxLength(256);
            e.Property(x => x.AgentVersion).HasMaxLength(32);
            e.Property(x => x.CertificateThumbprint).HasMaxLength(64);
            e.Property(x => x.PreviousThumbprint).HasMaxLength(64);
            e.Property(x => x.RevokedBy).HasMaxLength(256);
            e.HasIndex(x => x.CertificateThumbprint).IsUnique();
            e.HasIndex(x => x.SiteId);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.AuditName);
        });
        model.Entity<SiteHeartbeat>(e =>
        {
            e.HasKey(x => x.SiteId);
            e.Property(x => x.SiteId).HasMaxLength(64);
            e.Property(x => x.AgentId).HasMaxLength(64);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SealedCredentialRow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentId).HasMaxLength(64);
            e.Property(x => x.SiteId).HasMaxLength(64);
            e.Property(x => x.SourceId).HasMaxLength(64);
            e.Property(x => x.CredentialName).HasMaxLength(64);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.HasIndex(x => x.AgentId);
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.AuditName);
        });
        model.Entity<Command>(e =>
        {
            e.HasKey(x => x.CommandId);
            e.Property(x => x.AgentId).HasMaxLength(64);
            e.Property(x => x.SiteId).HasMaxLength(64);
            e.HasIndex(x => new { x.SiteId, x.IssuedUtc });
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.SourceId).HasMaxLength(64);
            e.Property(x => x.TableName).HasMaxLength(260);
            e.Property(x => x.IssuedBy).HasMaxLength(256);
            e.Property(x => x.Result).HasMaxLength(2000);
            e.HasIndex(x => new { x.AgentId, x.CompletedUtc });
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.AuditName);
        });
        model.Entity<AgentEventRow>(e =>
        {
            e.HasKey(x => x.EventId);
            e.Property(x => x.AgentId).HasMaxLength(64);
            e.Property(x => x.SiteId).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.SiteId, x.ReceivedUtc });
        });
        model.Entity<LogBundle>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentId).HasMaxLength(64);
        });
        model.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.HasIndex(x => x.UserName).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.AuthSource).HasMaxLength(16);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
            e.Ignore(x => x.AuditName);
        });
        model.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Ignore(x => x.AuditName);
        });
        model.Entity<Alert>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(600);
            e.Property(x => x.Rule).HasMaxLength(32);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.SiteId).HasMaxLength(64);
            e.Property(x => x.SourceId).HasMaxLength(64);
            e.Property(x => x.TableName).HasMaxLength(260);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.AcknowledgedBy).HasMaxLength(256);
            // At most one open alert per problem.
            e.HasIndex(x => x.Key).IsUnique().HasFilter("\"ResolvedUtc\" IS NULL");
            e.HasIndex(x => new { x.SiteId, x.OpenedUtc });
            e.HasIndex(x => x.ResolvedUtc);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Notes).WithOne().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AlertDelivery>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasMaxLength(16);
            e.Property(x => x.Event).HasMaxLength(16);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.AlertId, x.Channel });
            e.HasOne<Alert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AlertNote>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Author).HasMaxLength(256);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Text).HasMaxLength(2000);
            e.Ignore(x => x.AuditName);
        });
        model.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(16);
            e.Property(x => x.Entity).HasMaxLength(400);
            e.HasIndex(x => x.AtUtc);
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var touchedSites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            if (entry.Entity is not IAudited audited) continue;
            var before = entry.State == EntityState.Added ? null : Snapshot(entry, original: true);
            var after = entry.State == EntityState.Deleted ? null : Snapshot(entry, original: false);
            if (entry.State == EntityState.Modified && before == after) continue; // only unaudited columns changed
            AuditEntries.Add(new AuditEntry
            {
                AtUtc = now,
                Actor = actor.Name,
                Action = entry.State switch { EntityState.Added => "created", EntityState.Deleted => "deleted", _ => "updated" },
                Entity = audited.AuditName,
                Before = before,
                After = after,
            });
            // A new site starts at version 1; anything else that is part of a site's config moves its version on.
            if (audited is ISiteConfig siteConfig && !(entry.Entity is Site && entry.State != EntityState.Modified))
                touchedSites.Add(siteConfig.SiteId);
        }
        foreach (var siteId in touchedSites)
        {
            var site = Sites.Local.FirstOrDefault(s => s.SiteId == siteId) ?? await Sites.FindAsync([siteId], cancellationToken);
            if (site is not null && Entry(site).State != EntityState.Deleted) site.ConfigVersion++;
        }
        return await base.SaveChangesAsync(cancellationToken);
    }

    private static string Snapshot(EntityEntry entry, bool original)
    {
        var values = original ? entry.OriginalValues : entry.CurrentValues;
        var json = new JsonObject();
        foreach (var property in values.Properties)
        {
            if (NeverAudited.Contains(property.Name) || property.Name == nameof(Site.ConfigVersion)) continue;
            if (Secrets.Contains(property.Name))
            {
                var changed = entry.State == EntityState.Added ? values[property] is not null : entry.Property(property.Name).IsModified;
                if (!original && changed) json[property.Name] = "(changed)";
                continue;
            }
            json[property.Name] = Lakeflow.Contracts.ColumnValues.Encode(values[property] is Enum e ? e.ToString() : values[property]);
        }
        return json.ToJsonString();
    }
}
