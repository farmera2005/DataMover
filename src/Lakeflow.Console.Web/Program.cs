using Lakeflow.Console.Core;
using Lakeflow.Console.Core.ViewModels;
using Lakeflow.Console.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The API is this same server; the browser keeps the session cookie and sends it with every call.
var apiBase = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "/");
builder.Services.AddSingleton(_ => ConsoleApi.Create(apiBase));
builder.Services.AddSingleton<ShellViewModel>();
builder.Services.AddSingleton<ISecretSealer, BrowserSecretSealer>();
builder.Services.AddSingleton<BrowserFiles>();

await builder.Build().RunAsync();
