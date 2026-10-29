using System.Text;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Lakeflow.Snowflake;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Lakeflow.ControlApi.Endpoints;

/// <summary>
/// Configuration → Snowflake (Admins): the account, the key pair (generated here; the private key is never returned),
/// each hub's Listen key for the forwarder, a connection test, and the forwarder's status.
/// </summary>
public static class SnowflakeEndpoints
{
    public static void MapSnowflakeEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/settings/snowflake").RequireAuthorization(Policies.Admin);

        admin.MapGet("", async (SettingsStore settings, SnowflakeSettingsReader reader, CancellationToken ct) => await DescribeAsync(settings, reader, ct));

        admin.MapPut("", async Task<Results<Ok<SnowflakeSettingsDto>, ValidationProblem>> (SnowflakeSettingsRequest request, SettingsStore settings,
            SnowflakeSettingsReader reader, SnowflakeHost host, CancellationToken ct) =>
        {
            var errors = new List<string>();
            var account = request.Account?.Trim() ?? "";
            if (account.Length == 0 || account.Contains("snowflakecomputing", StringComparison.OrdinalIgnoreCase) || account.Contains('/')
                || !account.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                errors.Add("Enter the account identifier as orgname-accountname (Snowsight: your name, then Account, then Copy account identifier, with the dot made a hyphen), not a URL.");
            foreach (var (label, value) in new[] { ("User", request.User), ("Role", request.Role), ("Warehouse", request.Warehouse), ("Database", request.Database) })
                if (!SnowflakeOptions.IsValidIdentifier(value)) errors.Add($"{label}: letters, digits and _ only.");
            if (request.SilverIntervalMinutes is < 1 or > 1440) errors.Add("Build silver every 1 to 1440 minutes.");
            var (_, pem) = await reader.GetAsync(ct);
            if (request.Enabled && pem is null) errors.Add("Generate the key pair (and run the setup script in Snowflake) before turning it on.");
            if (request.Enabled && !await AnyHubAsync(settings, ct)) errors.Add("Add at least one hub's snowflake-listen connection string before turning it on.");
            if (errors.Count > 0) return AuthEndpoints.Invalid(errors);
            await settings.SetAsync(SettingsStore.Snowflake, new SnowflakeSetting(request.Enabled, account, request.User.Trim(), request.Role.Trim(),
                request.Warehouse.Trim(), request.Database.Trim(), request.SilverIntervalMinutes), null, ct);
            host.Changed();
            return TypedResults.Ok(await DescribeAsync(settings, reader, ct));
        });

        // A new key pair. The setup script (or just its ALTER USER line) must then be run again in Snowflake.
        admin.MapPost("/key", async (SettingsStore settings, SnowflakeSettingsReader reader, SnowflakeHost host, CancellationToken ct) =>
        {
            var (current, _) = await reader.GetAsync(ct);
            await settings.SetAsync(SettingsStore.Snowflake, current ?? new SnowflakeSetting(false, "", "LAKEFLOW_FORWARDER", "LAKEFLOW_FORWARDER",
                "LAKEFLOW_WH", "LAKEFLOW", 5), Encoding.UTF8.GetBytes(KeyPair.GeneratePrivateKeyPem()), ct);
            host.Changed();
            return await DescribeAsync(settings, reader, ct);
        });

        admin.MapPut("/hubs/{division}", async Task<Results<Ok<SnowflakeSettingsDto>, ValidationProblem, NotFound>> (string division,
            EventHubSettingsRequest request, SettingsStore settings, SnowflakeSettingsReader reader, SnowflakeHost host, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Division>(division, ignoreCase: true, out var d)) return TypedResults.NotFound();
            if (EventHubConnectionString.TryParse(request.ConnectionString) is not { HubName: not null } parsed)
                return AuthEndpoints.Invalid(["Paste the connection string of the hub's snowflake-listen policy (it ends with EntityPath=<hub>)."]);
            if (await settings.GetAsync<EventHubSetting>(SettingsStore.EventHub(d), ct) is { } send
                && (!send.Namespace.Equals(parsed.Namespace, StringComparison.OrdinalIgnoreCase) || !send.HubName.Equals(parsed.HubName, StringComparison.OrdinalIgnoreCase)))
                return AuthEndpoints.Invalid([$"That is {parsed.Namespace}/{parsed.HubName}, but the {d} hub collectors send to is {send.Namespace}/{send.HubName}."]);
            await settings.SetAsync(SettingsStore.SnowflakeHub(d), new EventHubSetting(parsed.Namespace, parsed.HubName, parsed.PolicyName),
                Encoding.UTF8.GetBytes(parsed.Key), ct);
            host.Changed();
            return TypedResults.Ok(await DescribeAsync(settings, reader, ct));
        });
        admin.MapDelete("/hubs/{division}", async Task<Results<NoContent, NotFound>> (string division, SettingsStore settings, SnowflakeHost host, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Division>(division, ignoreCase: true, out var d)) return TypedResults.NotFound();
            await settings.DeleteAsync(SettingsStore.SnowflakeHub(d), ct);
            host.Changed();
            return TypedResults.NoContent();
        });

        // Signs in with the key pair and runs a query; says what to fix if it cannot.
        admin.MapPost("/test", async (SnowflakeSettingsReader reader, IHttpClientFactory http, TimeProvider time, CancellationToken ct) =>
        {
            var (setting, pem) = await reader.GetAsync(ct);
            if (setting is null || pem is null || string.IsNullOrWhiteSpace(setting.Account))
                return new SettingsTestResult(false, "Save the account and generate the key pair first.");
            using var key = new KeyPair(pem);
            var sql = new SqlApi(http.CreateClient("snowflake"), new SnowflakeAuth(SnowflakeSettingsReader.Options(setting), key, time), time);
            try
            {
                var row = (await sql.QueryAsync("SELECT CURRENT_USER(), CURRENT_ROLE(), CURRENT_WAREHOUSE(), CURRENT_DATABASE(), CURRENT_VERSION()", ct)).Rows[0];
                if (row[3] is null) return new SettingsTestResult(false, $"Signed in as {row[0]}, but the database {setting.Database} is not usable by role {row[1]}: run the setup script.");
                if (row[2] is null) return new SettingsTestResult(false, $"Signed in as {row[0]}, but the warehouse {setting.Warehouse} is not usable by role {row[1]}: run the setup script.");
                return new SettingsTestResult(true, $"Connected as {row[0]} (role {row[1]}, warehouse {row[2]}, database {row[3]}; Snowflake {row[4]}).");
            }
            catch (SnowflakeException ex)
            {
                return new SettingsTestResult(false, Explain(ex));
            }
            catch (HttpRequestException ex)
            {
                return new SettingsTestResult(false, $"Could not reach {SnowflakeSettingsReader.Options(setting).AccountUrl.Host} ({ex.Message}). Check the account identifier and that this server may connect out to *.snowflakecomputing.com on 443.");
            }
        });

        admin.MapGet("/status", (SnowflakeRunner runner) => new SnowflakeStatusDto(runner.Running, runner.SetupError,
            runner.Forwarding.Partitions.Select(p => new SnowflakePartitionDto(p.Division, p.PartitionId, p.Position, p.LastEnqueuedUtc, p.LastSentUtc,
                p.RowsSent, p.ConsecutiveFailures, p.LastError)).ToList(),
            runner.Silver.LastRunUtc, runner.Silver.LastSuccessUtc, runner.Silver.LastRows, runner.Silver.LastError, runner.Silver.NextRunUtc));
    }

    /// <summary>Snowflake's sign-in errors, in terms of what to do.</summary>
    public static string Explain(SnowflakeException ex) => ex.Status switch
    {
        401 or 403 when ex.Message.Contains("JWT", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase) =>
            $"Snowflake refused the key ({ex.Message}). Run the setup script's ALTER USER line with the current public key, and check the user name.",
        401 or 403 => $"Snowflake refused the sign-in ({ex.Message}). Check the user and role, and that the setup script was run.",
        404 => $"No such account ({ex.Message}). Check the account identifier.",
        _ => ex.Message,
    };

    private static async Task<bool> AnyHubAsync(SettingsStore settings, CancellationToken ct)
    {
        foreach (var division in Enum.GetValues<Division>())
            if (await settings.HasSecretAsync(SettingsStore.SnowflakeHub(division), ct)) return true;
        return false;
    }

    private static async Task<SnowflakeSettingsDto> DescribeAsync(SettingsStore settings, SnowflakeSettingsReader reader, CancellationToken ct)
    {
        var (s, pem) = await reader.GetAsync(ct);
        var dto = s is null ? new SnowflakeSettingsDto() : new SnowflakeSettingsDto
        {
            Enabled = s.Enabled, Account = s.Account, User = s.User, Role = s.Role, Warehouse = s.Warehouse, Database = s.Database,
            SilverIntervalMinutes = s.SilverIntervalMinutes,
        };
        if (pem is not null)
        {
            using var key = new KeyPair(pem);
            var options = new SnowflakeOptions { Account = dto.Account.Length > 0 ? dto.Account : "account", User = dto.User, Role = dto.Role, Warehouse = dto.Warehouse, Database = dto.Database };
            dto = dto with { PublicKey = key.PublicKey, PublicKeyFingerprint = key.Fingerprint, SetupScript = SetupScript.For(options, key.PublicKey) };
        }
        var hubs = new List<SnowflakeHubDto>();
        foreach (var division in Enum.GetValues<Division>())
        {
            var send = await settings.GetAsync<EventHubSetting>(SettingsStore.EventHub(division), ct);
            var listen = await settings.GetAsync<EventHubSetting>(SettingsStore.SnowflakeHub(division), ct);
            if (send is null && listen is null) continue;
            hubs.Add(new SnowflakeHubDto(division, send is not null, (listen ?? send)!.Namespace, (listen ?? send)!.HubName,
                await settings.HasSecretAsync(SettingsStore.SnowflakeHub(division), ct)));
        }
        return dto with { Hubs = hubs };
    }
}
