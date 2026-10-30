using System.Text;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Lakeflow.ControlApi.Endpoints;

/// <summary>Configuration → Notifications (Admins): alert email through Graph, a Teams channel, quiet hours and harvest seasons.</summary>
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("").RequireAuthorization(Policies.Admin);

        admin.MapGet("/settings/notifications", DescribeAsync);

        admin.MapPut("/settings/notifications/email", async Task<Results<Ok<NotificationSettingsDto>, ValidationProblem>> (EmailNotificationRequest request,
            SettingsStore settings, NotificationState state, CancellationToken ct) =>
        {
            var recipients = request.Recipients.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var errors = new List<string>();
            var hasSecret = !string.IsNullOrEmpty(request.ClientSecret) || await settings.HasSecretAsync(SettingsStore.NotifyEmail, ct);
            if (request.Enabled || request.TenantId.Length > 0 || request.ClientId.Length > 0)
            {
                if (!Guid.TryParse(request.TenantId.Trim(), out _) && !request.TenantId.Trim().Contains('.'))
                    errors.Add("Tenant: the directory (tenant) ID from the app's Overview page, or your tenant's domain.");
                if (!Guid.TryParse(request.ClientId.Trim(), out _)) errors.Add("Application (client) ID: the GUID from the app's Overview page.");
                if (!Notifier.IsEmail(request.Sender)) errors.Add("Send from: the mailbox address alerts come from.");
                foreach (var bad in recipients.Where(r => !Notifier.IsEmail(r))) errors.Add($"{bad} is not an email address.");
            }
            if (request.Enabled && recipients.Count == 0) errors.Add("Add at least one recipient.");
            if (request.Enabled && !hasSecret) errors.Add("Enter the app's client secret.");
            if (errors.Count > 0) return AuthEndpoints.Invalid(errors);
            await settings.SetAsync(SettingsStore.NotifyEmail, new EmailNotifySetting(request.Enabled, request.TenantId.Trim(), request.ClientId.Trim(),
                request.Sender.Trim(), recipients, request.MinimumSeverity), string.IsNullOrEmpty(request.ClientSecret) ? null : Encoding.UTF8.GetBytes(request.ClientSecret), ct);
            return TypedResults.Ok(await DescribeAsync(settings, state, ct));
        });

        admin.MapPut("/settings/notifications/teams", async Task<Results<Ok<NotificationSettingsDto>, ValidationProblem>> (TeamsNotificationRequest request,
            SettingsStore settings, NotificationState state, CancellationToken ct) =>
        {
            var current = await settings.GetAsync<TeamsNotifySetting>(SettingsStore.NotifyTeams, ct);
            string? host = current?.WebhookHost;
            if (!string.IsNullOrWhiteSpace(request.WebhookUrl))
            {
                if (!Uri.TryCreate(request.WebhookUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                    return AuthEndpoints.Invalid(["Paste the HTTP POST URL from the workflow's \"When a Teams webhook request is received\" step (it starts with https://)."]);
                host = url.Host;
            }
            if (request.Enabled && host is null) return AuthEndpoints.Invalid(["Paste the workflow's URL first."]);
            await settings.SetAsync(SettingsStore.NotifyTeams, new TeamsNotifySetting(request.Enabled, host, request.MinimumSeverity),
                string.IsNullOrWhiteSpace(request.WebhookUrl) ? null : Encoding.UTF8.GetBytes(request.WebhookUrl.Trim()), ct);
            return TypedResults.Ok(await DescribeAsync(settings, state, ct));
        });

        admin.MapPut("/settings/notifications/schedule", async Task<Results<Ok<NotificationSettingsDto>, ValidationProblem>> (NotificationScheduleDto request,
            SettingsStore settings, NotificationState state, CancellationToken ct) =>
        {
            var schedule = request with
            {
                TimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "America/New_York" : request.TimeZone.Trim(),
                QuietStart = string.IsNullOrWhiteSpace(request.QuietStart) ? null : request.QuietStart.Trim(),
                QuietEnd = string.IsNullOrWhiteSpace(request.QuietEnd) ? null : request.QuietEnd.Trim(),
                HarvestSeasons = request.HarvestSeasons.Select(s => new HarvestSeasonDto(s.Name.Trim(), s.Start.Trim(), s.End.Trim())).ToList(),
            };
            var errors = QuietHours.Validate(schedule);
            if (errors.Count > 0) return AuthEndpoints.Invalid(errors);
            await settings.SetAsync(SettingsStore.NotifySchedule, schedule, null, ct);
            return TypedResults.Ok(await DescribeAsync(settings, state, ct));
        });

        // Sends a test now (quiet hours and back-off do not apply) to the saved settings.
        admin.MapPost("/settings/notifications/email/test", async (NotificationTestRequest request, SettingsStore settings, GraphMail mail,
            ServerOptions options, IActor actor, CancellationToken ct) =>
        {
            var (email, secret) = await settings.GetWithSecretAsync<EmailNotifySetting>(SettingsStore.NotifyEmail, ct);
            if (email is null || secret is null || email.Sender.Length == 0) return new SettingsTestResult(false, "Save the email settings first.");
            List<string> to = string.IsNullOrWhiteSpace(request.To) ? [.. email.Recipients] : [request.To.Trim()];
            if (to.Count == 0 || !to.All(Notifier.IsEmail)) return new SettingsTestResult(false, "Give an address to send the test to.");
            try
            {
                var consoleUrl = await Notifier.ConsoleUrlAsync(settings, options, ct);
                await mail.SendAsync(email, Encoding.UTF8.GetString(secret), to, "[Lakeflow] Test alert email",
                    $"<p>This is a test from Lakeflow, sent by {System.Net.WebUtility.HtmlEncode(actor.Name)}. Alert email works.</p>"
                    + (consoleUrl is null ? "" : $"<p><a href=\"{consoleUrl}\">Lakeflow console</a></p>"), ct);
                return new SettingsTestResult(true, $"Sent from {email.Sender} to {string.Join(", ", to)}. Check the inbox (and junk folder).");
            }
            catch (Exception ex) when (ex is NotificationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new SettingsTestResult(false, ex is NotificationException ? ex.Message
                    : $"Could not reach Microsoft ({ex.Message}). This server must reach login.microsoftonline.com and graph.microsoft.com on 443.");
            }
        });

        admin.MapPost("/settings/notifications/teams/test", async (SettingsStore settings, TeamsWorkflow teams, ServerOptions options, IActor actor, CancellationToken ct) =>
        {
            var (team, url) = await settings.GetWithSecretAsync<TeamsNotifySetting>(SettingsStore.NotifyTeams, ct);
            if (team is null || url is null) return new SettingsTestResult(false, "Save the workflow's URL first.");
            var card = new System.Text.Json.Nodes.JsonObject
            {
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["type"] = "AdaptiveCard", ["version"] = "1.4",
                ["body"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "TextBlock", ["wrap"] = true, ["text"] = $"Test from Lakeflow, sent by {actor.Name}. Alerts will appear in this channel.",
                }),
            };
            try
            {
                await teams.PostAsync(Encoding.UTF8.GetString(url), card, ct);
                return new SettingsTestResult(true, "Posted. It shows in the channel within a minute (the workflow runs it).");
            }
            catch (Exception ex) when (ex is NotificationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new SettingsTestResult(false, ex is NotificationException ? ex.Message : $"Could not reach {team.WebhookHost} ({ex.Message}).");
            }
        });
    }

    private static async Task<NotificationSettingsDto> DescribeAsync(SettingsStore settings, NotificationState state, CancellationToken ct)
    {
        var email = await settings.GetAsync<EmailNotifySetting>(SettingsStore.NotifyEmail, ct);
        var team = await settings.GetAsync<TeamsNotifySetting>(SettingsStore.NotifyTeams, ct);
        var schedule = await settings.GetAsync<NotificationScheduleDto>(SettingsStore.NotifySchedule, ct) ?? new NotificationScheduleDto();
        var emailDto = email is null ? new EmailNotificationDto() : new EmailNotificationDto
        {
            Enabled = email.Enabled, TenantId = email.TenantId, ClientId = email.ClientId, Sender = email.Sender, Recipients = email.Recipients,
            MinimumSeverity = email.MinimumSeverity, HasClientSecret = await settings.HasSecretAsync(SettingsStore.NotifyEmail, ct),
        };
        var teamsDto = team is null ? new TeamsNotificationDto() : new TeamsNotificationDto
        {
            Enabled = team.Enabled, WebhookHost = team.WebhookHost, MinimumSeverity = team.MinimumSeverity,
            HasWebhook = await settings.HasSecretAsync(SettingsStore.NotifyTeams, ct),
        };
        return new NotificationSettingsDto(emailDto, teamsDto, schedule, [state.Get(Notifier.Email), state.Get(Notifier.Teams)]);
    }
}
