using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent;

/// <summary>Runs capture and delivery on their own loops, so a slow source never delays sending and vice versa.</summary>
public sealed class AgentService(AgentPipeline pipeline, ILogger<AgentService> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await pipeline.LoadCachedConfigAsync())
            log.LogWarning("No config yet; waiting for enrollment and the first config from the control API");
        await Task.WhenAll(
            LoopAsync("capture", pipeline.RunCaptureOnceAsync, stoppingToken),
            LoopAsync("delivery", async ct => await pipeline.RunDeliveryOnceAsync(ct), stoppingToken));
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
