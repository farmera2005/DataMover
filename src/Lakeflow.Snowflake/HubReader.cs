using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Primitives;

namespace Lakeflow.Snowflake;

/// <summary>Reads one division's hub, partition by partition, from a given position.</summary>
public interface IHubReader : IAsyncDisposable
{
    string Division { get; }

    Task<IReadOnlyList<string>> GetPartitionIdsAsync(CancellationToken ct);

    /// <summary>Events after <paramref name="afterSequence"/> (null: from the start of the hub's retention), in order.</summary>
    Task<IReadOnlyList<HubEvent>> ReadAsync(string partitionId, long? afterSequence, int maxEvents, TimeSpan maxWait, CancellationToken ct);
}

/// <summary>
/// Event Hubs with its own consumer group (<c>snowflake</c>) and a Listen-only key on the hub; AMQP over WebSockets,
/// so only port 443 is needed. Positions are not stored in Event Hubs: Snowflake's committed offset tokens are the
/// checkpoint.
/// </summary>
public sealed class EventHubsReader(string division, string connectionString, string consumerGroup) : IHubReader
{
    private static readonly EventHubConnectionOptions Transport = new() { TransportType = EventHubsTransportType.AmqpWebSockets };

    private readonly Dictionary<string, (PartitionReceiver Receiver, long? Next)> _receivers = [];
    private readonly object _gate = new();

    public string Division => division;

    public async Task<IReadOnlyList<string>> GetPartitionIdsAsync(CancellationToken ct)
    {
        await using var client = new EventHubConsumerClient(consumerGroup, connectionString, new EventHubConsumerClientOptions { ConnectionOptions = Transport });
        return await client.GetPartitionIdsAsync(ct);
    }

    public async Task<IReadOnlyList<HubEvent>> ReadAsync(string partitionId, long? afterSequence, int maxEvents, TimeSpan maxWait, CancellationToken ct)
    {
        PartitionReceiver receiver;
        PartitionReceiver? stale = null;
        lock (_gate)
        {
            // A receiver continues from where it stopped; asked for another position, it is replaced.
            if (_receivers.TryGetValue(partitionId, out var current) && current.Next == afterSequence) receiver = current.Receiver;
            else
            {
                stale = _receivers.TryGetValue(partitionId, out var old) ? old.Receiver : null;
                var position = afterSequence is { } after ? EventPosition.FromSequenceNumber(after, isInclusive: false) : EventPosition.Earliest;
                receiver = new PartitionReceiver(consumerGroup, partitionId, position, connectionString,
                    new PartitionReceiverOptions { ConnectionOptions = Transport });
                _receivers[partitionId] = (receiver, afterSequence);
            }
        }
        if (stale is not null) await stale.CloseAsync(ct);
        var batch = await receiver.ReceiveBatchAsync(maxEvents, maxWait, ct);
        var events = batch.Select(e => new HubEvent(e.SequenceNumber, e.EnqueuedTime, e.EventBody.ToMemory())).ToList();
        lock (_gate)
        {
            if (events.Count > 0) _receivers[partitionId] = (receiver, events[^1].SequenceNumber);
        }
        return events;
    }

    public async ValueTask DisposeAsync()
    {
        List<PartitionReceiver> receivers;
        lock (_gate) receivers = _receivers.Values.Select(r => r.Receiver).ToList();
        foreach (var receiver in receivers) await receiver.CloseAsync();
    }
}
