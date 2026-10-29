using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent.Control;

/// <summary>
/// Enrollment on first start, from the console's one-time token: in Docker through the environment
/// (<c>LAKEFLOW_CONTROL_API</c>, <c>LAKEFLOW_ENROLLMENT_TOKEN</c>); for an agent installed at a site through
/// <c>enroll.json</c>, which the installer writes into the data folder and the agent deletes once enrolled.
/// An already enrolled collector ignores both.
/// </summary>
public sealed class EnrollmentFile(string dataDirectory, string agentVersion, ILogger<EnrollmentFile> log)
{
    private sealed record Pending(string ControlApi, string Token);

    public string Path => System.IO.Path.Combine(dataDirectory, "enroll.json");

    public async Task EnrollIfPendingAsync(ControlLoop control, CancellationToken ct)
    {
        if (control.Enrolled)
        {
            if (File.Exists(Path)) File.Delete(Path);
            return;
        }
        var pending = FromEnvironment() ?? await FromFileAsync(ct);
        if (pending is null) return;
        try
        {
            await control.EnrollAsync(pending.ControlApi, pending.Token, Environment.MachineName, agentVersion, ct);
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch (Exception ex) when (ex is HttpRequestException or ControlApiException)
        {
            // The next start tries again. A refused token needs a new one from the console.
            log.LogError("Enrollment failed: {Error}", ex.Message);
        }
    }

    private static Pending? FromEnvironment() =>
        Environment.GetEnvironmentVariable("LAKEFLOW_CONTROL_API") is { Length: > 0 } api
        && Environment.GetEnvironmentVariable("LAKEFLOW_ENROLLMENT_TOKEN") is { Length: > 0 } token
            ? new Pending(api, token)
            : null;

    private async Task<Pending?> FromFileAsync(CancellationToken ct) =>
        File.Exists(Path)
            ? JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(Path, ct),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            : null;
}
