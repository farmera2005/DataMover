using System.Net;
using System.Text.Json;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>The API serves the web console at /console, with its sign-in settings and a strict content policy.</summary>
public sealed class WebConsoleTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private HttpClient Browser()
    {
        Skip.If(ApiFactory.SkipReason is not null, ApiFactory.SkipReason);
        return api.Anonymous();
    }

    [SkippableFact]
    public async Task Console_shell_and_deep_links_are_served_without_signing_in()
    {
        var browser = Browser();
        foreach (var path in new[] { "console/", "console/sites/stmarys", "console/admin" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("<base href=\"/console/\" />", html);
            Assert.Contains("_framework/blazor.webassembly.js", html);
            Assert.Contains("wasm-unsafe-eval", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        }
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("console/js/seal.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("console/_framework/blazor.webassembly.js")).StatusCode);
        // Library assets resolve under the console's base path too (the sign-in library's script).
        var msal = await browser.GetAsync("console/_content/Microsoft.Authentication.WebAssembly.Msal/AuthenticationService.js");
        Assert.Equal(HttpStatusCode.OK, msal.StatusCode);
        Assert.Equal("text/javascript", msal.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    public async Task Console_settings_come_from_the_api_configuration()
    {
        var json = JsonDocument.Parse(await Browser().GetStringAsync("console/appsettings.json")).RootElement;
        Assert.Equal("https://login.microsoftonline.com/test-tenant", json.GetProperty("AzureAd").GetProperty("Authority").GetString());
        Assert.True(json.TryGetProperty("ApiScope", out _));
    }

    [SkippableFact]
    public async Task Api_routes_are_not_swallowed_by_the_console()
    {
        var browser = Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("no-such-route")).StatusCode);
        Assert.False((await browser.GetAsync("status")).Headers.Contains("Content-Security-Policy"));
    }
}
