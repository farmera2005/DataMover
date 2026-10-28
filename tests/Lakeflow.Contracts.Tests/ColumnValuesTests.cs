using System.Text.Json.Nodes;
using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Contracts.Tests;

public class ColumnValuesTests
{
    private static string Json(object? value) => ColumnValues.Encode(value)?.ToJsonString() ?? "null";

    [Fact]
    public void Nulls_and_db_nulls_encode_as_null()
    {
        Assert.Equal("null", Json(null));
        Assert.Equal("null", Json(DBNull.Value));
    }

    [Fact]
    public void Numbers_stay_exact()
    {
        Assert.Equal("123456789012.3456", Json(123456789012.3456m));
        Assert.Equal("9223372036854775807", Json(long.MaxValue));
        Assert.Equal("true", Json(true));
        Assert.Equal("\"NaN\"", Json(double.NaN));
    }

    [Fact]
    public void Source_local_times_get_no_offset_added()
    {
        Assert.Equal("\"2026-11-01T01:30:00.0000000\"", Json(new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified)));
        Assert.Equal("\"2026-11-01T05:30:00.0000000Z\"", Json(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("\"2026-11-01T01:30:00.0000000-04:00\"", Json(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4))));
        Assert.Equal("\"2026-11-01\"", Json(new DateOnly(2026, 11, 1)));
        Assert.Equal("\"13:05:00.0000000\"", Json(new TimeOnly(13, 5)));
    }

    [Fact]
    public void Binary_and_guids_are_text()
    {
        Assert.Equal("\"AQID\"", Json(new byte[] { 1, 2, 3 }));
        Assert.Equal("\"6f9619ff-8b86-d011-b42d-00c04fc964ff\"", Json(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF")));
    }

    [Fact]
    public void Unknown_types_fail_loudly() =>
        Assert.Throws<NotSupportedException>(() => ColumnValues.Encode(new object()));

    [Fact]
    public void Key_is_picked_from_the_row()
    {
        var row = ColumnValues.Row([new("Site", "A"), new("TicketId", 7), new("Net", 1.5m)]);
        var key = ColumnValues.KeyOf(row, ["Site", "TicketId"]);
        Assert.Equal("{\"Site\":\"A\",\"TicketId\":7}", ColumnValues.KeyText(key));
        Assert.Throws<ArgumentException>(() => ColumnValues.KeyOf(row, ["Missing"]));
    }
}
