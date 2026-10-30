using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core.ViewModels;

/// <summary>
/// Alerts, open (critical first) or cleared, for the whole fleet or one site. Operators acknowledge them (with an optional
/// note) and add notes; everyone else reads.
/// </summary>
public sealed partial class AlertsViewModel(ConsoleApi api, bool canOperate, string? siteId = null) : ObservableObject
{
    public ObservableCollection<AlertDto> Alerts { get; } = [];
    public bool CanOperate { get; } = canOperate;
    public string? SiteId { get; } = siteId;

    /// <summary>open, resolved or all.</summary>
    [ObservableProperty] private string _state = "open";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    /// <summary>The note being written, per alert.</summary>
    public Dictionary<Guid, string> Drafts { get; } = [];

    partial void OnStateChanged(string value) => _ = RefreshAsync(default);

    [RelayCommand]
    public Task RefreshAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var alerts = await api.GetAlertsAsync(State, SiteId, ct);
        Alerts.Clear();
        foreach (var alert in alerts) Alerts.Add(alert);
    });

    public Task AcknowledgeAsync(AlertDto alert, CancellationToken ct) => RunAsync(async () =>
        Replace(await api.AcknowledgeAlertAsync(alert.Id, TakeDraft(alert.Id), ct)));

    public Task AddNoteAsync(AlertDto alert, CancellationToken ct) => RunAsync(async () =>
    {
        if (!Drafts.TryGetValue(alert.Id, out var text) || string.IsNullOrWhiteSpace(text)) { Error = "Write a note first."; return; }
        Replace(await api.AddAlertNoteAsync(alert.Id, text, ct));
        Drafts.Remove(alert.Id);
    });

    private string? TakeDraft(Guid id) => Drafts.Remove(id, out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private void Replace(AlertDto updated)
    {
        var index = Alerts.ToList().FindIndex(a => a.Id == updated.Id);
        if (index >= 0) Alerts[index] = updated;
    }

    private async Task RunAsync(Func<Task> action)
    {
        (Error, IsBusy) = (null, true);
        try { await action(); }
        catch (Exception ex) when (ex is ConsoleApiException or HttpRequestException) { Error = ex.Message; }
        finally { IsBusy = false; }
    }
}
