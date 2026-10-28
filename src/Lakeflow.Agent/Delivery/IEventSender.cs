using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;

namespace Lakeflow.Agent.Delivery;

/// <summary>Sends buffered envelopes; returns once the service has acknowledged them.</summary>
public interface IEventSender : IAsyncDisposable
{
    /// <summary>Points the sender at the division hubs and their SAS tokens (rotated every 24 hours).</summary>
    void UpdateTargets(IReadOnlyList<EventHubTarget> targets);

    /// <summary>
    /// Sends a prefix of <paramref name="events"/> (all for one hub and partition key) as one batch.
    /// Returns how many were sent and acknowledged; throws when none could be.
    /// </summary>
    Task<int> SendAsync(string hub, string partitionKey, IReadOnlyList<OutboxEvent> events, CancellationToken cancellationToken);
}

/// <summary>The event can never be accepted (e.g. over the 1 MB Event Hubs limit); retrying will not help.</summary>
public sealed class EventRejectedException(long eventId, string reason) : Exception(reason)
{
    public long EventId { get; } = eventId;
}
