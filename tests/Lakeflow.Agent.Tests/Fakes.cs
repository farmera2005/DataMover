using System.Security.Cryptography;
using Lakeflow.Agent.Delivery;
using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;

namespace Lakeflow.Agent.Tests;

/// <summary>Stands in for DPAPI on the Linux test machine: AES-GCM with a key fixed per instance.</summary>
public sealed class TestProtector : ISecretProtector
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public byte[] Protect(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] data)
    {
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return plain;
    }
}

/// <summary>Event Hubs stand-in: records what it accepts, can be switched off, enforces a size limit.</summary>
public sealed class FakeSender : IEventSender
{
    public List<ChangeEnvelope> Received { get; } = [];
    public List<string> PartitionKeys { get; } = [];
    public bool Down { get; set; }
    public int MaxEventBytes { get; set; } = 1024 * 1024;
    public int MaxBatchEvents { get; set; } = 100;
    public int Attempts { get; private set; }
    public EventHubTarget? Target { get; private set; }

    public void UpdateTarget(EventHubTarget target) => Target = target;

    public Task<int> SendAsync(string partitionKey, IReadOnlyList<OutboxEvent> events, CancellationToken cancellationToken)
    {
        Attempts++;
        if (Down) throw new InvalidOperationException("Simulated outage: Event Hubs unreachable.");
        var count = 0;
        foreach (var e in events.Take(MaxBatchEvents))
        {
            if (e.Payload.Length > MaxEventBytes)
            {
                if (count == 0) throw new EventRejectedException(e.Id, "too large");
                break;
            }
            var envelope = EnvelopeJson.Deserialize<ChangeEnvelope>(e.Payload);
            if (envelope.PartitionKey != partitionKey) throw new InvalidOperationException("event sent with the wrong partition key");
            Received.Add(envelope);
            PartitionKeys.Add(partitionKey);
            count++;
        }
        return Task.FromResult(count);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
