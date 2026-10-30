using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Services;

/// <summary>Quiet hours and harvest seasons, in the schedule's time zone.</summary>
public static class QuietHours
{
    public static bool IsQuiet(NotificationScheduleDto schedule, DateTimeOffset now)
    {
        if (!TryTime(schedule.QuietStart, out var start) || !TryTime(schedule.QuietEnd, out var end) || start == end) return false;
        var local = TimeZoneInfo.ConvertTime(now, Zone(schedule.TimeZone));
        if (InHarvest(schedule, DateOnly.FromDateTime(local.DateTime))) return false;
        var time = TimeOnly.FromDateTime(local.DateTime);
        return start < end ? time >= start && time < end : time >= start || time < end;
    }

    public static bool InHarvest(NotificationScheduleDto schedule, DateOnly day)
    {
        var today = (day.Month, day.Day);
        foreach (var season in schedule.HarvestSeasons)
        {
            if (!TryDay(season.Start, out var from) || !TryDay(season.End, out var to)) continue;
            var inside = from.CompareTo(to) <= 0 ? today.CompareTo(from) >= 0 && today.CompareTo(to) <= 0 : today.CompareTo(from) >= 0 || today.CompareTo(to) <= 0;
            if (inside) return true;
        }
        return false;
    }

    public static List<string> Validate(NotificationScheduleDto schedule)
    {
        var errors = new List<string>();
        try { Zone(schedule.TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { errors.Add($"Unknown time zone {schedule.TimeZone} (use a name like America/New_York)."); }
        var hasStart = !string.IsNullOrWhiteSpace(schedule.QuietStart);
        var hasEnd = !string.IsNullOrWhiteSpace(schedule.QuietEnd);
        if (hasStart != hasEnd) errors.Add("Give quiet hours both a start and an end, or neither.");
        else if (hasStart && (!TryTime(schedule.QuietStart, out _) || !TryTime(schedule.QuietEnd, out _))) errors.Add("Quiet hours are times like 22:00 and 06:00.");
        foreach (var season in schedule.HarvestSeasons)
        {
            if (string.IsNullOrWhiteSpace(season.Name)) errors.Add("Give each harvest season a name.");
            if (!TryDay(season.Start, out _) || !TryDay(season.End, out _)) errors.Add($"{season.Name}: dates are month and day, like 09-15 and 11-30.");
        }
        return errors;
    }

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? "America/New_York" : id);

    private static bool TryTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static bool TryDay(string? text, out (int Month, int Day) day)
    {
        day = default;
        // A leap year, so 02-29 is accepted.
        if (!DateOnly.TryParseExact($"2024-{text?.Trim()}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
        day = (d.Month, d.Day);
        return true;
    }
}

/// <summary>A channel refused or failed a message; the text says what to fix.</summary>
public sealed class NotificationException(string message) : Exception(message);

/// <summary>Alert email through Microsoft Graph, signed in with the app's client secret (client credentials).</summary>
public sealed class GraphMail(IHttpClientFactory http, TimeProvider time)
{
    public const string LoginHost = "https://login.microsoftonline.com/";
    public const string GraphHost = "https://graph.microsoft.com/";

    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _tokens = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task SendAsync(EmailNotifySetting setting, string clientSecret, IReadOnlyList<string> to, string subject, string html, CancellationToken ct)
    {
        var token = await TokenAsync(setting, clientSecret, ct);
        var message = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["subject"] = subject,
                ["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = html },
                ["toRecipients"] = new JsonArray([.. to.Select(a => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = a } })]),
            },
            ["saveToSentItems"] = false,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GraphHost}v1.0/users/{Uri.EscapeDataString(setting.Sender)}/sendMail")
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.CreateClient("notifications").SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var (code, text) = GraphError(await response.Content.ReadAsStringAsync(ct));
        if (response.StatusCode == HttpStatusCode.Unauthorized) Forget(setting, clientSecret);
        throw new NotificationException(response.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                $"Graph refused to send as {setting.Sender} ({code}: {text}). Give the app Mail.Send for that mailbox (README, \"Alert email\").",
            HttpStatusCode.NotFound => $"Graph does not know the mailbox {setting.Sender} ({code}: {text}).",
            _ => $"Graph answered {(int)response.StatusCode} ({code}: {text}).",
        });
    }

    private async Task<string> TokenAsync(EmailNotifySetting setting, string clientSecret, CancellationToken ct)
    {
        var key = CacheKey(setting, clientSecret);
        await _lock.WaitAsync(ct);
        try
        {
            lock (_tokens)
                if (_tokens.TryGetValue(key, out var cached) && cached.Expires > time.GetUtcNow()) return cached.Token;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = setting.ClientId, ["client_secret"] = clientSecret, ["grant_type"] = "client_credentials",
                ["scope"] = $"{GraphHost}.default",
            });
            using var response = await http.CreateClient("notifications")
                .PostAsync($"{LoginHost}{Uri.EscapeDataString(setting.TenantId)}/oauth2/v2.0/token", form, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var description = TryJson(body)?["error_description"]?.GetValue<string>() ?? body;
                // AADSTS codes say what is wrong (7000215: bad secret; 700016: no such app; 90002: no such tenant).
                throw new NotificationException($"Entra ID refused the app sign-in: {FirstLine(description)}");
            }
            var json = TryJson(body) ?? throw new NotificationException("Entra ID answered without a token.");
            var token = json["access_token"]!.GetValue<string>();
            var seconds = json["expires_in"]?.GetValue<int>() ?? 3600;
            lock (_tokens) _tokens[key] = (token, time.GetUtcNow().AddSeconds(Math.Max(60, seconds - 300)));
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Forget(EmailNotifySetting setting, string clientSecret)
    {
        lock (_tokens) _tokens.Remove(CacheKey(setting, clientSecret));
    }

    private static string CacheKey(EmailNotifySetting s, string secret) =>
        $"{s.TenantId}|{s.ClientId}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))}";

    private static (string Code, string Message) GraphError(string body) =>
        TryJson(body)?["error"] is JsonObject error
            ? (error["code"]?.GetValue<string>() ?? "error", FirstLine(error["message"]?.GetValue<string>() ?? ""))
            : ("error", FirstLine(body));

    internal static JsonObject? TryJson(string body)
    {
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return null; }
    }

    internal static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= 300 ? line : line[..297] + "...";
    }
}

