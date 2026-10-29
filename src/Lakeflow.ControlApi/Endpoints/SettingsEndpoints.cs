using System.Text;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.SqlClient;

namespace Lakeflow.ControlApi.Endpoints;

/// <summary>
/// Configuration entered in the browser (Admins): Event Hubs, Fabric, directory sign-in and the server certificate.
/// Secrets are write-only: stored encrypted, never returned, audited as "(changed)".
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/settings").RequireAuthorization(Policies.Admin);

        admin.MapGet("/event-hubs", async (SettingsStore settings, CancellationToken ct) =>
        {
            var hubs = new List<EventHubSettingsDto>();
            foreach (var division in Enum.GetValues<Division>())
                if (await settings.GetAsync<EventHubSetting>(SettingsStore.EventHub(division), ct) is { } hub)
                    hubs.Add(new EventHubSettingsDto(division, hub.Namespace, hub.HubName, hub.PolicyName,
                        await settings.HasSecretAsync(SettingsStore.EventHub(division), ct)));
            return hubs;
        });
        admin.MapPut("/event-hubs/{division}", async Task<Results<Ok<EventHubSettingsDto>, ValidationProblem, NotFound>> (
            string division, EventHubSettingsRequest request, SettingsStore settings, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Division>(division, ignoreCase: true, out var d)) return TypedResults.NotFound();
            if (EventHubConnectionString.TryParse(request.ConnectionString) is not { } parsed)
                return AuthEndpoints.Invalid(["That is not an Event Hubs connection string. Copy \"Connection string–primary key\" from the hub's Shared access policies."]);
            if (parsed.HubName is null)
                return AuthEndpoints.Invalid(["Use a policy on the hub itself (its connection string ends with EntityPath=<hub>), not on the namespace, and give it only Send."]);
            var hub = new EventHubSetting(parsed.Namespace, parsed.HubName, parsed.PolicyName);
            await settings.SetAsync(SettingsStore.EventHub(d), hub, Encoding.UTF8.GetBytes(parsed.Key), ct);
            return TypedResults.Ok(new EventHubSettingsDto(d, hub.Namespace, hub.HubName, hub.PolicyName, true));
        });
        admin.MapDelete("/event-hubs/{division}", async Task<Results<NoContent, NotFound>> (string division, SettingsStore settings, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Division>(division, ignoreCase: true, out var d)) return TypedResults.NotFound();
            await settings.DeleteAsync(SettingsStore.EventHub(d), ct);
            return TypedResults.NoContent();
        });

        admin.MapGet("/fabric", async Task<Results<Ok<FabricSettingsDto>, NoContent>> (SettingsStore settings, CancellationToken ct) =>
            await settings.GetAsync<FabricSetting>(SettingsStore.Fabric, ct) is { } f
                ? TypedResults.Ok(new FabricSettingsDto(f.Server, f.Database, f.ClientId, await settings.HasSecretAsync(SettingsStore.Fabric, ct)))
                : TypedResults.NoContent());
        admin.MapPut("/fabric", async Task<Results<Ok<FabricSettingsDto>, ValidationProblem>> (FabricSettingsRequest request, SettingsStore settings, CancellationToken ct) =>
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(request.Server)) errors.Add("Enter the SQL analytics endpoint (…datawarehouse.fabric.microsoft.com).");
            if (string.IsNullOrWhiteSpace(request.Database)) errors.Add("Enter the silver lakehouse's name.");
            if (!Guid.TryParse(request.ClientId, out _)) errors.Add("The client id is the service principal's application (client) id, a GUID.");
            var hasSecret = await settings.HasSecretAsync(SettingsStore.Fabric, ct);
            if (string.IsNullOrEmpty(request.ClientSecret) && !hasSecret) errors.Add("Enter the client secret.");
            if (errors.Count > 0) return AuthEndpoints.Invalid(errors);
            var fabric = new FabricSetting(request.Server.Trim(), request.Database.Trim(), request.ClientId.Trim());
            await settings.SetAsync(SettingsStore.Fabric, fabric, string.IsNullOrEmpty(request.ClientSecret) ? null : Encoding.UTF8.GetBytes(request.ClientSecret), ct);
            return TypedResults.Ok(new FabricSettingsDto(fabric.Server, fabric.Database, fabric.ClientId, true));
        });
        admin.MapDelete("/fabric", async (SettingsStore settings, CancellationToken ct) =>
        {
            await settings.DeleteAsync(SettingsStore.Fabric, ct);
            return TypedResults.NoContent();
        });
        admin.MapPost("/fabric/test", async (SettingsStore settings, CancellationToken ct) =>
        {
            if (await settings.GetAsync<FabricSetting>(SettingsStore.Fabric, ct) is null)
                return new SettingsTestResult(false, "Save the Fabric settings first.");
            try
            {
                var results = await new SettingsReconciliation(settings).LatestAsync(null, ct);
                return new SettingsTestResult(true, results.Count == 0
                    ? "Connected. No reconciliation results yet: the notebook writes them after the collectors' nightly check (02:30 site time)."
                    : $"Connected; {results.Count} reconciliation results from the last 3 days.");
            }
            catch (SqlException ex)
            {
                return new SettingsTestResult(false, ex.Message);
            }
        });

        admin.MapGet("/ldap", async (SettingsStore settings, CancellationToken ct) =>
            (await settings.GetAsync<LdapSettingsDto>(SettingsStore.Ldap, ct) ?? new LdapSettingsDto())
                with { HasBindPassword = await settings.HasSecretAsync(SettingsStore.Ldap, ct) });
        admin.MapPut("/ldap", async Task<Results<Ok<LdapSettingsDto>, ValidationProblem>> (LdapSettingsRequest request, SettingsStore settings, CancellationToken ct) =>
        {
            var hasPassword = await settings.HasSecretAsync(SettingsStore.Ldap, ct);
            var errors = Validate(request.Settings, !string.IsNullOrEmpty(request.BindPassword) || hasPassword);
            if (errors.Count > 0) return AuthEndpoints.Invalid(errors);
            var stored = request.Settings with { HasBindPassword = false };
            await settings.SetAsync(SettingsStore.Ldap, stored, string.IsNullOrEmpty(request.BindPassword) ? null : Encoding.UTF8.GetBytes(request.BindPassword), ct);
            return TypedResults.Ok(stored with { HasBindPassword = true });
        });
        admin.MapPost("/ldap/test", async Task<Results<Ok<LdapTestResult>, ValidationProblem>> (LdapTestRequest request, SettingsStore settings,
            LdapDirectory directory, CancellationToken ct) =>
        {
            var (_, stored) = await settings.GetWithSecretAsync<LdapSettingsDto>(SettingsStore.Ldap, ct);
            var password = !string.IsNullOrEmpty(request.BindPassword) ? request.BindPassword : stored is null ? null : Encoding.UTF8.GetString(stored);
            var errors = Validate(request.Settings with { Enabled = true }, password is not null);
            if (errors.Count > 0) return AuthEndpoints.Invalid(errors);
            var result = await directory.AuthenticateAsync(request.Settings, password!, request.UserName ?? "", request.Password ?? "", ct);
            return TypedResults.Ok(new LdapTestResult(result.Succeeded, result.Message, result.Role, result.Groups));
        });

        admin.MapGet("/server", async (SettingsStore settings, ServerCertificates certificates, IKeyMaterial keys, ServerOptions options, CancellationToken ct) =>
            Describe(await settings.GetAsync<ServerSetting>(SettingsStore.Server, ct), certificates, keys, options));
        admin.MapPut("/server", async Task<Results<Ok<ServerSettingsDto>, ValidationProblem>> (ServerSettingsRequest request, SettingsStore settings,
            ServerCertificates certificates, IKeyMaterial keys, ServerOptions options, CancellationToken ct) =>
        {
            var host = request.Host?.Trim() ?? "";
            if (host.Length is 0 or > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                return AuthEndpoints.Invalid(["Enter the server's name or IP address as users and collectors reach it, e.g. lakeflow.corp.local."]);
            await settings.SetAsync(SettingsStore.Server, new ServerSetting(host), null, ct);
            await ReloadAsync(settings, certificates, ct);
            return TypedResults.Ok(Describe(new ServerSetting(host), certificates, keys, options));
        });
        admin.MapPost("/server/certificate", async Task<Results<Ok<ServerSettingsDto>, ValidationProblem>> (CertificateUploadRequest request,
            SettingsStore settings, ServerCertificates certificates, IKeyMaterial keys, ServerOptions options, CancellationToken ct) =>
        {
            byte[] pfx;
            try
            {
                pfx = Convert.FromBase64String(request.PfxBase64 ?? "");
            }
            catch (FormatException)
            {
                return AuthEndpoints.Invalid(["The certificate file could not be read."]);
            }
            if (!certificates.TryOpenPfx(pfx, request.Password, out var certificate, out var error)) return AuthEndpoints.Invalid([error!]);
            // The PFX is kept re-exported without a password, encrypted with the master key like every other secret.
            var all = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
            all.Import(pfx, request.Password, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
            var stored = all.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12)!;
            await settings.SetAsync(SettingsStore.WebCertificate, ServerCertificates.Describe(certificate!, true), stored, ct);
            await ReloadAsync(settings, certificates, ct);
            return TypedResults.Ok(Describe(await settings.GetAsync<ServerSetting>(SettingsStore.Server, ct), certificates, keys, options));
        });
        admin.MapDelete("/server/certificate", async (SettingsStore settings, ServerCertificates certificates, CancellationToken ct) =>
        {
            await settings.DeleteAsync(SettingsStore.WebCertificate, ct);
            await ReloadAsync(settings, certificates, ct);
            return TypedResults.NoContent();
        });
        // The Lakeflow CA certificate (public): trust it on PCs (e.g. by group policy) and the browser warning goes away.
        app.MapGet("/settings/server/ca.crt", (IKeyMaterial keys) =>
            TypedResults.File(Encoding.ASCII.GetBytes(keys.AgentCa.ExportCertificatePem()), "application/x-pem-file", "lakeflow-ca.crt"))
            .RequireAuthorization(Policies.Viewer);
    }

    public static async Task ReloadAsync(SettingsStore settings, ServerCertificates certificates, CancellationToken ct)
    {
        var server = await settings.GetAsync<ServerSetting>(SettingsStore.Server, ct);
        var (_, pfx) = await settings.GetWithSecretAsync<CertificateInfo>(SettingsStore.WebCertificate, ct);
        certificates.Load(server?.Host, pfx);
    }

    private static ServerSettingsDto Describe(ServerSetting? server, ServerCertificates certificates, IKeyMaterial keys, ServerOptions options)
    {
        var host = server?.Host ?? "localhost";
        return new ServerSettingsDto(host, $"https://{host}:{options.PublishedHttpsPort}/", $"https://{host}:{options.PublishedCollectorPort}/",
            ServerCertificates.Describe(certificates.Web, certificates.WebUploaded), ServerCertificates.Describe(certificates.Collector, false),
            LocalKeyMaterial.Fingerprint(keys.AgentCa));
    }

    private static List<string> Validate(LdapSettingsDto s, bool hasPassword)
    {
        var errors = new List<string>();
        if (!s.Enabled) return errors;
        if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var url) || url.Scheme is not ("ldap" or "ldaps"))
            errors.Add("The URL looks like ldaps://dc1.corp.local:636.");
        else if (url.Scheme == "ldap" && !s.StartTls) errors.Add("With ldap:// turn on StartTLS; passwords are never sent unencrypted.");
        if (string.IsNullOrWhiteSpace(s.BindDn)) errors.Add("Enter the service account's DN.");
        if (!hasPassword) errors.Add("Enter the service account's password (an empty password would be an anonymous bind).");
        if (string.IsNullOrWhiteSpace(s.UserBaseDn)) errors.Add("Enter where users are searched (base DN).");
        if (string.IsNullOrWhiteSpace(s.UserFilter) || !s.UserFilter.Contains("{0}")) errors.Add("The user filter must contain {0} for the user name.");
        if (string.IsNullOrWhiteSpace(s.AdminGroupDn) && string.IsNullOrWhiteSpace(s.OperatorGroupDn) && string.IsNullOrWhiteSpace(s.ViewerGroupDn))
            errors.Add("Name at least one group (Admin, Operator or Viewer).");
        if (!string.IsNullOrWhiteSpace(s.CaCertificatePem))
        {
            try
            {
                using var _ = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(s.CaCertificatePem);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                errors.Add("The CA certificate must be PEM (-----BEGIN CERTIFICATE-----).");
            }
        }
        return errors;
    }
}
