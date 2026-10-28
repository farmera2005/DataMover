using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Lakeflow.ControlApi.Security;

/// <summary>
/// Issues agent client certificates from certificate signing requests: the agent's private key never leaves the
/// agent. Subject CN is the agent id; client-authentication only; one year.
/// </summary>
public sealed class AgentCertificates(IKeyMaterial keys, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    public X509Certificate2 Ca => keys.AgentCa;

    public X509Certificate2 Issue(string csrPem, string agentId)
    {
        CertificateRequest request;
        try
        {
            request = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException("The certificate signing request is not valid.", ex);
        }
        using var publicKey = request.PublicKey.GetRSAPublicKey() ?? throw new ArgumentException("Agent keys must be RSA.");
        if (publicKey.KeySize < 2048) throw new ArgumentException("Agent keys must be at least 2048 bits.");

        // Only the public key is taken from the request; everything else is decided here.
        var issued = new CertificateRequest(new X500DistinguishedName($"CN={agentId}, O=Lakeflow Agent"), publicKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issued.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        issued.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        issued.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], true)); // client auth
        issued.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issued.PublicKey, false));
        issued.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Ca, true, false));

        var now = time.GetUtcNow();
        var notAfter = now + Lifetime;
        if (notAfter > Ca.NotAfter) notAfter = Ca.NotAfter;
        // Backdated a day, as CAs usually do, so clock differences never make a fresh certificate "not yet valid".
        var notBefore = now.AddDays(-1);
        if (notBefore < Ca.NotBefore) notBefore = Ca.NotBefore;
        return issued.Create(Ca, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
    }

    public static string Pem(X509Certificate2 certificate) => certificate.ExportCertificatePem();
}
