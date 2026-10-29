using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lakeflow.Contracts;

namespace Lakeflow.ControlApi.Security;

/// <summary>
/// Where the API keeps its own keys: the master key that encrypts secrets in the database, the collector CA and the
/// TLS certificates. Created on first start; nothing is configured. In Docker this is its own volume, apart from the
/// database's, so a copy of the database alone reveals no secret.
/// </summary>
public sealed class ServerOptions
{
    public string DataDirectory { get; set; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lakeflow", "ControlApi")
        : "/var/lib/lakeflow-api";

    /// <summary>The ports the stack is reached on from outside (as published by Docker); used in links and install files.</summary>
    public int PublishedHttpsPort { get; set; } = 8443;
    public int PublishedCollectorPort { get; set; } = 8444;
}

/// <summary>The collector CA (certificate with private key), which issues collector client certificates and the TLS certificates.</summary>
public interface IKeyMaterial
{
    X509Certificate2 AgentCa { get; }
}

/// <summary>Loads the CA from the data directory, creating it (RSA 3072, 20 years) on first start.</summary>
public sealed class LocalKeyMaterial : IKeyMaterial
{
    public LocalKeyMaterial(ServerOptions options)
    {
        var file = Path.Combine(options.DataDirectory, "collector-ca.pfx");
        if (!File.Exists(file))
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest($"CN=Lakeflow CA ({Environment.MachineName}), O=Mercer Landmark", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
            OwnerOnlyFile.Write(file, ca.Export(X509ContentType.Pfx));
        }
        AgentCa = new X509Certificate2(File.ReadAllBytes(file), (string?)null, X509KeyStorageFlags.EphemeralKeySet);
    }

    public X509Certificate2 AgentCa { get; }

    /// <summary>SHA-256 of the CA certificate, as collectors pin it before they first connect.</summary>
    public static string Fingerprint(X509Certificate2 ca) => Convert.ToHexString(SHA256.HashData(ca.RawData)).ToLowerInvariant();
}

/// <summary>The master key that encrypts setting secrets (AES-256-GCM), in the data directory.</summary>
public sealed class SettingsProtector(ServerOptions options) : ISecretProtector
{
    private readonly FileKeyProtector _inner = new(Path.Combine(options.DataDirectory, "master.key"));

    public byte[] Protect(byte[] plain) => _inner.Protect(plain);

    public byte[] Unprotect(byte[] protectedBytes) => _inner.Unprotect(protectedBytes);
}

internal static class OwnerOnlyFile
{
    public static void Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options)) stream.Write(content);
        File.Move(temp, path, overwrite: true);
    }
}
