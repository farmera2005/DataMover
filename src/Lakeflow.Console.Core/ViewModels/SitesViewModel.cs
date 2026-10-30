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
    [ObservableProperty] private string _collectorName = "collector-2";
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
            // A new site is read by the stack's own collector unless someone picks another.
            if (string.IsNullOrEmpty(SiteId) && CollectorId is null)
                CollectorId = Collectors.FirstOrDefault(c => c is { SiteId: null, Name: SetupViewModel.BuiltInCollector })?.AgentId;
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

/// <summary>
/// Import sites from CSV: <see cref="PreviewAsync"/> checks every row and saves nothing; <see cref="ImportAsync"/> saves
/// them all, or nothing if any row is wrong.
/// </summary>
public sealed partial class SiteImportViewModel(ConsoleApi api) : ObservableObject
{
    public const string Template = "site_id,name,time_zone,latitude,longitude,maintenance_start,maintenance_end,collector\n"
        + "stmarys,St. Marys Grain,America/New_York,40.54,-84.39,02:00,04:00,Built-in collector\n";

    [ObservableProperty] private string _csv = Template;
    [ObservableProperty] private SiteImportResult? _result;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public bool CanImport => Result is { Applied: false } r && r.Rows.Count > 0 && r.Rows.All(x => x.Action != "error") && r.Rows.Any(x => x.Action != "unchanged");

    partial void OnCsvChanged(string value) => Result = null; // preview again after an edit
    partial void OnResultChanged(SiteImportResult? value) => OnPropertyChanged(nameof(CanImport));

    [RelayCommand]
    public Task PreviewAsync(CancellationToken ct) => RunAsync(async () => Result = await api.ImportSitesAsync(Csv, apply: false, ct));

    [RelayCommand]
    public Task ImportAsync(CancellationToken ct) => RunAsync(async () => Result = await api.ImportSitesAsync(Csv, apply: true, ct));

    private async Task RunAsync(Func<Task> action)
    {
        (Error, IsBusy) = (null, true);
        try { await action(); }
        catch (Exception ex) when (ex is ConsoleApiException or HttpRequestException) { Error = ex.Message; }
        finally { IsBusy = false; }
    }
}
