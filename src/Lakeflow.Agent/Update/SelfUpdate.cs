using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lakeflow.Agent.Control;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent.Update;

/// <summary>
/// The files the agent and the updater service hand each other. The agent stages the MSI and a request in its own data
/// folder (<c>updates\</c>); the updater (SYSTEM) copies it into its folder, checks it again and installs it, and writes
/// its result there, which the agent may read but not change.
/// </summary>
public static partial class UpdateFiles
{
    public const string Request = "pending.json";
    public const string Result = "result.json";
    public const string Running = "running.json";
    public const string Attempts = "attempts.json";

    public sealed record InstallRequest(string Version, string Sha256);
    /// <param name="ExitCode">msiexec's exit code (0 or 3010 succeed), or -1 when the package was refused before installing.</param>
    public sealed record InstallResult(string Version, string Sha256, int ExitCode, string Detail, DateTimeOffset Utc);
    public sealed record AttemptLog(string Sha256, int Count, DateTimeOffset LastUtc);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    public static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,5}$")]
    public static partial Regex VersionPattern();

    public static string MsiName(string version) => $"LakeflowAgent-{version}.msi";

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), EnvelopeJson.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, EnvelopeJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
    }

    public static int Compare(string a, string b) => Version.Parse(a).CompareTo(Version.Parse(b));

    [GeneratedRegex(@"Installation success or error status: (\d+)")]
    private static partial Regex StatusLine();

    /// <summary>msiexec's verbose log ends with "... Installation success or error status: N." (UTF-16 or ANSI).</summary>
    public static int? ExitCodeFromLog(string logFile)
    {
        if (!File.Exists(logFile)) return null;
        try
        {
            var text = File.ReadAllText(logFile);
            return StatusLine().Matches(text) is { Count: > 0 } matches ? int.Parse(matches[^1].Groups[1].Value) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// The agent's side of self-update, run after each config pull. When the console wants another version: a Windows
/// service agent downloads the offered MSI, checks its SHA-256 and that it is signed by the same publisher as itself,
/// and in its site's maintenance window asks the updater service to install it (three tries per package). Docker and
/// other collectors only report that an administrator has to update them.
/// </summary>
public sealed class SelfUpdate(string dataDirectory, string currentVersion, IUpdatePlatform platform, TimeProvider time, ILogger<SelfUpdate> log)
{
    public const int MaxAttempts = 3;

    private string Folder => Path.Combine(dataDirectory, "updates");

    public string CurrentVersion => currentVersion;
    public string Platform => platform.Kind switch
    {
        CollectorPlatform.WindowsService => "windows-service",
        CollectorPlatform.Docker => "docker",
        _ => "other",
    };

    /// <summary>For the heartbeat; null when the collector is on the version the console wants.</summary>
    public UpdateStatus? Status { get; private set; }

    public async Task RunAsync(CollectorConfig config, ControlApiClient client, CancellationToken ct)
    {
        var target = config.DesiredAgentVersion;
        if (target is null || target == currentVersion || !UpdateFiles.VersionPattern().IsMatch(target))
        {
            Status = null;
            Clean(keep: null);
            return;
        }
        if (platform.Kind != CollectorPlatform.WindowsService)
        {
            Status = new UpdateStatus(UpdateState.Manual, target, platform.Kind == CollectorPlatform.Docker
                ? $"Running {currentVersion}. Update this Docker collector by hand: in its folder, git pull, then docker compose up -d --build."
                : $"Running {currentVersion}. Install {target} by hand.");
            return;
        }
        Status = await PrepareAsync(config, target, client, ct);
        if (Status.State is UpdateState.Failed) log.LogWarning("Update to {Version} not installed: {Detail}", target, Status.Detail);
    }

    private async Task<UpdateStatus> PrepareAsync(CollectorConfig config, string target, ControlApiClient client, CancellationToken ct)
    {
        UpdateStatus Failed(string detail) => new(UpdateState.Failed, target, detail);
        if (config.Update is not { } offer || offer.Version != target || !UpdateFiles.Sha256Pattern().IsMatch(offer.Sha256))
            return Failed($"No package for {target} has been uploaded (Configuration, then Updates).");
        if (UpdateFiles.Compare(target, currentVersion) < 0)
            return Failed($"{target} is older than the installed {currentVersion}; downgrades are done by hand (uninstall, then install {target}).");
        if (platform.OwnPublisher is not { } publisher)
            return Failed($"This agent ({currentVersion}) is not signed, so it does not install updates. Install {target} by hand.");

        Directory.CreateDirectory(Folder);
        var result = UpdateFiles.Read<UpdateFiles.InstallResult>(Path.Combine(platform.UpdaterFolder, UpdateFiles.Result));
        var lastFailure = result is { ExitCode: not (0 or 3010) } && result.Sha256 == offer.Sha256 ? result : null;
        var attempts = UpdateFiles.Read<UpdateFiles.AttemptLog>(Path.Combine(Folder, UpdateFiles.Attempts)) is { } a && a.Sha256 == offer.Sha256 ? a : null;
        if (File.Exists(Path.Combine(Folder, UpdateFiles.Request)) || File.Exists(Path.Combine(platform.UpdaterFolder, UpdateFiles.Running)))
            return new UpdateStatus(UpdateState.Installing, target, $"Installing {target}; the agent restarts when it is done.");
        if (attempts is { Count: >= MaxAttempts })
            return Failed($"Tried {attempts.Count} times. {lastFailure?.Detail ?? $"Still running {currentVersion} after installing."} Upload a new package, or install by hand.");

        var msi = Path.Combine(Folder, UpdateFiles.MsiName(target));
        if (!File.Exists(msi) || await UpdateFiles.Sha256Async(msi, ct) != offer.Sha256)
        {
            Clean(keep: null);
            Status = new UpdateStatus(UpdateState.Downloading, target, $"Downloading {offer.SizeBytes / 1024 / 1024} MB.");
            var temp = msi + ".download";
            try
            {
                await using (var file = File.Create(temp)) await client.DownloadAsync(offer.DownloadPath, file, ct);
                if (await UpdateFiles.Sha256Async(temp, ct) != offer.Sha256)
                    return Failed("The download does not match the package's SHA-256; it was discarded and will be fetched again.");
                File.Move(temp, msi, overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or ControlApiException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new UpdateStatus(UpdateState.Downloading, target, $"Download failed, retrying: {ex.Message}");
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        if (platform.Publisher(msi) is not { } signer)
            return Failed($"The {target} package is not signed. Agents install only packages signed like themselves ({publisher}).");
        if (signer != publisher)
            return Failed($"The {target} package is signed by {signer}, not by {publisher} like this agent; it will not be installed.");

        var window = config.Sites.Select(s => (s.MaintenanceWindow, s.SiteTimeZone)).ToList();
        var retry = lastFailure is null ? "" : $" The last attempt failed ({lastFailure.Detail}); trying again.";
        if (!InWindow(window, time.GetUtcNow()))
            return new UpdateStatus(UpdateState.Waiting, target, $"Downloaded and checked; installs in the maintenance window ({Describe(window)}).{retry}");

        UpdateFiles.Write(Path.Combine(Folder, UpdateFiles.Attempts), new UpdateFiles.AttemptLog(offer.Sha256, (attempts?.Count ?? 0) + 1, time.GetUtcNow()));
        UpdateFiles.Write(Path.Combine(Folder, UpdateFiles.Request), new UpdateFiles.InstallRequest(target, offer.Sha256));
        log.LogInformation("Update to {Version}: asked the updater service to install it", target);
        return new UpdateStatus(UpdateState.Installing, target, $"Installing {target}; the agent restarts when it is done.");
    }

    /// <summary>Inside any of the sites' maintenance windows (in each site's time zone); with no window set, any time.</summary>
    public static bool InWindow(IReadOnlyList<(MaintenanceWindow? Window, string TimeZone)> windows, DateTimeOffset now)
    {
        var set = windows.Where(w => w.Window is not null).ToList();
        if (set.Count == 0) return true;
        foreach (var (window, zone) in set)
        {
            var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone(zone)).DateTime);
            var (start, end) = (window!.Start, window.End);
            if (start <= end ? local >= start && local < end : local >= start || local < end) return true;
        }
        return false;
    }

    private static string Describe(IReadOnlyList<(MaintenanceWindow? Window, string TimeZone)> windows) =>
        windows.FirstOrDefault(w => w.Window is not null) is { Window: { } w } first ? $"{w.Start:HH\\:mm} to {w.End:HH\\:mm} {first.TimeZone}" : "any time";

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>Removes staged packages other than <paramref name="keep"/> (and the attempt log once on target).</summary>
    private void Clean(string? keep)
    {
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.EnumerateFiles(Folder, "LakeflowAgent-*.msi*").Where(f => Path.GetFileName(f) != keep))
            TryDelete(file);
        if (keep is null && Status is null) TryDelete(Path.Combine(Folder, UpdateFiles.Attempts));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
