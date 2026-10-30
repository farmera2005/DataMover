using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Endpoints;

/// <summary>
/// Configuration → Updates (Admins): upload signed agent MSIs, set the version collectors should run (fleet-wide; a
/// site's own target, on the site, wins), and see where each collector is.
/// </summary>
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("").RequireAuthorization(Policies.Admin);

        admin.MapGet("/settings/updates", DescribeAsync);

        admin.MapPut("/settings/updates", async Task<Results<Ok<UpdateSettingsDto>, ValidationProblem>> (UpdateSettingsRequest request,
            LakeflowDb db, SettingsStore settings, CancellationToken ct) =>
        {
            var target = string.IsNullOrWhiteSpace(request.TargetVersion) ? null : request.TargetVersion.Trim();
            if (target is not null && await db.AgentPackages.FindAsync([target], ct) is null)
                return AuthEndpoints.Invalid([$"Upload the {target} package first."]);
            await settings.SetAsync(SettingsStore.Updates, new UpdateSetting(target), null, ct);
            return TypedResults.Ok(await DescribeAsync(db, settings, ct));
        });

        // The MSI itself is the request body (application/octet-stream); the version and file name come in the query.
        admin.MapPost("/settings/updates/packages", async Task<Results<Ok<UpdateSettingsDto>, ValidationProblem>> (HttpContext http, string? version,
            string? fileName, LakeflowDb db, SettingsStore settings, AgentPackages packages, IActor actor, TimeProvider time, CancellationToken ct) =>
        {
            if (!AgentPackages.IsVersion(version))
                return AuthEndpoints.Invalid(["Give the package's version, like 1.4.0 (the MSI's product version, as CI names the file)."]);
            if (await db.AgentPackages.FindAsync([version!], ct) is not null)
                return AuthEndpoints.Invalid([$"Version {version} is already uploaded. Delete it first to replace it."]);
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = AgentPackages.MaxBytes + 1;
            var (stored, error) = await packages.SaveAsync(http.Request.Body, ct);
            if (stored is null) return AuthEndpoints.Invalid([error!]);
            var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? $"LakeflowAgent-{version}.msi" : fileName.Trim());
            db.AgentPackages.Add(new AgentPackage
            {
                Version = version!, FileName = name.Length <= 260 ? name : name[..260], Sha256 = stored.Sha256, SizeBytes = stored.SizeBytes,
                UploadedBy = actor.Name, UploadedUtc = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(await DescribeAsync(db, settings, ct));
        }).DisableAntiforgery();

        admin.MapDelete("/settings/updates/packages/{version}", async Task<Results<Ok<UpdateSettingsDto>, ValidationProblem, NotFound>> (string version,
            LakeflowDb db, SettingsStore settings, AgentPackages packages, CancellationToken ct) =>
        {
            if (await db.AgentPackages.FindAsync([version], ct) is not { } package) return TypedResults.NotFound();
            if ((await settings.GetAsync<UpdateSetting>(SettingsStore.Updates, ct))?.TargetVersion == version)
                return AuthEndpoints.Invalid([$"{version} is the fleet's target version; choose another first."]);
            if (await db.Sites.Where(s => s.DesiredAgentVersion == version).Select(s => s.Name).ToListAsync(ct) is { Count: > 0 } sites)
                return AuthEndpoints.Invalid([$"{version} is the target of {string.Join(", ", sites)}; change those sites first."]);
            db.AgentPackages.Remove(package);
            await db.SaveChangesAsync(ct);
            // Another version may share the file only if it is the same file; keep it then.
            if (!await db.AgentPackages.AnyAsync(p => p.Sha256 == package.Sha256, ct)) packages.Delete(package);
            return TypedResults.Ok(await DescribeAsync(db, settings, ct));
        });
    }

    private static async Task<UpdateSettingsDto> DescribeAsync(LakeflowDb db, SettingsStore settings, CancellationToken ct)
    {
        var fleet = (await settings.GetAsync<UpdateSetting>(SettingsStore.Updates, ct))?.TargetVersion;
        var packages = await db.AgentPackages.AsNoTracking().OrderByDescending(p => p.UploadedUtc).ToListAsync(ct);
        var sites = await db.Sites.AsNoTracking().Where(s => s.AgentId != null).ToListAsync(ct);
        var agents = await db.Agents.AsNoTracking().Where(a => a.RevokedUtc == null).OrderBy(a => a.Name).ToListAsync(ct);
        return new UpdateSettingsDto(fleet,
            packages.Select(p => new AgentPackageDto(p.Version, p.FileName, p.Sha256, p.SizeBytes, p.UploadedBy, p.UploadedUtc)).ToList(),
            agents.Select(a => new CollectorUpdateDto(a.AgentId, a.Name, a.SiteId, a.Platform, a.AgentVersion,
                sites.Where(s => s.AgentId == a.AgentId && s.DesiredAgentVersion != null).OrderBy(s => s.SiteId).Select(s => s.DesiredAgentVersion).FirstOrDefault() ?? fleet,
                a.UpdateJson is null ? null : EnvelopeJson.Deserialize<UpdateStatus>(a.UpdateJson), a.LastSeenUtc)).ToList());
    }
}