/// <summary>Posts an Adaptive Card to a Teams Workflows webhook ("Post to a channel when a webhook request is received").</summary>
public sealed class TeamsWorkflow(IHttpClientFactory http)
{
    public async Task PostAsync(string url, JsonObject card, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = card }),
        };
        using var response = await http.CreateClient("notifications")
            .PostAsync(url, new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        if (response.IsSuccessStatusCode) return;
        var body = GraphMail.FirstLine(await response.Content.ReadAsStringAsync(ct));
        throw new NotificationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                $"The Teams workflow refused the message ({(int)response.StatusCode}: {body}). Is the flow still on, and is this its current URL?",
            _ => $"The Teams workflow answered {(int)response.StatusCode}: {body}",
        });
    }
}

/// <summary>One line of a notification.</summary>
public sealed record NotificationItem(Alert Alert, string SiteName, bool Cleared, bool Escalated);

/// <summary>The subject, email body and Teams card for a batch of alerts (worst first).</summary>
public static class AlertMessages
{
    public static string Subject(IReadOnlyList<NotificationItem> items)
    {
        if (items.Count == 1) return $"[Lakeflow] {Label(items[0])}: {items[0].SiteName}: {items[0].Alert.Message}";
        var parts = new List<string>();
        var critical = items.Count(i => !i.Cleared && i.Alert.Severity == AlertSeverity.Critical);
        var warning = items.Count(i => !i.Cleared && i.Alert.Severity == AlertSeverity.Warning);
        var cleared = items.Count(i => i.Cleared);
        if (critical > 0) parts.Add($"{critical} critical");
        if (warning > 0) parts.Add($"{warning} warning{(warning == 1 ? "" : "s")}");
        if (cleared > 0) parts.Add($"{cleared} cleared");
        var sites = items.Select(i => i.SiteName).Distinct().ToList();
        return $"[Lakeflow] {string.Join(", ", parts)} at {(sites.Count == 1 ? sites[0] : $"{sites.Count} sites")}";
    }

