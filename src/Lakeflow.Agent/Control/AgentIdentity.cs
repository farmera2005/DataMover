using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lakeflow.Agent.Storage;

namespace Lakeflow.Agent.Control;

/// <summary>
/// The agent's client certificate. The key pair is made here and only the public half leaves the machine (in a
/// certificate signing request); the certificate and key are kept in the local store under DPAPI.
/// </summary>
public sealed class AgentIdentity : IDisposable
{
    private AgentIdentity(X509Certificate2 certificate, string controlApi, string? caPem) =>
        (Certificate, ControlApi, Ca) = (certificate, controlApi, string.IsNullOrEmpty(caPem) ? null : X509Certificate2.CreateFromPem(caPem));

    public X509Certificate2 Certificate { get; }
    public string ControlApi { get; }
    /// <summary>The control API's CA, received at enrollment: the only CA trusted for its TLS certificate.</summary>
    public X509Certificate2? Ca { get; }
    public ServerTrust Trust => Ca is null ? ServerTrust.System : new ServerTrust(Ca);
    public string AgentId => Certificate.GetNameInfo(X509NameType.SimpleName, false);

    public static AgentIdentity? Load(LocalStore store) =>
        store.LoadIdentity() is { } saved
            ? new AgentIdentity(X509CertificateLoader(saved.Pfx), saved.ControlApi, saved.CaPem)
            : null;

    /// <summary>A new key pair and the CSR for it.</summary>
    public static (RSA Key, string CsrPem) NewRequest(string hostname)
    {
        var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={hostname}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return (key, request.CreateSigningRequestPem());
    }

    /// <summary>Combines the issued certificate with its key and saves both.</summary>
    public static AgentIdentity Save(LocalStore store, RSA key, string certificatePem, string controlApi, string? caPem)
    {
        using var publicOnly = X509Certificate2.CreateFromPem(certificatePem);
        using var withKey = publicOnly.CopyWithPrivateKey(key);
        var pfx = withKey.Export(X509ContentType.Pkcs12);
        try
        {
            store.SaveIdentity(pfx, controlApi, caPem);
            return new AgentIdentity(X509CertificateLoader(pfx), controlApi, caPem);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    // EphemeralKeySet keeps the key in memory only (it is persisted, protected, in the store) where the OS supports it.
    private static X509Certificate2 X509CertificateLoader(byte[] pfx) =>
        new(pfx, (string?)null, OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);

    public void Dispose()
    {
        Certificate.Dispose();
        Ca?.Dispose();
    }
}
