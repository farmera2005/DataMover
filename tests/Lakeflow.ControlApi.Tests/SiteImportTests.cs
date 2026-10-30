using System.Net;
using Lakeflow.ControlApi.Endpoints;
using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>Bulk site import from CSV: previewed row by row, saved all together or not at all.</summary>
public sealed class SiteImportTests : IAsyncLifetime
{
    private readonly ApiFactory _api = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public void Csv_handles_quotes_semicolons_and_any_newline()
    {
        var rows = Csv.Parse("﻿site_id,name\r\nstmarys,\"St. Marys, Grain \"\"North\"\"\"\nceleste,Celeste\r\n\r\n");
        Assert.Equal([["site_id", "name"], ["stmarys", "St. Marys, Grain \"North\""], ["celeste", "Celeste"]], rows);
        Assert.Equal([["site_id", "name"], ["a", "b,c"]], Csv.Parse("site_id;name\na;b,c"));
    }

    [SkippableFact]
    public async Task A_preview_shows_every_problem_and_saves_nothing_then_the_fixed_file_is_imported()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = _api.Console();
        var (agent, _) = await _api.EnrollAsync(admin, await SiteAsync(admin, "anchor"));
        var collectorName = (await admin.GetAsync<List<AgentDto>>("agents")).Single(a => a.AgentId == agent).Name;

        const string header = "site_id,name,time_zone,latitude,longitude,maintenance_start,maintenance_end,collector";
        var bad = $"""
            {header}
            stmarys,"St. Marys, Grain",America/New_York,40.54,-84.39,02:00,04:00,{collectorName}
            celeste,Celeste,Mars/Olympus,,,,,
            celeste,Celeste again,,,,,,
            ,No id,,,,,,
            rockford,Rockford,,91,,2am,,nobody
            """;
        var preview = await (await admin.PostAsync("sites/import", new SiteImportRequest(bad, Apply: true))).ReadAsync<SiteImportResult>();
        Assert.False(preview.Applied);
        Assert.Equal(["create", "error", "error", "error", "error"], preview.Rows.Select(r => r.Action));
        Assert.Contains(preview.Rows[1].Errors, e => e.Contains("time zone"));
        Assert.Contains("earlier line", preview.Rows[2].Errors[0]);
        Assert.Equal((5, "site_id is empty."), (preview.Rows[3].Line, preview.Rows[3].Errors[0]));
        var rockford = string.Join(" ", preview.Rows[4].Errors);
        foreach (var expected in new[] { "latitude", "not a time", "No active collector is called nobody" }) Assert.Contains(expected, rockford);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("sites/stmarys")).StatusCode); // nothing saved

        var good = $"""
            {header}
            stmarys,"St. Marys, Grain",America/New_York,40.54,-84.39,02:00,04:00,{collectorName}
            celeste,Celeste,America/Chicago,,,,,
            """;
        var dryRun = await (await admin.PostAsync("sites/import", new SiteImportRequest(good, Apply: false))).ReadAsync<SiteImportResult>();
        Assert.Equal((false, 2), (dryRun.Applied, dryRun.Created));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("sites/stmarys")).StatusCode);

        var imported = await (await admin.PostAsync("sites/import", new SiteImportRequest(good, Apply: true))).ReadAsync<SiteImportResult>();
        Assert.Equal((true, 2), (imported.Applied, imported.Created));
        var stmarys = await admin.GetAsync<SiteDto>("sites/stmarys");
        Assert.Equal(("St. Marys, Grain", 40.54, new TimeOnly(2, 0), agent), (stmarys.Name, stmarys.Latitude, stmarys.MaintenanceStart, stmarys.CollectorId));

        // Again: nothing to do. With only some columns: the rest is kept.
        Assert.Equal(2, (await (await admin.PostAsync("sites/import", new SiteImportRequest(good, Apply: true))).ReadAsync<SiteImportResult>()).Unchanged);
        var partial = await (await admin.PostAsync("sites/import", new SiteImportRequest("site_id;name\nstmarys;St. Marys Grain", Apply: true))).ReadAsync<SiteImportResult>();
        Assert.Equal((true, 1), (partial.Applied, partial.Updated));
        stmarys = await admin.GetAsync<SiteDto>("sites/stmarys");
        Assert.Equal(("St. Marys Grain", "America/New_York", agent), (stmarys.Name, stmarys.TimeZone, stmarys.CollectorId));
        Assert.Contains("site stmarys", await admin.GetStringAsync("audit?take=1000"));

        Assert.Contains("Unknown column", await (await admin.PostAsync("sites/import", new SiteImportRequest("site_id,name,colour\na,b,c", true))).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.Console("olive@mercerlandmark.com", Lakeflow.ControlApi.Security.Roles.Operator)
            .PostAsync("sites/import", new SiteImportRequest(good, false))).StatusCode);
    }

    [SkippableFact]
    public async Task The_fleet_lists_each_sites_divisions()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        var admin = _api.Console();
        var grain = await SiteAsync(admin, "grain");
        var feed = await SiteAsync(admin, "feed", Division.Feed);
        var fleet = await admin.GetAsync<FleetStatus>("status");
        Assert.Equal([Division.Grain], fleet.Sites.Single(s => s.SiteId == grain).Divisions);
        Assert.Equal([Division.Feed], fleet.Sites.Single(s => s.SiteId == feed).Divisions);
    }

    private static async Task<string> SiteAsync(HttpClient admin, string prefix, Division division = Division.Grain)
    {
        var site = prefix + Guid.NewGuid().ToString("N")[..6];
        await admin.CreateSiteAsync(site, division);
        return site;
    }
}
