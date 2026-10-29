using Lakeflow.Contracts;
using Lakeflow.Console.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Lakeflow.Console.Pages;

public sealed partial class SiteDetailPage : Page
{
    public SiteDetailViewModel ViewModel { get; private set; } = null!;

    public SiteDetailPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = new SiteDetailViewModel(App.Api, App.Shell.User!, (string)e.Parameter);
        Bindings.Update();
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnAddSource(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SourceWizardPage), ViewModel.SiteId);

    private async void OnPause(object sender, RoutedEventArgs e) => await Confirmed(sender, "Pause", ViewModel.PauseTableCommand.ExecuteAsync);
    private async void OnResume(object sender, RoutedEventArgs e) => await Confirmed(sender, "Resume", ViewModel.ResumeTableCommand.ExecuteAsync);
    private async void OnResnapshot(object sender, RoutedEventArgs e) => await Confirmed(sender, "Resnapshot", ViewModel.ResnapshotTableCommand.ExecuteAsync);

    /// <summary>Table actions change what lands in the lake, so they are confirmed first.</summary>
    private async Task Confirmed(object sender, string verb, Func<object?, Task> action)
    {
        var row = (TableRow)((FrameworkElement)sender).Tag;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{verb} {row.Table}?",
            Content = verb == "Resnapshot"
                ? "The whole table is copied again from the site. The scale system is read in small, throttled batches."
                : $"{verb} capture of {row.Table} at {ViewModel.Site?.Name}.",
            PrimaryButtonText = verb,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await action(row);
    }

    private async void OnDownloadLogs(object sender, RoutedEventArgs e)
    {
        var command = (CommandDto)((FrameworkElement)sender).Tag;
        var zip = await ViewModel.DownloadLogsAsync(command);
        if (zip is null) return;
        var picker = new FileSavePicker { SuggestedFileName = $"{ViewModel.SiteId}-logs" };
        picker.FileTypeChoices.Add("Zip archive", [".zip"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).Window));
        if (await picker.PickSaveFileAsync() is { } file) await File.WriteAllBytesAsync(file.Path, zip);
    }
}
