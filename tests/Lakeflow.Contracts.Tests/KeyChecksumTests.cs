using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Contracts.Tests;

public class KeyChecksumTests
{
    // The same vectors are checked by fabric/tests/test_keychecksum.py, so the agent and the notebook agree.
    [Theory]
    [InlineData("{\"id\":1}", 251236297832090819UL)]
    [InlineData("{\"Site\":\"AA\",\"TicketId\":12345}", 2725067276086842005UL)]
    [InlineData("{\"name\":\"é\"}", 3393101986252890546UL)]
    public void Matches_the_shared_vectors(string key, ulong expected) => Assert.Equal(expected, KeyChecksum.Of(key));

    [Fact]
    public void Sum_ignores_order_and_wraps_at_2_to_the_64()
    {
        var a = new KeyChecksum.Accumulator();
        var b = new KeyChecksum.Accumulator();
        var keys = Enumerable.Range(1, 1000).Select(i => $"{{\"id\":{i}}}").ToList();
        foreach (var k in keys) a.Add(k);
        foreach (var k in Enumerable.Reverse(keys)) b.Add(k);
        Assert.Equal(1000, a.Count);
        Assert.Equal(a.Sum, b.Sum);
        var expected = keys.Aggregate(System.Numerics.BigInteger.Zero, (s, k) => s + KeyChecksum.Of(k)) % System.Numerics.BigInteger.Pow(2, 64);
        Assert.Equal(expected, new System.Numerics.BigInteger(a.Sum));
    }
}
