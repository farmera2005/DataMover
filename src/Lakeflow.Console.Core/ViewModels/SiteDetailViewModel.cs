using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core.ViewModels;

/// <summary>One captured table on the site detail page.</summary>
public sealed record TableRow(TableStatus Status, ReconciliationResult? Reconciliation, DateTimeOffset Now)
{
    public string SourceId => Status.SourceId;
    public string Table => Status.Table;
    public string State => Status.State.ToString();
    public string Method => Format.Method(Status.Method);
    public string Lag => Format.Lag(Status.LagSeconds);
    public string RowsPerMinute => $"{Status.RowsPerMinute:F0}";
    public string LastCapture => Format.Ago(Status.LastCaptureUtc, Now);
    public string? Error => Status.LastError;
    public string ReconciliationText => Reconciliation is null ? "—"
        : Reconciliation.Status == "ok" ? $"OK ({Reconciliation.SourceRows:N0} rows)"
        : $"{Reconciliation.Status.Replace('_', ' ')}: source {Reconciliation.SourceRows:N0}, silver {Reconciliation.SilverRows:N0}";
    public bool IsPaused => Status.State == TableState.Paused;
}

/// <summary>
/// Site detail: agent version and health, each table's state, lag and rows/min, recent events and commands, and the
/// actions the user's role allows.
/// </summary>
public sealed partial class SiteDetailViewModel(ConsoleApi api, SignedInUser user, string siteId, TimeProvider? time = null) : ObservableObject
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string SiteId => siteId;
    public SignedInUser User => user;
    public ObservableCollection<TableRow> Tables { get; } = [];
    public ObservableCollection<SourceDto> Sources { get; } = [];
    public ObservableCollection<AgentEventDto> Events { get; } = [];
    public ObservableCollection<CommandDto> Commands { get; } = [];

    [ObservableProperty] private SiteDto? _site;
    [ObservableProperty] private SiteStatus? _status;
    [ObservableProperty] private AgentDto? _agent;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;

    public bool CanOperate => user.CanOperate && Agent is not null;
    public bool CanAdminister => user.CanAdminister;

    partial void OnAgentChanged(AgentDto? value)
    {
        OnPropertyChanged(nameof(CanOperate));
        CollectLogsCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            Site = await api.GetSiteAsync(siteId, ct);
            var fleet = await api.GetStatusAsync(ct);
            Status = fleet.Sites.FirstOrDefault(s => s.SiteId == siteId);
            Agent = (await api.GetAgentsAsync(ct)).Where(a => a.SiteId == siteId && !a.Revoked).MaxBy(a => a.EnrolledUtc);
            var checks = await api.GetReconciliationAsync(siteId, ct);
            var now = _time.GetUtcNow();
            Replace(Tables, (Status?.Heartbeat?.Tables ?? []).Select(t =>
                new TableRow(t, checks.FirstOrDefault(c => c.SourceId == t.SourceId && c.Table == t.Table), now)));
            Replace(Sources, await api.GetSourcesAsync(siteId, ct));
            Replace(Events, await api.GetEventsAsync(siteId, 50, ct));
            Replace(Commands, Agent is null ? [] : (await api.GetCommandsAsync(Agent.AgentId, ct)).Take(20));
            Error = null;
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

    [RelayCommand] public Task PauseTableAsync(TableRow row) => SendAsync(CommandType.PauseTable, row);
    [RelayCommand] public Task ResumeTableAsync(TableRow row) => SendAsync(CommandType.ResumeTable, row);
    [RelayCommand] public Task ResnapshotTableAsync(TableRow row) => SendAsync(CommandType.ResnapshotTable, row);

    [RelayCommand(CanExecute = nameof(CanAdministerAgent))]
    public Task CollectLogsAsync() => SendAsync(CommandType.CollectLogs, null);

    [RelayCommand(CanExecute = nameof(CanAdministerAgent))]
    public Task RestartAsync() => SendAsync(CommandType.Restart, null);

    private bool CanAdministerAgent() => user.CanAdminister && Agent is not null;

    private async Task SendAsync(CommandType type, TableRow? row)
    {
        if (Agent is null) return;
        try
        {
            var command = await api.PostCommandAsync(Agent.AgentId, new CommandRequest { Type = type, SourceId = row?.SourceId, Table = row?.Table });
            Message = $"{type} sent; the agent picks it up within a minute.";
            Commands.Insert(0, command);
            Error = null;
        }
        catch (ConsoleApiException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>The zip a completed "collect logs" command uploaded.</summary>
    public async Task<byte[]?> DownloadLogsAsync(CommandDto command, CancellationToken ct = default)
    {
        const string prefix = "Logs uploaded: ";
        if (command.Result is not { } result || !result.StartsWith(prefix)) return null;
        return await api.DownloadAsync(result[prefix.Length..], ct);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
