using System.Security.Cryptography;
using System.Text.Json;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Endpoints;
using Lakeflow.ControlApi.Security;

namespace Lakeflow.ControlApi.Services;

/// <summary>
/// Enrolls the Docker stack's own collector with nobody copying a token: while no built-in collector is enrolled,
/// keeps a one-time token, the collector port's address and the CA's SHA-256 in <c>enroll.json</c> on a volume only
/// the api and collector containers mount. The collector enrolls with it, and the file is removed.
/// </summary>
public sealed class BuiltInCollector(IServiceScopeFactory scopes, IKeyMaterial keys, IConfiguration config, TimeProvider time,
    ILogger<BuiltInCollector> log) : BackgroundService
{
    public const string Name = "Built-in collector";
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);

    private sealed record EnrollFile(string ControlApi, string Token, string CaSha256);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config["Bootstrap:File"] is not { Length: > 0 } file) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureAsync(file, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Could not prepare the built-in collector's enrollment: {Error}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task EnsureAsync(string file, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LakeflowDb>();
        if (db.Agents.Any(a => a.Name == Name && a.SiteId == null && a.RevokedUtc == null))
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        var now = time.GetUtcNow();
        if (File.Exists(file))
        {
            var existing = JsonSerializer.Deserialize<EnrollFile>(await File.ReadAllTextAsync(file, ct), Json);
            var hash = AgentEndpoints.HashToken(existing?.Token ?? "");
            if (db.EnrollmentTokens.Any(t => t.TokenHash == hash && t.UsedUtc == null && t.ExpiresUtc > now + TimeSpan.FromDays(1))) return;
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        db.EnrollmentTokens.Add(new EnrollmentToken
        {
            Id = Guid.NewGuid(), Name = Name, TokenHash = AgentEndpoints.HashToken(token), CreatedUtc = now, CreatedBy = "system",
            ExpiresUtc = now + TokenLifetime,
        });
        await db.SaveChangesAsync(ct);
        var controlApi = config["Bootstrap:ControlApi"] ?? "https://api:8444/";
        OwnerOnlyFile.Write(file, JsonSerializer.SerializeToUtf8Bytes(new EnrollFile(controlApi, token, LocalKeyMaterial.Fingerprint(keys.AgentCa)), Json));
        log.LogInformation("Built-in collector enrollment prepared");
    }
}
