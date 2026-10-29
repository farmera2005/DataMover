using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core.ViewModels;

/// <summary>One site in the fleet overview.</summary>
public sealed record SiteRow(SiteStatus Status, DateTimeOffset Now)
{
    public string SiteId => Status.SiteId;
    public string Name => Status.Name;
    public Health Health => Status.Health;
    public string HealthText => Format.Health(Status.Health);
    public string Reason => Status.Reasons.FirstOrDefault() ?? "All tables streaming.";
    public int MoreReasons => Math.Max(0, Status.Reasons.Count - 1);
    public string WorstLag => Format.Lag(Status.WorstLagSeconds);
    public string Buffer => Status.BufferPercent is null ? "—" : $"{Format.Bytes(Status.BufferBytes)} ({Status.BufferPercent:F0}%)";
    public string LastHeartbeat => Format.Ago(Status.LastHeartbeatUtc, Now);
    public string AgentVersion => Status.AgentVersion ?? "—";
    public double? Latitude => Status.Latitude;
    public double? Longitude => Status.Longitude;
}

/// <summary>Fleet overview: every site green/amber/red with worst lag, buffer depth and last heartbeat.</summary>
public sealed partial class FleetViewModel(ConsoleApi api, TimeProvider? time = null) : ObservableObject
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IReadOnlyList<SiteRow> _all = [];

    public ObservableCollection<SiteRow> Sites { get; } = [];

    [ObservableProperty] private int _green;
    [ObservableProperty] private int _amber;
    [ObservableProperty] private int _red;
    [ObservableProperty] private DateTimeOffset? _asOf;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _filter = "";
    /// <summary>Show only sites in this state; null for all.</summary>
    [ObservableProperty] private Health? _healthFilter;

    partial void OnFilterChanged(string value) => Apply();
    partial void OnHealthFilterChanged(Health? value) => Apply();

    /// <summary>Called on open and every 30 s by the page.</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var fleet = await api.GetStatusAsync(ct);
            var now = _time.GetUtcNow();
            // Worst first, so problems are at the top.
            _all = fleet.Sites.Select(s => new SiteRow(s, now))
                .OrderByDescending(r => r.Health).ThenByDescending(r => r.Status.WorstLagSeconds ?? 0).ThenBy(r => r.Name).ToList();
            (Green, Amber, Red, AsOf) = (fleet.Green, fleet.Amber, fleet.Red, fleet.AsOfUtc);
            Error = null;
            Apply();
        }
        catch (Exception ex) when (ex is ConsoleApiException or HttpRequestException)
        {
            Error = ex.Message; // keep showing the last good view
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply()
    {
        Sites.Clear();
        foreach (var row in _all.Where(r => (HealthFilter is null || r.Health == HealthFilter)
                     && (Filter.Length == 0 || r.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                         || r.SiteId.Contains(Filter, StringComparison.OrdinalIgnoreCase))))
            Sites.Add(row);
    }
}
