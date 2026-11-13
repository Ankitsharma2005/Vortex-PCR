using System.Windows;
using System.Windows.Controls;
namespace NewsPlay.Desktop;
public sealed class PromptWindow : Window
{
    public static string? Ask(Window owner, string title, string description, string value = "")
    {
        var window = new PromptWindow { Style = (Style)Application.Current.Resources[typeof(Window)], Owner = owner, Title = title, Width = 510, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 0, 3, 12) });
        var input = new TextBox { Text = value }; panel.Children.Add(input); var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var save = new Button { Content = "Continue", IsDefault = true }; save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) window.DialogResult = true; }; buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); window.Content = panel; window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
