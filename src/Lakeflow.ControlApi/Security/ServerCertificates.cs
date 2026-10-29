using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lakeflow.Contracts;

namespace Lakeflow.ControlApi.Security;

/// <summary>
/// The TLS certificates the stack serves. Collectors always get one issued by the Lakeflow CA (they pin that CA when
/// they enroll, so no certificate is installed anywhere). Browsers get an uploaded certificate (your own CA's, for the
/// server's name) or, until one is uploaded, another one from the Lakeflow CA: browsers warn about it unless the
/// Lakeflow CA certificate (downloadable in the console) is trusted on the PC.
/// </summary>
public sealed class ServerCertificates(IKeyMaterial keys, TimeProvider time)
{
    public static readonly TimeSpan GeneratedLifetime = TimeSpan.FromDays(398);

    private volatile SslStreamCertificateContext? _web;
    private volatile SslStreamCertificateContext? _collector;
    private volatile bool _webUploaded;

    public X509Certificate2 Web => (_web ?? throw new InvalidOperationException("Certificates not loaded yet.")).TargetCertificate;
    public X509Certificate2 Collector => (_collector ?? throw new InvalidOperationException("Certificates not loaded yet.")).TargetCertificate;
    public bool WebUploaded => _webUploaded;

    /// <summary>With the chain, so a collector can check it against the pinned CA.</summary>
    public SslStreamCertificateContext CollectorContext => _collector ?? throw new InvalidOperationException("Certificates not loaded yet.");
    public SslStreamCertificateContext WebContext => _web ?? throw new InvalidOperationException("Certificates not loaded yet.");

    /// <summary>(Re)issues the generated certificates for the server's name, and uses the uploaded one if there is one.</summary>
    public void Load(string? host, byte[]? uploadedPfx, string? uploadedPassword = null)
    {
        var names = Names(host);
        var generated = Issue(names);
        _collector = SslStreamCertificateContext.Create(generated, new X509Certificate2Collection(keys.AgentCa), offline: true);
        if (uploadedPfx is not null && TryOpenPfx(uploadedPfx, uploadedPassword, out var uploaded, out _))
        {
            var chain = new X509Certificate2Collection();
            chain.Import(uploadedPfx, uploadedPassword, X509KeyStorageFlags.EphemeralKeySet);
            var extra = new X509Certificate2Collection(chain.Where(c => c.Thumbprint != uploaded!.Thumbprint).ToArray());
            _web = SslStreamCertificateContext.Create(uploaded!, extra, offline: true);
            _webUploaded = true;
        }
        else
        {
            _web = SslStreamCertificateContext.Create(Issue(names), new X509Certificate2Collection(keys.AgentCa), offline: true);
            _webUploaded = false;
        }
    }

    /// <summary>The names on generated certificates: the server's name, plus the names it has inside Docker.</summary>
    public static IReadOnlyList<string> Names(string? host)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(host)) names.Add(host.Trim());
        names.AddRange(["api", "localhost", Environment.MachineName.ToLowerInvariant(), "127.0.0.1"]);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private X509Certificate2 Issue(IReadOnlyList<string> names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={names[0]}, O=Lakeflow", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(keys.AgentCa, true, false));
        var now = time.GetUtcNow();
        var notBefore = now.AddDays(-1) < keys.AgentCa.NotBefore ? keys.AgentCa.NotBefore : now.AddDays(-1);
        var notAfter = now + GeneratedLifetime < keys.AgentCa.NotAfter ? now + GeneratedLifetime : new DateTimeOffset(keys.AgentCa.NotAfter);
        using var issued = request.Create(keys.AgentCa, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        // Through PKCS#12 so the key works for TLS on every platform.
        return new X509Certificate2(issued.CopyWithPrivateKey(key).Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>Opens an uploaded PFX: it must hold a server certificate with its private key that has not expired.</summary>
    public bool TryOpenPfx(byte[] pfx, string? password, out X509Certificate2? certificate, out string? error)
    {
        certificate = null;
        try
        {
            var all = new X509Certificate2Collection();
            all.Import(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
            certificate = all.FirstOrDefault(c => c.HasPrivateKey);
        }
        catch (CryptographicException ex)
        {
            error = $"The file could not be opened ({ex.Message}); check the password.";
            return false;
        }
        if (certificate is null)
        {
            error = "The file has no private key; export the certificate with its private key (PFX).";
            return false;
        }
        if (certificate.NotAfter.ToUniversalTime() < time.GetUtcNow().UtcDateTime)
        {
            error = $"The certificate expired on {certificate.NotAfter:yyyy-MM-dd}.";
            certificate = null;
            return false;
        }
        error = null;
        return true;
    }

    public static CertificateInfo Describe(X509Certificate2 certificate, bool uploaded)
    {
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames().Concat(e.EnumerateIPAddresses().Select(ip => ip.ToString()))).ToList();
        return new CertificateInfo(certificate.Subject, certificate.Issuer, certificate.NotAfter.ToUniversalTime(), certificate.Thumbprint, names, uploaded);
    }
}
