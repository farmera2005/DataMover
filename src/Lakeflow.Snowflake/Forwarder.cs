using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Snowflake;

/// <summary>Where each partition stands, for the console.</summary>
public sealed record PartitionStatus
{
    public required string Division { get; init; }
    public required string PartitionId { get; init; }
    /// <summary>The last Event Hubs sequence number sent to Snowflake.</summary>
    public long? Position { get; init; }
    public DateTimeOffset? LastEnqueuedUtc { get; init; }
    public DateTimeOffset? LastSentUtc { get; init; }
    public long RowsSent { get; init; }
    public int ConsecutiveFailures { get; init; }
    public string? LastError { get; init; }
}

public sealed class ForwarderStatus
{
    private readonly ConcurrentDictionary<(string, string), PartitionStatus> _partitions = new();

    public IReadOnlyList<PartitionStatus> Partitions => _partitions.Values.OrderBy(p => p.Division).ThenBy(p => p.PartitionId, StringComparer.Ordinal).ToList();

    public void Update(string division, string partition, Func<PartitionStatus, PartitionStatus> change) =>
        _partitions.AddOrUpdate((division, partition), _ => change(new PartitionStatus { Division = division, PartitionId = partition }), (_, s) => change(s));

    public void Clear() => _partitions.Clear();
}

/// <summary>
/// Moves one division's hub into <c>BRONZE.EVENTS</c>: one Snowflake channel per partition, the Event Hubs sequence
/// number as the offset token. On (re)start a channel reports its last committed offset and reading resumes right
/// after it, so a restart or a lost answer neither loses nor duplicates rows. Each partition retries on its own with
/// back-off (1 s doubling to 5 min); one failing partition or division never holds up another.
/// </summary>
public sealed class HubForwarder(IHubReader reader, IStreamingIngest ingest, ForwarderStatus status, TimeProvider time, ILogger log)
{
    public const int MaxEventsPerRead = 1000;
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    public static string ChannelName(string division, string partitionId) =>
        $"LAKEFLOW_{division.ToUpperInvariant()}_{partitionId}";

    public async Task RunAsync(CancellationToken ct)
    {
        IReadOnlyList<string> partitions = [];
        for (var failures = 0; partitions.Count == 0 && !ct.IsCancellationRequested;)
        {
            try
            {
                partitions = await reader.GetPartitionIdsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                log.LogWarning("Snowflake forwarder for {Division}: cannot list partitions: {Error}", reader.Division, ex.Message);
                status.Update(reader.Division, "-", s => s with { ConsecutiveFailures = failures, LastError = ex.Message });
                await DelayAsync(failures, ct);
            }
        }
        await Task.WhenAll(partitions.Select(p => PumpAsync(p, ct)));
    }

    private async Task PumpAsync(string partition, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var session = await OpenAsync(partition, ct);
                failures = 0;
                while (!ct.IsCancellationRequested) session = await PumpRoundAsync(partition, session, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                log.LogWarning("Snowflake forwarder {Division}/{Partition} failed ({Failures} in a row), reopening: {Error}",
                    reader.Division, partition, failures, ex.Message);
                status.Update(reader.Division, partition, s => s with { ConsecutiveFailures = failures, LastError = ex.Message });
                await DelayAsync(failures, ct);
            }
        }
    }

    public sealed record Session(ChannelSession Channel, long? Position);

    /// <summary>Opens the partition's channel and resumes after its last committed offset.</summary>
    public async Task<Session> OpenAsync(string partition, CancellationToken ct)
    {
        var channel = await ingest.OpenChannelAsync(ChannelName(reader.Division, partition), ct);
        status.Update(reader.Division, partition, s => s with { Position = channel.CommittedOffset ?? s.Position });
        return new Session(channel, channel.CommittedOffset);
    }

    /// <summary>One read and its appends; returns the session moved on.</summary>
    public async Task<Session> PumpRoundAsync(string partition, Session session, CancellationToken ct)
    {
        var events = await reader.ReadAsync(partition, session.Position, MaxEventsPerRead, MaxWait, ct);
        // Never send a position twice, whatever the reader returns.
        var fresh = events.Where(e => session.Position is not { } p || e.SequenceNumber > p).ToList();
        if (fresh.Count == 0) return session;
        var rows = fresh.Select(e => (e.SequenceNumber, BronzeRows.Row(reader.Division, partition, e))).ToList();
        var channel = session.Channel;
        long? position = session.Position;
        foreach (var chunk in Ndjson.Chunks(rows))
        {
            var next = await ingest.AppendAsync(channel, chunk.Bytes, chunk.LastSequence, ct);
            channel = channel with { ContinuationToken = next };
            position = chunk.LastSequence;
            var sent = chunk.Rows;
            var lastEnqueued = fresh.Last(e => e.SequenceNumber <= chunk.LastSequence).EnqueuedTime;
            status.Update(reader.Division, partition, s => s with
            {
                Position = chunk.LastSequence, LastEnqueuedUtc = lastEnqueued, LastSentUtc = time.GetUtcNow(),
                RowsSent = s.RowsSent + sent, ConsecutiveFailures = 0, LastError = null,
            });
        }
        return new Session(channel, position);
    }

    private Task DelayAsync(int failures, CancellationToken ct)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(failures - 1, 9)));
        return Task.Delay(TimeSpan.FromSeconds(seconds), time, ct);
    }
}
