using System.Windows;
using System.Windows.Controls;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
    private void ShowShortcuts()
    {
        var rows = KeyActions.Select(x => (Hotkeys[x.Gesture].Length == 0 ? "Disabled" : Hotkeys[x.Gesture], x.Action));
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "PLAYOUT KEYBOARD SHORTCUTS", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "Active only in the main console. Typing fields and source-preview controls keep their own keys. Live feeds cannot pause or seek.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var (key, description) in rows)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(195) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = key, FontWeight = FontWeights.SemiBold });
            var label = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }; Grid.SetColumn(label, 1); row.Children.Add(label); panel.Children.Add(row);
        }
        new Window { Owner = this, Title = "Keyboard shortcuts · F12", Width = 700, Height = 720, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.ShowDialog();
    }
}
