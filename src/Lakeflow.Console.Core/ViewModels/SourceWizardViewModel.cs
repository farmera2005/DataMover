using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core.ViewModels;

public enum WizardStep { Source, Connection, Tables, Done }

public sealed record EngineChoice(SourceEngine Engine, string Name, bool Available, string? Note);

/// <summary>A table offered by the source, and how it will be captured if selected.</summary>
public sealed partial class TableChoice(SourceTableInfo info) : ObservableObject
{
    public SourceTableInfo Info => info;
    public string Table => info.Table;
    public string Rows => info.RowCount.ToString("N0");
    public string Note => info.Note;
    public IReadOnlyList<CaptureMethod> Methods { get; } = AvailableMethods(info);

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private CaptureMethod? _method = info.SuggestedMethod;
    [ObservableProperty] private string? _watermark = info.SuggestedWatermark;
    /// <summary>Comma-separated, for tables without a primary key.</summary>
    [ObservableProperty] private string? _keyColumns;

    public bool Capturable => Method is not null;

    /// <summary><see cref="Method"/> as a list selection (selection controls hand back objects).</summary>
    public object? MethodItem
    {
        get => Method;
        set => Method = value as CaptureMethod?;
    }

    partial void OnMethodChanged(CaptureMethod? value) => OnPropertyChanged(nameof(MethodItem));

    private static List<CaptureMethod> AvailableMethods(SourceTableInfo info)
    {
        var methods = new List<CaptureMethod>();
        if (info.ChangeTrackingEnabled && info.KeyColumns.Count > 0) methods.Add(CaptureMethod.ChangeTracking);
        if (info.RowVersionColumn is not null) methods.Add(CaptureMethod.RowVersion);
        methods.Add(CaptureMethod.TimestampPolling);
        return methods;
    }
}

