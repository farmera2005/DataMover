using Lakeflow.Contracts;
using Lakeflow.Console.Core.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Lakeflow.Console;

/// <summary>Small functions the pages call from x:Bind, instead of value converters.</summary>
public static class Ui
{
    private static readonly SolidColorBrush Green = new(ColorHelper.FromArgb(255, 0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush Amber = new(ColorHelper.FromArgb(255, 0xE0, 0x9B, 0x00));
    private static readonly SolidColorBrush Red = new(ColorHelper.FromArgb(255, 0xC6, 0x28, 0x28));

    public static Brush HealthBrush(Health health) => health switch { Health.Green => Green, Health.Amber => Amber, _ => Red };

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ShowText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility StepIs(WizardStep step, string name) => step.ToString() == name ? Visibility.Visible : Visibility.Collapsed;

    public static string Text(object? value) => value?.ToString() ?? "";

    public static string Time(DateTimeOffset? value) => value is null ? "" : value.Value.ToLocalTime().ToString("g");

    public static string Join(System.Collections.Generic.IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);
}
