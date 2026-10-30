using System.Net.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Endpoints;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));

// Same wire format as the collector: snake_case names and enum values.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
});

// Nothing to configure: every value has a default that works in the Docker stack; environment variables
// (LAKEFLOW_*, mapped below) are optional overrides, used by tests.
builder.Configuration.AddEnvironmentVariables("LAKEFLOW_");
var server = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
var listen = builder.Configuration.GetSection("Listen").Get<ListenOptions>() ?? new ListenOptions();
builder.Services.AddSingleton(server);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpActor>();
builder.Services.AddScoped<IActor>(sp => sp.GetRequiredService<HttpActor>());
builder.Services.AddDbContext<LakeflowDb>(o => o.UseNpgsql(DatabaseConnectionString(builder.Configuration), npgsql => npgsql.EnableRetryOnFailure()));

var keyMaterial = new LocalKeyMaterial(server);
builder.Services.AddSingleton<IKeyMaterial>(keyMaterial);
builder.Services.AddSingleton<ISecretProtector>(sp => new SettingsProtector(sp.GetRequiredService<ServerOptions>()));
builder.Services.AddSingleton<ServerCertificates>();
builder.Services.AddSingleton<AgentCertificates>();
builder.Services.AddSingleton<SetupWindow>();
builder.Services.AddSingleton<LdapDirectory>();
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddScoped<Accounts>();
builder.Services.AddScoped<ConfigBuilder>();
builder.Services.AddScoped<FleetStatusService>();
builder.Services.AddScoped<AlertBook>();
builder.Services.AddScoped<Notifier>();
builder.Services.AddSingleton<AgentPackages>();
builder.Services.AddSingleton<NotificationState>();
builder.Services.AddSingleton<GraphMail>();
builder.Services.AddSingleton<TeamsWorkflow>();
builder.Services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<SettingsReconciliation>();
builder.Services.AddScoped<SnowflakeSettingsReader>();
builder.Services.AddScoped<SnowflakeReconciliation>();
builder.Services.AddScoped<IReconciliationSource, AllReconciliation>();
// Snowflake: statements can run for minutes (the SQL API waits up to 10).
builder.Services.AddHttpClient("snowflake", c => c.Timeout = TimeSpan.FromMinutes(11));
builder.Services.AddSingleton(sp => new Lakeflow.Snowflake.SnowflakeRunner(
    settings => settings.Hubs.Select(h => (Lakeflow.Snowflake.IHubReader)new Lakeflow.Snowflake.EventHubsReader(
        LakeNames.HubName(h.Division), h.ListenConnectionString, settings.ConsumerGroup)).ToArray(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("snowflake"), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<SnowflakeHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SnowflakeHost>());
builder.Services.AddHostedService<Retention>();
builder.Services.AddSingleton<AlertMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertMonitor>());
builder.Services.AddHostedService<BuiltInCollector>();

builder.Services.AddLakeflowAuth();
// Session cookies are signed with keys kept (encrypted to the CA's key) beside the master key, so a rebuilt container
// keeps everyone signed in.
builder.Services.AddDataProtection().SetApplicationName("Lakeflow")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(server.DataDirectory, "session-keys")))
    .ProtectKeysWithCertificate(keyMaterial.AgentCa)
    .UnprotectKeysWithAnyCertificate(keyMaterial.AgentCa);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // The anonymous routes: a handful of attempts per address per minute.
    o.AddPolicy("enroll", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimits:EnrollPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("sign-in", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimits:SignInPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddProblemDetails();

// Three ports: the console over HTTP and over HTTPS, and the collectors' port (HTTPS with client certificates,
// certificate from the Lakeflow CA). Tests run in-process without Kestrel, so none of this applies to them.
if (listen.Enabled)
{
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(listen.HttpPort);
        kestrel.ListenAnyIP(listen.HttpsPort, l => l.UseHttps(new TlsHandshakeCallbackOptions
        {
            OnConnection = context => ValueTask.FromResult(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = kestrel.ApplicationServices.GetRequiredService<ServerCertificates>().WebContext,
            }),
        }));
        kestrel.ListenAnyIP(listen.CollectorPort, l => l.UseHttps(new TlsHandshakeCallbackOptions
        {
            OnConnection = context => ValueTask.FromResult(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = kestrel.ApplicationServices.GetRequiredService<ServerCertificates>().CollectorContext,
                // Ask for a client certificate (enrollment comes without one); the certificate handler validates it.
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }),
        }));
    });
}

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LakeflowDb>().Database.MigrateAsync();
}
using (var scope = app.Services.CreateScope())
{
    await SettingsEndpoints.ReloadAsync(scope.ServiceProvider.GetRequiredService<SettingsStore>(),
        scope.ServiceProvider.GetRequiredService<ServerCertificates>(), CancellationToken.None);
}

