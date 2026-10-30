using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Lakeflow.Agent.Update;

/// <summary>
/// The Lakeflow Agent Updater service (LocalSystem; installer/Package.wxs): the only part that may install software. It
/// takes the agent's request from the agent's data folder, copies the package into its own folder (writable only by
/// SYSTEM and Administrators), and installs it only if its SHA-256 matches the request, Windows trusts its signature,
/// and it is signed by the same publisher as this program. Nothing in the request is used as a path.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdaterService(string agentData, string folder, ILogger<UpdaterService> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Runs as the LakeflowAgentUpdater service; logs to its own folder.</summary>
    public static async Task<int> RunAsync(string agentData)
    {
        var folder = UpdatePlatform.DefaultUpdaterFolder;
        Directory.CreateDirectory(folder);
        Log.Logger = AgentLogging.Configure(new LoggerConfiguration(), Path.Combine(folder, "logs"), () => null).CreateLogger();
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWindowsService(options => options.ServiceName = "LakeflowAgentUpdater");
            builder.Services.AddSerilog();
            builder.Services.AddHostedService(sp => new UpdaterService(agentData, folder, sp.GetRequiredService<ILogger<UpdaterService>>()));
            await builder.Build().RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Updater stopped");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(folder);
        FinishInterruptedInstall();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { log.LogError(ex, "Updater round failed"); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var requestPath = Path.Combine(agentData, "updates", UpdateFiles.Request);
        if (UpdateFiles.Read<UpdateFiles.InstallRequest>(requestPath) is not { } request)
        {
            if (File.Exists(requestPath)) File.Delete(requestPath);
            return;
        }
        File.Delete(requestPath);
        if (!UpdateFiles.VersionPattern().IsMatch(request.Version) || !UpdateFiles.Sha256Pattern().IsMatch(request.Sha256))
        {
            log.LogWarning("Ignored a malformed update request");
            return;
        }
        var copy = Path.Combine(folder, UpdateFiles.MsiName(request.Version));
        await using (var source = new FileStream(Path.Combine(agentData, "updates", UpdateFiles.MsiName(request.Version)), FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var target = File.Create(copy))
            await source.CopyToAsync(target, ct);

        string? refused = null;
        var own = Environment.ProcessPath is { } exe ? Authenticode.Publisher(exe) : null;
        if (await UpdateFiles.Sha256Async(copy, ct) != request.Sha256) refused = "the package does not match its SHA-256";
        else if (Authenticode.Verify(copy) is var trust and not 0) refused = $"Windows does not trust its signature (0x{trust:X8})";
        else if (own is null) refused = "this updater is not signed, so it installs nothing";
        else if (Authenticode.Publisher(copy) is var signer && signer != own) refused = $"it is signed by {signer}, not {own}";
        if (refused is not null)
        {
            log.LogWarning("Refused update {Version}: {Reason}", request.Version, refused);
            UpdateFiles.Write(Path.Combine(folder, UpdateFiles.Result), new UpdateFiles.InstallResult(request.Version, request.Sha256, -1, $"Refused: {refused}.", DateTimeOffset.UtcNow));
            File.Delete(copy);
            return;
        }

        var logFile = Path.Combine(folder, $"install-{request.Version}.log");
        UpdateFiles.Write(Path.Combine(folder, UpdateFiles.Running), request);
        log.LogInformation("Installing update {Version}", request.Version);
        // The upgrade stops this service too; msiexec carries on, and the next start reads its log (FinishInterruptedInstall).
        using var msiexec = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            ArgumentList = { "/i", copy, "/qn", "/norestart", "/l*v", logFile },
            UseShellExecute = false,
        })!;
        await msiexec.WaitForExitAsync(ct);
        Finish(request, msiexec.ExitCode, logFile);
    }

    /// <summary>After a restart: if an install was running, record how it ended from msiexec's log.</summary>
    private void FinishInterruptedInstall()
    {
        if (UpdateFiles.Read<UpdateFiles.InstallRequest>(Path.Combine(folder, UpdateFiles.Running)) is not { } request) return;
        var logFile = Path.Combine(folder, $"install-{request.Version}.log");
        var code = UpdateFiles.ExitCodeFromLog(logFile) ?? -1;
        Finish(request, code, logFile);
    }

    private void Finish(UpdateFiles.InstallRequest request, int exitCode, string logFile)
    {
        var detail = exitCode switch
        {
            0 => "Installed.",
            3010 => "Installed; Windows wants a restart to finish.",
            1603 => $"msiexec failed (1603); see {logFile}.",
            1618 => "Another installation was in progress (1618).",
            -1 => $"The installation did not finish; see {logFile}.",
            _ => $"msiexec exit code {exitCode}; see {logFile}.",
        };
        UpdateFiles.Write(Path.Combine(folder, UpdateFiles.Result), new UpdateFiles.InstallResult(request.Version, request.Sha256, exitCode, detail, DateTimeOffset.UtcNow));
        File.Delete(Path.Combine(folder, UpdateFiles.Running));
        var copy = Path.Combine(folder, UpdateFiles.MsiName(request.Version));
        if (File.Exists(copy)) File.Delete(copy);
    }
}
