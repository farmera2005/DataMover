using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Lakeflow.Contracts;

/// <summary>
/// Seals a source password to one agent's certificate, so the console can hand it to the agent through the control
/// API without the API (or anyone reading its database) ever being able to read it. Hybrid encryption: a random
/// AES-256-GCM key encrypts the secret and is itself encrypted with the agent's RSA public key (OAEP-SHA256).
/// The certificate thumbprint is bound in as associated data, so a sealed value only opens with the key it was made for.
/// </summary>
public static class SealedSecret
{
    private const string Algorithm = "RSA-OAEP-256+A256GCM";

    public static string Seal(string secret, RSA publicKey, string thumbprint)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(secret);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(thumbprint));
            var envelope = new JsonObject
            {
                ["v"] = 1,
                ["alg"] = Algorithm,
                ["kid"] = thumbprint,
                ["ek"] = Convert.ToBase64String(publicKey.Encrypt(key, RSAEncryptionPadding.OaepSHA256)),
                ["iv"] = Convert.ToBase64String(nonce),
                ["ct"] = Convert.ToBase64String(cipher),
                ["tag"] = Convert.ToBase64String(tag),
            };
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Thumbprint of the certificate a sealed value was made for, readable without the key.</summary>
    public static string KeyId(string sealedValue) => Parse(sealedValue)["kid"]!.GetValue<string>();

    /// <summary>True when the text is a well-formed sealed value (the API stores nothing else).</summary>
    public static bool IsWellFormed(string sealedValue)
    {
        try
        {
            var e = Parse(sealedValue);
            return e["v"]?.GetValue<int>() == 1 && e["alg"]?.GetValue<string>() == Algorithm
                && new[] { "kid", "ek", "iv", "ct", "tag" }.All(k => !string.IsNullOrEmpty(e[k]?.GetValue<string>()));
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    public static string Open(string sealedValue, RSA privateKey, string thumbprint)
    {
        var e = Parse(sealedValue);
        if (e["alg"]?.GetValue<string>() != Algorithm) throw new CryptographicException("Unknown sealing algorithm.");
        if (!string.Equals(e["kid"]?.GetValue<string>(), thumbprint, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Sealed for a different certificate.");
        var key = privateKey.Decrypt(Convert.FromBase64String(e["ek"]!.GetValue<string>()), RSAEncryptionPadding.OaepSHA256);
        var cipher = Convert.FromBase64String(e["ct"]!.GetValue<string>());
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(e["iv"]!.GetValue<string>()), cipher,
                Convert.FromBase64String(e["tag"]!.GetValue<string>()), plain, Encoding.UTF8.GetBytes(thumbprint));
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static JsonObject Parse(string sealedValue) =>
        JsonNode.Parse(Convert.FromBase64String(sealedValue))?.AsObject() ?? throw new FormatException("Empty sealed value.");
}
