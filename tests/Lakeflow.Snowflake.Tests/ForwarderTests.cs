using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lakeflow.Snowflake.Tests;

public sealed class ForwarderTests
{
    private sealed class Hub(int events) : IHubReader
    {
        public string Division => "grain";
        public List<long?> Reads { get; } = [];

        public Task<IReadOnlyList<string>> GetPartitionIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["0", "1"]);

        public Task<IReadOnlyList<HubEvent>> ReadAsync(string partitionId, long? afterSequence, int maxEvents, TimeSpan maxWait, CancellationToken ct)
        {
            Reads.Add(afterSequence);
            var from = (afterSequence ?? -1) + 1;
            IReadOnlyList<HubEvent> batch = Enumerable.Range((int)from, (int)Math.Max(0, Math.Min(maxEvents, events - from)))
                .Select(i => new HubEvent(i, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes($$"""{"site_id":"s","table":"dbo.T","op":"insert","seq":{{i}}}""")))
                .ToList();
            return Task.FromResult(batch);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Snowflake's side: what each channel committed, and every append.</summary>
    private sealed class Ingest : IStreamingIngest
    {
        public Dictionary<string, long?> Committed { get; } = [];
        public List<(string Channel, long Offset, int Rows)> Appends { get; } = [];
        public int FailNextAppends { get; set; }

        public Task<ChannelSession> OpenChannelAsync(string channel, CancellationToken ct) =>
            Task.FromResult(new ChannelSession(channel, Guid.NewGuid().ToString(), Committed.GetValueOrDefault(channel)));

        public Task<string> AppendAsync(ChannelSession session, ReadOnlyMemory<byte> ndjson, long offsetToken, CancellationToken ct)
        {
            if (FailNextAppends-- > 0) throw new SnowflakeException("Snowpipe Streaming: 503", 503, true);
            Appends.Add((session.Channel, offsetToken, Encoding.UTF8.GetString(ndjson.Span).Count(c => c == '\n')));
            Committed[session.Channel] = offsetToken;
            return Task.FromResult(Guid.NewGuid().ToString());
        }
    }

    [Fact]
    public async Task Each_partition_resumes_after_snowflakes_committed_offset()
    {
        var hub = new Hub(events: 2500);
        var ingest = new Ingest();
        ingest.Committed[HubForwarder.ChannelName("grain", "0")] = 1199; // a previous run got this far
        var status = new ForwarderStatus();
        var forwarder = new HubForwarder(hub, ingest, status, new FakeTimeProvider(), NullLogger.Instance);

        var session = await forwarder.OpenAsync("0", CancellationToken.None);
        Assert.Equal(1199, session.Position);
        while (true)
        {
            var next = await forwarder.PumpRoundAsync("0", session, CancellationToken.None);
            if (next.Position == session.Position) break;
            session = next;
        }

        Assert.Equal(1300, ingest.Appends.Sum(a => a.Rows)); // 1200..2499, nothing again, nothing missed
        Assert.Equal(2499, ingest.Committed["LAKEFLOW_GRAIN_0"]);
        Assert.Equal(1199, hub.Reads[0]);
        Assert.Equal(2499, status.Partitions.Single().Position);
        Assert.Equal(1300, status.Partitions.Single().RowsSent);
    }

    [Fact]
    public async Task A_failed_append_is_retried_from_the_committed_offset_without_loss_or_duplicates()
    {
        var hub = new Hub(events: 300);
        var ingest = new Ingest { FailNextAppends = 1 };
        var forwarder = new HubForwarder(hub, ingest, new ForwarderStatus(), new FakeTimeProvider(), NullLogger.Instance);

        var session = await forwarder.OpenAsync("1", CancellationToken.None);
        await Assert.ThrowsAsync<SnowflakeException>(() => forwarder.PumpRoundAsync("1", session, CancellationToken.None));
        session = await forwarder.OpenAsync("1", CancellationToken.None); // what the pump does after a failure
        Assert.Null(session.Position);
        session = await forwarder.PumpRoundAsync("1", session, CancellationToken.None);

        Assert.Equal(300, ingest.Appends.Sum(a => a.Rows));
        Assert.Equal(299, session.Position);
        Assert.Equal([299L], ingest.Appends.Select(a => a.Offset));
    }

    [Fact]
    public void Channel_names_are_per_division_and_partition() =>
        Assert.Equal("LAKEFLOW_AGRONOMY_12", HubForwarder.ChannelName("agronomy", "12"));
}