    public static string Html(IReadOnlyList<NotificationItem> items, string? consoleUrl, TimeZoneInfo zone)
    {
        var html = new StringBuilder("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px\">");
        html.Append("<table style=\"border-collapse:collapse\">");
        foreach (var item in items)
        {
            var colour = item.Cleared ? "#2e7d32" : item.Alert.Severity == AlertSeverity.Critical ? "#c62828" : "#ef8f00";
            html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:6px 10px;border-left:4px solid {colour}\"><b>{E(Label(item))}</b> · {E(item.SiteName)}<br>")
                .Append(E(item.Alert.Message)).Append("<br><span style=\"color:#666\">")
                .Append(E(When(item, zone))).Append("</span></td></tr>");
        }
        html.Append("</table>");
        if (consoleUrl is not null) html.Append(CultureInfo.InvariantCulture, $"<p><a href=\"{E(consoleUrl)}\">Open alerts in the Lakeflow console</a></p>");
        html.Append("<p style=\"color:#666;font-size:12px\">Sent by Lakeflow. Change who gets these in Configuration, then Notifications.</p></div>");
        return html.ToString();
    }

    public static JsonObject TeamsCard(IReadOnlyList<NotificationItem> items, string? consoleUrl, TimeZoneInfo zone)
    {
        var body = new JsonArray(new JsonObject { ["type"] = "TextBlock", ["text"] = Subject(items).Replace("[Lakeflow] ", "Lakeflow: "), ["weight"] = "Bolder", ["wrap"] = true });
        foreach (var item in items.Take(25))
        {
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock", ["wrap"] = true, ["spacing"] = "Medium",
                ["color"] = item.Cleared ? "Good" : item.Alert.Severity == AlertSeverity.Critical ? "Attention" : "Warning",
                ["text"] = $"**{Label(item)}** · {item.SiteName}",
            });
            body.Add(new JsonObject { ["type"] = "TextBlock", ["wrap"] = true, ["spacing"] = "None", ["text"] = item.Alert.Message });
            body.Add(new JsonObject { ["type"] = "TextBlock", ["wrap"] = true, ["spacing"] = "None", ["isSubtle"] = true, ["size"] = "Small", ["text"] = When(item, zone) });
        }
        if (items.Count > 25) body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = $"and {items.Count - 25} more", ["isSubtle"] = true });
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["type"] = "AdaptiveCard", ["version"] = "1.4", ["body"] = body,
        };
        if (consoleUrl is not null)
            card["actions"] = new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open alerts", ["url"] = consoleUrl });
        return card;
    }

    private static string Label(NotificationItem item) =>
        item.Cleared ? "Cleared" : item.Escalated ? "Now critical" : item.Alert.Severity == AlertSeverity.Critical ? "Critical" : "Warning";

    private static string When(NotificationItem item, TimeZoneInfo zone)
    {
        string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).ToString("ddd MMM d, h:mm tt", CultureInfo.InvariantCulture);
        return item.Cleared && item.Alert.ResolvedUtc is { } done
            ? $"Since {Local(item.Alert.OpenedUtc)}, cleared {Local(done)}"
            : $"Since {Local(item.Alert.OpenedUtc)}" + (item.Alert.AcknowledgedBy is { } by ? $", acknowledged by {by}" : "");
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}

/// <summary>How each channel is doing, for the console; and when a failing one may try again (30 s doubling to 15 min).</summary>
public sealed class NotificationState(TimeProvider time)
{
    private readonly Dictionary<string, (NotificationChannelStatus Status, int Failures)> _channels = [];

    public NotificationChannelStatus Get(string channel)
    {
        lock (_channels) return _channels.TryGetValue(channel, out var c) ? c.Status : new NotificationChannelStatus(channel, null, null, null, null);
    }

    public bool MayTry(string channel) => Get(channel).NextAttemptUtc is not { } next || next <= time.GetUtcNow();

    public void Sent(string channel)
    {
        lock (_channels) _channels[channel] = (Get(channel) with { LastSentUtc = time.GetUtcNow(), NextAttemptUtc = null }, 0);
    }

    public void Failed(string channel, string error)
    {
        lock (_channels)
        {
            var failures = (_channels.TryGetValue(channel, out var c) ? c.Failures : 0) + 1;
            var wait = TimeSpan.FromSeconds(Math.Min(15 * 60, 30 * Math.Pow(2, failures - 1)));
            _channels[channel] = (Get(channel) with { LastFailureUtc = time.GetUtcNow(), LastError = error, NextAttemptUtc = time.GetUtcNow() + wait }, failures);
        }
    }
}

