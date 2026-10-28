using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent.Control;

/// <summary>
/// The installer writes <c>enroll.json</c> (control API URL and one-time token) into the data folder, readable by
/// the service account only. The agent enrolls on start and deletes the file once enrolled.
/// </summary>
public sealed class EnrollmentFile(string dataDirectory, string agentVersion, ILogger<EnrollmentFile> log)
{
    private sealed record Pending(string ControlApi, string Token);

    public string Path => System.IO.Path.Combine(dataDirectory, "enroll.json");

    public async Task EnrollIfPendingAsync(ControlLoop control, CancellationToken ct)
    {
        if (!File.Exists(Path)) return;
        if (control.Enrolled)
        {
            File.Delete(Path);
            return;
        }
        var pending = JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(Path, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
        if (pending is null) return;
        try
        {
            await control.EnrollAsync(pending.ControlApi, pending.Token, Environment.MachineName, agentVersion, ct);
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is HttpRequestException or ControlApiException)
        {
            // Leave the file; the next service start tries again. A refused token needs a new one from the console.
            log.LogError("Enrollment failed: {Error}", ex.Message);
        }
    }
}
