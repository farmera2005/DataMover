using Azure;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;

namespace Lakeflow.Agent.Delivery;

/// <summary>
/// Azure Event Hubs over AMQP on WebSockets (outbound HTTPS 443 only), authenticated with the agent's Send-only
/// SAS token for its own hub.
/// </summary>
public sealed class EventHubsSender : IEventSender
{
    private EventHubProducerClient? _client;
    private AzureSasCredential? _credential;
    private EventHubTarget? _target;

    public void UpdateTarget(EventHubTarget target)
    {
        if (_client is not null && _target is not null
            && _target.FullyQualifiedNamespace == target.FullyQualifiedNamespace && _target.HubName == target.HubName)
        {
            if (_target.SasToken != target.SasToken) _credential!.Update(target.SasToken);
            _target = target;
            return;
        }
        var old = _client;
        _credential = new AzureSasCredential(target.SasToken);
        _client = new EventHubProducerClient(target.FullyQualifiedNamespace, target.HubName, _credential, new EventHubProducerClientOptions
        {
            ConnectionOptions = new EventHubConnectionOptions { TransportType = EventHubsTransportType.AmqpWebSockets },
            // The delivery loop owns retries and back-off; fail fast here.
            RetryOptions = new EventHubsRetryOptions { MaximumRetries = 1, TryTimeout = TimeSpan.FromSeconds(30) },
            Identifier = "lakeflow-agent",
        });
        _target = target;
        if (old is not null) _ = old.DisposeAsync().AsTask();
    }

    public async Task<int> SendAsync(string partitionKey, IReadOnlyList<OutboxEvent> events, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("No Event Hubs target configured yet.");
        using var batch = await client.CreateBatchAsync(new CreateBatchOptions { PartitionKey = partitionKey }, cancellationToken);
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
        await client.SendAsync(batch, cancellationToken);
        return added;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
