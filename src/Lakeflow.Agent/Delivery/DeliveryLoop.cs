using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent.Delivery;

/// <summary>
/// Drains the outbox oldest-first. Events leave the buffer only once acknowledged; on failure nothing more is sent
/// until a back-off (1 s doubling to 5 min) passes, so per-table order is kept and an outage costs few attempts.
/// </summary>
public sealed class DeliveryLoop(LocalStore store, IEventSender sender, TimeProvider time, ILogger<DeliveryLoop> log, Random? random = null)
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(5);
    private const long MaxBatchBytes = 900 * 1024;

    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset? LastSuccess { get; private set; }
    public string? LastError { get; private set; }
    public long Attempts { get; private set; }
    public bool Reachable => ConsecutiveFailures == 0;

    /// <summary>Sends one round of up to <paramref name="maxEvents"/> events. Returns how many were acknowledged.</summary>
    public async Task<int> SendOnceAsync(int maxEvents, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (now < NextAttempt) return 0;
        var pending = store.Peek(maxEvents, MaxBatchBytes * 4);
        if (pending.Count == 0) return 0;

        Attempts++;
        var sent = 0;
        try
        {
            // Group by hub and partition key, keeping each key's events in buffer order.
            foreach (var group in pending.GroupBy(e => (e.Hub, e.PartitionKey)))
            {
                var events = group.ToList();
                while (events.Count > 0)
                {
                    int count;
                    try
                    {
                        count = await sender.SendAsync(group.Key.Hub, group.Key.PartitionKey, events, ct);
                    }
                    catch (EventRejectedException rejected)
                    {
                        var bad = events.First(e => e.Id == rejected.EventId);
                        store.DeadLetter(bad, rejected.Message, now);
                        store.AddEvent(new AgentEvent
                        {
                            EventId = Guid.NewGuid(), OccurredUtc = now, Kind = AgentEventKind.Error, Severity = EventSeverity.Critical,
                            SourceId = bad.SourceId, Table = bad.Table,
                            Message = $"Change seq {bad.Seq} could not be sent and was set aside: {rejected.Message}",
                        });
                        log.LogError("Dead-lettered change {Seq}: {Reason}", bad.Seq, rejected.Message);
                        events.Remove(bad);
                        continue;
                    }
                    store.Acknowledge(events.Take(count).Select(e => e.Id).ToList());
                    sent += count;
                    events.RemoveRange(0, count);
                }
            }
            if (ConsecutiveFailures > 0) log.LogInformation("Event Hubs reachable again after {Failures} failed attempts", ConsecutiveFailures);
            ConsecutiveFailures = 0;
            LastError = null;
            LastSuccess = now;
            NextAttempt = DateTimeOffset.MinValue;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ConsecutiveFailures++;
            LastError = ex.Message;
            var delay = Backoff.Delay(ConsecutiveFailures, Initial, Max, random);
            NextAttempt = now + delay;
            log.LogWarning(ex, "Sending to Event Hubs failed ({Failures} in a row); retrying in {Delay}", ConsecutiveFailures, delay);
        }
        return sent;
    }
}
