using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Lakeflow.Contracts;

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

public sealed class EventHubsOptions
{
    /// <summary>e.g. <c>lakeflow-prod.servicebus.windows.net</c>.</summary>
    public string Namespace { get; set; } = "";
    /// <summary>Hub name per division when it differs from the default (<c>grain</c>, <c>feed</c>, ...).</summary>
    public Dictionary<string, string> Hubs { get; set; } = [];

    public string HubName(Division division) => Hubs.GetValueOrDefault(LakeNames.HubName(division)) ?? LakeNames.HubName(division);
}
