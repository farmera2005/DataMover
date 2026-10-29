using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Lakeflow.ControlApi.Security;

/// <summary>
/// Event Hubs shared access signatures for agents, signed with a hub's Send-only policy key, so an agent can send to
/// its division's hub and do nothing else. They are short-lived (24 hours): a revoked agent's access ends when its
/// last token expires.
/// </summary>
public static class SasTokens
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public static string Create(string fullyQualifiedNamespace, string hub, string policyName, byte[] key, DateTimeOffset expires)
    {
        var resource = WebUtility.UrlEncode($"https://{fullyQualifiedNamespace}/{hub}").ToLowerInvariant();
        var expiry = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(key);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{resource}\n{expiry}")));
        return $"SharedAccessSignature sr={resource}&sig={WebUtility.UrlEncode(signature)}&se={expiry}&skn={policyName}";
    }

    /// <summary>Checks a token's signature and expiry (tests, and diagnosing agent reports).</summary>
    public static bool IsValid(string token, string fullyQualifiedNamespace, string hub, byte[] key, DateTimeOffset now)
    {
        var parts = token["SharedAccessSignature ".Length..].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var resource = WebUtility.UrlEncode($"https://{fullyQualifiedNamespace}/{hub}").ToLowerInvariant();
        if (parts["sr"] != resource) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts["se"], CultureInfo.InvariantCulture)) <= now) return false;
        using var hmac = new HMACSHA256(key);
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{resource}\n{parts["se"]}")));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(WebUtility.UrlDecode(parts["sig"])));
    }
}

/// <summary>An Event Hubs connection string, as the Azure portal shows it for a shared access policy.</summary>
public sealed record EventHubConnectionString(string Namespace, string? HubName, string PolicyName, string Key)
{
    /// <summary>Parses <c>Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=..;SharedAccessKey=..;EntityPath=hub</c>.</summary>
    public static EventHubConnectionString? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);
        if (!parts.TryGetValue("Endpoint", out var endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "sb") return null;
        if (!parts.TryGetValue("SharedAccessKeyName", out var policy) || !parts.TryGetValue("SharedAccessKey", out var key)) return null;
        if (string.IsNullOrEmpty(policy) || string.IsNullOrEmpty(key)) return null;
        return new EventHubConnectionString(uri.Host, parts.GetValueOrDefault("EntityPath") is { Length: > 0 } hub ? hub : null, policy, key);
    }
}
