using System.Text;
using System.Text.Json;

namespace Lakeflow.Console.Core;

/// <summary>Gets Entra ID access tokens for the control API (MSAL in the app, a test issuer in tests).</summary>
public interface IAccessTokenProvider
{
    /// <summary>A current token, signing in interactively if needed (Entra applies MFA through Conditional Access).</summary>
    Task<string> GetTokenAsync(CancellationToken ct);

    Task SignOutAsync();
}

public enum ConsoleRole { None, Viewer, Operator, Admin }

/// <summary>Who is signed in, read from the access token (the API validates it; this only shapes the UI).</summary>
public sealed record SignedInUser(string Name, IReadOnlyList<string> Roles)
{
    public ConsoleRole Role => Roles.Contains("Lakeflow.Admin") ? ConsoleRole.Admin
        : Roles.Contains("Lakeflow.Operator") ? ConsoleRole.Operator
        : Roles.Contains("Lakeflow.Viewer") ? ConsoleRole.Viewer
        : ConsoleRole.None;

    public bool CanOperate => Role >= ConsoleRole.Operator;
    public bool CanAdminister => Role == ConsoleRole.Admin;

    public static SignedInUser FromToken(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) throw new FormatException("Not a JWT.");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var root = json.RootElement;
        string Claim(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var roles = root.TryGetProperty("roles", out var r)
            ? r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.GetString()!).ToList() : [r.GetString()!]
            : new List<string>();
        var name = Claim("preferred_username");
        return new SignedInUser(name.Length > 0 ? name : Claim("name"), roles);
    }
}
