using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent.Control;

/// <summary>
/// Enrollment from the console's one-time token, with the SHA-256 of the control API's CA to trust:
/// <list type="bullet">
/// <item>the built-in collector of the Docker stack reads <c>enroll.json</c>, which the control API writes into a
/// volume only the two of them share (<c>LAKEFLOW_ENROLL_FILE</c>), so nobody copies anything;</item>
/// <item>another collector gets <c>LAKEFLOW_CONTROL_API</c>, <c>LAKEFLOW_ENROLLMENT_TOKEN</c> and
/// <c>LAKEFLOW_CA_SHA256</c> from the file the console offers for download;</item>
/// <item>an agent installed at a site gets the same three from the MSI, in a registry key only it, SYSTEM and
/// Administrators can read (<see cref="RegistryKeyPath"/>), and deletes them once enrolled.</item>
/// </list>
/// An already enrolled collector ignores all of these.
/// </summary>
public sealed class EnrollmentFile(string dataDirectory, string agentVersion, ILogger<EnrollmentFile> log)
{
    private sealed record Pending(string ControlApi, string Token, string? CaSha256);

    /// <summary>Written by the installer (installer/Package.wxs) under HKLM.</summary>
    public const string RegistryKeyPath = @"SOFTWARE\Lakeflow\Agent\Enrollment";

    public string Path => Environment.GetEnvironmentVariable("LAKEFLOW_ENROLL_FILE") is { Length: > 0 } file
        ? file
        : System.IO.Path.Combine(dataDirectory, "enroll.json");

    /// <summary>True when there is a token to try (so it is worth trying again later if this attempt fails).</summary>
    public async Task<bool> EnrollIfPendingAsync(ControlLoop control, CancellationToken ct)
    {
        if (control.Enrolled)
        {
            TryDelete();
            return false;
        }
        var pending = FromEnvironment() ?? FromRegistry() ?? await FromFileAsync(ct);
        if (pending is null) return false;
        try
        {
            await control.EnrollAsync(pending.ControlApi, pending.Token, pending.CaSha256, Environment.MachineName, agentVersion, ct);
            TryDelete();
        }
        catch (Exception ex) when (ex is HttpRequestException or ControlApiException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Tried again shortly (the control API may still be starting). A refused token needs a new one from the console.
            log.LogError("Enrollment failed: {Error}", ex.Message);
        }
        return true;
    }

    /// <summary>The built-in collector's file is on a read-only volume; the control API removes it.</summary>
    private void TryDelete()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (OperatingSystem.IsWindows()) Microsoft.Win32.Registry.LocalMachine.DeleteSubKey(RegistryKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.LogWarning("Could not remove the used enrollment token: {Error}", ex.Message);
        }
    }

    private Pending? FromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKeyPath);
            return key?.GetValue("ControlApi") is string { Length: > 0 } api && key.GetValue("Token") is string { Length: > 0 } token
                ? new Pending(api, token, key.GetValue("CaSha256") as string is { Length: > 0 } ca ? ca : null)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.LogWarning("Could not read the enrollment token from the registry: {Error}", ex.Message);
            return null;
        }
    }

    private static Pending? FromEnvironment() =>
        Environment.GetEnvironmentVariable("LAKEFLOW_CONTROL_API") is { Length: > 0 } api
        && Environment.GetEnvironmentVariable("LAKEFLOW_ENROLLMENT_TOKEN") is { Length: > 0 } token
            ? new Pending(api, token, Environment.GetEnvironmentVariable("LAKEFLOW_CA_SHA256"))
            : null;

    private async Task<Pending?> FromFileAsync(CancellationToken ct)
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(Path, ct),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.LogWarning("Could not read {Path}: {Error}", Path, ex.Message);
            return null;
        }
    }
}
