using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed partial class OutputSettingsWindow
{
    private void AddAutoLevelPanel(TabControl tabs)
    {
        var panel = Panel(tabs, "Auto audio level");
        Label(panel, "Automatic clip volume balance · selected program port", true);
        var enabled = new CheckBox { Content = "Enable automatic audio leveling" }; panel.Children.Add(enabled);
        var mode = new ComboBox { ItemsSource = new[] { "RMS", "LUFS" }, SelectedIndex = 0 }; Label(panel, "Leveling mode · LUFS requires 48000 Hz PCM"); panel.Children.Add(mode);
        var lufsTarget = new TextBox { Text = "-23" }; Label(panel, "LUFS target (-36 to -6; broadcast target -23)"); panel.Children.Add(lufsTarget);
        var target = new TextBox(); var ceiling = new TextBox(); var boost = new TextBox();
        Label(panel, "Target RMS level (dBFS, -36 to -6; recommended -18)"); panel.Children.Add(target);
        Label(panel, "Sample peak ceiling (dBFS, -6 to 0; recommended -1)"); panel.Children.Add(ceiling);
        Label(panel, "Maximum quiet-clip boost (dB, 0 to 24; recommended 12)"); panel.Children.Add(boost);
        async Task Reload()
        {
            try { var s = (await client.GetState()).Channels.Single(c => c.Id == channel.SelectedIndex + 1).AutoLevel;
                mode.SelectedItem = s.Mode; lufsTarget.Text = s.TargetLufs.ToString(CultureInfo.InvariantCulture); enabled.IsChecked = s.Enabled; target.Text = s.TargetDb.ToString(CultureInfo.InvariantCulture); ceiling.Text = s.CeilingDb.ToString(CultureInfo.InvariantCulture); boost.Text = s.MaxBoostDb.ToString(CultureInfo.InvariantCulture);
            } catch (Exception ex) { feedback.Text = ex.Message; }
        }
        Button(panel, "Apply to selected port", async () =>
        {
            var s = new AudioLevelSettings { Enabled = enabled.IsChecked == true, Mode = (string)mode.SelectedItem, TargetLufs = double.Parse(lufsTarget.Text, CultureInfo.InvariantCulture), TargetDb = double.Parse(target.Text, CultureInfo.InvariantCulture), CeilingDb = double.Parse(ceiling.Text, CultureInfo.InvariantCulture), MaxBoostDb = double.Parse(boost.Text, CultureInfo.InvariantCulture) };
            s.Validate(); await send(new EngineCommand { Action = "AudioAutoLevel", Channel = channel.SelectedIndex + 1, AutoLevel = s }); feedback.Text = "Audio leveling saved and applied live.";
        });
        Label(panel, "Balances stereo native file/network PCM before output meters, Windows audio, SDI and encoder taps. Silence is not boosted further; manual clip/port gain still applies. Browser page and NDI bridge audio bypass this processor. LUFS mode uses 400 ms stereo K-weighted loudness, silence gating and smooth gain adjustment. It is live momentary loudness leveling, not integrated programme normalization or EBU R128 certification. The ceiling remains sample peak, not true peak.");
        Loaded += async (_, _) => await Reload(); channel.SelectionChanged += async (_, _) => { if (IsLoaded) await Reload(); };
    }
}