/// <summary>
/// Source setup wizard: pick the engine, enter the connection, test it through the site's agent, browse the tables the
/// login can read, choose tables and capture methods, and start the snapshot.
///
/// The password is sealed here, in the browser, to the collector's certificate before it leaves this machine; the
/// control API never sees it. Testing and browsing run on the collector serving the site (only it can reach the site's
/// database), so each takes until its next config pull (about 10 s while a command waits).
/// </summary>
public sealed partial class SourceWizardViewModel : ObservableObject
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]{0,63}$");
    private readonly ConsoleApi _api;
    private readonly ISecretSealer _sealer;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _pollEvery;
    private AgentDto? _agent;

    public SourceWizardViewModel(ConsoleApi api, ISecretSealer sealer, string siteId, TimeSpan? commandTimeout = null, TimeSpan? pollEvery = null)
    {
        (_api, _sealer, SiteId) = (api, sealer, siteId);
        _commandTimeout = commandTimeout ?? TimeSpan.FromMinutes(3);
        _pollEvery = pollEvery ?? TimeSpan.FromSeconds(3);
    }

    public string SiteId { get; }

    public IReadOnlyList<EngineChoice> Engines { get; } =
    [
        new(SourceEngine.SqlServer, "SQL Server (CompuWeigh, WEM/CPM, Kahler)", true, null),
        new(SourceEngine.Db2i, "Db2 for IBM i (AgVantage)", false, "Phase 3"),
        new(SourceEngine.MySql, "MySQL", false, "Phase 4"),
        new(SourceEngine.Postgres, "PostgreSQL", false, "Phase 4"),
        new(SourceEngine.Access, "Microsoft Access", false, "Phase 4"),
        new(SourceEngine.Files, "CSV / files", false, "Phase 4"),
    ];

    public IReadOnlyList<Division> Divisions { get; } = Enum.GetValues<Division>();

    [ObservableProperty] private WizardStep _step = WizardStep.Source;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    public ObservableCollection<string> Errors { get; } = [];

    // Source
    [ObservableProperty] private string _sourceId = "";
    [ObservableProperty] private string _sourceSystem = "";
    [ObservableProperty] private Division _division = Division.Grain;
    [ObservableProperty] private SourceEngine _engine = SourceEngine.SqlServer;

    // Connection
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int? _port = 1433;
    [ObservableProperty] private string? _instance;
    [ObservableProperty] private string? _database;
    [ObservableProperty] private AuthMethod _auth = AuthMethod.SqlLogin;
    [ObservableProperty] private string? _userName;
    /// <summary>Held only until it is sealed to the agent; never stored or sent in the clear.</summary>
    [ObservableProperty] private string _password = "";
    /// <summary>The collector the password is sealed to (set once the connection has been tested).</summary>
    public AgentDto? Collector => _agent;
    [ObservableProperty] private bool _trustServerCertificate;
    /// <summary>A double because that is what number inputs edit.</summary>
    [ObservableProperty] private double _maxRowsPerSecond = 2000;
    [ObservableProperty] private ConnectionTestResult? _testResult;

    // Tables
    public ObservableCollection<TableChoice> Tables { get; } = [];
    [ObservableProperty] private bool _tablesTruncated;

    [RelayCommand]
    public void Next()
    {
        Errors.Clear();
        if (Step == WizardStep.Source)
        {
            if (!IdPattern.IsMatch(SourceId)) Errors.Add("Source id: lower-case letters, digits, '-' or '_' (e.g. compuweigh).");
            if (!IdPattern.IsMatch(SourceSystem)) Errors.Add("Source system: lower-case letters, digits, '-' or '_' (e.g. compuweigh). It names the silver tables.");
            if (!Engines.Single(e => e.Engine == Engine).Available) Errors.Add($"{Engine} sources come in a later phase.");
            if (Errors.Count == 0) Step = WizardStep.Connection;
        }
    }

    [RelayCommand]
    public void Back()
    {
        Errors.Clear();
        if (Step > WizardStep.Source && Step != WizardStep.Done) Step--;
    }

    private SourceDto Draft(bool paused) => new()
    {
        SourceId = SourceId,
        SourceSystem = SourceSystem,
        Division = Division,
        Engine = Engine,
        Paused = paused,
        MaxRowsPerSecond = double.IsFinite(MaxRowsPerSecond) && MaxRowsPerSecond >= 1 ? (int)MaxRowsPerSecond : 2000,
        Connection = new ConnectionSettings
        {
            Host = Host.Trim(),
            Port = Port,
            Instance = string.IsNullOrWhiteSpace(Instance) ? null : Instance.Trim(),
            Database = string.IsNullOrWhiteSpace(Database) ? null : Database.Trim(),
            Auth = Auth,
            UserName = Auth == AuthMethod.SqlLogin ? UserName?.Trim() : null,
            CredentialName = Auth == AuthMethod.SqlLogin ? SourceId : null,
            TrustServerCertificate = TrustServerCertificate,
        },
    };

    /// <summary>Saves the source (paused, so nothing is captured yet), seals the password to the agent, and has the agent connect.</summary>
    [RelayCommand]
    public async Task TestConnectionAsync(CancellationToken ct)
    {
        Errors.Clear();
        if (string.IsNullOrWhiteSpace(Host)) Errors.Add("Host is required.");
        if (Auth == AuthMethod.SqlLogin && string.IsNullOrWhiteSpace(UserName)) Errors.Add("User name is required for a SQL login.");
        if (Errors.Count > 0) return;
        IsBusy = true;
        TestResult = null;
        try
        {
            var site = await _api.GetSiteAsync(SiteId, ct);
            _agent = site.CollectorId is { } collector ? await _api.GetAgentAsync(collector, ct) : null;
            if (_agent is null || _agent.Revoked)
            {
                Errors.Add("No collector is assigned to this site yet. Assign one under Sites and collectors, then add sources.");
                return;
            }
            Status = "Saving the source…";
            await _api.PutSourceAsync(SiteId, Draft(paused: true), ct);
            if (Auth == AuthMethod.SqlLogin && Password.Length > 0)
            {
                var sealedPassword = await _sealer.SealAsync(Password, _agent.PublicKeySpki, _agent.CertificateThumbprint);
                await _api.PutCredentialAsync(SiteId, SourceId, new CredentialRequest(_agent.AgentId, sealedPassword), ct);
                Password = "";
            }
            Status = "Waiting for the collector to connect (it checks in every few seconds)…";
            var result = await _api.RunCommandAsync(SiteId, new CommandRequest { Type = CommandType.TestConnection, SourceId = SourceId },
                _commandTimeout, _pollEvery, ct);
            if (result.CompletedUtc is null)
            {
                Errors.Add("The collector has not answered. Check it is running and online (Sites and collectors), then try again.");
                return;
            }
            TestResult = result.Details?.Deserialize<ConnectionTestResult>(EnvelopeJson.Options)
                ?? new ConnectionTestResult { Succeeded = result.Succeeded == true, Error = result.Result };
            if (!TestResult.Succeeded)
            {
                Errors.Add(TestResult.Error ?? result.Result ?? "The connection failed.");
                return;
            }
            Status = $"Connected to {TestResult.Database} as {TestResult.Login} ({TestResult.ServerVersion}). Reading the tables…";
            await BrowseAsync(ct);
        }
        catch (ConsoleApiException ex)
        {
            foreach (var error in ex.Errors) Errors.Add(error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BrowseAsync(CancellationToken ct)
    {
        var result = await _api.RunCommandAsync(SiteId, new CommandRequest { Type = CommandType.BrowseSchema, SourceId = SourceId },
            _commandTimeout, _pollEvery, ct);
        var browse = result.Details?.Deserialize<SchemaBrowseResult>(EnvelopeJson.Options);
        if (result.Succeeded != true || browse is null)
        {
            Errors.Add(result.Result ?? "The collector could not list the tables.");
            return;
        }
        Tables.Clear();
        foreach (var table in browse.Tables) Tables.Add(new TableChoice(table));
        TablesTruncated = browse.Truncated;
        Status = $"{browse.Tables.Count} tables the login can read. Choose the ones to capture.";
        Step = WizardStep.Tables;
    }

    /// <summary>Saves the chosen tables and unpauses the source: the agent starts snapshotting on its next pull.</summary>
    [RelayCommand]
    public async Task FinishAsync(CancellationToken ct)
    {
        Errors.Clear();
        var chosen = Tables.Where(t => t.Selected).ToList();
        if (chosen.Count == 0) Errors.Add("Choose at least one table.");
        foreach (var t in chosen.Where(t => t.Method is null)) Errors.Add($"{t.Table}: choose a capture method.");
        foreach (var t in chosen.Where(t => t.Info.KeyColumns.Count == 0 && string.IsNullOrWhiteSpace(t.KeyColumns)))
            Errors.Add($"{t.Table} has no primary key: enter its key columns.");
        if (Errors.Count > 0) return;
        IsBusy = true;
        try
        {
            foreach (var t in chosen)
            {
                await _api.PutTableAsync(SiteId, SourceId, new TableDto
                {
                    Table = t.Table,
                    Method = t.Method!.Value,
                    WatermarkColumn = t.Method is CaptureMethod.TimestampPolling or CaptureMethod.RowVersion ? t.Watermark : null,
                    KeyColumns = string.IsNullOrWhiteSpace(t.KeyColumns)
                        ? null
                        : t.KeyColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                }, ct);
            }
            await _api.PutSourceAsync(SiteId, Draft(paused: false), ct);
            Status = $"{chosen.Count} table(s) saved. The collector starts the snapshot within a minute; follow it on the site page.";
            Step = WizardStep.Done;
        }
        catch (ConsoleApiException ex)
        {
            foreach (var error in ex.Errors) Errors.Add(error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
