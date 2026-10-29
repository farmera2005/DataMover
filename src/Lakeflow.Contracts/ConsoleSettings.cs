namespace Lakeflow.Contracts;

// Shapes for setting up Lakeflow in the browser: first-run setup, sign-in, users, and the settings under
// Configuration. Secrets go in (write-only) and never come back: responses say only whether one is set.

public enum ConsoleRole
{
    Viewer,
    Operator,
    Admin,
}

/// <param name="NeedsSetup">No user exists yet: the setup wizard creates the first administrator.</param>
/// <param name="WindowOpen">Setup is only accepted within an hour of the control API starting.</param>
public sealed record SetupStatus(bool NeedsSetup, bool WindowOpen, DateTimeOffset WindowClosesUtc);

public sealed record SetupRequest(string UserName, string Password, string? DisplayName);

public sealed record SignInRequest(string UserName, string Password);

public sealed record Me(string UserName, string? DisplayName, ConsoleRole Role, string AuthSource);

public sealed record UserDto(Guid Id, string UserName, string? DisplayName, string AuthSource, ConsoleRole Role, bool Disabled,
    DateTimeOffset CreatedUtc, DateTimeOffset? LastSignInUtc);

/// <summary>Creates or edits a local account. <paramref name="Password"/> is required for a new account, optional on edit.</summary>
public sealed record UserRequest(string UserName, string? DisplayName, ConsoleRole Role, bool Disabled, string? Password);

/// <summary>The Event Hub a division's changes go to.</summary>
/// <param name="HasKey">The Send policy's key is stored (it is never returned).</param>
public sealed record EventHubSettingsDto(Division Division, string Namespace, string HubName, string PolicyName, bool HasKey);

/// <summary>
/// Paste the connection string of a shared access policy on the hub with only <b>Send</b> (Azure portal: the hub, then
/// Shared access policies), e.g. <c>Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=lakeflow-send;SharedAccessKey=...;EntityPath=grain</c>.
/// </summary>
public sealed record EventHubSettingsRequest(string ConnectionString);

/// <summary>The silver lakehouse's SQL analytics endpoint, read with a service principal, for nightly reconciliation results.</summary>
public sealed record FabricSettingsDto(string Server, string Database, string ClientId, bool HasSecret);

/// <param name="ClientSecret">Leave empty to keep the stored secret.</param>
public sealed record FabricSettingsRequest(string Server, string Database, string ClientId, string? ClientSecret);

/// <summary>Directory (AD/LDAP) sign-in. Roles come from group membership at every sign-in.</summary>
public sealed record LdapSettingsDto
{
    public bool Enabled { get; init; }
    /// <summary><c>ldaps://dc1.corp.example:636</c>, or <c>ldap://dc1.corp.example:389</c> with <see cref="StartTls"/>.</summary>
    public string Url { get; init; } = "";
    public bool StartTls { get; init; }
    /// <summary>PEM of the CA that issued the directory servers' certificates, when it is your own (AD Certificate Services).</summary>
    public string? CaCertificatePem { get; init; }
    public string BindDn { get; init; } = "";
    public bool HasBindPassword { get; init; }
    public string UserBaseDn { get; init; } = "";
    /// <summary><c>{0}</c> is replaced by the escaped user name.</summary>
    public string UserFilter { get; init; } = "(&(objectClass=user)(sAMAccountName={0}))";
    public string? AdminGroupDn { get; init; }
    public string? OperatorGroupDn { get; init; }
    public string? ViewerGroupDn { get; init; }
}

/// <param name="BindPassword">Leave empty to keep the stored password. An empty password is never used (it would be an anonymous bind).</param>
public sealed record LdapSettingsRequest(LdapSettingsDto Settings, string? BindPassword);

/// <summary>Tries the settings (saved or as entered) by signing a user in, without saving or creating anything.</summary>
public sealed record LdapTestRequest(LdapSettingsDto Settings, string? BindPassword, string UserName, string Password);

public sealed record LdapTestResult(bool Succeeded, string Message, ConsoleRole? Role, IReadOnlyList<string> Groups);

/// <summary>How the stack is reached, and the certificate browsers and collectors see.</summary>
/// <param name="Host">The name (or address) users and collectors use for this server.</param>
public sealed record ServerSettingsDto(string Host, string ConsoleUrl, string CollectorUrl, CertificateInfo WebCertificate, CertificateInfo CollectorCertificate,
    string CaFingerprint);

public sealed record CertificateInfo(string Subject, string Issuer, DateTimeOffset NotAfter, string Thumbprint, IReadOnlyList<string> Names, bool Uploaded);

public sealed record ServerSettingsRequest(string Host);

/// <summary>A PFX (PKCS#12) with its private key, for the browser HTTPS port.</summary>
public sealed record CertificateUploadRequest(string PfxBase64, string? Password);

public sealed record SettingsTestResult(bool Succeeded, string Message);

/// <summary>
/// The Snowflake destination: where Lakeflow writes, and how it signs in (a key pair the server generates; the private
/// key is never returned, the public key and the setup script are).
/// </summary>
public sealed record SnowflakeSettingsDto
{
    public bool Enabled { get; init; }
    /// <summary><c>orgname-accountname</c>, or a locator with its region (<c>xy12345.east-us-2.azure</c>).</summary>
    public string Account { get; init; } = "";
    public string User { get; init; } = "LAKEFLOW_FORWARDER";
    public string Role { get; init; } = "LAKEFLOW_FORWARDER";
    public string Warehouse { get; init; } = "LAKEFLOW_WH";
    public string Database { get; init; } = "LAKEFLOW";
    /// <summary>How often silver is rebuilt from bronze (the warehouse runs only then).</summary>
    public int SilverIntervalMinutes { get; init; } = 5;
    /// <summary>Read only: for <c>ALTER USER ... SET RSA_PUBLIC_KEY</c>.</summary>
    public string? PublicKey { get; init; }
    public string? PublicKeyFingerprint { get; init; }
    /// <summary>Read only: the one-time script for a Snowflake administrator, with this key in it.</summary>
    public string? SetupScript { get; init; }
    public IReadOnlyList<SnowflakeHubDto> Hubs { get; init; } = [];
}

/// <param name="HubConfigured">The division's Event Hub is set up (Destinations), so there is something to read.</param>
/// <param name="HasListenKey">The hub's <c>snowflake-listen</c> connection string is stored.</param>
public sealed record SnowflakeHubDto(Division Division, bool HubConfigured, string? Namespace, string? HubName, bool HasListenKey);

public sealed record SnowflakeSettingsRequest(bool Enabled, string Account, string User, string Role, string Warehouse, string Database,
    int SilverIntervalMinutes);

public sealed record SnowflakePartitionDto(string Division, string PartitionId, long? Position, DateTimeOffset? LastEnqueuedUtc,
    DateTimeOffset? LastSentUtc, long RowsSent, int ConsecutiveFailures, string? LastError);

public sealed record SnowflakeStatusDto(bool Running, string? SetupError, IReadOnlyList<SnowflakePartitionDto> Partitions,
    DateTimeOffset? SilverLastRunUtc, DateTimeOffset? SilverLastSuccessUtc, long SilverLastRows, string? SilverLastError, DateTimeOffset? SilverNextRunUtc);
