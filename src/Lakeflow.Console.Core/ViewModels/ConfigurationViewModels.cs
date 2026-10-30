using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core.ViewModels;

/// <summary>Error, message and busy state shared by the Configuration pages.</summary>
public abstract partial class FormViewModel : ObservableObject
{
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;

    protected async Task RunAsync(Func<Task> action, string? done = null)
    {
        Error = null;
        Message = null;
        IsBusy = true;
        try
        {
            await action();
            if (done is not null) Message = done;
        }
        catch (Exception ex) when (ex is ConsoleApiException or HttpRequestException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}

/// <summary>Users: local accounts (created here) and directory users (created at their first sign-in; role from their groups).</summary>
public sealed partial class UsersViewModel(ConsoleApi api) : FormViewModel
{
    public ObservableCollection<UserDto> Users { get; } = [];

    [ObservableProperty] private Guid? _editingId;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private ConsoleRole _role = ConsoleRole.Viewer;
    [ObservableProperty] private bool _disabled;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isDirectoryUser;

    [RelayCommand]
    public Task LoadAsync(CancellationToken ct) => RunAsync(async () => Replace(Users, await api.GetUsersAsync(ct)));

    public void New() => (EditingId, UserName, DisplayName, Role, Disabled, Password, IsDirectoryUser) = (null, "", null, ConsoleRole.Viewer, false, "", false);

    public void Edit(UserDto user) =>
        (EditingId, UserName, DisplayName, Role, Disabled, Password, IsDirectoryUser) = (user.Id, user.UserName, user.DisplayName, user.Role, user.Disabled, "", user.AuthSource == "ldap");

    [RelayCommand]
    public Task SaveAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var request = new UserRequest(UserName.Trim(), DisplayName, Role, Disabled, string.IsNullOrEmpty(Password) ? null : Password);
        var saved = EditingId is { } id ? await api.UpdateUserAsync(id, request, ct) : await api.CreateUserAsync(request, ct);
        Password = "";
        Replace(Users, await api.GetUsersAsync(ct));
        Edit(saved);
        Message = $"{saved.UserName} saved.";
    });

    [RelayCommand]
    public Task DeleteAsync(CancellationToken ct) => RunAsync(async () =>
    {
        if (EditingId is not { } id) return;
        await api.DeleteUserAsync(id, ct);
        Message = $"{UserName} deleted.";
        New();
        Replace(Users, await api.GetUsersAsync(ct));
    });
}

/// <summary>Destinations: each division's Event Hub (where collectors send changes) and the Fabric SQL endpoint (reconciliation).</summary>
public sealed partial class DestinationsViewModel(ConsoleApi api) : FormViewModel
{
    public ObservableCollection<EventHubSettingsDto> Hubs { get; } = [];
    public IReadOnlyList<Division> Divisions { get; } = Enum.GetValues<Division>();

    [ObservableProperty] private Division _division = Division.Grain;
    [ObservableProperty] private string _connectionString = "";

    [ObservableProperty] private string _fabricServer = "";
    [ObservableProperty] private string _fabricDatabase = "";
    [ObservableProperty] private string _fabricClientId = "";
    [ObservableProperty] private string _fabricClientSecret = "";
    [ObservableProperty] private bool _fabricHasSecret;
    [ObservableProperty] private SettingsTestResult? _fabricTest;

    [RelayCommand]
    public Task LoadAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Replace(Hubs, await api.GetEventHubsAsync(ct));
        if (await api.GetFabricAsync(ct) is { } fabric)
            (FabricServer, FabricDatabase, FabricClientId, FabricHasSecret) = (fabric.Server, fabric.Database, fabric.ClientId, fabric.HasSecret);
    });

    [RelayCommand]
    public Task SaveHubAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var saved = await api.PutEventHubAsync(Division, ConnectionString.Trim(), ct);
        ConnectionString = "";
        Replace(Hubs, await api.GetEventHubsAsync(ct));
        Message = $"{saved.Division}: changes go to {saved.Namespace}/{saved.HubName}. Collectors pick this up within a minute.";
    });

    public Task RemoveHubAsync(Division division, CancellationToken ct) => RunAsync(async () =>
    {
        await api.DeleteEventHubAsync(division, ct);
        Replace(Hubs, await api.GetEventHubsAsync(ct));
    }, $"{division} hub removed; that division's sites stop sending until a hub is set again.");

    [RelayCommand]
    public Task SaveFabricAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var saved = await api.PutFabricAsync(new FabricSettingsRequest(FabricServer, FabricDatabase, FabricClientId,
            string.IsNullOrEmpty(FabricClientSecret) ? null : FabricClientSecret), ct);
        FabricClientSecret = "";
        FabricHasSecret = saved.HasSecret;
    }, "Fabric settings saved.");

    [RelayCommand]
    public Task TestFabricAsync(CancellationToken ct) => RunAsync(async () => FabricTest = await api.TestFabricAsync(ct));
}

