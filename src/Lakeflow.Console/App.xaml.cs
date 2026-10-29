using System.Text.Json;
using Lakeflow.Console.Auth;
using Lakeflow.Console.Core;
using Lakeflow.Console.Core.ViewModels;
using Microsoft.UI.Xaml;

namespace Lakeflow.Console;

/// <summary>Composition root: settings, sign-in, the API client, the window.</summary>
public partial class App : Application
{
    private MainWindow? _window;

    public Window Window => _window!;

    public App() => InitializeComponent();

    /// <summary>Shared by the pages.</summary>
    public static ConsoleApi Api { get; private set; } = null!;
    public static ShellViewModel Shell { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Non-secret settings only (API address, tenant, client id); sign-in is Entra ID.
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        var root = settings.RootElement;
        var entra = root.GetProperty("Entra");

        _window = new MainWindow();
        var tokens = new MsalTokenProvider(entra.GetProperty("TenantId").GetString()!, entra.GetProperty("ClientId").GetString()!,
            entra.GetProperty("Scope").GetString()!, () => WinRT.Interop.WindowNative.GetWindowHandle(_window));
        Api = ConsoleApi.Create(new Uri(root.GetProperty("ControlApi").GetString()!), tokens);
        Shell = new ShellViewModel(tokens);
        _window.Start();
        _window.Activate();
    }
}
