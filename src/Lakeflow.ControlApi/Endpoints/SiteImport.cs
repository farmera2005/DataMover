using System.Globalization;
using System.Text;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Endpoints;

/// <summary>
/// Many sites at once from CSV (Configuration, then Sites and collectors, Import), checked row by row with the same rules
/// as saving one site; previewed first, then saved all together or not at all.
/// </summary>
public static class SiteImport
{
    public const int MaxRows = 1000;
    public static readonly string[] Columns = ["site_id", "name", "time_zone", "latitude", "longitude", "maintenance_start", "maintenance_end", "collector"];

    public static void MapSiteImport(this IEndpointRouteBuilder app) =>
        app.MapPost("/sites/import", async Task<Results<Ok<SiteImportResult>, ValidationProblem>> (SiteImportRequest request, LakeflowDb db, CancellationToken ct) =>
        {
            var table = Csv.Parse(request.Csv ?? "");
            if (table.Count == 0) return AuthEndpoints.Invalid(["Paste the CSV, starting with a header row (site_id,name,...)."]);
            var header = table[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
            var unknown = header.Where(h => !Columns.Contains(h)).ToList();
            var problems = new List<string>();
            if (!header.Contains("site_id") || !header.Contains("name")) problems.Add("The header needs at least site_id and name.");
            if (unknown.Count > 0) problems.Add($"Unknown column{(unknown.Count == 1 ? "" : "s")}: {string.Join(", ", unknown)}. Columns: {string.Join(", ", Columns)}.");
            if (header.Distinct().Count() != header.Count) problems.Add("A column appears twice in the header.");
            if (table.Count - 1 > MaxRows) problems.Add($"At most {MaxRows} sites at a time.");
            if (problems.Count > 0) return AuthEndpoints.Invalid(problems);

            var agents = await db.Agents.Where(a => a.RevokedUtc == null).ToListAsync(ct);
            var rows = new List<SiteImportRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i < table.Count; i++)
            {
                var cells = table[i];
                if (cells.All(string.IsNullOrWhiteSpace)) continue;
                string? Cell(string column) => header.IndexOf(column) is var at and >= 0 && at < cells.Count && cells[at].Trim() is { Length: > 0 } v ? v : null;
                var errors = new List<string>();
                if (cells.Count > header.Count) errors.Add($"{cells.Count} values for {header.Count} columns (a comma in a name needs quotes).");
                var siteId = Cell("site_id");
                if (siteId is null) { rows.Add(new SiteImportRow(i + 1, null, Cell("name"), "error", ["site_id is empty."])); continue; }
                if (!seen.Add(siteId)) { rows.Add(new SiteImportRow(i + 1, siteId, Cell("name"), "error", [$"{siteId} is already on an earlier line."])); continue; }

                var site = await db.Sites.FindAsync([siteId], ct);
                var isNew = site is null;
                var current = site is null ? new SiteDto { SiteId = siteId, Name = "" } : ConsoleEndpoints.ToDto(site);
                double? Number(string column, double? keep)
                {
                    if (Cell(column) is not { } text) return keep;
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
                    errors.Add($"{column} '{text}' is not a number.");
                    return keep;
                }
                TimeOnly? Time(string column, TimeOnly? keep)
                {
                    if (Cell(column) is not { } text) return keep;
                    if (TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) return value;
                    errors.Add($"{column} '{text}' is not a time like 02:00.");
                    return keep;
                }
                var collector = current.CollectorId;
                if (Cell("collector") is { } who)
                {
                    var matches = agents.Where(a => a.AgentId == who || string.Equals(a.Name, who, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 1) collector = matches[0].AgentId;
                    else errors.Add(matches.Count == 0 ? $"No active collector is called {who}." : $"{matches.Count} collectors are called {who}; use its id.");
                }
                var latitude = Number("latitude", current.Latitude);
                var longitude = Number("longitude", current.Longitude);
                if (latitude is < -90 or > 90) errors.Add("latitude is between -90 and 90.");
                if (longitude is < -180 or > 180) errors.Add("longitude is between -180 and 180.");
                var dto = current with
                {
                    Name = Cell("name") ?? current.Name,
                    TimeZone = Cell("time_zone") ?? current.TimeZone,
                    Latitude = latitude,
                    Longitude = longitude,
                    MaintenanceStart = Time("maintenance_start", current.MaintenanceStart),
                    MaintenanceEnd = Time("maintenance_end", current.MaintenanceEnd),
                    CollectorId = collector,
                };
                if (!isNew && dto == current && errors.Count == 0)
                {
                    rows.Add(new SiteImportRow(i + 1, siteId, dto.Name, "unchanged", []));
                    continue;
                }
                if (site is null) db.Sites.Add(site = new Site { SiteId = siteId, Name = dto.Name });
                errors.AddRange(await ConsoleEndpoints.ApplySiteAsync(site, dto, db, ct));
                rows.Add(new SiteImportRow(i + 1, siteId, dto.Name, errors.Count > 0 ? "error" : isNew ? "create" : "update", errors));
            }
            var valid = rows.All(r => r.Action != "error");
            var applied = request.Apply && valid && rows.Count > 0;
            if (applied) await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new SiteImportResult(applied, rows.Count(r => r.Action == "create"), rows.Count(r => r.Action == "update"),
                rows.Count(r => r.Action == "unchanged"), rows));
        }).RequireAuthorization(Policies.Admin);
}

/// <summary>RFC 4180 CSV: commas or semicolons (whichever the header uses), quoted fields with doubled quotes, any newline.</summary>
public static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n', 2)[0];
        var separator = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == separator) { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows.Where(r => !(r.Count == 1 && r[0].Length == 0)).ToList();
    }
}