/// <summary>
/// Sends what has not been sent: alerts raised (or now worse) and alerts cleared after being sent, one message per channel
/// per round. During quiet hours only critical alerts go; warnings go when quiet hours end, if still open.
/// </summary>
public sealed class Notifier(LakeflowDb db, SettingsStore settings, GraphMail mail, TeamsWorkflow teams, NotificationState state,
    ServerOptions options, TimeProvider time, ILogger<Notifier> log)
{
    public const string Email = "email";
    public const string Teams = "teams";
    private const string Raised = "raised";
    private const string ClearedEvent = "cleared";

    /// <summary>Cleared alerts older than this are not announced (a channel that was down for a week does not catch up).</summary>
    public static readonly TimeSpan ClearedWindow = TimeSpan.FromDays(1);

    public async Task RunAsync(CancellationToken ct)
    {
        var schedule = await settings.GetAsync<NotificationScheduleDto>(SettingsStore.NotifySchedule, ct) ?? new NotificationScheduleDto();
        var quiet = QuietHours.IsQuiet(schedule, time.GetUtcNow());
        var (email, emailSecret) = await settings.GetWithSecretAsync<EmailNotifySetting>(SettingsStore.NotifyEmail, ct);
        var (team, teamsUrl) = await settings.GetWithSecretAsync<TeamsNotifySetting>(SettingsStore.NotifyTeams, ct);
        var consoleUrl = await ConsoleUrlAsync(settings, options, ct);
        var zone = Zone(schedule);
        if (email is { Enabled: true, Recipients.Count: > 0 } && emailSecret is not null && state.MayTry(Email))
            await DeliverAsync(Email, email.MinimumSeverity, quiet, items =>
                mail.SendAsync(email, Encoding.UTF8.GetString(emailSecret), email.Recipients, AlertMessages.Subject(items), AlertMessages.Html(items, consoleUrl, zone), ct), ct);
        if (team is { Enabled: true } && teamsUrl is not null && state.MayTry(Teams))
            await DeliverAsync(Teams, team.MinimumSeverity, quiet, items =>
                teams.PostAsync(Encoding.UTF8.GetString(teamsUrl), AlertMessages.TeamsCard(items, consoleUrl, zone), ct), ct);
    }

    private async Task DeliverAsync(string channel, AlertSeverity minimum, bool quiet, Func<IReadOnlyList<NotificationItem>, Task> send, CancellationToken ct)
    {
        var items = await PendingAsync(channel, minimum, quiet, ct);
        if (items.Count == 0) return;
        try
        {
            await send(items);
        }
        catch (Exception ex) when (ex is NotificationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var error = ex is NotificationException ? ex.Message : $"Could not reach the service ({ex.Message}).";
            state.Failed(channel, error);
            log.LogWarning("Alert {Channel} not sent ({Count} alerts): {Error}", channel, items.Count, error);
            return;
        }
        var now = time.GetUtcNow();
        foreach (var item in items)
            db.AlertDeliveries.Add(new AlertDelivery
            {
                Id = Guid.NewGuid(), AlertId = item.Alert.Id, Channel = channel, Event = item.Cleared ? ClearedEvent : Raised,
                Severity = item.Alert.Severity, SentUtc = now,
            });
        await db.SaveChangesAsync(ct);
        state.Sent(channel);
        log.LogInformation("Alert {Channel} sent: {Count} alerts", channel, items.Count);
    }

    /// <summary>Worst first: critical, then warnings, then cleared.</summary>
    public async Task<IReadOnlyList<NotificationItem>> PendingAsync(string channel, AlertSeverity minimum, bool quiet, CancellationToken ct)
    {
        var since = time.GetUtcNow() - ClearedWindow;
        // Severity is stored as text, so it is compared here rather than in SQL.
        var alerts = (await db.Alerts.AsNoTracking().Where(a => a.ResolvedUtc == null || a.ResolvedUtc > since).ToListAsync(ct))
            .Where(a => a.ResolvedUtc is not null || a.Severity >= minimum).ToList();
        if (alerts.Count == 0) return [];
        var ids = alerts.Select(a => a.Id).ToList();
        var sent = (await db.AlertDeliveries.AsNoTracking().Where(d => d.Channel == channel && ids.Contains(d.AlertId)).ToListAsync(ct))
            .ToLookup(d => d.AlertId);
        var names = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.SiteId, s => s.Name, ct);
        var items = new List<NotificationItem>();
        foreach (var alert in alerts)
        {
            var raised = sent[alert.Id].Where(d => d.Event == Raised).ToList();
            AlertSeverity? sentAt = raised.Count == 0 ? null : raised.Max(d => d.Severity);
            var name = names.GetValueOrDefault(alert.SiteId) ?? alert.SiteId;
            if (alert.ResolvedUtc is null)
            {
                if (alert.Severity < minimum || (sentAt is { } s && s >= alert.Severity)) continue;
                if (quiet && alert.Severity != AlertSeverity.Critical) continue;
                items.Add(new NotificationItem(alert, name, Cleared: false, Escalated: sentAt is not null));
            }
            else if (sentAt is not null && !sent[alert.Id].Any(d => d.Event == ClearedEvent))
            {
                if (quiet && sentAt != AlertSeverity.Critical) continue;
                items.Add(new NotificationItem(alert, name, Cleared: true, Escalated: false));
            }
        }
        return items.OrderBy(i => i.Cleared).ThenByDescending(i => i.Alert.Severity).ThenBy(i => i.SiteName).ThenBy(i => i.Alert.OpenedUtc).ToList();
    }

    public static async Task<string?> ConsoleUrlAsync(SettingsStore settings, ServerOptions options, CancellationToken ct) =>
        await settings.GetAsync<ServerSetting>(SettingsStore.Server, ct) is { Host: { Length: > 0 } host }
            ? $"https://{host}:{options.PublishedHttpsPort}/console/alerts"
            : null;

    public static TimeZoneInfo Zone(NotificationScheduleDto schedule)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(schedule.TimeZone) ? "America/New_York" : schedule.TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    public static bool IsEmail(string address) => MailAddress.TryCreate(address.Trim(), out var parsed) && parsed.Address == address.Trim();
}
