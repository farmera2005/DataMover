using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Lakeflow.Contracts;

namespace Lakeflow.ControlApi.Security;

/// <summary>
/// Secrets the API needs: the agent CA (certificate with private key) and the Send-only SAS policy key of each
/// division hub. In Azure they come from Key Vault through the API's managed identity; nothing is in config files.
/// </summary>
public interface IKeyMaterial
{
    X509Certificate2 AgentCa { get; }

    /// <summary>Name and key of the hub's Send-only shared access policy.</summary>
    (string PolicyName, byte[] Key) HubSendKey(Division division);
}

public sealed class KeyVaultOptions
{
    /// <summary>e.g. <c>https://lakeflow-kv.vault.azure.net/</c>.</summary>
    public string? VaultUri { get; set; }
    public string AgentCaSecret { get; set; } = "agent-ca";
    /// <summary>Secret per hub holding the policy key; <c>{hub}</c> is replaced by the hub name.</summary>
    public string HubKeySecretPattern { get; set; } = "eventhubs-{hub}-send-key";
    public string HubPolicyName { get; set; } = "agent-send";
}

/// <summary>Loads everything once at startup (a restart picks up rotated keys).</summary>
public sealed class KeyVaultKeyMaterial : IKeyMaterial
{
    private readonly Dictionary<Division, (string, byte[])> _hubKeys = [];

    public KeyVaultKeyMaterial(KeyVaultOptions options)
    {
        var client = new SecretClient(new Uri(options.VaultUri ?? throw new InvalidOperationException("KeyVault:VaultUri is not set.")),
            new DefaultAzureCredential());
        // A Key Vault certificate's secret is its PFX (base64), private key included.
        var pfx = Convert.FromBase64String(client.GetSecret(options.AgentCaSecret).Value.Value);
        AgentCa = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.EphemeralKeySet);
        foreach (var division in Enum.GetValues<Division>())
        {
            var name = options.HubKeySecretPattern.Replace("{hub}", LakeNames.HubName(division));
            try
            {
                _hubKeys[division] = (options.HubPolicyName, System.Text.Encoding.UTF8.GetBytes(client.GetSecret(name).Value.Value));
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Division not deployed yet; sites with sources in it are refused until it is.
            }
        }
    }

    public X509Certificate2 AgentCa { get; }

    public (string PolicyName, byte[] Key) HubSendKey(Division division) =>
        _hubKeys.TryGetValue(division, out var key) ? key : throw new InvalidOperationException($"No Send key for the {division} hub in Key Vault.");
}

/// <summary>For local development and tests: a throw-away CA and random hub keys, generated at startup.</summary>
public sealed class DevelopmentKeyMaterial : IKeyMaterial
{
    private readonly Dictionary<Division, byte[]> _keys = Enum.GetValues<Division>().ToDictionary(d => d, _ => RandomNumberGenerator.GetBytes(32));

    public DevelopmentKeyMaterial()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=Lakeflow Agent CA (development)", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        AgentCa = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }

    public X509Certificate2 AgentCa { get; }

    public (string PolicyName, byte[] Key) HubSendKey(Division division) => ("agent-send", _keys[division]);
}
