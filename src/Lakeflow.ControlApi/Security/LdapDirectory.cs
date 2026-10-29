using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Lakeflow.Contracts;
using Novell.Directory.Ldap;

namespace Lakeflow.ControlApi.Security;

/// <summary>
/// Signs directory users in against AD (or any LDAP server): binds with the service account, finds the user, checks
/// their password by binding as them, and maps their groups to a role. Only TLS connections (LDAPS or StartTLS), the
/// server certificate is checked (against the CA given in the settings, or the system's), and an empty password is
/// never sent (it would be an anonymous bind that "succeeds").
/// </summary>
public sealed class LdapDirectory(ILogger<LdapDirectory> log)
{
    /// <summary>AD's LDAP_MATCHING_RULE_IN_CHAIN: membership through nested groups.</summary>
    private const string InChain = "1.2.840.113556.1.4.1941";
    private const int TimeoutMs = 5000;

    public sealed record Result(bool Succeeded, string Message, ConsoleRole? Role, string? DisplayName, IReadOnlyList<string> Groups)
    {
        public static Result Fail(string message) => new(false, message, null, null, []);
    }

    public Task<Result> AuthenticateAsync(LdapSettingsDto settings, string bindPassword, string userName, string password, CancellationToken ct) =>
        Task.Run(() => Authenticate(settings, bindPassword, userName, password), ct);

    private Result Authenticate(LdapSettingsDto settings, string bindPassword, string userName, string password)
    {
        if (string.IsNullOrEmpty(password)) return Result.Fail("Enter your password.");
        if (string.IsNullOrEmpty(bindPassword)) return Result.Fail("The directory service account has no password set.");
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var url) || url.Scheme is not ("ldap" or "ldaps"))
            return Result.Fail("The directory URL must look like ldaps://dc1.example.local:636.");
        var ldaps = url.Scheme == "ldaps";
        if (!ldaps && !settings.StartTls) return Result.Fail("Use ldaps:// or turn on StartTLS: passwords are never sent unencrypted.");
        var account = AccountName(userName);
        if (account.Length == 0) return Result.Fail("Enter your user name.");

        var options = new LdapConnectionOptions().ConfigureRemoteCertificateValidationCallback(CertificateCheck(settings.CaCertificatePem));
        if (ldaps) options = options.UseSsl();
        using var connection = new LdapConnection(options) { ConnectionTimeout = TimeoutMs };
        try
        {
            connection.Connect(url.Host, url.Port > 0 ? url.Port : ldaps ? 636 : 389);
            if (!ldaps) connection.StartTls();
        }
        catch (Exception ex) when (ex is LdapException or System.Net.Sockets.SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            log.LogWarning("Directory server {Host} unreachable: {Error}", url.Host, ex.Message);
            return Result.Fail($"Could not reach the directory server securely ({ex.Message}). Check the URL, the port, and the CA certificate.");
        }

        try
        {
            connection.Bind(settings.BindDn, bindPassword);
        }
        catch (LdapException ex)
        {
            log.LogWarning("Directory service account bind failed: {Error}", ex.ResultCode);
            return Result.Fail("The directory did not accept the service account (bind DN or password).");
        }

        LdapEntry? entry = null;
        var constraints = new LdapSearchConstraints { TimeLimit = TimeoutMs, MaxResults = 2 };
        var search = connection.Search(settings.UserBaseDn, LdapConnection.ScopeSub, string.Format(settings.UserFilter, Escape(account)),
            ["distinguishedName", "displayName", "memberOf", "cn"], false, constraints);
        var found = 0;
        while (search.HasMore())
        {
            try
            {
                var next = search.Next();
                found++;
                entry ??= next;
            }
            catch (LdapReferralException)
            {
                // Referrals to other domains are not followed.
            }
        }
        if (found == 0 || entry is null) return Result.Fail("User name or password is not correct.");
        if (found > 1) return Result.Fail("More than one directory entry matches that user name; narrow the user filter.");

        try
        {
            connection.Bind(entry.Dn, password);
        }
        catch (LdapException)
        {
            return Result.Fail("User name or password is not correct.");
        }

        var memberOf = Values(entry, "memberOf");
        var displayName = Values(entry, "displayName").FirstOrDefault() ?? Values(entry, "cn").FirstOrDefault();
        bool Member(string? group) => !string.IsNullOrWhiteSpace(group)
            && (memberOf.Contains(group.Trim(), StringComparer.OrdinalIgnoreCase) || NestedMember(connection, entry.Dn, group.Trim()));
        ConsoleRole? role = Member(settings.AdminGroupDn) ? ConsoleRole.Admin
            : Member(settings.OperatorGroupDn) ? ConsoleRole.Operator
            : Member(settings.ViewerGroupDn) ? ConsoleRole.Viewer
            : null;
        return role is null
            ? new Result(false, "You are not in any of the Lakeflow groups.", null, displayName, memberOf)
            : new Result(true, $"Signed in as {displayName ?? account} ({role}).", role, displayName, memberOf);
    }

    /// <summary>AD only: is the user in the group through nested groups? Other servers reject the rule; that is a no.</summary>
    private static bool NestedMember(LdapConnection connection, string userDn, string groupDn)
    {
        try
        {
            var results = connection.Search(userDn, LdapConnection.ScopeBase, $"(memberOf:{InChain}:={Escape(groupDn)})", ["cn"], false,
                new LdapSearchConstraints { TimeLimit = TimeoutMs, MaxResults = 1 });
            while (results.HasMore())
            {
                results.Next();
                return true;
            }
        }
        catch (LdapException)
        {
        }
        return false;
    }

    private static List<string> Values(LdapEntry entry, string attribute)
    {
        try
        {
            return entry.GetAttribute(attribute)?.StringValueArray.ToList() ?? [];
        }
        catch (KeyNotFoundException)
        {
            return [];
        }
    }

    /// <summary>DOMAIN\user and user@domain both mean the account name for the user filter.</summary>
    public static string AccountName(string userName)
    {
        var name = userName.Trim();
        if (name.Contains('\\')) name = name[(name.LastIndexOf('\\') + 1)..];
        return name;
    }

    /// <summary>RFC 4515 escaping, so a user name cannot change the filter.</summary>
    public static string Escape(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => c.ToString(),
            });
        }
        return text.ToString();
    }

    /// <summary>Trusts the CA from the settings (your own AD CA), else the system's roots; the name must always match.</summary>
    public static System.Net.Security.RemoteCertificateValidationCallback CertificateCheck(string? caPem) => (_, certificate, _, errors) =>
    {
        if (certificate is null) return false;
        if (string.IsNullOrWhiteSpace(caPem)) return errors == SslPolicyErrors.None;
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
        using var ca = X509Certificate2.CreateFromPem(caPem);
        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    };
}
