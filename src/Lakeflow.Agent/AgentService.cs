using Lakeflow.Agent.Control;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent;

/// <summary>
/// Runs capture, delivery and the control-plane exchange on their own loops, so a slow source never delays sending,
/// and an unreachable control API never stops either.
/// </summary>
public sealed class AgentService(AgentPipeline pipeline, ControlLoop control, EnrollmentFile enrollment, ILogger<AgentService> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await enrollment.EnrollIfPendingAsync(control, stoppingToken);
        if (!await pipeline.LoadCachedConfigAsync())
            log.LogWarning("No config yet; waiting for the first config from the control API");
        await Task.WhenAll(
            LoopAsync("capture", pipeline.RunCaptureOnceAsync, stoppingToken),
            LoopAsync("delivery", async ct => await pipeline.RunDeliveryOnceAsync(ct), stoppingToken),
            LoopAsync("control", control.RunOnceAsync, stoppingToken));
    }

    private async Task LoopAsync(string name, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await step(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Steps handle their own failures; reaching here is a bug, so log it and keep the service up.
                log.LogError(ex, "Unexpected error in the {Loop} loop", name);
            }
            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
