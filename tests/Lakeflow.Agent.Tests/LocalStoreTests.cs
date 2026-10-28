using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Lakeflow.Agent.Storage;
using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Agent.Tests;

public sealed class LocalStoreTests : IDisposable
{
    private static readonly ChangeOrigin Origin = new("site", "grain", "src", "sys");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lakeflow-store-" + Guid.NewGuid().ToString("N"));
    private readonly TestProtector _protector = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static RowChange Change(int id, string name) =>
        new(ChangeOp.Insert, new JsonObject { ["id"] = id }, null, new JsonObject { ["id"] = id, ["name"] = name });

    private static Checkpoint Cp(string position) => new(CaptureMethod.ChangeTracking, CheckpointPhase.Incremental, position);

    [Fact]
    public void Nothing_is_readable_in_the_files()
    {
        using (var store = LocalStore.Open(_dir, _protector))
        {
            store.Append(Origin, "dbo.T", [Change(1, "TICKET-SECRET-4711")], Cp("1"), true, false, Now);
            store.SetCredential("cw", "PASSWORD-XYZZY");
            store.SaveConfig(Harness.Config());
        }
        foreach (var file in Directory.GetFiles(_dir))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("TICKET-SECRET-4711", text);
            Assert.DoesNotContain("PASSWORD-XYZZY", text);
            Assert.DoesNotContain("test.servicebus", text);
            Assert.DoesNotContain("CREATE TABLE", text);
        }
    }

    [Fact]
    public void Files_copied_to_another_machine_cannot_be_opened()
    {
        using (LocalStore.Open(_dir, _protector)) { }
        Assert.ThrowsAny<CryptographicException>(() => LocalStore.Open(_dir, new TestProtector()));
    }

    [Fact]
    public void A_database_without_its_key_is_not_silently_replaced()
    {
        using (LocalStore.Open(_dir, _protector)) { }
        File.Delete(Path.Combine(_dir, "buffer.key"));
        Assert.Throws<InvalidOperationException>(() => LocalStore.Open(_dir, _protector));
    }

    [Fact]
    public void Sequence_and_checkpoint_survive_a_restart_and_a_resnapshot()
    {
        using (var store = LocalStore.Open(_dir, _protector))
        {
            store.Append(Origin, "dbo.T", [Change(1, "a"), Change(2, "b")], Cp("7"), true, false, Now);
        }
        using (var store = LocalStore.Open(_dir, _protector))
        {
            var record = store.GetTable("src", "dbo.T");
            Assert.Equal(2, record.LastSeq);
            Assert.Equal("7", record.Checkpoint!.Position);
            Assert.Equal(Now, record.LastCaughtUp);

            store.ResetCheckpoint("src", "dbo.T");
            Assert.Null(store.GetTable("src", "dbo.T").Checkpoint);
            Assert.Equal(3, store.StartSnapshot("src", "dbo.T"));
            store.Append(Origin, "dbo.T", [Change(1, "a")], null, false, false, Now);
            Assert.Equal(3, store.GetTable("src", "dbo.T").LastSeq);
            Assert.Null(store.GetTable("src", "dbo.T").Checkpoint); // null leaves it as it was

            var seqs = store.Peek(10, long.MaxValue).Select(e => e.Seq);
            Assert.Equal([1, 2, 3], seqs);
        }
    }

    [Fact]
    public void Outbox_is_drained_only_by_acknowledgement()
    {
        using var store = LocalStore.Open(_dir, _protector);
        store.Append(Origin, "dbo.T", Enumerable.Range(1, 5).Select(i => Change(i, "x")).ToList(), Cp("1"), true, false, Now);
        var stats = store.GetBufferStats();
        Assert.Equal(5, stats.Count);
        Assert.Equal(Now, stats.Oldest);

        var first = store.Peek(2, long.MaxValue);
        Assert.Equal(2, first.Count);
        Assert.Equal("site|dbo.T", first[0].PartitionKey);
        Assert.Equal(5, store.GetBufferStats().Count); // peeking removes nothing

        store.Acknowledge(first.Select(e => e.Id).ToList());
        store.Acknowledge(first.Select(e => e.Id).ToList()); // repeated ack is harmless
        stats = store.GetBufferStats();
        Assert.Equal(3, stats.Count);
        Assert.Equal(store.Peek(10, long.MaxValue).Sum(e => (long)e.Payload.Length), stats.Bytes);
    }

    [Fact]
    public void Peek_respects_the_byte_budget_but_always_returns_one()
    {
        using var store = LocalStore.Open(_dir, _protector);
        store.Append(Origin, "dbo.T", [Change(1, new string('x', 5000)), Change(2, "y")], Cp("1"), true, false, Now);
        Assert.Single(store.Peek(10, 100));
    }

    [Fact]
    public async Task Finds_keys_no_longer_at_the_source()
    {
        using var store = LocalStore.Open(_dir, _protector);
        store.Append(Origin, "dbo.T", Enumerable.Range(1, 6).Select(i => Change(i, "x")).ToList(), Cp("1"), true, trackKeys: true, Now);
        store.Append(Origin, "dbo.T", [new RowChange(ChangeOp.Delete, new JsonObject { ["id"] = 6 }, null, null)], Cp("2"), true, trackKeys: true, Now);
        Assert.Equal(5, store.KeyCount("src", "dbo.T"));

        var missing = await store.FindMissingKeysAsync("src", "dbo.T", Keys(1, 3, 5), CancellationToken.None);
        Assert.Equal(["{\"id\":2}", "{\"id\":4}"], missing.Order());
    }

    private static async IAsyncEnumerable<string> Keys(params int[] ids)
    {
        foreach (var id in ids) yield return $"{{\"id\":{id}}}";
        await Task.CompletedTask;
    }

    [Fact]
    public void Credentials_events_and_config_round_trip()
    {
        using var store = LocalStore.Open(_dir, _protector);
        Assert.Null(store.GetCredential("cw"));
        store.SetCredential("cw", "p@ss");
        Assert.Equal("p@ss", store.GetCredential("cw"));

        Assert.Null(store.LoadConfig());
        store.SaveConfig(Harness.Config() with { ConfigVersion = 9 });
        Assert.Equal(9, store.LoadConfig()!.ConfigVersion);

        store.AddEvent(new AgentEvent { EventId = Guid.NewGuid(), OccurredUtc = Now, Kind = AgentEventKind.Audit, Severity = EventSeverity.Info, Message = "hi" });
        var events = store.PeekEvents(10);
        Assert.Equal("hi", Assert.Single(events).Event.Message);
        store.RemoveEvents(events.Select(e => e.Id));
        Assert.Empty(store.PeekEvents(10));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