app.UseSerilogRequestLogging();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/agents"))
    {
        // The console: scripts from this server only, no framing, no referrers.
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
            "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    await next();
});
// Library assets (_content/...) are served at the root; the console asks for them under its own base path.
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/console/_content", out var rest)) context.Request.Path = "/_content" + rest;
    return next();
});
app.UseBlazorFrameworkFiles("/console");
app.UseStaticFiles();
app.UseExceptionHandler();
app.UseRouting();
app.Use(async (context, next) =>
{
    // Collector routes answer only on the collectors' port, everything else only on the console ports.
    var port = context.Connection.LocalPort;
    if (listen.Enabled && port != 0 && context.Request.Path != "/health")
    {
        var collectorRoute = context.GetEndpoint()?.Metadata.GetMetadata<CollectorRoute>() is not null;
        if (collectorRoute != (port == listen.CollectorPort))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }
    // A signed-in browser's changes must come from the console itself (see AuthSetup.RequestHeader).
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
        && context.GetEndpoint()?.Metadata.GetMetadata<CollectorRoute>() is null
        && !context.Request.Headers.ContainsKey(AuthSetup.RequestHeader))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync($"Missing {AuthSetup.RequestHeader} header.");
        return;
    }
    await next();
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/", () => Results.Redirect("/console/")).AllowAnonymous().ExcludeFromDescription();
app.MapFallbackToFile("console/{*path:nonfile}", "console/index.html").AllowAnonymous();
app.MapAgentEndpoints();
app.MapAuthEndpoints();
app.MapSettingsEndpoints();
app.MapSnowflakeEndpoints();
app.MapConsoleEndpoints();
app.MapAlertEndpoints();
app.MapNotificationEndpoints();
app.MapUpdateEndpoints();

app.Run();

/// <summary>
/// The control-plane database. Default: the stack's PostgreSQL (<c>db</c>), with the password the init service
/// generated into the secrets volume. <c>ConnectionStrings:Lakeflow</c> overrides it (tests).
/// </summary>
static string DatabaseConnectionString(IConfiguration config)
{
    if (config.GetConnectionString("Lakeflow") is { Length: > 0 } explicitString) return explicitString;
    var passwordFile = config["Database:PasswordFile"] ?? "/run/lakeflow-secrets/db_password";
    return new NpgsqlConnectionStringBuilder
    {
        Host = config["Database:Host"] ?? "db",
        Database = config["Database:Name"] ?? "lakeflow",
        Username = config["Database:User"] ?? "lakeflow",
        Password = File.Exists(passwordFile) ? File.ReadAllText(passwordFile).Trim() : null,
    }.ConnectionString;
}

public partial class Program;

/// <summary>Ports inside the container (Docker publishes them; see compose.yaml).</summary>
public sealed class ListenOptions
{
    public bool Enabled { get; set; } = true;
    public int HttpPort { get; set; } = 8080;
    public int HttpsPort { get; set; } = 8443;
    public int CollectorPort { get; set; } = 8444;
}

/// <summary>Marks the routes collectors call, served only on the collectors' port.</summary>
public sealed class CollectorRoute;
