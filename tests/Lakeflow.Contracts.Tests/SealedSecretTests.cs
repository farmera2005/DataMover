using System.Security.Cryptography;
using System.Text;
using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Contracts.Tests;

public class SealedSecretTests
{
    private static readonly RSA Agent = RSA.Create(2048);
    private const string Thumbprint = "ABC123";

    [Fact]
    public void Only_the_agent_key_opens_it()
    {
        var sealedValue = SealedSecret.Seal("p@ssw0rd!", Agent, Thumbprint);

        Assert.True(SealedSecret.IsWellFormed(sealedValue));
        Assert.Equal(Thumbprint, SealedSecret.KeyId(sealedValue));
        Assert.DoesNotContain("p@ssw0rd", Encoding.UTF8.GetString(Convert.FromBase64String(sealedValue)));
        Assert.Equal("p@ssw0rd!", SealedSecret.Open(sealedValue, Agent, Thumbprint));
        Assert.ThrowsAny<CryptographicException>(() => SealedSecret.Open(sealedValue, RSA.Create(2048), Thumbprint));
        Assert.ThrowsAny<CryptographicException>(() => SealedSecret.Open(sealedValue, Agent, "OTHER"));
    }

    [Fact]
    public void Sealing_twice_gives_different_values() =>
        Assert.NotEqual(SealedSecret.Seal("x", Agent, Thumbprint), SealedSecret.Seal("x", Agent, Thumbprint));

    [Theory]
    [InlineData("plain text password")]
    [InlineData("")]
    [InlineData("eyJ2IjoxfQ==")]
    public void Anything_else_is_not_well_formed(string value) => Assert.False(SealedSecret.IsWellFormed(value));
}
