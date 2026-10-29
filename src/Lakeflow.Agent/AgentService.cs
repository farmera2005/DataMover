using Lakeflow.Agent.Control;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent;

/// <summary>
/// Starts the collector: enrolls if a token is waiting, starts every site it served last time (each runs its own
/// capture and delivery loops), then keeps the control-plane exchange going.
/// </summary>
public sealed class AgentService(CollectorHost host, ControlLoop control, EnrollmentFile enrollment, ILogger<AgentService> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await enrollment.EnrollIfPendingAsync(control, stoppingToken);
        if (!control.Enrolled)
            log.LogWarning("Not enrolled: set LAKEFLOW_CONTROL_API and LAKEFLOW_ENROLLMENT_TOKEN (or install with the console's command)");
        if (!await host.LoadCachedAsync(stoppingToken))
            log.LogWarning("No config yet; waiting for the first config from the control API");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await control.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unexpected error in the control loop");
            }
            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        await host.DisposeAsync();
    }
}
