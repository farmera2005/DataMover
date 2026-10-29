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
