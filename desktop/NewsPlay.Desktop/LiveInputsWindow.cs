using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class LiveInputsWindow : Window
{
    private readonly EngineClient client;
    private readonly BrowserSources browsers;
    private Guid draftBrowserId = Guid.NewGuid();
    private readonly ContentControl browserPanel = new() { MinHeight = 360 };
    private readonly DockPanel browserArea = new() { Margin = new Thickness(8) };
    private readonly ColumnDefinition browserColumn = new() { Width = new GridLength(0) };
    private readonly TextBlock browserStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) };
    private BrowserSources.Session? browserSession;
    private readonly Image inputPicture = new() { Stretch = Stretch.Uniform };
    private readonly FrameReader inputReader = new();
    private int inputSlot = -1;
    private readonly HashSet<Guid> ownedPreviews = [];
    private readonly Func<EngineCommand, Task> send;
    private readonly ListBox sources = new() { DisplayMemberPath = "Name", MinWidth = 200, Background = new SolidColorBrush(Color.FromRgb(48, 49, 55)), Foreground = Brushes.White };
    private readonly TextBox name = new(), address = new(), cache = new() { Text = "500" }, timeout = new() { Text = "8" }, duration = new() { Text = "30" }, schedule = new();
    private readonly ComboBox type = new() { ItemsSource = new[] { "RTSP", "SRT", "RTMP", "HLS", "Screen", "DeckLink", "NDI", "Browser", "USB / Webcam", "Window capture" }, SelectedIndex = 0 };
    private readonly ComboBox videoDevice = new(), audioDevice = new();
    private readonly StackPanel captureOptions = new();
    private readonly StackPanel ndiOptions = new();
    private readonly ComboBox ndiSource = new();
#if MCR_VARIANT
    private readonly ComboBox port = new() { ItemsSource = new[] { "Program" }, SelectedIndex = 0 }, backup = new() { DisplayMemberPath = "Name" };
#else
    private readonly ComboBox port = new() { ItemsSource = new[] { "Port 1", "Port 2" }, SelectedIndex = 0 }, backup = new() { DisplayMemberPath = "Name" };
