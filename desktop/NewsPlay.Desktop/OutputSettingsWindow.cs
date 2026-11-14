using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class DisplayOutputProfile
{
    public List<string> Displays { get; set; } = [];
    public int Channel { get; set; } = 1;
    public string Fit { get; set; } = "Fit";
}
public sealed partial class OutputSettingsWindow : Window
{
    private readonly StackPanel displayList = new();
#if MCR_VARIANT
    private readonly ComboBox channel = new() { ItemsSource = new[] { "Program" }, SelectedIndex = 0 };
    private readonly ComboBox audioTarget = new() { ItemsSource = new[] { "Program", "Source / trim preview" }, SelectedIndex = 0 };
    private const int PreviewAudioTargetIndex = 1;
#else
    private readonly ComboBox channel = new() { ItemsSource = new[] { "Program 1", "Program 2" }, SelectedIndex = 0 };
    private readonly ComboBox audioTarget = new() { ItemsSource = new[] { "Port 1", "Port 2", "Source / trim preview" }, SelectedIndex = 0 };
    private const int PreviewAudioTargetIndex = 2;
#endif
    private readonly ComboBox fit = new() { ItemsSource = new[] { "Fit", "Fill", "Stretch" }, SelectedIndex = 0 };
    private readonly ComboBox audio = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly ComboBox sampleRate = new() { ItemsSource = new[] { 32000, 44100, 48000, 88200, 96000, 176400, 192000 }, SelectedItem = 48000 };
    private readonly ComboBox bitDepth = new() { ItemsSource = new[] { 16, 24, 32 }, SelectedItem = 16 };
    private readonly ComboBox display = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly ComboBox displayMode = new();
    private readonly ComboBox format = new() { ItemsSource = Timecode.Formats };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6), Foreground = System.Windows.Media.Brushes.Khaki };
    private readonly EngineClient client;
    private readonly Func<EngineCommand, Task> send;
    private readonly Action<DisplayOutputProfile> start;
    private readonly string profileFile;
    private readonly Dictionary<string, CheckBox> checks = [];
    private bool loading;
    public OutputSettingsWindow(EngineClient client, Func<EngineCommand, Task> send, Action<DisplayOutputProfile> start, Action stop, int selected)
    {
        this.client = client; this.send = send; this.start = start; profileFile = Path.Combine(client.DataDirectory, "display-output.json");
        Style = (Style)Application.Current.Resources[typeof(Window)]; Title = "Output · Video and audio"; Width = 980; Height = 760; MinWidth = 820; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18) }; var tabs = new TabControl(); Content = root;
        feedback.Text = "Choose an output category. Settings apply to the selected program.";
        var statusCard = SettingsUi.Status(feedback); DockPanel.SetDock(statusCard, Dock.Bottom); root.Children.Add(statusCard);
        tabs.TabStripPlacement = Dock.Left; tabs.ItemContainerStyle = (Style)Application.Current.FindResource("SettingsTabItem");
        tabs.Background = SettingsUi.Ink("#101419"); tabs.BorderThickness = new Thickness(0); root.Children.Add(tabs);
        var heading = new DockPanel(); channel.MinWidth = 140; channel.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(channel, Dock.Right); heading.Children.Add(channel);
        heading.Children.Add(SettingsUi.Heading("Output settings", "Route video, audio and hardware outputs from one place.")); DockPanel.SetDock(heading, Dock.Top); root.Children.Insert(0, heading);
        AddProjectVideoPanel(tabs);
        var gpu = Panel(tabs, "Master video · HDMI / DP"); Label(gpu, "Windows display destinations (same program can mirror to multiple displays)"); gpu.Children.Add(displayList);
        Button(gpu, "Refresh displays", () => { RefreshDisplays(checks.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToList()); return Task.CompletedTask; });
        Label(gpu, "Picture scaling"); gpu.Children.Add(fit); Label(gpu, "HDMI/DP follows the connector physically attached to the selected Windows display. Native preview frames are scaled; this is not full-resolution SDI output.");
        Label(gpu, "Display resolution / refresh rate"); Button(gpu, "Open Windows display settings", () => { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); return Task.CompletedTask; });
        Label(gpu, "Configure physical display mode", true); gpu.Children.Add(display); gpu.Children.Add(displayMode);
        display.SelectionChanged += (_, _) => { if (display.SelectedItem is DisplayDevice monitor) { displayMode.ItemsSource = DisplayModes.All(monitor.Id); displayMode.SelectedItem = DisplayModes.Current(monitor.Id); } };
        Button(gpu, "Apply display resolution / refresh", ApplyDisplayMode);
        Button(gpu, "Start / replace display outputs", () => { var p = Profile(); if (p.Displays.Count == 0) throw new InvalidOperationException("Select at least one display"); if (DisplayDevices.All().Any(d => d.Primary && p.Displays.Contains(d.Id)) && MessageBox.Show(this, "Output will cover the primary display. Press Esc in the output window to exit fullscreen. Continue?", "Primary display output", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return Task.CompletedTask; start(p); Save(p); feedback.Text = "Display output started. Audio follows the chosen audio endpoint, not every video mirror."; return Task.CompletedTask; });
        Button(gpu, "Stop display outputs", () => { stop(); feedback.Text = "Display outputs closed; playout engine continues."; return Task.CompletedTask; });
        var av = Panel(tabs, "Audio routing / PCM"); Label(av, "Independent audio destination", true); av.Children.Add(audioTarget); av.Children.Add(audio); Button(av, "Refresh audio devices", RefreshAudio);
        Button(av, "Save audio diagnostics…", async () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Audio diagnostics|*.json", FileName = "NewsPlay-audio-diagnostics.json" };
            if (dialog.ShowDialog(this) != true) return;
            var state = await client.GetState();
            var routes = state.Channels.Select(c => new { c.Id, c.Status, c.AudioDeviceId, c.AudioSampleRate, c.AudioBitDepth, c.GainDb, c.Mute, c.AutoLevel });
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Version = typeof(OutputSettingsWindow).Assembly.GetName().Version?.ToString(), Routes = routes, Timing = await client.AudioSyncStatus() }, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
            feedback.Text = "Audio diagnostics saved: routing, render queue, source starvation and callback errors. No media URLs or stream credentials included.";
        });
        Label(av, "PCM sample frequency (Hz) · broadcast / SDI: 48000"); av.Children.Add(sampleRate); Label(av, "PCM bit depth · stereo L/R"); av.Children.Add(bitDepth);
        Button(av, "Apply audio routing and PCM format", async () => { if (audio.SelectedValue is not string id || sampleRate.SelectedItem is not int rate || bitDepth.SelectedItem is not int bits) throw new InvalidOperationException("Select audio device and PCM format"); var cmd = new EngineCommand { Action = "AudioProfile", Channel = audioTarget.SelectedIndex + 1, Text = id, Value = rate, Index = bits }; if (audioTarget.SelectedIndex == PreviewAudioTargetIndex) await client.SetPreviewAudio(cmd); else await send(cmd); feedback.Text = $"{audioTarget.SelectedItem}: {rate} Hz / {bits}-bit stereo PCM applied. Source preview was stopped if reconfigured."; });
        audioTarget.SelectionChanged += async (_, _) => { if (IsLoaded) { try { await RefreshAudio(); } catch (Exception ex) { feedback.Text = ex.Message; } } };
        Label(av, "Each program and source preview has an independent PCM render client. Windows shared audio can convert PCM to the endpoint's hardware mix format. Pause/stop the program before changing its route. Browser page audio still uses the page / Windows output settings. For native PCM embedded SDI audio use the DeckLink tab. NDI transmission is unavailable.");
        Button(av, "Open Windows sound format settings", () => { Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true }); return Task.CompletedTask; });
        Label(gpu, "Video / timecode standard for selected program", true); Label(gpu, "SD: 720×480 / 720×576 · HD: 1280×720 / 1920×1080 · UHD 4K: 3840×2160 · UHD 8K: 7680×4320. These legacy presets set timecode only. Use Project Video Format for real SD through UHD 2160 production. Physical outputs keep independent modes; 8K production is not supported. For interlaced presets, i50/i59.94/i60 denote fields per second."); gpu.Children.Add(format); Button(gpu, "Apply program standard", async () => { await send(new EngineCommand { Action = "Format", Channel = channel.SelectedIndex + 1, Text = format.SelectedItem as string }); feedback.Text = "Program timecode standard applied. Choose a driver-supported display mode above for the physical output timing. Software frames are scaled; source frame cadence is preserved."; });
        AddAutoLevelPanel(tabs);
        AddDeckLinkPanel(tabs);
        AddHardwareCompatibilityPanel(tabs);
        var ndi = Panel(tabs, "NDI network"); Label(ndi, "NDI input available · output unavailable", true); Label(ndi, "Configure NDI receive in Input → Live Inputs. This panel configures display/audio render endpoints; NDI transmission is not implemented.");
        Loaded += async (_, _) => { try { loading = true; DisplayOutputProfile p = File.Exists(profileFile) ? JsonSerializer.Deserialize<DisplayOutputProfile>(File.ReadAllText(profileFile), Json.Options) ?? new() : new(); channel.SelectedIndex = selected - 1; fit.SelectedItem = new[] { "Fit", "Fill", "Stretch" }.Contains(p.Fit) ? p.Fit : "Fit"; RefreshDisplays(p.Displays); await RefreshAudio(); } catch (Exception ex) { feedback.Text = ex.Message; } finally { loading = false; } };
        channel.SelectionChanged += async (_, _) => { if (!loading && IsLoaded) { try { await RefreshAudio(); } catch (Exception ex) { feedback.Text = ex.Message; } } };
    }
    private DisplayOutputProfile Profile() => new() { Channel = channel.SelectedIndex + 1, Fit = fit.SelectedItem as string ?? "Fit", Displays = checks.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToList() };
    private void Save(DisplayOutputProfile profile) { var tmp = profileFile + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(profile, Json.Options)); File.Move(tmp, profileFile, true); }
    private void RefreshDisplays(List<string> selected) { checks.Clear(); displayList.Children.Clear(); foreach (var d in DisplayDevices.All()) { var box = new CheckBox { Content = d.Name, IsChecked = selected.Contains(d.Id) }; checks[d.Id] = box; displayList.Children.Add(box); } }
    private async Task RefreshAudio() { var previewTarget = audioTarget.SelectedIndex == PreviewAudioTargetIndex; var id = previewTarget ? 1 : Math.Min(2, audioTarget.SelectedIndex + 1); var devices = await client.AudioDevices(id); devices.Insert(0, new("", "Windows default audio endpoint")); audio.ItemsSource = devices; var state = await client.GetState(); format.SelectedItem = state.Channels.Single(c => c.Id == channel.SelectedIndex + 1).Format; if (previewTarget) { var preview = await client.PreviewAudio(); audio.SelectedValue = preview.Text ?? ""; sampleRate.SelectedItem = (int)preview.Value; bitDepth.SelectedItem = preview.Index; } else { var c = state.Channels.Single(c => c.Id == id); audio.SelectedValue = c.AudioDeviceId; sampleRate.SelectedItem = c.AudioSampleRate; bitDepth.SelectedItem = c.AudioBitDepth; } display.ItemsSource = DisplayDevices.All(); if (display.SelectedIndex < 0) display.SelectedIndex = 0; }
    private Task ApplyDisplayMode()
    {
        if (display.SelectedItem is not DisplayDevice monitor || displayMode.SelectedItem is not DisplayMode selected) throw new InvalidOperationException("Select a display and mode");
        var original = DisplayModes.Current(monitor.Id); DisplayModes.Apply(monitor.Id, selected);
        var keep = false; var dialog = new Window { Owner = this, Title = "Keep display mode?", Width = 390, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(15) }; var text = new TextBlock { TextWrapping = TextWrapping.Wrap }; var button = new Button { Content = "Keep this display mode" }; panel.Children.Add(text); panel.Children.Add(button); dialog.Content = panel;
        var seconds = 15; var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; text.Text = "Display reverts automatically in 15 seconds.";
        timer.Tick += (_, _) => { text.Text = $"Display reverts in {--seconds} seconds."; if (seconds <= 0) dialog.Close(); }; button.Click += (_, _) => { keep = true; dialog.Close(); };
        try { timer.Start(); dialog.ShowDialog(); } finally { timer.Stop(); if (!keep) DisplayModes.Apply(monitor.Id, original); }
        feedback.Text = keep ? "Display mode applied." : "Original display mode restored."; RefreshDisplays(checks.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToList()); return Task.CompletedTask;
    }
    private static StackPanel Panel(TabControl tabs, string name)
    {
        var shortName = name == "Project Video Format" ? "Project format" : name == "Hardware Compatibility" ? "Compatibility" : name.StartsWith("Master") ? "Displays" : name.StartsWith("Audio routing") ? "Audio / PCM" : name.StartsWith("Auto") ? "Audio leveling" : name.StartsWith("DeckLink") ? "DeckLink" : "NDI network";
        var p = new StackPanel { Margin = new Thickness(3) }; SettingsUi.Section(p, name);
        tabs.Items.Add(new TabItem { Header = shortName, ToolTip = name, Content = SettingsUi.Card(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = p }) }); return p;
    }
    private static void Label(Panel p, string text, bool bold = false) => p.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = SettingsUi.Ink(bold ? "#DDECF6" : "#AFC2D4"), FontSize = bold ? 14 : 12, Margin = new Thickness(4, bold ? 18 : 10, 4, 6), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
    private void Button(Panel p, string text, Func<Task> action) { var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left }; SettingsUi.Action(b, text); b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } catch (Exception ex) { feedback.Text = ex.Message; } finally { b.IsEnabled = true; } }; p.Children.Add(b); }
}


