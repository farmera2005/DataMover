using Azure;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;

namespace Lakeflow.Agent.Delivery;

/// <summary>
/// Azure Event Hubs over AMQP on WebSockets (outbound HTTPS 443 only), one producer per division hub, each
/// authenticated with the agent's Send-only SAS token for that hub.
/// </summary>
public sealed class EventHubsSender : IEventSender
{
    private sealed record Producer(EventHubTarget Target, AzureSasCredential Credential, EventHubProducerClient Client);

    private readonly Dictionary<string, Producer> _producers = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void UpdateTargets(IReadOnlyList<EventHubTarget> targets)
    {
        var retired = new List<EventHubProducerClient>();
        lock (_lock)
        {
            var wanted = targets.ToDictionary(t => LocalStore.HubKey(t.Division));
            foreach (var key in _producers.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                retired.Add(_producers[key].Client);
                _producers.Remove(key);
            }
            foreach (var (key, target) in wanted)
            {
                if (_producers.TryGetValue(key, out var current)
                    && current.Target.FullyQualifiedNamespace == target.FullyQualifiedNamespace && current.Target.HubName == target.HubName)
                {
                    if (current.Target.SasToken != target.SasToken) current.Credential.Update(target.SasToken);
                    _producers[key] = current with { Target = target };
                    continue;
                }
                if (current is not null) retired.Add(current.Client);
                var credential = new AzureSasCredential(target.SasToken);
                var client = new EventHubProducerClient(target.FullyQualifiedNamespace, target.HubName, credential, new EventHubProducerClientOptions
                {
                    ConnectionOptions = new EventHubConnectionOptions { TransportType = EventHubsTransportType.AmqpWebSockets },
                    // The delivery loop owns retries and back-off; fail fast here.
                    RetryOptions = new EventHubsRetryOptions { MaximumRetries = 1, TryTimeout = TimeSpan.FromSeconds(30) },
                    Identifier = "lakeflow-agent",
                });
                _producers[key] = new Producer(target, credential, client);
            }
        }
        foreach (var client in retired) _ = client.DisposeAsync().AsTask();
    }

    public async Task<int> SendAsync(string hub, string partitionKey, IReadOnlyList<OutboxEvent> events, CancellationToken cancellationToken)
    {
        Producer producer;
        lock (_lock)
        {
            if (!_producers.TryGetValue(hub, out producer!))
                throw new InvalidOperationException($"No Event Hubs target for hub '{hub}' in the current config.");
        }
        using var batch = await producer.Client.CreateBatchAsync(new CreateBatchOptions { PartitionKey = partitionKey }, cancellationToken);
        var added = 0;
        foreach (var outboxEvent in events)
        {
            var data = new EventData(outboxEvent.Payload) { ContentType = "application/json" };
            data.Properties["site_id"] = partitionKey[..partitionKey.IndexOf('|')];
            data.Properties["source_id"] = outboxEvent.SourceId;
            data.Properties["table"] = outboxEvent.Table;
            data.Properties["seq"] = outboxEvent.Seq;
            if (!batch.TryAdd(data))
            {
                if (added == 0)
                    throw new EventRejectedException(outboxEvent.Id, $"Change of {outboxEvent.Payload.Length} bytes exceeds the Event Hubs batch limit ({batch.MaximumSizeInBytes}).");
                break;
            }
            added++;
        }
        await producer.Client.SendAsync(batch, cancellationToken);
        return added;
    }

    public async ValueTask DisposeAsync()
    {
        List<Producer> producers;
        lock (_lock)
        {
            producers = [.. _producers.Values];
            _producers.Clear();
        }
        foreach (var producer in producers) await producer.Client.DisposeAsync();
    }
}
