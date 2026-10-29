using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Endpoints;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

// Same wire format as the agent: snake_case names and enum values.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IActor, HttpActor>();
// Azure SQL with the app's managed identity (Authentication=Active Directory Default): no password anywhere.
builder.Services.AddDbContext<LakeflowDb>(o => o.UseSqlServer(
    builder.Configuration.GetConnectionString("Lakeflow") ?? throw new InvalidOperationException("ConnectionStrings:Lakeflow is not set."),
    sql => sql.EnableRetryOnFailure()));

builder.Services.Configure<EventHubsOptions>(builder.Configuration.GetSection("EventHubs"));
var keyVault = builder.Configuration.GetSection("KeyVault").Get<KeyVaultOptions>() ?? new KeyVaultOptions();
if (!string.IsNullOrEmpty(keyVault.VaultUri)) builder.Services.AddSingleton<IKeyMaterial>(_ => new KeyVaultKeyMaterial(keyVault));
else if (builder.Environment.IsDevelopment()) builder.Services.AddSingleton<IKeyMaterial, DevelopmentKeyMaterial>();
else throw new InvalidOperationException("KeyVault:VaultUri must be set outside Development.");
builder.Services.AddSingleton<AgentCertificates>();
builder.Services.AddScoped<ConfigBuilder>();
var fabric = builder.Configuration.GetSection("Fabric").Get<FabricOptions>() ?? new FabricOptions();
if (string.IsNullOrEmpty(fabric.SqlEndpointConnectionString)) builder.Services.AddSingleton<IReconciliationSource, NoReconciliation>();
else builder.Services.AddSingleton<IReconciliationSource>(new SqlEndpointReconciliation(fabric));
builder.Services.AddHostedService<Retention>();

builder.Services.AddLakeflowAuth(builder.Configuration);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Enrollment is the only anonymous route: a handful of attempts per address per minute.
    o.AddPolicy("enroll", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimits:EnrollPerMinute", 10),
            Window = TimeSpan.FromMinutes(1),
        }));
});
builder.Services.AddProblemDetails();
// App Service terminates TLS and forwards the scheme; certificate authentication needs to see HTTPS.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LakeflowDb>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
// The web console: a static Blazor WebAssembly app, signed in with Entra ID in the browser. Its pages hold no data;
// everything comes from this API with the user's token.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/console"))
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; " +
            "connect-src 'self' https://login.microsoftonline.com; frame-src https://login.microsoftonline.com; " +
            "frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://login.microsoftonline.com";
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
app.UseCertificateForwarding();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
// The console's sign-in settings, from this API's configuration (nothing is built into the console).
app.MapGet("/console/appsettings.json", (IConfiguration config) => Results.Content(System.Text.Json.JsonSerializer.Serialize(new
{
    AzureAd = new
    {
        Authority = $"https://login.microsoftonline.com/{config["Entra:TenantId"]}",
        ClientId = config["Console:ClientId"],
        ValidateAuthority = true,
    },
    ApiScope = config["Console:ApiScope"],
}), "application/json")).AllowAnonymous();
app.MapFallbackToFile("console/{*path:nonfile}", "console/index.html").AllowAnonymous();
app.MapAgentEndpoints();
app.MapConsoleEndpoints(app.Configuration["PublicBaseUrl"] ?? "https://<control-api>");

app.Run();

public partial class Program;
