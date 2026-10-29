using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core.ViewModels;

public enum SetupStep { Administrator, Destinations, FirstSite, Done }

/// <summary>
/// The first-run wizard: the administrator account (a local account that always signs in), where changes go (each
/// division's Event Hub; can be skipped), and the first site (read by the built-in collector). Sources are then added
/// with the source wizard, from the site's page.
/// </summary>
public sealed partial class SetupViewModel(ConsoleApi api) : ObservableObject
{
    public const string BuiltInCollector = "Built-in collector";

    [ObservableProperty] private SetupStep _step = SetupStep.Administrator;
    [ObservableProperty] private string _userName = "admin";
    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirmPassword = "";

    [ObservableProperty] private Division _division = Division.Grain;
    [ObservableProperty] private string _connectionString = "";
    [ObservableProperty] private List<EventHubSettingsDto> _hubs = [];

    [ObservableProperty] private string _siteId = "";
    [ObservableProperty] private string _siteName = "";
    [ObservableProperty] private string _timeZone = "America/New_York";
    [ObservableProperty] private string? _createdSiteId;

    [ObservableProperty] private Me? _administrator;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task CreateAdministratorAsync(CancellationToken ct)
    {
        Error = null;
        if (Password != ConfirmPassword)
        {
            Error = "The two passwords are not the same.";
            return;
        }
        await RunAsync(async () =>
        {
            // Signs the administrator in (session cookie); the rest of the wizard runs as them.
            Administrator = await api.SetupAsync(new SetupRequest(UserName.Trim(), Password, DisplayName), ct);
            Password = ConfirmPassword = "";
            Step = SetupStep.Destinations;
        });
    }

    [RelayCommand]
    public Task SaveHubAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var saved = await api.PutEventHubAsync(Division, ConnectionString.Trim(), ct);
        ConnectionString = "";
        Hubs = await api.GetEventHubsAsync(ct);
        Message = $"{saved.Division}: {saved.Namespace}/{saved.HubName} saved.";
    });

    [RelayCommand]
    public void Next() => Step = Step switch
    {
        SetupStep.Administrator => SetupStep.Destinations,
        SetupStep.Destinations => SetupStep.FirstSite,
        _ => SetupStep.Done,
    };

    /// <summary>Creates the site, read by the built-in collector once it has enrolled (it does so on its own).</summary>
    [RelayCommand]
    public Task CreateSiteAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var collector = (await api.GetAgentsAsync(ct)).FirstOrDefault(a => a is { Revoked: false, SiteId: null, Name: BuiltInCollector });
        var site = await api.PutSiteAsync(new SiteDto
        {
            SiteId = SiteId.Trim().ToLowerInvariant(), Name = SiteName.Trim(), TimeZone = TimeZone, CollectorId = collector?.AgentId,
        }, ct);
        CreatedSiteId = site.SiteId;
        Message = collector is null
            ? $"{site.Name} saved. The built-in collector has not checked in yet; choose it for the site under Configuration, then Sites and collectors, once it shows there."
            : $"{site.Name} saved, read by the built-in collector.";
        Step = SetupStep.Done;
    });

    private async Task RunAsync(Func<Task> action)
    {
        Error = null;
        IsBusy = true;
        try
        {
            await action();
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
}
