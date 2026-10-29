using Lakeflow.Console.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Lakeflow.Console.Pages;

public sealed partial class EnrollmentPage : Page
{
    public EnrollmentViewModel ViewModel { get; } = new(App.Api);

    public EnrollmentPage() => InitializeComponent();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Token is null) return;
        var package = new DataPackage();
        package.SetText(ViewModel.Token.InstallCommand);
        Clipboard.SetContent(package);
    }
}
