using System.Net;
using Xunit;

namespace Lakeflow.ControlApi.Tests;

/// <summary>The API serves the web console at /console (and from /), with a strict content policy and no outside scripts.</summary>
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
        foreach (var path in new[] { "console/", "console/sites/stmarys", "console/config/users" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("<base href=\"/console/\" />", html);
            Assert.Contains("_framework/blazor.webassembly.js", html);
            Assert.DoesNotContain("https://", html);
            var csp = response.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("wasm-unsafe-eval", csp);
            Assert.Contains("frame-ancestors 'none'", csp);
            Assert.Contains("connect-src 'self'", csp);
        }
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("console/js/seal.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("console/_framework/blazor.webassembly.js")).StatusCode);
        var root = await api.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/");
        Assert.Equal("/console/", root.Headers.Location?.ToString());
    }

    [SkippableFact]
    public async Task Api_routes_are_not_swallowed_by_the_console()
    {
        var browser = Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("no-such-route")).StatusCode);
    }
}
