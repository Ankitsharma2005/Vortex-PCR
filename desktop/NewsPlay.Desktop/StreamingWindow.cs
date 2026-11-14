using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class StreamingWindow : Window
{
    private readonly EngineClient client;
    private readonly DataGrid destinations = new() { SelectionMode = DataGridSelectionMode.Single, MinHeight = 180 };
    private readonly TextBox name = new() { Text = "Stream 1" }, url = new(), bitrate = new() { Text = "4000" };
    private readonly ComboBox audioCodec = Choices("aac", "ac3");
    private readonly ComboBox videoEncoder = new() { SelectedValuePath = "Key", DisplayMemberPath = "Value" };
    private readonly ComboBox codec = Choices("h264", "hevc"), encoderProfile = Choices("auto", "baseline", "main", "high"), encoderLevel = Choices("auto", "3.1", "4.0", "4.1", "4.2", "5.0", "5.1", "5.2", "6.0", "6.1", "6.2");
    private readonly ComboBox audioRate = new() { ItemsSource = new[] { 32000, 44100, 48000 }, SelectedIndex = 2 }, audioChannels = new() { ItemsSource = new[] { 1, 2 }, SelectedIndex = 1 };
    private readonly TextBox audioBitrate = new() { Text = "128" }, keyframe = new() { Text = "2" };
    private readonly TextBox tlsCa = new();
    private readonly ComboBox savedProfiles = new() { DisplayMemberPath = "Name" };
    private Guid draftProfileId = Guid.NewGuid();
    private readonly PasswordBox key = new() { Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(3), Background = Brush("#111923"), Foreground = Brushes.White, BorderBrush = Brush("#36465B") };
    private readonly ComboBox platform = Choices("Custom RTMP", "YouTube", "Facebook Live", "X / Twitter", "SRT caller", "SRT listener", "UDP");
#if MCR_VARIANT
    private readonly ComboBox port = Choices("Program");
#else
    private readonly ComboBox port = Choices("Port 1", "Port 2");
#endif
    private readonly ComboBox size = Choices("640x360", "960x540", "1280x720", "1920x1080", "720x576", "3840x2160");
    private readonly ComboBox fps = new() { ItemsSource = new[] { 25, 30, 50, 60 }, SelectedIndex = 0 };
    private readonly TextBlock notice = Text("Connecting to engine…", "#A7B4C5");
    private readonly TextBlock summary = Text("Checking destinations…", "#A7B4C5");
    private readonly TextBlock detail = Text("Select a destination to see its status.", "#A7B4C5");
    private readonly TextBlock addressHint = Text("Enter the ingest URL supplied by your platform.", "#A7B4C5");
    private readonly StackPanel keyField;
    private readonly StackPanel form = new();
    private readonly Button start = new() { Content = "Start stream", Background = Brush("#176E79"), FontWeight = FontWeights.SemiBold, MinWidth = 135, IsEnabled = false };
    private readonly Button stop = new() { Content = "Stop selected", IsEnabled = false };
    private readonly Button stopAll = new() { Content = "Stop all", IsEnabled = false };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private StreamStatus[] states = [];
    private bool busy, refreshing, connected, closed;

    public StreamingWindow(EngineClient client)
    {
        this.client = client;
        Title = "NewsPlay · Streaming"; Width = 1040; Height = 780; MinWidth = 880; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.Resources[typeof(Window)];
        SettingsUi.Action(start, "Start stream"); SettingsUi.Action(stop, "Stop selected"); SettingsUi.Action(stopAll, "Stop all");
        size.SelectedIndex = 2;
        UpdateEncoders(); codec.SelectionChanged += (_, _) => UpdateEncoders();
        var root = new DockPanel { Margin = new Thickness(22) }; Content = root;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(new TextBlock { Text = "Streaming", FontSize = 25, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(Text("Send either program to up to four destinations.", "#A7B4C5"));
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new Border { Padding = new Thickness(12), Background = Brush("#17232E"), CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 14, 0, 0), Child = notice };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new() { Width = new GridLength(1.05, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new()); root.Children.Add(columns);
        var left = new DockPanel(); var leftCard = Card(left); leftCard.Margin = new Thickness(0, 0, 16, 0); columns.Children.Add(leftCard);
        var liveHeading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        liveHeading.Children.Add(SectionTitle("Destinations")); liveHeading.Children.Add(summary); DockPanel.SetDock(liveHeading, Dock.Top); left.Children.Add(liveHeading);
        var liveFooter = new StackPanel(); var actions = new WrapPanel(); actions.Children.Add(stop); actions.Children.Add(stopAll); liveFooter.Children.Add(actions);
        detail.Margin = new Thickness(3, 12, 3, 0); liveFooter.Children.Add(detail);
        liveFooter.Children.Add(Text("Streams keep running when this panel closes.", "#A7B4C5", new Thickness(3, 18, 3, 0)));
        DockPanel.SetDock(liveFooter, Dock.Bottom); left.Children.Add(liveFooter);
        destinations.Columns.Add(new DataGridTextColumn { Header = "DESTINATION", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        destinations.Columns.Add(new DataGridTextColumn { Header = "PORT", Binding = new System.Windows.Data.Binding("Port"), Width = 48 });
        destinations.Columns.Add(new DataGridTextColumn { Header = "STATUS", Binding = new System.Windows.Data.Binding("State"), Width = 92 });
        destinations.SelectionChanged += (_, _) => UpdateSelection(); left.Children.Add(destinations);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = form };
        var editor = new DockPanel(); start.Margin = new Thickness(3, 12, 3, 0); DockPanel.SetDock(start, Dock.Bottom); editor.Children.Add(start); editor.Children.Add(scroll);
        var right = Card(editor); Grid.SetColumn(right, 1); columns.Children.Add(right);
        form.Children.Add(SectionTitle("New destination"));
        var profileActions = new WrapPanel(); var loadProfile = new Button { Content = "Load options" }; var saveProfile = new Button { Content = "Save options" }; profileActions.Children.Add(loadProfile); profileActions.Children.Add(saveProfile);
        form.Children.Add(Field("Saved stream options · URLs and keys excluded", savedProfiles)); form.Children.Add(profileActions);
        loadProfile.Click += (_, _) => { if (savedProfiles.SelectedItem is StreamProfile profile) LoadProfile(profile); };
        saveProfile.Click += async (_, _) => await RunAction(async () => { var profile = BuildProfile(); profile.Url = ""; await client.SaveStreamProfile(profile); await LoadProfiles(); SetNotice("Stream options saved. Re-enter the destination URL/key when loading."); });
        form.Children.Add(Pair(Field("Name", name), Field("Program", port)));
        form.Children.Add(Field("Platform / protocol", platform)); form.Children.Add(Field("Server URL", url)); form.Children.Add(addressHint);
        keyField = Field("Stream key · optional", key); form.Children.Add(keyField);
        form.Children.Add(Pair(Field("Output size", size), Field("Video bitrate · kbps", bitrate)));
        form.Children.Add(Text("Streaming size and FPS are independent of project and DeckLink formats.", "#A7B4C5", new Thickness(3, 8, 3, 12)));
        var advanced = new StackPanel(); advanced.Children.Add(Field("Video codec", codec)); advanced.Children.Add(Field("Video encoder", videoEncoder)); advanced.Children.Add(Pair(Field("Frame rate", fps), Field("Keyframe interval · seconds", keyframe)));
        advanced.Children.Add(Pair(Field("Encoder profile", encoderProfile), Field("Encoder level", encoderLevel)));
        advanced.Children.Add(Pair(Field("Audio codec", audioCodec), Field("Audio bitrate · kbps", audioBitrate)));
        advanced.Children.Add(Pair(Field("Audio sample rate · Hz", audioRate), Field("Audio channels", audioChannels)));
        advanced.Children.Add(Field("RTMPS custom CA certificate · optional local PEM file", tlsCa));
        form.Children.Add(new Expander { Header = "Advanced settings", Foreground = Brush("#DDE7F5"), Content = advanced, Margin = new Thickness(3, 3, 3, 12) });
        var notes = new StackPanel();
        notes.Children.Add(Text("Set program audio to 48 kHz. RTMP uses AAC; SRT and UDP also support AC3. GPU encoding requires a compatible driver and FFmpeg build.", "#A7B4C5"));
        notes.Children.Add(Text("Source: selected project production raster, or the legacy 960 × 540 buffer for older projects. Smaller sources are upscaled, not native UHD. HEVC is available for SRT/UDP when the encoder supports it. Desktop CG and browser/NDI bridge audio are not included.", "#A7B4C5", new Thickness(0, 10, 0, 0)));
        form.Children.Add(new Expander { Header = "Output details", Foreground = Brush("#A7B4C5"), Content = notes, Margin = new Thickness(3, 14, 3, 0) });
        platform.SelectionChanged += (_, _) => UpdateProtocol(); UpdateProtocol();
        start.Click += async (_, _) => await RunAction(StartStream);
        stop.Click += async (_, _) => { if (destinations.SelectedItem is StreamStatus selected) await RunAction(async () => { await client.StreamStop(selected.Id); SetNotice("Destination removed."); }); };
        stopAll.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "Stop all streaming destinations?", "Stop streams", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await RunAction(async () => { var current = await client.StreamStatus(); await Task.WhenAll(current.Select(s => client.StreamStop(s.Id))); SetNotice("All streams stopped."); });
        };
        timer.Tick += async (_, _) => { if (!busy) await Refresh(); };
        Loaded += async (_, _) => { await Refresh(); try { await LoadProfiles(); } catch (Exception ex) { SetNotice(ex.Message, true); } if (!closed) timer.Start(); };
        Closed += (_, _) => { closed = true; timer.Stop(); key.Clear(); };
    }
    private string Protocol => platform.SelectedItem is string value && (value.StartsWith("SRT", StringComparison.Ordinal) || value == "UDP") ? value : "RTMP";
    private void UpdateProtocol()
    {
        var rtmp = Protocol == "RTMP"; keyField.Visibility = rtmp ? Visibility.Visible : Visibility.Collapsed;
        if (!rtmp) key.Clear(); audioCodec.IsEnabled = !rtmp; if (rtmp) audioCodec.SelectedIndex = 0;
        codec.IsEnabled = !rtmp; if (rtmp) codec.SelectedItem = "h264";
        addressHint.Text = Protocol switch
        {
            "SRT caller" => "Example: srt://receiver-host:9000 · connects to your receiver.",
            "SRT listener" => "Example: srt://0.0.0.0:9000 · waits for a receiver connection.",
            "UDP" => "Example: udp://receiver-host:1234",
            _ => "Use your platform's rtmp:// or rtmps:// ingest URL. Keys are not saved."
        };
    }
    private void UpdateEncoders()
    {
        var selected = Math.Max(0, videoEncoder.SelectedIndex); var hevc = (string)codec.SelectedItem == "hevc";
        var values = hevc ? new[] { "libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf" } : new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf" };
        videoEncoder.Items.Clear(); var labels = new[] { "CPU", "NVIDIA · NVENC", "Intel · Quick Sync", "AMD · AMF" };
        for (int i = 0; i < values.Length; i++) videoEncoder.Items.Add(new KeyValuePair<string, string>(values[i], labels[i])); videoEncoder.SelectedIndex = selected;
        encoderProfile.ItemsSource = hevc ? new[] { "auto", "main" } : new[] { "auto", "baseline", "main", "high" }; encoderProfile.SelectedIndex = 0;
    }
    private async Task LoadProfiles() { var id = (savedProfiles.SelectedItem as StreamProfile)?.Id; var profiles = await client.StreamProfiles(); savedProfiles.ItemsSource = profiles; savedProfiles.SelectedItem = profiles.FirstOrDefault(p => p.Id == id) ?? profiles.FirstOrDefault(); }
    private void LoadProfile(StreamProfile profile)
    {
        draftProfileId = profile.Id; name.Text = profile.Name; platform.SelectedItem = profile.Protocol == "RTMP" || profile.Protocol == "RTMPS" ? "Custom RTMP" : profile.Protocol;
        port.SelectedIndex = profile.Port - 1; size.SelectedItem = $"{profile.Width}x{profile.Height}"; fps.SelectedItem = profile.Fps; bitrate.Text = profile.VideoKbps.ToString(CultureInfo.InvariantCulture);
        codec.SelectedItem = profile.VideoCodec; videoEncoder.SelectedValue = profile.VideoEncoder; encoderProfile.SelectedItem = profile.EncoderProfile; encoderLevel.SelectedItem = profile.EncoderLevel;
        audioCodec.SelectedItem = profile.AudioCodec; audioBitrate.Text = profile.AudioKbps.ToString(CultureInfo.InvariantCulture); audioRate.SelectedItem = profile.AudioSampleRate; audioChannels.SelectedItem = profile.AudioChannels; keyframe.Text = profile.KeyframeSeconds.ToString(CultureInfo.InvariantCulture);
        url.Clear(); key.Clear(); SetNotice("Options loaded. Enter the destination URL/key before starting.");
        tlsCa.Text = profile.TlsCaFile;
    }
    private StreamProfile BuildProfile()
    {
        if (!int.TryParse(bitrate.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps)) throw new InvalidOperationException("Enter a video bitrate from 200 to 50000 kbps.");
        if (!int.TryParse(audioBitrate.Text.Trim(), out var audioKbps) || !int.TryParse(keyframe.Text.Trim(), out var keyframeSeconds)) throw new InvalidOperationException("Enter a numeric audio bitrate and keyframe interval.");
        var dimensions = ((string)size.SelectedItem).Split('x');
        var profile = new StreamProfile
        {
            Id = draftProfileId, Name = name.Text.Trim(), Port = port.SelectedIndex + 1, Protocol = Protocol,
            Url = StreamProfile.IngestUrl(Protocol, url.Text, key.Password),
            Width = int.Parse(dimensions[0], CultureInfo.InvariantCulture), Height = int.Parse(dimensions[1], CultureInfo.InvariantCulture),
            Fps = (int)fps.SelectedItem, VideoKbps = kbps, AudioCodec = (string)audioCodec.SelectedItem, VideoEncoder = (string)videoEncoder.SelectedValue,
            VideoCodec = (string)codec.SelectedItem, EncoderProfile = (string)encoderProfile.SelectedItem, EncoderLevel = (string)encoderLevel.SelectedItem,
            KeyframeSeconds = keyframeSeconds, AudioKbps = audioKbps, AudioSampleRate = (int)audioRate.SelectedItem, AudioChannels = (int)audioChannels.SelectedItem, TlsCaFile = tlsCa.Text.Trim()
        };
        return profile;
    }
    private async Task StartStream()
    {
        var profile = BuildProfile(); profile.Id = Guid.NewGuid(); profile.Validate(); await client.StreamStart(profile); key.Clear();
        SetNotice("Stream started. Check the destination status and verify the receiver.");
    }
    private async Task RunAction(Func<Task> action)
    {
        if (busy || closed) return; busy = true; UpdateButtons(); form.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { SetNotice(ex.Message, true); }
        finally { busy = false; form.IsEnabled = true; await Refresh(); UpdateButtons(); }
    }
    private async Task Refresh()
    {
        if (refreshing || closed) return; refreshing = true;
        try
        {
            var latest = await client.StreamStatus(); if (closed) return;
            var selectedId = (destinations.SelectedItem as StreamStatus)?.Id; var reconnect = !connected;
            connected = true; states = latest; destinations.ItemsSource = latest;
            destinations.SelectedItem = latest.FirstOrDefault(s => s.Id == selectedId) ?? latest.FirstOrDefault();
            summary.Text = latest.Length == 0 ? "No destinations yet" : $"{latest.Length} / 4 destinations · {latest.Count(s => s.State == "Encoding")} encoding";
            if (reconnect) SetNotice("Connected. Ready to stream."); UpdateSelection();
        }
        catch { connected = false; summary.Text = "Engine unavailable"; SetNotice("Cannot refresh streams. Check the engine connection; existing streams may still be running.", true); }
        finally { refreshing = false; UpdateButtons(); }
    }
    private void UpdateSelection()
    {
        detail.Text = destinations.SelectedItem is StreamStatus selected ? $"{selected.State} · {selected.Frames:N0} frames · {selected.DroppedFrames:N0} skipped\n{selected.Format}\n{selected.Message}" : "Start a stream, then select it here to monitor or stop it.";
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        start.IsEnabled = !busy && connected && states.Length < 4; start.Content = busy ? "Please wait…" : states.Length >= 4 ? "4 destinations in use" : "Start stream";
        stop.IsEnabled = !busy && connected && destinations.SelectedItem is StreamStatus; stopAll.IsEnabled = !busy && connected && states.Length > 0;
    }
    private void SetNotice(string message, bool error = false) { notice.Text = message; notice.Foreground = Brush(error ? "#FFB58A" : "#A7DCD5"); }
    private static ComboBox Choices(params string[] values) => new() { ItemsSource = values, SelectedIndex = 0 };
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static TextBlock Text(string text, string color, Thickness? margin = null) => new() { Text = text, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(3, 5, 3, 3), FontSize = 12 };
    private static TextBlock SectionTitle(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(3, 0, 3, 8) };
    private static Border Card(UIElement content) => new() { Background = Brush("#191F27"), BorderBrush = Brush("#303B49"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = content };
    private static StackPanel Field(string label, Control control) { var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 2) }; panel.Children.Add(Text(label, "#A7B4C5")); panel.Children.Add(control); return panel; }
    private static Grid Pair(UIElement left, UIElement right) { var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); Grid.SetColumn(right, 1); grid.Children.Add(left); grid.Children.Add(right); return grid; }
}
