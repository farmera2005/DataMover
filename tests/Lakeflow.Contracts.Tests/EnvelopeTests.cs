using System.Text.Json.Nodes;
using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Contracts.Tests;

public class EnvelopeTests
{
    private static ChangeEnvelope Sample() => new RowChange(
            ChangeOp.Update,
            new JsonObject { ["TicketId"] = 42 },
            new JsonObject { ["TicketId"] = 42, ["Net"] = 1000.5m },
            new JsonObject { ["TicketId"] = 42, ["Net"] = 1200.25m, ["Note"] = null },
            new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.Zero))
        .ToEnvelope("stmarys", "compuweigh", "compuweigh", "dbo.Tickets", 17, new DateTimeOffset(2026, 10, 28, 14, 0, 5, TimeSpan.Zero));

    [Fact]
    public void Serializes_with_the_spec_field_names()
    {
        var json = JsonNode.Parse(EnvelopeJson.Serialize(Sample()))!.AsObject();

        Assert.Equal(
            ["envelope_version", "site_id", "source_id", "source_system", "table", "op", "key", "key_id", "before", "after", "source_ts", "capture_ts", "seq", "partition_key"],
            json.Select(p => p.Key));
        Assert.Equal("update", json["op"]!.GetValue<string>());
        Assert.Equal("stmarys|dbo.Tickets", json["partition_key"]!.GetValue<string>());
        Assert.Equal(17, json["seq"]!.GetValue<long>());
        Assert.Equal("{\"TicketId\":42}", json["key_id"]!.GetValue<string>());
        Assert.True(json["after"]!.AsObject().ContainsKey("Note"), "null columns are kept, not dropped");
    }

    [Fact]
    public void Round_trips()
    {
        var original = Sample();
        var copy = EnvelopeJson.Deserialize<ChangeEnvelope>(EnvelopeJson.Serialize(original));

        Assert.Equal(original.SiteId, copy.SiteId);
        Assert.Equal(original.Op, copy.Op);
        Assert.Equal(original.Seq, copy.Seq);
        Assert.Equal(original.CaptureTs, copy.CaptureTs);
        Assert.Equal(original.SourceTs, copy.SourceTs);
        Assert.True(JsonNode.DeepEquals(original.Key, copy.Key));
        Assert.True(JsonNode.DeepEquals(original.After, copy.After));
        Assert.Equal(1200.25m, copy.After!["Net"]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData(ChangeOp.SnapshotComplete, "snapshot_complete")]
    [InlineData(ChangeOp.Reconcile, "reconcile")]
    [InlineData(ChangeOp.Insert, "insert")]
    [InlineData(ChangeOp.Delete, "delete")]
    [InlineData(ChangeOp.Snapshot, "snapshot")]
    public void Ops_are_lower_case_words(ChangeOp op, string expected) =>
        Assert.Equal($"\"{expected}\"", EnvelopeJson.Serialize(op));

    [Fact]
    public void Unknown_op_is_rejected() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => EnvelopeJson.Deserialize<ChangeOp>("\"upsert\""));

    [Fact]
    public void Envelope_does_not_share_row_objects_with_the_change()
    {
        var after = new JsonObject { ["a"] = 1 };
        var envelope = new RowChange(ChangeOp.Insert, new JsonObject { ["a"] = 1 }, null, after)
            .ToEnvelope("s", "src", "sys", "t", 1, DateTimeOffset.UtcNow);
        after["a"] = 2;
        Assert.Equal(1, envelope.After!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Checkpoint_round_trips()
    {
        var checkpoint = new Checkpoint(CaptureMethod.ChangeTracking, CheckpointPhase.Snapshot, "12345", new JsonObject { ["id"] = 99 });
        var json = EnvelopeJson.Serialize(checkpoint);
        Assert.Contains("\"method\":\"change_tracking\"", json);
        var copy = EnvelopeJson.Deserialize<Checkpoint>(json);
        Assert.Equal(checkpoint.Method, copy.Method);
        Assert.Equal(checkpoint.Phase, copy.Phase);
        Assert.Equal("12345", copy.Position);
        Assert.Equal(99, copy.SnapshotAfterKey!["id"]!.GetValue<int>());
    }
}
