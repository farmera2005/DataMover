using Lakeflow.Contracts;
using Lakeflow.Console.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lakeflow.Console.Pages;

public sealed partial class FleetPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };

    public FleetViewModel ViewModel { get; } = new(App.Api);

    public FleetPage()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _timer.Start();
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    private void OnHealthFilter(object sender, SelectionChangedEventArgs e) =>
        ViewModel.HealthFilter = HealthBox.SelectedIndex switch { 1 => Health.Red, 2 => Health.Amber, 3 => Health.Green, _ => null };

    private void OnSiteClick(object sender, ItemClickEventArgs e) =>
        Frame.Navigate(typeof(SiteDetailPage), ((SiteRow)e.ClickedItem).SiteId);
}
