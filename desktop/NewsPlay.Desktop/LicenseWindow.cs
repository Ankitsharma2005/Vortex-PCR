using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NewsPlay.Desktop;

internal sealed class LicenseWindow : Window
{
    private readonly LicenseClient client = new();
    private readonly TextBox server = new() { MinWidth = 390, Text = "https://playout.tejtak.com/public" };
    private readonly TextBox key = new() { MinWidth = 390 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) };
    private readonly Button activate = new() { Content = "Activate NewsPlay", MinWidth = 150, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 8, 0) };
    private readonly Button renew = new() { Content = "Check / renew", MinWidth = 130, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 8, 0) };

    public LicenseWindow()
    {
        Title = "NewsPlay · License"; Width = 570; Height = 410; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        server.Text = client.Server;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "NewsPlay License", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(Label("License server HTTPS URL")); panel.Children.Add(server);
        panel.Children.Add(Label("License key from your portal")); panel.Children.Add(key);
        status.Text = "Status: " + client.Status; panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(activate); buttons.Children.Add(renew);
        var deactivate = new Button { Content = "Deactivate this PC", Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(deactivate); panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "Activation needs internet access. The signed license is stored for offline use during the server's grace period. For PC replacement, deactivate here first or reset the activation from the customer portal.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 16, 0, 0) });
        Content = new Border { Background = Brushes.White, Child = panel };
        activate.Click += async (_, _) => await Run(async () => { await client.Activate(server.Text, key.Text); key.Clear(); });
        renew.Click += async (_, _) => await Run(client.Refresh);
        deactivate.Click += async (_, _) => await Run(async () => { await client.Deactivate(); status.Text = "Status: Not activated"; });
        UpdateStatus();
    }

    private async Task Run(Func<Task> action)
    {
        activate.IsEnabled = renew.IsEnabled = false;
        try { await action(); UpdateStatus(); }
        catch (Exception ex) { status.Text = "License action failed: " + ex.Message; status.Foreground = Brushes.OrangeRed; }
        finally { activate.IsEnabled = renew.IsEnabled = true; }
    }
    private void UpdateStatus()
    {
        status.Text = "Status: " + client.Status;
        status.Foreground = client.ValidateCached(out _) ? Brushes.DarkGreen : Brushes.DarkOrange;
    }
    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 5), FontWeight = FontWeights.SemiBold };
}
