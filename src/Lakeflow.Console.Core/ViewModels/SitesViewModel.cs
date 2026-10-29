using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core.ViewModels;

/// <summary>
/// Sites and collectors: add or edit a site and choose the collector that reads it; enroll a central collector (or an
/// agent at a site the centre cannot reach) with a one-time token.
/// </summary>
public sealed partial class SitesViewModel(ConsoleApi api) : ObservableObject
{
    public ObservableCollection<SiteDto> Sites { get; } = [];
    public ObservableCollection<AgentDto> Collectors { get; } = [];

    // Site form
    [ObservableProperty] private string _siteId = "";
    [ObservableProperty] private string _siteName = "";
    [ObservableProperty] private string _timeZone = "America/New_York";
    /// <summary>NaN when not given (as number inputs report an empty value).</summary>
    [ObservableProperty] private double _latitude = double.NaN;
    [ObservableProperty] private double _longitude = double.NaN;
    [ObservableProperty] private string? _collectorId;

    // Enrollment
    [ObservableProperty] private string _collectorName = "central-1";
    [ObservableProperty] private EnrollmentTokenResponse? _token;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _message;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            Replace(Sites, await api.GetSitesAsync(ct));
            Replace(Collectors, (await api.GetAgentsAsync(ct)).Where(a => !a.Revoked));
            Error = null;
        }
        catch (Exception ex) when (ex is ConsoleApiException or HttpRequestException)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Fills the form from an existing site, to edit it.</summary>
    public void Edit(SiteDto site)
    {
        (SiteId, SiteName, TimeZone, CollectorId) = (site.SiteId, site.Name, site.TimeZone, site.CollectorId);
        (Latitude, Longitude) = (site.Latitude ?? double.NaN, site.Longitude ?? double.NaN);
    }

    [RelayCommand]
    public async Task SaveSiteAsync(CancellationToken ct)
    {
        try
        {
            var existing = Sites.FirstOrDefault(s => s.SiteId == SiteId);
            var saved = await api.PutSiteAsync((existing ?? new SiteDto { SiteId = SiteId, Name = SiteName }) with
            {
                Name = SiteName, TimeZone = TimeZone, Latitude = Given(Latitude), Longitude = Given(Longitude),
                CollectorId = string.IsNullOrEmpty(CollectorId) ? null : CollectorId,
            }, ct);
            Message = existing is not null && existing.CollectorId != saved.CollectorId && saved.CollectorId is not null
                ? $"{saved.Name} now read by {saved.CollectorId}. Re-enter its source passwords: they were sealed to the previous collector."
                : $"{saved.Name} saved.";
            Error = null;
            await LoadAsync(ct);
        }
        catch (ConsoleApiException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>A one-time token (24 hours, shown once) for a central collector.</summary>
    [RelayCommand]
    public Task EnrollCollectorAsync(CancellationToken ct) => TokenAsync(new EnrollmentTokenRequest(null, CollectorName), ct);

    /// <summary>A one-time token for an agent installed at the site in the form (for a site the centre cannot reach).</summary>
    [RelayCommand]
    public Task EnrollSiteAgentAsync(CancellationToken ct) => TokenAsync(new EnrollmentTokenRequest(SiteId, null), ct);

    private async Task TokenAsync(EnrollmentTokenRequest request, CancellationToken ct)
    {
        try
        {
            Token = await api.CreateEnrollmentTokenAsync(request, ct);
            Error = null;
        }
        catch (ConsoleApiException ex)
        {
            Error = ex.Message;
        }
    }

    private static double? Given(double value) => double.IsNaN(value) ? null : value;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
