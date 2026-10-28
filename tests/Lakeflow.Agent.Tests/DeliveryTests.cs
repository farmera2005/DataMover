using System.Text.Json.Nodes;
using Lakeflow.Agent.Delivery;
using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lakeflow.Agent.Tests;

public sealed class DeliveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lakeflow-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStore _store;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSender _sender = new();
    private readonly DeliveryLoop _loop;

    public DeliveryTests()
    {
        _store = LocalStore.Open(_dir, new TestProtector());
        _loop = new DeliveryLoop(_store, _sender, _time, NullLogger<DeliveryLoop>.Instance, new Random(1));
    }

    private void Buffer(string table, int count, int size = 10)
    {
        var changes = Enumerable.Range(1, count)
            .Select(i => new RowChange(ChangeOp.Insert, new JsonObject { ["id"] = i }, null, new JsonObject { ["id"] = i, ["v"] = new string('x', size) }))
            .ToList();
        _store.Append("site", "src", table, changes, null, true, false, _time.GetUtcNow());
    }

    [Fact]
    public async Task Each_tables_changes_arrive_in_order_under_their_own_partition_key()
    {
        Buffer("dbo.A", 30);
        Buffer("dbo.B", 30);
        Buffer("dbo.A", 30);
        _sender.MaxBatchEvents = 7; // force many small batches

        while (await _loop.SendOnceAsync(25, CancellationToken.None) > 0) { }

        Assert.Equal(0, _store.GetBufferStats().Count);
        foreach (var table in _sender.Received.GroupBy(e => e.Table))
        {
            Assert.Equal(Enumerable.Range(1, table.Count()).Select(i => (long)i), table.Select(e => e.Seq));
            Assert.All(table, e => Assert.Equal($"site|{table.Key}", e.PartitionKey));
        }
    }

    [Fact]
    public async Task Failed_sends_back_off_exponentially_to_five_minutes()
    {
        Buffer("dbo.A", 3);
        _sender.Down = true;
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(0, await _loop.SendOnceAsync(10, CancellationToken.None));
            var wait = _loop.NextAttempt - _time.GetUtcNow();
            delays.Add(wait);
            Assert.Equal(0, await _loop.SendOnceAsync(10, CancellationToken.None)); // too early: no attempt
            _time.Advance(wait);
        }
        Assert.Equal(12, _sender.Attempts);
        Assert.InRange(delays[0].TotalSeconds, 0.8, 1.2);
        Assert.InRange(delays[3].TotalSeconds, 6.4, 9.6);
        Assert.All(delays.Skip(9), d => Assert.InRange(d.TotalMinutes, 4, 5));
        Assert.Equal(3, _store.GetBufferStats().Count);
        Assert.False(_loop.Reachable);

        _sender.Down = false;
        Assert.Equal(3, await _loop.SendOnceAsync(10, CancellationToken.None));
        Assert.True(_loop.Reachable);
    }

    [Fact]
    public async Task An_event_too_large_for_Event_Hubs_is_set_aside_and_the_rest_still_flow()
    {
        Buffer("dbo.A", 2);
        Buffer("dbo.A", 1, size: 5000);
        Buffer("dbo.A", 2);
        _sender.MaxEventBytes = 1000;

        while (await _loop.SendOnceAsync(10, CancellationToken.None) > 0) { }

        Assert.Equal([1, 2, 4, 5], _sender.Received.Select(e => e.Seq));
        Assert.Equal(1, _store.DeadLetterCount());
        Assert.Equal(0, _store.GetBufferStats().Count);
        var alert = Assert.Single(_store.PeekEvents(10)).Event;
        Assert.Equal(EventSeverity.Critical, alert.Severity);
        Assert.Contains("seq 3", alert.Message);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 16)]
    [InlineData(30, 300)]
    public void Backoff_doubles_with_jitter_up_to_the_cap(int failures, double expectedSeconds)
    {
        var delay = Backoff.Delay(failures, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        Assert.InRange(delay.TotalSeconds, Math.Min(expectedSeconds * 0.8, 300), Math.Min(expectedSeconds * 1.2, 300));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
