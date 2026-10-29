using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core.ViewModels;

/// <summary>Add a site and generate the one-time enrollment token and install command for its agent.</summary>
public sealed partial class EnrollmentViewModel(ConsoleApi api) : ObservableObject
{
    [ObservableProperty] private string _siteId = "";
    [ObservableProperty] private string _siteName = "";
    [ObservableProperty] private string _timeZone = "America/New_York";
    /// <summary>NaN when not given (as number inputs report an empty value).</summary>
    [ObservableProperty] private double _latitude = double.NaN;
    [ObservableProperty] private double _longitude = double.NaN;
    [ObservableProperty] private EnrollmentTokenResponse? _token;
    [ObservableProperty] private string? _error;

    [RelayCommand]
    public async Task SaveSiteAsync(CancellationToken ct)
    {
        try
        {
            await api.PutSiteAsync(new SiteDto { SiteId = SiteId, Name = SiteName, TimeZone = TimeZone, Latitude = Given(Latitude), Longitude = Given(Longitude) }, ct);
            Error = null;
        }
        catch (ConsoleApiException ex)
        {
            Error = ex.Message;
        }
    }

    private static double? Given(double value) => double.IsNaN(value) ? null : value;

    /// <summary>The token is shown once; it works for 24 hours, for one agent.</summary>
    [RelayCommand]
    public async Task GenerateTokenAsync(CancellationToken ct)
    {
        try
        {
            Token = await api.CreateEnrollmentTokenAsync(SiteId, ct);
            Error = null;
        }
        catch (ConsoleApiException ex)
        {
            Error = ex.Message;
        }
    }
}
