using Lakeflow.ControlApi.Data;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Services;

/// <summary>Daily clean-up: audit kept 1 year, agent events 90 days, log bundles 30 days, unused enrollment tokens 30 days.</summary>
public sealed class Retention(IServiceScopeFactory scopes, TimeProvider time, ILogger<Retention> log) : BackgroundService
{
    public static readonly TimeSpan Audit = TimeSpan.FromDays(366);
    public static readonly TimeSpan Events = TimeSpan.FromDays(90);
    public static readonly TimeSpan Logs = TimeSpan.FromDays(30);

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LakeflowDb>();
        var now = time.GetUtcNow();
        var audit = await db.AuditEntries.Where(a => a.AtUtc < now - Audit).ExecuteDeleteAsync(ct);
        var events = await db.AgentEvents.Where(e => e.ReceivedUtc < now - Events).ExecuteDeleteAsync(ct);
        var logs = await db.LogBundles.Where(l => l.ReceivedUtc < now - Logs).ExecuteDeleteAsync(ct);
        var tokens = await db.EnrollmentTokens.Where(t => t.ExpiresUtc < now - Logs).ExecuteDeleteAsync(ct);
        log.LogInformation("Retention removed {Audit} audit entries, {Events} events, {Logs} log bundles, {Tokens} tokens", audit, events, logs, tokens);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { log.LogError(ex, "Retention clean-up failed"); }
            try { await Task.Delay(TimeSpan.FromDays(1), time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
