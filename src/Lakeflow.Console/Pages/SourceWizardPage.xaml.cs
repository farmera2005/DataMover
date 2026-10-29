using Lakeflow.Contracts;
using Lakeflow.Console.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lakeflow.Console.Pages;

public sealed partial class SourceWizardPage : Page
{
    public SourceWizardViewModel ViewModel { get; private set; } = null!;

    public SourceWizardPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = new SourceWizardViewModel(App.Api, (string)e.Parameter);
        Bindings.Update();
    }

    private void OnEngine(object sender, SelectionChangedEventArgs e)
    {
        if (EngineBox.SelectedItem is EngineChoice choice) ViewModel.Engine = choice.Engine;
    }

    private void OnDivision(object sender, SelectionChangedEventArgs e)
    {
        if (DivisionBox.SelectedItem is Division division) ViewModel.Division = division;
    }

    private void OnAuth(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Auth = AuthBox.SelectedIndex == 1 ? AuthMethod.Windows : AuthMethod.SqlLogin;

    // PasswordBox cannot be bound; the value goes to the view model only until it is sealed.
    private void OnPassword(object sender, RoutedEventArgs e) => ViewModel.Password = PasswordBox.Password;

    private void OnDone(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SiteDetailPage), ViewModel.SiteId);
}
