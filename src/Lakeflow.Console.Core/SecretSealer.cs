using System.Security.Cryptography;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core;

/// <summary>
/// Seals a password to a collector's public key on the user's machine, so it leaves the browser already encrypted.
/// The web console uses WebCrypto (browsers have no .NET RSA); this .NET version serves tests and tools, and the two
/// produce the same <see cref="SealedSecret"/> format.
/// </summary>
public interface ISecretSealer
{
    /// <param name="publicKeySpki">The collector's RSA public key, SubjectPublicKeyInfo, base64 (<see cref="AgentDto.PublicKeySpki"/>).</param>
    Task<string> SealAsync(string secret, string publicKeySpki, string thumbprint);
}

public sealed class DotNetSecretSealer : ISecretSealer
{
    public Task<string> SealAsync(string secret, string publicKeySpki, string thumbprint)
    {
        using var key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
        return Task.FromResult(SealedSecret.Seal(secret, key, thumbprint));
    }
}
