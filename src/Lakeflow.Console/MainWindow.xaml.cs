using Lakeflow.Console.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lakeflow.Console;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = false;
    }

    /// <summary>Tries a silent sign-in with the Windows account; shows the sign-in panel if that is not enough.</summary>
    public async void Start() => await SignInAsync();

    private async void OnSignIn(object sender, RoutedEventArgs e) => await SignInAsync();

    private async Task SignInAsync()
    {
        SignInButton.IsEnabled = false;
        await App.Shell.SignInCommand.ExecuteAsync(null);
        SignInButton.IsEnabled = true;
        SignInError.Text = App.Shell.Error ?? "";
        if (App.Shell.User is not { } user) return;

        SignInPanel.Visibility = Visibility.Collapsed;
        Nav.Visibility = Visibility.Visible;
        UserItem.Content = $"{user.Name} ({user.Role})";
        EnrollItem.Visibility = user.CanAdminister ? Visibility.Visible : Visibility.Collapsed;
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private async void OnSignOut(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        await App.Shell.SignOutCommand.ExecuteAsync(null);
        Nav.Visibility = Visibility.Collapsed;
        SignInPanel.Visibility = Visibility.Visible;
    }

    private void OnNavigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        switch ((args.SelectedItem as NavigationViewItem)?.Tag as string)
        {
            case "fleet": ContentFrame.Navigate(typeof(FleetPage)); break;
            case "enroll": ContentFrame.Navigate(typeof(EnrollmentPage)); break;
        }
    }

    private void OnBack(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }
}
