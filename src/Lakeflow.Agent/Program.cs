using Lakeflow.Agent;
using Lakeflow.Agent.Capture;
using Lakeflow.Agent.Control;
using Lakeflow.Agent.Delivery;
using Lakeflow.Agent.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The Lakeflow agent runs on Windows (it protects its secrets with DPAPI).");
    return 1;
}

// Optional override for testing; the installer uses the default.
var dataDirectory = Environment.GetEnvironmentVariable("LAKEFLOW_AGENT_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lakeflow", "Agent");
Directory.CreateDirectory(dataDirectory);

AgentPipeline? pipeline = null;
Log.Logger = AgentLogging.Configure(new LoggerConfiguration(), Path.Combine(dataDirectory, "logs"), () => pipeline?.Config?.SiteId)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = "Lakeflow Agent");
    builder.Services.AddSerilog();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ISecretProtector, DpapiProtector>();
    builder.Services.AddSingleton(sp => LocalStore.Open(dataDirectory, sp.GetRequiredService<ISecretProtector>()));
    builder.Services.AddSingleton<Lakeflow.Contracts.ICredentialStore, StoreCredentials>();
    builder.Services.AddSingleton<IConnectorFactory, ConnectorFactory>();
    builder.Services.AddSingleton<IEventSender, EventHubsSender>();
    var version = typeof(AgentPipeline).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    builder.Services.AddSingleton(sp => pipeline = ActivatorUtilities.CreateInstance<AgentPipeline>(sp, version));
    builder.Services.AddSingleton(ControlApiClient.DefaultHandler);
    builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ControlLoop>(sp, Path.Combine(dataDirectory, "logs"),
        // Exit with an error so the Windows service recovery settings start the service again.
        (Action)(() => { Environment.ExitCode = 1; sp.GetRequiredService<IHostApplicationLifetime>().StopApplication(); })));
    builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<EnrollmentFile>(sp, dataDirectory, version));
    builder.Services.AddHostedService<AgentService>();
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent stopped");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
