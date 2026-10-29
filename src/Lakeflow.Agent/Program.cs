using Lakeflow.Contracts;
using Lakeflow.Agent;
using Lakeflow.Agent.Capture;
using Lakeflow.Agent.Control;
using Lakeflow.Agent.Delivery;
using Lakeflow.Agent.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// The central collector runs in Docker on Linux; an agent installed at a site runs as a Windows service. Same program.
// Optional overrides (the defaults suit both): LAKEFLOW_AGENT_DATA, LAKEFLOW_KEY_FILE.
var dataDirectory = Environment.GetEnvironmentVariable("LAKEFLOW_AGENT_DATA")
    ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lakeflow", "Agent")
        : "/var/lib/lakeflow");
Directory.CreateDirectory(dataDirectory);

Log.Logger = AgentLogging.Configure(new LoggerConfiguration(), Path.Combine(dataDirectory, "logs"), () => null).CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    // The service name matches the installer (installer/Package.wxs); its virtual account is NT SERVICE\LakeflowAgent.
    builder.Services.AddWindowsService(options => options.ServiceName = "LakeflowAgent");
    builder.Services.AddSerilog();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ISecretProtector>(_ => OperatingSystem.IsWindows()
        ? new DpapiProtector()
        : new FileKeyProtector(Environment.GetEnvironmentVariable("LAKEFLOW_KEY_FILE") ?? "/var/lib/lakeflow-secrets/master.key"));
    var version = typeof(AgentPipeline).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    builder.Services.AddSingleton(sp =>
    {
        var protector = sp.GetRequiredService<ISecretProtector>();
        var loggers = sp.GetRequiredService<ILoggerFactory>();
        var time = sp.GetRequiredService<TimeProvider>();
        // Each site gets its own store (buffer, checkpoints, credentials) and its own Event Hubs connections.
        SiteParts OpenSite(string siteId)
        {
            var store = LocalStore.Open(Path.Combine(dataDirectory, "sites", siteId), protector);
            var pipeline = new AgentPipeline(store, new ConnectorFactory(new StoreCredentials(store), loggers), new EventHubsSender(),
                time, loggers, version);
            return new SiteParts(store, pipeline);
        }
        return new CollectorHost(LocalStore.Open(Path.Combine(dataDirectory, "collector"), protector), OpenSite,
            loggers.CreateLogger<CollectorHost>());
    });
    builder.Services.AddSingleton(ControlApiClient.DefaultHandler);
    builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ControlLoop>(sp, Path.Combine(dataDirectory, "logs"),
        // Exit with an error so the service manager (Docker restart policy, Windows service recovery) starts it again.
        (Action)(() => { Environment.ExitCode = 1; sp.GetRequiredService<IHostApplicationLifetime>().StopApplication(); })));
    builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<EnrollmentFile>(sp, dataDirectory, version));
    builder.Services.AddHostedService<AgentService>();
    await builder.Build().RunAsync();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Collector stopped");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
