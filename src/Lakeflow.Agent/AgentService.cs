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
    private static readonly TimeSpan EnrollRetry = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextEnroll = DateTimeOffset.MinValue;
        if (!await enrollment.EnrollIfPendingAsync(control, stoppingToken) && !control.Enrolled)
            log.LogWarning("Not enrolled: waiting for an enrollment token (the console's install file, or the stack's built-in token)");
        if (!await host.LoadCachedAsync(stoppingToken))
            log.LogWarning("No config yet; waiting for the first config from the control API");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Not enrolled yet: keep looking for a token (the stack's control API writes the built-in collector's
                // shortly after it starts) and retry a failed attempt.
                if (!control.Enrolled && DateTimeOffset.UtcNow >= nextEnroll)
                {
                    nextEnroll = DateTimeOffset.UtcNow + EnrollRetry;
                    await enrollment.EnrollIfPendingAsync(control, stoppingToken);
                }
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
