using Lakeflow.Console.Core;
using Lakeflow.Console.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Settings come from the control API (/console/appsettings.json): tenant, client id and API scope. Nothing is built in,
// so the file is fetched here (Blazor only loads settings files that exist in the build output).
using (var settings = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) })
    builder.Configuration.AddJsonStream(await settings.GetStreamAsync("appsettings.json"));
var scope = builder.Configuration["ApiScope"] ?? throw new InvalidOperationException("ApiScope missing from the console settings.");
var apiBase = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "/");

builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
    options.ProviderOptions.LoginMode = "redirect";
    options.ProviderOptions.Cache.CacheLocation = "sessionStorage";
});

// The API is the same origin; tokens are attached to calls to it only.
builder.Services.AddScoped(sp => new ConsoleApi(new HttpClient(sp.GetRequiredService<AuthorizationMessageHandler>()
    .ConfigureHandler(authorizedUrls: [apiBase.ToString()], scopes: [scope]))
{
    BaseAddress = apiBase,
}));
builder.Services.AddScoped<ISecretSealer, BrowserSecretSealer>();
builder.Services.AddScoped<BrowserFiles>();
builder.Services.AddScoped<UserSession>();

await builder.Build().RunAsync();
