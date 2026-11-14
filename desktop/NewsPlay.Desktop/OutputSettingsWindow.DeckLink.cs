using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed partial class OutputSettingsWindow
{
    private void AddDeckLinkPanel(TabControl tabs)
    {
        var panel = Panel(tabs, "DeckLink · SDI / HDMI");
        Label(panel, "DeckLink output · automatic driver compatibility", true);
        Label(panel, "Choose the master port above, then a dedicated card subdevice and supported SDI/HDMI format. Start each port separately. Current and legacy driver interfaces are detected automatically. Start with Device default for fixed SDI/HDMI outputs; card profiles/duplex are configured in Desktop Video Setup.");
        var device = new ComboBox { DisplayMemberPath = "Name", Margin = new Thickness(6) };
        var connection = new ComboBox { Margin = new Thickness(6) };
        var mode = new ComboBox { DisplayMemberPath = "Name", Margin = new Thickness(6) };
        var embedded = new CheckBox { Content = "Embedded stereo audio · 48 kHz / 16-bit native PCM", IsChecked = true };
        var audioDelay = new TextBox { Text="0", Width=90, HorizontalAlignment=HorizontalAlignment.Left, Margin=new Thickness(6) };
        var legal = new CheckBox { Content="SDI nominal range limiter · Y 16–235 / C 16–240 (8-bit)", IsChecked=true };
        var status = new TextBlock { Text = "Click Discover devices to check the card and driver.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
        Label(panel, "DeckLink device / subdevice"); panel.Children.Add(device);
        Label(panel, "Connector"); panel.Children.Add(connection); Label(panel, "Hardware video format (SD/HD)"); panel.Children.Add(mode); panel.Children.Add(embedded);
        Label(panel,"Audio sync trim (ms, −500 to +500; applies to SDI and routed sound-card audio. Positive = audio later)");panel.Children.Add(audioDelay);
        panel.Children.Add(legal);
        device.SelectionChanged += (_, _) => { if (device.SelectedItem is DeckLinkDeviceInfo selected) { connection.ItemsSource = selected.Connections; connection.SelectedIndex = 0; mode.ItemsSource = selected.Modes; mode.SelectedItem = selected.Modes.FirstOrDefault(m => m.Name.Contains("1080i50", StringComparison.OrdinalIgnoreCase)) ?? selected.Modes.FirstOrDefault(); status.Text = selected.Name + "\nDetected API: " + selected.Api + "\n" + selected.Issue + (selected.Modes.Count == 0 ? "\nNo usable modes reported. Save diagnostics below." : "\nChoose a hardware format, then Start output."); } };
        DeckLinkScan? lastScan = null;
        async Task Discover()
        {
            lastScan = await client.DeckLinkDevices();
            device.ItemsSource = lastScan.Devices;
            device.SelectedItem = lastScan.Devices.FirstOrDefault(d => d.Playback && d.Modes.Count > 0) ?? lastScan.Devices.FirstOrDefault();
            if (device.SelectedItem is null) status.Text = lastScan.Message;
            else status.Text += "\n" + lastScan.Message;
        }
        Button(panel, "Discover / refresh devices", Discover);
        Loaded += async (_, _) => { try { await Discover(); } catch (Exception ex) { status.Text = "Discovery failed: " + ex.Message; } };
        Button(panel, "Start DeckLink output for selected port", async () =>
        {
            if (device.SelectedItem is not DeckLinkDeviceInfo selected || mode.SelectedItem is not DeckLinkMode selectedMode || connection.SelectedItem is not string connector) throw new InvalidOperationException("Discover devices and choose a connector and mode first");
            if(!int.TryParse(audioDelay.Text,out var delay)||delay < -500||delay > 500)throw new InvalidOperationException("Audio trim must be −500 to +500 milliseconds");
            var route = new DeckLinkRoute { Port = channel.SelectedIndex + 1, DeviceId = selected.Id, Connection = connector, Mode = selectedMode.Id, Audio = embedded.IsChecked == true, AudioDelayMs=delay, LegalRange=legal.IsChecked==true };
            await client.DeckLinkStart(route);
            File.WriteAllText(Path.Combine(client.DataDirectory, $"decklink-port-{route.Port}.json"), JsonSerializer.Serialize(route, Json.Options));
            status.Text = $"Port {route.Port}: started on {selected.Name} · {connector} · {selectedMode.Name}. Verify picture and embedded audio on the receiving monitor.";
        });
        Button(panel, "Load saved route for selected port", () =>
        {
            var path = Path.Combine(client.DataDirectory, $"decklink-port-{channel.SelectedIndex + 1}.json");
            if (!File.Exists(path)) throw new InvalidOperationException("No saved route for this port. Discover devices, select settings and Start first.");
            var saved = JsonSerializer.Deserialize<DeckLinkRoute>(File.ReadAllText(path), Json.Options)!;
            var selected = device.Items.OfType<DeckLinkDeviceInfo>().FirstOrDefault(d => d.Id == saved.DeviceId) ?? throw new InvalidOperationException("Discover devices first; saved card must be connected.");
            device.SelectedItem = selected; connection.SelectedItem = saved.Connection; mode.SelectedItem = selected.Modes.FirstOrDefault(m => m.Id == saved.Mode); embedded.IsChecked = saved.Audio;
            audioDelay.Text=saved.AudioDelayMs.ToString();
            legal.IsChecked=saved.LegalRange;
            status.Text = "Saved settings loaded. Click Start to enable output."; return Task.CompletedTask;
        });
        Button(panel, "Stop DeckLink output for selected port", async () => { await client.DeckLinkStop(channel.SelectedIndex + 1); status.Text = "DeckLink output stopped for selected port."; });
        Button(panel, "Refresh output status", async () => { var states = await client.DeckLinkStatus(); status.Text = states.Length == 0 ? "No DeckLink output routes are active." : string.Join("\n", states.Select(s => $"Port {s.Port}: {s.Message} · {s.Frames} video / {s.AudioFrames} audio frames submitted")); });
        Button(panel, "Save device diagnostics…", async () =>
        {
            lastScan ??= await client.DeckLinkDevices();
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "DeckLink diagnostics|*.json", FileName = "NewsPlay-DeckLink-diagnostics.json" };
            if (dialog.ShowDialog(this) != true) return;
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(new { Build = "0.1.1", At = DateTimeOffset.Now, Discovery = lastScan, Outputs = await client.DeckLinkStatus() }, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
            status.Text = "Device diagnostics saved. This file contains device/mode information, not stream credentials.";
        });
        panel.Children.Add(status);
        Label(panel, "Current adapter: decoded program picture scaled from the software frame buffer; CG/logo/ticker overlays are not embedded. Native file/network/capture PCM can be embedded; browser-page and NDI audio are not routed into this adapter. Interlaced formats do not restore lost source field cadence. No automatic start on launch. Hardware picture, audio sync and sustained playback must be tested on the target PC.");
        Label(panel, "DeckLink capture remains available through Desktop Video DirectShow in Live Inputs; native SDK capture is not part of this output adapter.");
    }
}

