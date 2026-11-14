using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NewsPlay.Desktop;

internal static class SettingsUi
{
    public static Brush Ink(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    public static FrameworkElement Heading(string title, string description)
    {
        var panel = new StackPanel { Margin = new Thickness(2, 0, 2, 18) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 25, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = description, Foreground = Ink("#A8BDCE"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        return panel;
    }
    public static Border Card(UIElement content, Thickness? margin = null) => new() { Background = Ink("#18232E"), BorderBrush = Ink("#304455"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(14), Margin = margin ?? new Thickness(0), Child = content };
    public static FrameworkElement Status(TextBlock text)
    {
        text.Foreground = Ink("#B5DFD8"); text.Margin = new Thickness(0); text.MinHeight = 16;
        return new Border { Background = Ink("#172C35"), BorderBrush = Ink("#2A4C56"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 14, 0, 0), Child = text };
    }
    public static void Action(Button button, string label)
    {
        var key = label.StartsWith("Stop", StringComparison.OrdinalIgnoreCase) || label.StartsWith("Delete", StringComparison.OrdinalIgnoreCase) ? "DangerActionButton"
            : new[] { "Start", "Apply", "Save", "TAKE", "+ New", "Back to" }.Any(s => label.StartsWith(s, StringComparison.OrdinalIgnoreCase)) ? "PrimaryActionButton" : "ActionButtonBase";
        button.SetResourceReference(FrameworkElement.StyleProperty, key);
    }
    public static void Section(Panel panel, string title)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Ink("#DCEAF5"), Margin = new Thickness(3, 14, 3, 7) });
    }
}
