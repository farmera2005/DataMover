using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lakeflow.Snowflake;

/// <summary>Where and as whom Lakeflow writes in Snowflake (all entered in the console).</summary>
public sealed record SnowflakeOptions
{
    /// <summary>
    /// The account identifier: <c>orgname-accountname</c> (Snowsight: account menu, "Copy account identifier", with the
    /// dot replaced by a hyphen), or a legacy locator with its region, e.g. <c>xy12345.east-us-2.azure</c>.
    /// </summary>
    public required string Account { get; init; }
    public string User { get; init; } = "LAKEFLOW_FORWARDER";
    public string Role { get; init; } = "LAKEFLOW_FORWARDER";
    public string Warehouse { get; init; } = "LAKEFLOW_WH";
    public string Database { get; init; } = "LAKEFLOW";

    /// <summary><c>https://&lt;account&gt;.snowflakecomputing.com/</c> (underscores are not valid in host names).</summary>
    public Uri AccountUrl => new($"https://{Account.Trim().ToLowerInvariant().Replace('_', '-')}.snowflakecomputing.com/");

    /// <summary>The account as key-pair JWTs name it: upper case, and for a locator only the part before the region.</summary>
    public string JwtAccount
    {
        get
        {
            var account = Account.Trim().ToUpperInvariant();
            var dot = account.IndexOf('.');
            return dot > 0 ? account[..dot] : account;
        }
    }

    public static bool IsValidIdentifier(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$');
}

/// <summary>
/// The key pair Lakeflow signs in to Snowflake with. The control API generates it; the private key never leaves the
/// server (encrypted with its master key at rest); the public key goes into Snowflake with <c>ALTER USER ... SET
/// RSA_PUBLIC_KEY</c> (the console's setup script).
/// </summary>
public sealed class KeyPair : IDisposable
{
    private readonly RSA _rsa = RSA.Create();

    public KeyPair(string privateKeyPem) => _rsa.ImportFromPem(privateKeyPem);

    public static string GeneratePrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>The public key as Snowflake's RSA_PUBLIC_KEY takes it: base64 SubjectPublicKeyInfo, no PEM lines.</summary>
    public string PublicKey => Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());

    /// <summary>What Snowflake shows as RSA_PUBLIC_KEY_FP (DESC USER), and what the JWT names.</summary>
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(_rsa.ExportSubjectPublicKeyInfo()));

    /// <summary>A key-pair JWT for the account and user, valid for 59 minutes (Snowflake accepts at most an hour).</summary>
    public string CreateJwt(SnowflakeOptions options, DateTimeOffset now)
    {
        var subject = $"{options.JwtAccount}.{options.User.Trim().ToUpperInvariant()}";
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = $"{subject}.{Fingerprint}",
            ["sub"] = subject,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(59).ToUnixTimeSeconds(),
        }));
        var signature = _rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _rsa.Dispose();
}

/// <summary>A Snowflake API call failed; <see cref="Retryable"/> tells transient failures from refusals.</summary>
public sealed class SnowflakeException(string message, int status, bool retryable) : Exception(message)
{
    public int Status { get; } = status;
    public bool Retryable { get; } = retryable;
}

/// <summary>Key-pair JWT, cached until shortly before it expires.</summary>
public sealed class SnowflakeAuth(SnowflakeOptions options, KeyPair key, TimeProvider time)
{
    private string? _jwt;
    private DateTimeOffset _jwtExpires;
    private readonly object _gate = new();

    public SnowflakeOptions Options => options;

    public string Jwt()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_jwt is null || now >= _jwtExpires)
            {
                _jwt = key.CreateJwt(options, now);
                _jwtExpires = now.AddMinutes(50);
            }
            return _jwt;
        }
    }

    public void Authorize(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt());
        request.Headers.TryAddWithoutValidation("X-Snowflake-Authorization-Token-Type", "KEYPAIR_JWT");
    }

    internal static async Task ThrowAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var message = body;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString()!;
        }
        catch (JsonException)
        {
        }
        if (message.Length > 500) message = message[..500];
        var status = (int)response.StatusCode;
        throw new SnowflakeException($"{what}: {status} {message}", status, status is 408 or 429 or >= 500);
    }
}
