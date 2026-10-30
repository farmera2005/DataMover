using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Endpoints;

/// <summary>Alerts: everyone signed in reads them; Operators and Admins acknowledge them and add notes.</summary>
public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("").RequireAuthorization(Policies.Viewer);
        var operate = app.MapGroup("").RequireAuthorization(Policies.Operator);

        // state: open (default), resolved or all. Newest first; open alerts critical first.
        read.MapGet("/alerts", async (string? state, string? siteId, int? take, LakeflowDb db, CancellationToken ct) =>
        {
            var query = db.Alerts.AsNoTracking().Include(a => a.Notes).AsQueryable();
            query = (state ?? "open") switch
            {
                "resolved" => query.Where(a => a.ResolvedUtc != null),
                "all" => query,
                _ => query.Where(a => a.ResolvedUtc == null),
            };
            if (siteId is not null) query = query.Where(a => a.SiteId == siteId);
            var alerts = await query.OrderByDescending(a => a.ResolvedUtc == null).ThenByDescending(a => a.Severity == AlertSeverity.Critical)
                .ThenByDescending(a => a.OpenedUtc).Take(Math.Clamp(take ?? 200, 1, 1000)).ToListAsync(ct);
            var names = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.SiteId, s => s.Name, ct);
            return alerts.Select(a => ToDto(a, names.GetValueOrDefault(a.SiteId)));
        });

        operate.MapPost("/alerts/{id:guid}/acknowledge", async Task<Results<Ok<AlertDto>, NotFound, ValidationProblem>> (Guid id, AlertNoteRequest request,
            LakeflowDb db, IActor actor, TimeProvider time, CancellationToken ct) =>
        {
            if (await db.Alerts.Include(a => a.Notes).FirstOrDefaultAsync(a => a.Id == id, ct) is not { } alert) return TypedResults.NotFound();
            if (alert.ResolvedUtc is not null) return AuthEndpoints.Invalid(["This alert has already cleared."]);
            if (Text(request.Text) is { Error: { } error }) return AuthEndpoints.Invalid([error]);
            if (alert.AcknowledgedUtc is null)
            {
                (alert.AcknowledgedUtc, alert.AcknowledgedBy) = (time.GetUtcNow(), actor.Name);
                AddNote(db, alert, actor, "acknowledged", Text(request.Text).Value, time);
                await db.SaveChangesAsync(ct);
            }
            return TypedResults.Ok(ToDto(alert, (await db.Sites.FindAsync([alert.SiteId], ct))?.Name));
        });

        operate.MapPost("/alerts/{id:guid}/notes", async Task<Results<Ok<AlertDto>, NotFound, ValidationProblem>> (Guid id, AlertNoteRequest request,
            LakeflowDb db, IActor actor, TimeProvider time, CancellationToken ct) =>
        {
            if (await db.Alerts.Include(a => a.Notes).FirstOrDefaultAsync(a => a.Id == id, ct) is not { } alert) return TypedResults.NotFound();
            var (text, error) = Text(request.Text);
            if (error is not null || text is null) return AuthEndpoints.Invalid([error ?? "Write a note."]);
            AddNote(db, alert, actor, "note", text, time);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(ToDto(alert, (await db.Sites.FindAsync([alert.SiteId], ct))?.Name));
        });
    }

    private static (string? Value, string? Error) Text(string? text) => text?.Trim() switch
    {
        null or "" => (null, null),
        { Length: > 2000 } => (null, "Keep notes under 2000 characters."),
        var t => (t, null),
    };

    private static void AddNote(LakeflowDb db, Alert alert, IActor actor, string kind, string? text, TimeProvider time)
    {
        var note = new AlertNote { Id = Guid.NewGuid(), AlertId = alert.Id, Author = actor.Name, Kind = kind, Text = text, CreatedUtc = time.GetUtcNow() };
        db.AlertNotes.Add(note); // EF also puts it in alert.Notes (the alert is tracked)
    }

    public static AlertDto ToDto(Alert a, string? siteName) => new(a.Id, a.Rule, a.Severity, a.SiteId, siteName, a.SourceId, a.TableName, a.Message,
        a.OpenedUtc, a.LastSeenUtc, a.EscalatedUtc, a.ResolvedUtc, a.AcknowledgedUtc, a.AcknowledgedBy,
        a.Notes.OrderBy(n => n.CreatedUtc).Select(n => new AlertNoteDto(n.Author, n.Kind, n.Text, n.CreatedUtc)).ToList());
}