/// <summary>
/// Snowflake: the account, the key pair the server generates (the setup script carries its public half), each hub's
/// snowflake-listen key, a connection test, and what the forwarder and the silver build are doing.
/// </summary>
public sealed partial class SnowflakeViewModel(ConsoleApi api) : FormViewModel
{
    public IReadOnlyList<Division> Divisions { get; } = Enum.GetValues<Division>();

    [ObservableProperty] private SnowflakeSettingsDto _settings = new();
    [ObservableProperty] private Division _division = Division.Grain;
    [ObservableProperty] private string _connectionString = "";
    [ObservableProperty] private SettingsTestResult? _testResult;
    [ObservableProperty] private SnowflakeStatusDto? _status;

    [RelayCommand]
    public Task LoadAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Settings = await api.GetSnowflakeAsync(ct);
        Status = await api.GetSnowflakeStatusAsync(ct);
    });

    [RelayCommand]
    public Task RefreshStatusAsync(CancellationToken ct) => RunAsync(async () => Status = await api.GetSnowflakeStatusAsync(ct));

    [RelayCommand]
    public Task SaveAsync(CancellationToken ct) => RunAsync(async () => Settings = await api.PutSnowflakeAsync(Request(Settings.Enabled), ct),
        "Snowflake settings saved.");

    public Task SetEnabledAsync(bool enabled, CancellationToken ct) => RunAsync(async () =>
        Settings = await api.PutSnowflakeAsync(Request(enabled), ct),
        enabled ? "Forwarding to Snowflake is on. The first rows arrive within a minute; silver follows on its schedule." : "Forwarding to Snowflake is off.");

    [RelayCommand]
    public Task GenerateKeyAsync(CancellationToken ct) => RunAsync(async () => Settings = await api.GenerateSnowflakeKeyAsync(ct),
        "New key pair generated. Run the setup script (or its ALTER USER line) in Snowflake so the new key is accepted.");

    [RelayCommand]
    public Task SaveHubAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Settings = await api.PutSnowflakeHubAsync(Division, ConnectionString.Trim(), ct);
        ConnectionString = "";
    }, $"{Division}: the forwarder reads this hub with the snowflake consumer group.");

    public Task RemoveHubAsync(Division division, CancellationToken ct) => RunAsync(async () =>
    {
        await api.DeleteSnowflakeHubAsync(division, ct);
        Settings = await api.GetSnowflakeAsync(ct);
    }, $"{division} is no longer forwarded to Snowflake.");

    [RelayCommand]
    public Task TestAsync(CancellationToken ct) => RunAsync(async () => TestResult = await api.TestSnowflakeAsync(ct));

    private SnowflakeSettingsRequest Request(bool enabled) =>
        new(enabled, Settings.Account, Settings.User, Settings.Role, Settings.Warehouse, Settings.Database, Settings.SilverIntervalMinutes);
}

/// <summary>
/// Notifications: alert email through Microsoft Graph, a Teams channel through a Workflows webhook, quiet hours and
/// harvest seasons, with a test for each channel and how each is doing.
/// </summary>
public sealed partial class NotificationsViewModel(ConsoleApi api) : FormViewModel
{
    public IReadOnlyList<AlertSeverity> Severities { get; } = Enum.GetValues<AlertSeverity>();

    [ObservableProperty] private EmailNotificationDto _email = new();
    /// <summary>One address per line (or separated by commas or semicolons).</summary>
    [ObservableProperty] private string _recipients = "";
    [ObservableProperty] private string _clientSecret = "";
    [ObservableProperty] private string _testTo = "";
    [ObservableProperty] private SettingsTestResult? _emailTest;

    [ObservableProperty] private TeamsNotificationDto _teams = new();
    [ObservableProperty] private string _webhookUrl = "";
    [ObservableProperty] private SettingsTestResult? _teamsTest;

    [ObservableProperty] private NotificationScheduleDto _schedule = new();
    public ObservableCollection<HarvestSeasonDto> Seasons { get; } = [];
    [ObservableProperty] private IReadOnlyList<NotificationChannelStatus> _status = [];

    [RelayCommand]
    public Task LoadAsync(CancellationToken ct) => RunAsync(async () => Show(await api.GetNotificationsAsync(ct)));