#endif
    private readonly CheckBox deinterlace = new() { Content = "Deinterlace (YADIF)", IsChecked = true }, mute = new() { Content = "Mute on program" };
    private readonly Slider gain = new() { Minimum = -60, Maximum = 6, Value = 0, Width = 180 };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly TextBlock support = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly WrapPanel monitors = new();
    private readonly Dictionary<Guid, (FrameReader Reader, Image Image, TextBlock Label, Border Border)> tiles = [];
    private readonly Dictionary<Guid, Window> previewWindows = [];
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(1) }, frames = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private List<LivePreviewStatus> statuses = [];
    private bool busy, closed;
    private EngineState? state;
    public LiveInputsWindow(EngineClient client, Func<EngineCommand, Task> send, int channel, BrowserSources browsers)
    {
        this.browsers = browsers;
        this.client = client; this.send = send; port.SelectedIndex = channel - 1;
        Title = "NewsPlay · Live Inputs"; Width = 1120; Height = 760; MinWidth = 900; MinHeight = 650;
        Style = (Style)Application.Current.Resources[typeof(Window)]; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(10) }; Content = root;
        DockPanel.SetDock(message, Dock.Bottom); root.Children.Add(message);
        var tabs = new TabControl(); root.Children.Add(tabs);
        var setup = new Grid(); setup.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); setup.ColumnDefinitions.Add(new ColumnDefinition()); setup.ColumnDefinitions.Add(browserColumn);
        var left = new DockPanel(); var add = Button("+ New input", () => { sources.SelectedItem = null; draftBrowserId = Guid.NewGuid(); browsers.Detach(browserPanel); browserSession = null; name.Text = "Live input"; address.Text = ""; return Task.CompletedTask; }); DockPanel.SetDock(add, Dock.Bottom); left.Children.Add(add); var delete = Button("Delete selected input", DeleteInput); DockPanel.SetDock(delete, Dock.Bottom); left.Children.Add(delete); left.Children.Add(sources); setup.Children.Add(left);
        Grid.SetColumn(browserArea, 2); setup.Children.Add(browserArea);
        var browserTools = new WrapPanel(); browserTools.Children.Add(Button("Load preview", LoadAnyPreview)); browserTools.Children.Add(Button("Refresh", () => { browserSession?.View.CoreWebView2.Reload(); return Task.CompletedTask; }));
        browserTools.Children.Add(Button("Zoom −", () => { if (browserSession is not null) browserSession.View.ZoomFactor = Math.Max(.25, browserSession.View.ZoomFactor - .1); return Task.CompletedTask; }));
        browserTools.Children.Add(Button("Zoom +", () => { if (browserSession is not null) browserSession.View.ZoomFactor = Math.Min(3, browserSession.View.ZoomFactor + .1); return Task.CompletedTask; }));
        browserTools.Children.Add(Button("Listen / mute preview", () => { if (browserSession is not null) browsers.SetPreviewAudio(browserSession, !browserSession.PreviewAudio); return Task.CompletedTask; }));
        DockPanel.SetDock(browserTools, Dock.Top); browserArea.Children.Add(browserTools); DockPanel.SetDock(browserStatus, Dock.Bottom); browserArea.Children.Add(browserStatus); browserArea.Children.Add(browserPanel);
        var form = new StackPanel { Margin = new Thickness(12) }; Grid.SetColumn(form, 1); setup.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }.AtColumn(1));
        Field(form, "Input name", name); Field(form, "Input type", type); Field(form, "Source URL (Screen: screen://)", address); form.Children.Add(support);
        Field(captureOptions, "Video capture device", videoDevice); Field(captureOptions, "Audio capture device (select explicitly)", audioDevice);
        captureOptions.Children.Add(Button("Refresh capture devices", () => { RefreshDevices(); return Task.CompletedTask; })); form.Children.Add(captureOptions);
        form.Children.Add(new OutputAudioMeter());
        Field(ndiOptions, "NDI network sources", ndiSource);
        ndiOptions.Children.Add(Button("Discover NDI sources", async () => { var found = await NdiNative.Discover(); ndiSource.ItemsSource = found; message.Text = found.Length == 0 ? "No NDI senders found. Check sender, network and firewall." : $"Found {found.Length} NDI source(s)"; }));
        ndiSource.SelectionChanged += (_, _) => { if (ndiSource.SelectedItem is string source) address.Text = "ndi://" + Uri.EscapeDataString(source); };
        form.Children.Add(ndiOptions);
        form.Children.Add(Button("Load source preview", LoadAnyPreview));
        address.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && (string)type.SelectedItem == "Browser") { e.Handled = true; await Run(LoadBrowser); } };
        Field(form, "Network buffer (ms) — this is not measured end-to-end latency", cache); form.Children.Add(deinterlace);
        Field(form, "No fresh video timeout (seconds)", timeout); Field(form, "Backup clip (loops until operator takes another clip)", backup);
        form.Children.Add(mute); Field(form, "Input audio gain (−60 to +6 dB)", gain);
        var actions = new WrapPanel(); actions.Children.Add(Button("Save input (Browser: add to playlist)", () => Save())); actions.Children.Add(Button("Start preview", StartPreview)); actions.Children.Add(Button("Stop preview", StopPreview)); form.Children.Add(actions);
        Field(form, "Destination", port); var take = new WrapPanel(); take.Children.Add(Button("TAKE / CUT LIVE", Take)); take.Children.Add(Button("Back to Playlist", () => send(new EngineCommand { Action = "Live.Back", Channel = port.SelectedIndex + 1 }))); form.Children.Add(take);
        Field(form, "Playlist live duration (seconds)", duration); Field(form, "Optional scheduled start (local yyyy-MM-dd HH:mm:ss)", schedule); form.Children.Add(Button("Add Live item to playlist", AddToPlaylist));
        tabs.Items.Add(new TabItem { Header = "Inputs / Take", Content = setup });
        tabs.Items.Add(new TabItem { Header = "Multi-viewer", Content = new ScrollViewer { Content = monitors, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "Adapters & features", Content = new ScrollViewer { Content = new TextBlock { Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap, FontSize = 14, Text =
            "AVAILABLE IN THIS BUILD\n\nRTSP / SRT / RTMP URL inputs and full-desktop capture via the installed decoder. Protocols still require compatible reachable sources. Up to eight muted preview monitors, independent preview windows, source pixel size, observed preview FPS, tally, preview audio control, network buffering and deinterlace.\n\nCUT to either port; timed Live playlist items; existing rundown scheduling; manual Back to Playlist; current CG/logo/ticker stay applied. No-fresh-frame/error detection with alarm and local backup-clip or playlist return. Preview auto-reconnect every five seconds.\n\nSHORTCUTS\nCtrl+Alt+1/2/3 takes saved Live 1/2/3 on selected port. Ctrl+Alt+B returns to playlist. Start and verify previews before a live take.\n\nNOT IMPLEMENTED / NOT VALIDATED\nDeckLink capture and NDI discovery require dedicated adapters. DirectShow webcam module is absent from the packaged decoder. Interactive transparent browser input, individual-window capture, fade/mix, PiP/split/layers, L-band, source crop/color correction, embedded/separate audio mapping and lip-sync delay need additional implementation.\n\nAudio meters/silence detection, content freeze/black detection, ISO recording, instant replay/delay, input-to-input automatic failover, combined CG presets and mobile control are not available yet. No-signal means missing decoded frames, not a content-freeze/black test.\n\nFORMAT NOTES\nResolution is decoder-reported; FPS is observed preview delivery, not a certified input format. Interlace mode and end-to-end latency are not auto-measured. Preview is capped at 960×540. Desktop capture includes the entire desktop; avoid capturing the preview window itself." } } });
        sources.SelectionChanged += (_, _) => LoadSelected(); type.SelectionChanged += (_, _) => UpdateSupport();
        var features = (TextBlock)((ScrollViewer)((TabItem)tabs.Items[2]).Content).Content;
        features.Text = "LIVE INPUTS\n\nBrowser: interactive URL preview, keyboard/mouse, refresh, zoom, Save to playlist. Audio follows on-air and port mute. Listen enables preview audio. Page player / Windows mixer controls browser volume.\n\nRTSP / RTMP / SRT / HLS: enter a direct feed URL, Save, Start preview, then TAKE. HLS requires an HTTP(S) playlist. RTMP is a pull input, not a publishing server. SRT interoperability must be checked against the sender configuration.\n\nUSB / Webcam: detected DirectShow video and separate audio device selection. Select microphone explicitly; 'none' means no capture audio. Some devices allow only one capture session.\n\nDeckLink: capture from driver-exposed Blackmagic DirectShow inputs. Configure connector and format in Desktop Video. Native DeckLink SDK routing and hardware certification are pending.\n\nNDI: official x64 runtime required. Discover network sources, select one, Save and Start preview. The receiver handles video plus the first two PCM audio channels. Browser and NDI use a 960×540 / up to 30 fps opaque video bridge and Windows default audio output. Keep the operator console open. Audio is not embedded in SDI/NDI output; A/V sync is not certified.\n\nAUDIO MONITOR\nGreen/yellow/red L/R meters measure the actual Windows default output mix, including other apps. They are not source-isolated or SDI embedded meters.\n\nCONTROL\nCut/Take to either port, timed Live playlist items, manual Back, CG preserved, signal-loss alarm and backup clip. Ctrl+Alt+1/2/3: Live presets; Ctrl+Alt+B: Back.\n\nNOT IMPLEMENTED\nTransparent composition, fade/mix, PiP/layers, individual-window capture, audio delay/channel mapping beyond stereo, silence/black/content-freeze detection, ISO recording and instant replay. No Signal detects missing frames, not black/static content. Hardware inputs must be tested on the destination PC.";
#if MCR_VARIANT
        features.Text = features.Text.Replace("either port", "the single Program output", StringComparison.Ordinal).Replace("selected port", "Program", StringComparison.Ordinal);
#endif
        Loaded += async (_, _) => await Run(async () => { await Refresh(); UpdateSupport(); poll.Start(); frames.Start(); });
        poll.Tick += async (_, _) => { if (busy || closed) return; busy = true; try { statuses = await client.LiveStatus(); UpdateMonitors(); } catch (Exception ex) { message.Text = ex.Message; } finally { busy = false; } };
        frames.Tick += (_, _) => { if (sources.SelectedItem is MediaAsset selected && selected.Live?.Type != "Browser") { var selectedStatus = statuses.FirstOrDefault(s => s.Id == selected.Id); if (selectedStatus is not null) { if (inputSlot != selectedStatus.Slot) { inputReader.Dispose(); inputSlot = selectedStatus.Slot; inputPicture.Source = null; } if (selectedStatus.Signal == "Signal OK" && inputReader.Read(client.Instance, selectedStatus.Slot)) inputPicture.Source = inputReader.Image; browserStatus.Text = $"{selected.Name} · {selectedStatus.Signal} · {selectedStatus.Width}×{selectedStatus.Height} · {selectedStatus.ObservedFps:0.#} fps"; } } foreach (var status in statuses) if (tiles.TryGetValue(status.Id, out var tile)) { if (status.Signal != "Signal OK") tile.Image.Source = null; else if (tile.Reader.Read(client.Instance, status.Slot)) tile.Image.Source = tile.Reader.Image; } };
        Closed += async (_, _) => { closed = true; foreach (var id in ownedPreviews) { try { await client.LivePreview(new EngineCommand { Action = "Stop", MediaId = id }); browsers.Ndi.Unmonitor(id); if (!LiveUsage.IsOnAir(await client.GetState(), id)) { browsers.Ndi.Stop(id); browsers.Stop(id); } } catch { } } inputReader.Dispose(); browsers.Detach(browserPanel); poll.Stop(); frames.Stop(); foreach (var w in previewWindows.Values.ToArray()) w.Close(); foreach (var tile in tiles.Values) tile.Reader.Dispose(); };
        frames.Tick += (_, _) => { if (browserSession is { Disposed: false }) { var core = browserSession.View.CoreWebView2; browserStatus.Text = browserSession.Status + $"\nPublished frames: {browserSession.Frames}. Click inside the page to use mouse and keyboard.\nAudio: {(core?.IsMuted == true ? "MUTED" : core?.IsDocumentPlayingAudio == true ? "PLAYING" : "NO PAGE AUDIO")} · Windows default output.\nKeep the console open. Browser audio is not embedded in SDI/NDI; port gain/device selection does not control it. Use the page player / Windows mixer."; } };
    }
    private Button Button(string text, Func<Task> action) { var b = new Button { Content = text }; b.Click += async (_, _) => await Run(action); return b; }
    private async Task Run(Func<Task> action) { try { await action(); } catch (Exception ex) { message.Text = ex.Message; } }
    private static void Field(Panel panel, string label, UIElement control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(4, 8, 0, 0) }); panel.Children.Add(control); }
    private MediaAsset Selected => sources.SelectedItem as MediaAsset ?? throw new InvalidOperationException("Select or save a live input first");
    private async Task Refresh(Guid? selected = null)
    {
        state = await client.GetState(); var id = selected ?? (sources.SelectedItem as MediaAsset)?.Id;
        sources.ItemsSource = state.Media.Where(m => m.Kind == "Live").ToList(); sources.SelectedItem = state.Media.FirstOrDefault(m => m.Id == id);
        var none = new MediaAsset { Id = Guid.Empty, Name = "No backup — return to playlist" };
        backup.ItemsSource = new[] { none }.Concat(state.Media.Where(m => m.Kind == "Clip")).ToList(); backup.SelectedIndex = 0;
        LoadSelected();
    }
    private async void LoadSelected()
    {
        if (sources.SelectedItem is not MediaAsset m || m.Live is not { } settings) return;
        name.Text = m.Name; address.Text = m.Path; type.SelectedItem = settings.Type; cache.Text = settings.CacheMs.ToString(); timeout.Text = settings.SignalTimeoutSeconds.ToString(CultureInfo.InvariantCulture); deinterlace.IsChecked = settings.Deinterlace; gain.Value = settings.GainDb; mute.IsChecked = settings.Mute;
        videoDevice.SelectedItem = settings.VideoDevice; audioDevice.SelectedItem = settings.AudioDevice;
        if (backup.ItemsSource is IEnumerable<MediaAsset> items) backup.SelectedItem = items.FirstOrDefault(a => a.Id == settings.BackupMediaId) ?? items.First();
        if (settings.Type == "Browser") await Run(LoadBrowser); else { inputPicture.Source = null; browserPanel.Content = inputPicture; browserStatus.Text = "Click Load preview to connect this source."; }
    }
    private void UpdateSupport()
    {
        var t = type.SelectedItem as string;
        var isBrowser = t == "Browser";
        browserArea.Visibility = Visibility.Visible; browserColumn.Width = new GridLength(1.3, GridUnitType.Star);
        if (isBrowser) Width = Math.Min(1600, SystemParameters.WorkArea.Width);
        else { browsers.Detach(browserPanel); browserSession = null; browserPanel.Content = inputPicture; }
        gain.IsEnabled = !isBrowser;
        captureOptions.Visibility = t is "USB / Webcam" or "DeckLink" ? Visibility.Visible : Visibility.Collapsed;
        ndiOptions.Visibility = t == "NDI" ? Visibility.Visible : Visibility.Collapsed;
        if (captureOptions.Visibility == Visibility.Visible) { address.Text = "dshow://"; RefreshDevices(); }
        support.Text = isBrowser ? "Enter URL and press Enter or Load URL. Save adds a Browser playlist item. Audio follows on-air to Windows default output; use Listen for preview audio. If the page player is paused/muted, click Play/unmute inside it. Browser volume uses the page player / Windows mixer." : t is "RTSP" or "SRT" or "RTMP" or "Screen" ? "Decoder adapter available. Start preview to verify the actual source signal." : "This adapter is not implemented in this build; it cannot be saved or taken live.";
        if (t == "Screen") address.Text = "screen://";
        if (t == "HLS") support.Text = "HTTP/HTTPS HLS playlist (.m3u8). Use the stream URL, not the player webpage. Verify audio and video in preview.";
        if (t == "SRT") support.Text = "SRT receiver caller mode: connect to a listening sender, e.g. srt://sender-ip:9000?mode=caller&latency=200. Listener/rendezvous receiver modes are unavailable in this decoder.";
        if (t == "NDI") support.Text = "Requires official NDI x64 Runtime. Discover and select a sender, then Save / Start preview. Receiver bridge: up to 960×540 at up to 30 fps, first two audio channels to Windows default output. Console must remain open. Hardware/network validation pending.";
        if (t is "USB / Webcam" or "DeckLink") support.Text = t == "DeckLink" ? "DirectShow capture from a Blackmagic driver-exposed input. Configure connector and format in Blackmagic Desktop Video first. Native DeckLink SDK routing is not implemented. Output-only cards cannot capture." : "Choose camera and microphone. Close other apps using the camera. Stop preview before TAKE if the device supports only one capture session.";
    }
    private void RefreshDevices()
    {
        var video = videoDevice.SelectedItem as string; var audio = audioDevice.SelectedItem as string;
        var available = CaptureDevices.Video();
        if ((string)type.SelectedItem == "DeckLink") available = available.Where(n => n.Contains("Blackmagic", StringComparison.OrdinalIgnoreCase) || n.Contains("Decklink", StringComparison.OrdinalIgnoreCase) || n.Contains("Intensity", StringComparison.OrdinalIgnoreCase)).ToArray();
        videoDevice.ItemsSource = available; videoDevice.SelectedItem = available.Contains(video ?? "") ? video : available.FirstOrDefault();
        var audioNames = new[] { "none" }.Concat(CaptureDevices.Audio()).ToArray(); audioDevice.ItemsSource = audioNames; audioDevice.SelectedItem = audioNames.Contains(audio ?? "") ? audio : "none";
        if (available.Length == 0) message.Text = "No matching capture device detected. Connect the device and install its capture driver, then Refresh.";
    }
    private async Task Save(bool addBrowserToPlaylist = true)
    {
        var isBrowser = (string)type.SelectedItem == "Browser";
        if (isBrowser) { double.Parse(duration.Text, CultureInfo.InvariantCulture); await LoadBrowser(); }
        var m = new MediaAsset { Id = (sources.SelectedItem as MediaAsset)?.Id ?? (isBrowser ? draftBrowserId : Guid.NewGuid()), Name = name.Text.Trim(), Path = address.Text.Trim(), Live = new LiveInputSettings { Type = (string)type.SelectedItem, CacheMs = int.Parse(cache.Text), SignalTimeoutSeconds = double.Parse(timeout.Text, CultureInfo.InvariantCulture), Deinterlace = deinterlace.IsChecked == true, Mute = mute.IsChecked == true, GainDb = gain.Value, BackupMediaId = backup.SelectedItem is MediaAsset b && b.Id != Guid.Empty ? b.Id : null } };
        m.Live!.VideoDevice = videoDevice.SelectedItem as string ?? ""; m.Live.AudioDevice = audioDevice.SelectedItem as string ?? "none";
        if (m.Live.Type == "NDI") NdiNative.Check();
        PlayoutController.ValidateLive(m);
        if (isBrowser && browserSession is not null) { m.Width = 960; m.Height = 540; }
        await send(new EngineCommand { Action = "Live.Save", Asset = m });
        
        await client.LivePreview(new EngineCommand { Action = "Stop", MediaId = m.Id });
        await Refresh(m.Id); message.Text = "Input saved. Start preview to verify signal.";
        if (isBrowser && addBrowserToPlaylist) { await AddToPlaylist(); message.Text = "Browser saved and added to playlist. Select its row and TAKE / double-click to play. Keep this console open."; }
    }
    private async Task LoadAnyPreview()
    {
        if ((string)type.SelectedItem == "Browser") { await LoadBrowser(); return; }
        await Save(false); await StartPreview();
    }
    private async Task DeleteInput()
    {
        var selected = Selected;
        if (MessageBox.Show(this, "Delete this input and all its playlist entries? Any port playing it will stop.", "Delete live input", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await send(new EngineCommand { Action = "Live.Delete", MediaId = selected.Id, Confirm = true });
        await client.LivePreview(new EngineCommand { Action = "Stop", MediaId = selected.Id });
        browsers.Detach(browserPanel); browsers.Stop(selected.Id); browsers.Ndi.Stop(selected.Id); ownedPreviews.Remove(selected.Id);
        browserSession = null; inputPicture.Source = null; inputReader.Dispose(); browserStatus.Text = "Input deleted; background capture stopped.";
        await Refresh(); statuses = await client.LiveStatus(); UpdateMonitors();
    }
    private async Task LoadBrowser()
    {
        if ((string)type.SelectedItem != "Browser") throw new InvalidOperationException("Select Browser input first");
        var id = (sources.SelectedItem as MediaAsset)?.Id ?? draftBrowserId;
        browserSession = await browsers.Ensure(id, address.Text.Trim(), true); browsers.Attach(browserSession, browserPanel);
        if (string.IsNullOrWhiteSpace(name.Text)) name.Text = new Uri(address.Text.Trim()).Host;
    }
    internal Task TestBrowserResave() => Save(false);
    internal async Task<BrowserSources.Session> TestBrowserSave(string url)
    {
        type.SelectedItem = "Browser"; address.Text = url; name.Text = "Browser integration test"; duration.Text = "30";
        await Save(); return browserSession!;
    }
    private async Task StartPreview() { if (Selected.Live?.Type == "NDI") browsers.Ndi.Monitor(Selected); if (Selected.Live?.Type == "Browser") await LoadBrowser(); ownedPreviews.Add(Selected.Id); await client.LivePreview(new EngineCommand { Action = "Start", MediaId = Selected.Id }); statuses = await client.LiveStatus(); UpdateMonitors(); message.Text = "Preview connected on the right. Multi-viewer shows all monitored inputs."; }
    private async Task StopPreview() { ownedPreviews.Remove(Selected.Id); if (Selected.Live?.Type == "Browser") { browsers.Detach(browserPanel); browserSession = null; if (!LiveUsage.IsOnAir(await client.GetState(), Selected.Id)) browsers.Stop(Selected.Id); } if (Selected.Live?.Type == "NDI") browsers.Ndi.Unmonitor(Selected.Id); await client.LivePreview(new EngineCommand { Action = "Stop", MediaId = Selected.Id }); statuses = await client.LiveStatus(); UpdateMonitors(); }
    private async Task Take() { if (Selected.Live?.Type is "USB / Webcam" or "DeckLink") await client.LivePreview(new EngineCommand { Action = "Stop", MediaId = Selected.Id }); await send(new EngineCommand { Action = "Live.Take", Channel = port.SelectedIndex + 1, MediaId = Selected.Id }); message.Text = "Live take sent to " + port.SelectedItem; }
    private async Task AddToPlaylist()
    {
        var m = Selected; var seconds = double.Parse(duration.Text, CultureInfo.InvariantCulture); PlayoutController.Number(seconds, 1, 86400);
        DateTimeOffset? at = string.IsNullOrWhiteSpace(schedule.Text) ? null : DateTimeOffset.Parse(schedule.Text, CultureInfo.CurrentCulture);
        if (at <= DateTimeOffset.Now) throw new InvalidOperationException("Schedule must be in the future");
        var item = PlayoutController.NewItem(m); item.Out = seconds; var channel = port.SelectedIndex + 1;
        var fresh = await client.GetState(); var rundown = fresh.Rundowns.Single(p => p.Id == fresh.Channels.Single(c => c.Id == channel).RundownId);
        if (rundown.Locked)
        {
            if (MessageBox.Show(this, "Unlock this rundown and add the live item?", "Rundown locked", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) throw new InvalidOperationException("Input saved; playlist add cancelled because the rundown is locked.");
            await send(new EngineCommand { Action = "Rundown.Lock", Channel = channel, Enabled = false });
        }
        await send(new EngineCommand { Action = "Live.Add", Channel = channel, Item = item, Schedule = at is null ? null : new ScheduledTake { At = at.Value } });
        message.Text = at is null ? "Timed Live item added. Playback continues to the next playlist item after its duration." : "Timed Live item added and scheduled.";
    }
    private void UpdateMonitors()
    {
        foreach (var removed in tiles.Keys.Where(id => statuses.All(s => s.Id != id)).ToArray()) { var tile = tiles[removed]; monitors.Children.Remove(tile.Border); tile.Reader.Dispose(); tiles.Remove(removed); if (previewWindows.Remove(removed, out var w)) w.Close(); }
        foreach (var s in statuses)
        {
            if (!tiles.TryGetValue(s.Id, out var tile))
            {
                var panel = new StackPanel(); var img = new Image { Width = 440, Height = 248, Stretch = Stretch.Uniform }; var label = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
                panel.Children.Add(img); panel.Children.Add(label); var buttons = new WrapPanel();
                buttons.Children.Add(Button("Open preview window", () => { OpenPreview(s.Id); return Task.CompletedTask; }));
                buttons.Children.Add(Button("Mute / unmute", async () =>
                {
                    if (state?.Media.FirstOrDefault(m => m.Id == s.Id)?.Live?.Type == "Browser")
                    {
                        var asset = state.Media.Single(m => m.Id == s.Id); var browser = await browsers.Ensure(asset.Id, asset.Path, true); browsers.SetPreviewAudio(browser, !browser.PreviewAudio);
                    }
                    else if (state?.Media.FirstOrDefault(m => m.Id == s.Id)?.Live?.Type == "NDI") browsers.Ndi.ToggleListen(s.Id);
                    else await client.LivePreview(new EngineCommand { Action = "Audio", MediaId = s.Id, Enabled = !(statuses.FirstOrDefault(x => x.Id == s.Id)?.Muted ?? true), Value = 0 });
                }));
                var volume = new Slider { Minimum = -60, Maximum = 6, Width = 100, ToolTip = "Preview gain dB" }; volume.PreviewMouseLeftButtonUp += async (_, _) => await Run(() => client.LivePreview(new EngineCommand { Action = "Audio", MediaId = s.Id, Enabled = statuses.FirstOrDefault(x => x.Id == s.Id)?.Muted ?? true, Value = volume.Value })); buttons.Children.Add(volume);
                volume.IsEnabled = state?.Media.FirstOrDefault(m => m.Id == s.Id)?.Live?.Type is not ("Browser" or "NDI");
                panel.Children.Add(buttons); var border = new Border { Child = panel, Background = Brushes.Black, BorderThickness = new Thickness(2), Margin = new Thickness(5) };
                tile = (new FrameReader(), img, label, border); tiles.Add(s.Id, tile); monitors.Children.Add(border);
            }
            tile.Label.Text = $"{s.Name} · {s.Signal} · {(s.OnAir.Length > 0 ? "ON AIR PORT " + string.Join(",", s.OnAir) : "PREVIEW")}\n{s.Width}×{s.Height} · {s.ObservedFps:0.#} observed fps · buffer {s.CacheMs} ms · retries {s.Retries}\nAudio {(s.Muted ? "muted" : "enabled")}. {s.Message}";
            tile.Border.BorderBrush = s.OnAir.Length > 0 ? Brushes.Red : s.Signal == "Signal OK" ? Brushes.SeaGreen : Brushes.DarkOrange;
            if (state?.Media.FirstOrDefault(m => m.Id == s.Id)?.Live?.Type is not ("Browser" or "NDI")) tile.Label.Text += s.AudioTrack >= 0 ? "\nDecoded audio track detected" : "\nNo active decoded audio track";
            if (state?.Media.FirstOrDefault(m => m.Id == s.Id)?.Live?.Type == "NDI") tile.Label.Text += "\n" + browsers.Ndi.Status(s.Id);
            if (browsers.Find(s.Id)?.View.CoreWebView2 is { } core) tile.Label.Text = $"{s.Name} · {s.Signal} · {(s.OnAir.Length > 0 ? "ON AIR" : "PREVIEW")}\nBrowser audio {(core.IsMuted ? "MUTED" : core.IsDocumentPlayingAudio ? "PLAYING" : "NO PAGE AUDIO")} · Windows default output\nUse page player / Windows mixer for browser volume.";
        }
    }
    private void OpenPreview(Guid id)
    {
        if (previewWindows.TryGetValue(id, out var existing)) { existing.Activate(); return; }
        var source = tiles[id].Image; var image = new Image { Stretch = Stretch.Uniform }; image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("Source") { Source = source });
        var w = new Window { Title = "Live preview · " + statuses.Single(s => s.Id == id).Name, Owner = this, Width = 800, Height = 500, Background = Brushes.Black, Content = image }; previewWindows[id] = w; w.Closed += (_, _) => previewWindows.Remove(id); w.Show();
    }
}
internal static class LiveLayoutExtensions { public static T AtColumn<T>(this T element, int column) where T : UIElement { Grid.SetColumn(element, column); return element; } }