    [RelayCommand]
    public Task SaveEmailAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Show(await api.PutEmailNotificationsAsync(new EmailNotificationRequest(Email.Enabled, Email.TenantId, Email.ClientId,
            string.IsNullOrEmpty(ClientSecret) ? null : ClientSecret, Email.Sender, SplitRecipients(Recipients), Email.MinimumSeverity), ct));
        ClientSecret = "";
    }, "Email settings saved.");

    [RelayCommand]
    public Task TestEmailAsync(CancellationToken ct) => RunAsync(async () =>
        EmailTest = await api.TestEmailNotificationAsync(string.IsNullOrWhiteSpace(TestTo) ? null : TestTo.Trim(), ct));

    [RelayCommand]
    public Task SaveTeamsAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Show(await api.PutTeamsNotificationsAsync(new TeamsNotificationRequest(Teams.Enabled,
            string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim(), Teams.MinimumSeverity), ct));
        WebhookUrl = "";
    }, "Teams settings saved.");

    [RelayCommand]
    public Task TestTeamsAsync(CancellationToken ct) => RunAsync(async () => TeamsTest = await api.TestTeamsNotificationAsync(ct));

    public void AddSeason() => Seasons.Add(new HarvestSeasonDto("Fall harvest", "09-15", "11-30"));

    public void SetSeason(int index, HarvestSeasonDto season) => Seasons[index] = season;

    public void RemoveSeason(int index) => Seasons.RemoveAt(index);

    [RelayCommand]
    public Task SaveScheduleAsync(CancellationToken ct) => RunAsync(async () =>
        Show(await api.PutNotificationScheduleAsync(Schedule with { HarvestSeasons = [.. Seasons] }, ct)), "Quiet hours saved.");

    public static List<string> SplitRecipients(string text) =>
        text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void Show(NotificationSettingsDto settings)
    {
        (Email, Teams, Schedule, Status) = (settings.Email, settings.Teams, settings.Schedule, settings.Status);
        Recipients = string.Join("\n", settings.Email.Recipients);
        Replace(Seasons, settings.Schedule.HarvestSeasons);
    }
}

/// <summary>Sign-in: directory (AD/LDAP) sign-in, with roles from groups, tested with a real user before it is turned on.</summary>
public sealed partial class SignInSettingsViewModel(ConsoleApi api) : FormViewModel
{
    [ObservableProperty] private LdapSettingsDto _settings = new();
    [ObservableProperty] private string _bindPassword = "";
    [ObservableProperty] private string _testUserName = "";
    [ObservableProperty] private string _testPassword = "";
    [ObservableProperty] private LdapTestResult? _testResult;

    [RelayCommand]
    public Task LoadAsync(CancellationToken ct) => RunAsync(async () => Settings = await api.GetLdapAsync(ct));

    [RelayCommand]
    public Task SaveAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Settings = await api.PutLdapAsync(new LdapSettingsRequest(Settings, string.IsNullOrEmpty(BindPassword) ? null : BindPassword), ct);
        BindPassword = "";
    }, "Sign-in settings saved.");

    [RelayCommand]
    public Task TestAsync(CancellationToken ct) => RunAsync(async () =>
    {
        TestResult = await api.TestLdapAsync(new LdapTestRequest(Settings, string.IsNullOrEmpty(BindPassword) ? null : BindPassword, TestUserName, TestPassword), ct);
        TestPassword = "";
    });
}

/// <summary>The server's name (in links, install files and certificates) and the certificate browsers see.</summary>
public sealed partial class ServerSettingsViewModel(ConsoleApi api) : FormViewModel
{
    [ObservableProperty] private ServerSettingsDto? _server;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _certificatePassword = "";

    [RelayCommand]
    public Task LoadAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Server = await api.GetServerAsync(ct);
        Host = Server.Host;
    });

    [RelayCommand]
    public Task SaveHostAsync(CancellationToken ct) => RunAsync(async () => Server = await api.PutServerAsync(new ServerSettingsRequest(Host.Trim()), ct),
        "Saved. New certificates were issued for that name.");

    public Task UploadCertificateAsync(byte[] pfx, CancellationToken ct) => RunAsync(async () =>
    {
        Server = await api.UploadCertificateAsync(new CertificateUploadRequest(Convert.ToBase64String(pfx), string.IsNullOrEmpty(CertificatePassword) ? null : CertificatePassword), ct);
        CertificatePassword = "";
    }, "Certificate installed; new HTTPS connections use it.");

    [RelayCommand]
    public Task RemoveCertificateAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await api.DeleteCertificateAsync(ct);
        Server = await api.GetServerAsync(ct);
    }, "Back to the certificate from the Lakeflow CA.");
}
