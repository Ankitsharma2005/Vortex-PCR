using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public partial class MainWindow : Window
{
    private readonly EngineClient client = new();
    private readonly DispatcherTimer stateTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer frameTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly FrameReader[] frames = [new(), new()];
    private readonly List<OutputWindow> outputs = [];
    private readonly Dictionary<int, Window> toolWindows = [];
    private EngineState? state;
    private int selectedChannel = 1;
    private bool polling, closing, connected;
    private Guid[] dragMediaSelection = [];
    private string librarySignature = "", scheduleSignature = "", cartSignature = "";
    private string rundownBrowserSignature = "";
    private DateTime lastError = DateTime.MinValue;
    private readonly string? smokeFile = Environment.GetCommandLineArgs().SkipWhile(a => a != "--smoke-test").Skip(1).FirstOrDefault();
    public MainWindow()
    {
        InitializeComponent(); ChannelOne.ChannelId = 1; ChannelTwo.ChannelId = 2; ChannelOne.Host = this; ChannelTwo.Host = this;
        PortOneRundown.Content = ChannelOne.DetachRundown(); PortTwoRundown.Content = ChannelTwo.DetachRundown();
        if (smokeFile is not null) { ShowInTaskbar = false; ShowActivated = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -20000; Top = -20000; Width = 1550; Height = 850; }
        FormatOne.ItemsSource = Timecode.Formats; FormatTwo.ItemsSource = Timecode.Formats; DataPathText.Text = "SQLite database and logs: " + client.DataDirectory;
        for (var i = 0; i < 48; i++) { var index = i; var b = new Button { Content = $"{i + 1:00}\n＋", Height = 42, FontSize = 9, Tag = i, Padding = new Thickness(2) }; b.Click += async (_, _) => await Try(() => Send(new EngineCommand { Action = "Cart.Take", Channel = selectedChannel, Index = index })); b.PreviewMouseRightButtonDown += async (_, e) => { e.Handled = true; await Try(async () => { if (MediaGrid.SelectedItem is not MediaAsset m) throw new InvalidOperationException("Select a media asset in the library first"); await Send(new EngineCommand { Action = "Cart.Assign", Index = index, MediaId = m.Id }); }); }; CartGrid.Children.Add(b); }
        stateTimer.Tick += async (_, _) => await Poll(); frameTimer.Tick += (_, _) => UpdateFrames(); InitializeClipWorkspace();
#if MCR_VARIANT
        ConfigureMcrVariant();
#else
        SelectChannel(1);
#endif
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await Try(async () => { await client.Connect(); await Poll(); if (!state!.SharedRundown) await Send(new EngineCommand { Action = "Rundown.Share" }); SelectChannel(selectedChannel); LoadGraphics(); FormatOne.SelectedItem = state!.Channels[0].Format;
#if !MCR_VARIANT
            FormatTwo.SelectedItem = state.Channels[1].Format;
#endif
            WatchPath.Text = state.WatchFolder; });
        stateTimer.Start(); frameTimer.Start();
        if (smokeFile is null && Environment.GetCommandLineArgs().Contains("--live-inputs")) OpenLiveInputs(this, new RoutedEventArgs());
        if (smokeFile is not null)
        {
            try
            {
                await Task.Delay(1800);
                if (!connected || state is null) throw new InvalidOperationException("Desktop did not connect to engine");
                WindowKeyDown(this, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, Key.F10) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = SearchBox });
                if (selectedChannel != 2) throw new InvalidOperationException("F10 did not select Port 2 from a typing field");
                WindowKeyDown(this, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, Key.F9) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = SearchBox });
                if (selectedChannel != 1) throw new InvalidOperationException("F9 did not select Port 1");
                var originalHotkeys = new Dictionary<string, string>(Hotkeys);
                try
                {
                    hotkeys!["F10"] = "F11";
                    WindowKeyDown(this, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, Key.F11) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = SearchBox });
                    if (selectedChannel != 2) throw new InvalidOperationException("Custom port hotkey did not execute");
                    var oldKey = Key.F10; var oldMods = ModifierKeys.None;
                    if (TranslateHotkey(ref oldKey, ref oldMods)) throw new InvalidOperationException("Reassigned default key remains active");
                }
                finally { hotkeys = originalHotkeys; SelectChannel(1); }
                if (state.Media.FirstOrDefault(m => m.Kind == "Clip") is { } sourceAsset) { await SourcePreview.Load(sourceAsset); for (var attempt = 0; attempt < 40 && !SourcePreview.HasFrame; attempt++) await Task.Delay(100); if (!SourcePreview.HasFrame) throw new InvalidOperationException("Source preview did not receive a native frame within four seconds"); }
                UpdateLayout(); UpdateFrames();
                if (state.Channels.Any(c => c.Status == "Playing" && !c.Emergency && state.Rundowns.Single(p => p.Id == c.RundownId).Items.FirstOrDefault(i => i.Id == c.CurrentId)?.Kind == "Clip") && frames.Any(f => f.Image is null)) throw new InvalidOperationException("Desktop did not receive native preview frames");
                var target = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                target.Render(this); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
                await using var file = File.Create(smokeFile); png.Save(file);
                await File.WriteAllTextAsync(smokeFile + ".result.txt", "PASS: WPF window loaded, connected, and rendered both native channel previews.");
                if (ToolsTabs.IsVisible || ChannelTwo.IsVisible) throw new InvalidOperationException("Clean workspace did not hide extra panels");
                OpenToolMenu(new MenuItem { Tag = "0" }, new RoutedEventArgs());
                var cg = toolWindows[0]; cg.Left = -20000; cg.Top = -20000; cg.ShowInTaskbar = false; cg.UpdateLayout();
                CaptureWindow(cg, smokeFile + ".cg.png"); cg.Close();
                if (((TabItem)ToolsTabs.Items[0]).Content is null) throw new InvalidOperationException("CG content was not restored after closing its window");
                var settings = new OutputSettingsWindow(client, Send, StartDisplayOutputs, StopDisplayOutputs, 1) { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                var liveSettings = new LiveInputsWindow(client, Send, 1, BrowserInputs) { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                liveSettings.Show(); await Task.Delay(500); liveSettings.UpdateLayout(); CaptureWindow(liveSettings, smokeFile + ".live.png"); liveSettings.Close();
                settings.Show(); await Task.Delay(900); settings.UpdateLayout(); CaptureWindow(settings, smokeFile + ".output.png"); settings.Close();
                var monitorTest = new OutputWindow(1) { Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false };
                try { monitorTest.Show(); monitorTest.SetFrame(frames[0].Image); monitorTest.Update(state); monitorTest.UpdateLayout(); CaptureWindow(monitorTest, smokeFile + ".monitor.png"); monitorTest.Hide(); var display = DisplayDevices.All().First(); DisplayDevices.Place(monitorTest, display); } finally { monitorTest.Close(); }
                SelectChannel(2); if (!ChannelTwo.IsVisible || ChannelOne.IsVisible) throw new InvalidOperationException("Program selection did not switch single visible deck"); SelectChannel(1);
                SetPortMode(true, false); UpdateLayout();
                if (!ChannelOne.IsVisible || !ChannelTwo.IsVisible || Grid.GetColumn(ChannelTwo) != 1 || PortTwoRundown.IsVisible || !RundownTabs.IsVisible) throw new InvalidOperationException("Dual playout layout failed");
                SelectChannel(2); UpdateLayout();
                if (!ChannelOne.IsVisible || !ChannelTwo.IsVisible || RundownTabs.SelectedIndex != 0) throw new InvalidOperationException("Dual port playlist tab selection failed");
                SelectChannel(1); UpdateLayout();
                CaptureWindow(this, smokeFile + ".dual.png");
                var clipMenu = new ContextMenu(); var testItem = Rundown(1).Items.FirstOrDefault();
                if (testItem is not null) { PopulateClipMenu(clipMenu, 1, testItem); if (!clipMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == "Copy") || !clipMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == "Delete")) throw new InvalidOperationException("Clip editing context actions missing"); }
                SetPortMode(false, false); UpdateLayout();
                await TestTransportToggle();
                await TestColourWorkflow();
                await TestBrowserWorkflow();
                await TestNdiWorkflow();
                await TestCaptureWorkflow();
                await TestAdvancedCg();
                if (ChannelTwo.IsVisible || DeckRow.Height.Value != 462) throw new InvalidOperationException("Single port layout failed");
                await File.WriteAllTextAsync(smokeFile + ".result.txt", "PASS: Single/dual workspace, clip context actions, native previews, CG detach/restore, output settings and monitor composition.");
            }
            catch (Exception ex) { await File.WriteAllTextAsync(smokeFile + ".result.txt", "FAIL: " + ex); Environment.ExitCode = 1; }
            Close();
        }
    }
    public async Task Try(Func<Task> action) { try { await action(); } catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(this, ex.Message, "NewsPlay", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    private async Task TestTransportToggle()
    {
        var item = Rundown(1).Items.FirstOrDefault(i => i.Kind != "Live" && i.Duration > 3);
        if (item is null) throw new InvalidOperationException("Transport smoke requires a non-live clip longer than three seconds");
        await Send(new EngineCommand { Action = "Take", Channel = 1, ItemId = item.Id });
        await Task.Delay(300);
        ChannelOne.RundownGrid.SelectedItem = ChannelOne.RundownGrid.Items.Cast<RundownItem>().FirstOrDefault(i => i.Id != item.Id);
        ChannelOne.PlayPauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 30 && Channel(1).Status != "Paused"; i++) await Task.Delay(100);
        if (Channel(1).Status != "Paused" || Channel(1).CurrentId != item.Id) throw new InvalidOperationException("Play/Pause button did not pause the current clip");
        var position = Channel(1).Position;
        await Task.Delay(500); await Poll();
        if (Math.Abs(Channel(1).Position - position) > .15) throw new InvalidOperationException("Paused clip continued advancing");
        ChannelOne.PlayPauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 30 && Channel(1).Status != "Playing"; i++) await Task.Delay(100);
        if (Channel(1).Status != "Playing" || Channel(1).CurrentId != item.Id) throw new InvalidOperationException("Play/Pause button did not resume the same clip");
        if (item.Duration > 10)
        {
            await Send(new EngineCommand { Action = "Seek", Channel = 1, Value = item.Out - 8 });
            await Task.Delay(350); await Poll(); UpdateLayout();
            if (!ChannelOne.RemainingBackground.Background.HasAnimatedProperties) throw new InvalidOperationException("Last ten seconds warning did not animate");
            CaptureWindow(this, smokeFile + ".countdown.png");
            await Send(new EngineCommand { Action = "Pause", Channel = 1 });
            if (ChannelOne.RemainingBackground.Background.HasAnimatedProperties) throw new InvalidOperationException("Countdown warning continued after pause");
        }
        await Send(new EngineCommand { Action = "Stop", Channel = 1, Confirm = true });
    }
    private static void CaptureWindow(Window window, string path)
    {
        var target = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); target.Render(window);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target)); using var file = File.Create(path); png.Save(file);
    }
    private readonly SemaphoreSlim stateOperations = new(1, 1);
    public async Task Send(EngineCommand command) { await stateOperations.WaitAsync(); try { state = await client.Send(command); Render(); } finally { stateOperations.Release(); } }
    public void SelectChannel(int channel) { selectedChannel = channel; ChannelOne.Visibility = dualPort || channel == 1 ? Visibility.Visible : Visibility.Collapsed; ChannelTwo.Visibility = dualPort || channel == 2 ? Visibility.Visible : Visibility.Collapsed; ChannelOne.SetSelected(channel == 1); ChannelTwo.SetSelected(channel == 2); PortOneRundown.Visibility = Visibility.Visible; PortTwoRundown.Visibility = Visibility.Collapsed; RundownTabs.SelectedIndex = 0; }
    private void RundownTabChanged(object sender, SelectionChangedEventArgs e) {  }
    private async void PreviewMedia(object sender, RoutedEventArgs e) => await Try(async () => { if (MediaGrid.SelectedItem is not MediaAsset media) throw new InvalidOperationException("Select a clip first"); await SourcePreview.Load(media); });
    private async void LoadSavedRundown(object sender, MouseButtonEventArgs e) => await Try(async () => { if (RundownBrowser.SelectedItem is Rundown p) { await Send(new EngineCommand { Action = "Rundown.Load", Channel = p.Channel, RundownId = p.Id }); SelectChannel(p.Channel); } });
    private ChannelView View(int id) => id == 1 ? ChannelOne : ChannelTwo;
    private ChannelState Channel(int id) => state!.Channels.Single(c => c.Id == id);
    private Rundown Rundown(int id) => state!.Rundowns.Single(p => p.Id == Channel(id).RundownId);
    private async Task Poll()
    {
        if (polling || closing) return; polling = true; await stateOperations.WaitAsync();
        try { state = await client.GetState(); connected = true; ConnectionText.Text = "● Engine connected"; ConnectionText.Foreground = System.Windows.Media.Brushes.MediumAquamarine; if (StatusText.Text.StartsWith("Starting") || StatusText.Text.StartsWith("Waiting")) StatusText.Text = "Engine online · SQLite autosave active · Native software playback"; Render(); }
        catch { connected = false; ConnectionText.Text = "● Engine disconnected"; ConnectionText.Foreground = System.Windows.Media.Brushes.Salmon; if (DateTime.Now - lastError > TimeSpan.FromSeconds(3)) { StatusText.Text = "Waiting for engine / watchdog recovery…"; lastError = DateTime.Now; } }
        finally { polling = false; stateOperations.Release(); }
    }
    private void Render()
    {
        if (state is not null) BrowserInputs.Sync(state);
        if (state is null || closing) return;
        ChannelOne.Update(state);
#if !MCR_VARIANT
        ChannelTwo.Update(state);
#endif
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss"); DateText.Text = DateTime.Now.ToString("dd MMM yyyy").ToUpperInvariant();
#if MCR_VARIANT
        ConnectionText.Text = "● Engine connected · SINGLE PROGRAM" + (state.Linked ? " · LINKED TRANSPORT" : "");
#else
        var other = state.Channels.Single(c => c.Id != selectedChannel);
        ConnectionText.Text = "● Engine connected" + (state.Linked ? " · LINKED TRANSPORT" : "") + (other.Status == "Playing" || other.Emergency ? $" · Program {other.Id} {other.Status.ToUpperInvariant()}" : "");
#endif
        AlarmButton.Content = $"⚑ {state.Alarms.Count(a => !a.Acknowledged)} alarms"; LinkButton.Content = state.Linked ? "⇄ Transport linked" : "⇄ Link transport";
        RenderLibrary(); var sig = JsonSerializer.Serialize(state.Schedule, Json.Options); if (sig != scheduleSignature) { scheduleSignature = sig; var id = (ScheduleGrid.SelectedItem as ScheduledTake)?.Id; ScheduleGrid.ItemsSource = state.Schedule.OrderBy(e => e.At).ToList(); ScheduleGrid.SelectedItem = state.Schedule.FirstOrDefault(e => e.Id == id); }
        var rs = string.Join('|', state.Rundowns.Select(p => p.Id + p.Name)); if (rs != rundownBrowserSignature) { rundownBrowserSignature = rs; var selected = (RundownBrowser.SelectedItem as Rundown)?.Id; RundownBrowser.ItemsSource = state.Rundowns.ToList(); RundownBrowser.SelectedItem = state.Rundowns.FirstOrDefault(p => p.Id == selected); }
        sig = string.Join('|', state.Carts) + string.Join('|', state.Media.Select(m => m.Id + m.Name));
        if (sig != cartSignature) { cartSignature = sig; for (var i = 0; i < 48; i++) { var media = state.Media.FirstOrDefault(m => m.Id == state.Carts[i]); var b = (Button)CartGrid.Children[i]; b.Content = $"{i + 1:00}\n" + (media?.Name ?? "＋"); b.ToolTip = media?.Name ?? "Right-click to assign the selected asset"; } }
        foreach (var output in outputs) output.Update(state);
#if MCR_VARIANT
        RefreshMcrDashboard();
#endif
    }
    private void UpdateFrames()
    {
        if (!connected) return;
#if MCR_VARIANT
        for (var i = 0; i < 1; i++)
#else
        for (var i = 0; i < 2; i++)
#endif
        if (frames[i].Read(client.Instance, i + 1)) { View(i + 1).SetFrame(frames[i].Image); foreach (var output in outputs.Where(o => o.ChannelId == i + 1)) output.SetFrame(frames[i].Image);
#if MCR_VARIANT
            if (i == 0) McrDashPreview.Source = frames[0].Image;
#endif
        }
    }
    private void RenderLibrary(bool force = false)
    {
        if (state is null) return; var sig = JsonSerializer.Serialize(state.Media, Json.Options) + SearchBox.Text + CategoryBox.SelectedIndex;
        if (!force && librarySignature == sig) return; librarySignature = sig; var selected = (MediaGrid.SelectedItem as MediaAsset)?.Id;
        var search = SearchBox.Text.Trim(); var category = (CategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var assets = state.Media.Where(m => !m.RemovedFromBrowser && (category is null or "All categories" || m.Category == category) && $"{m.Name} {m.Tags} {m.Category}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        MediaGrid.ItemsSource = assets; MediaGrid.SelectedItem = assets.FirstOrDefault(m => m.Id == selected); MediaCount.Text = $"CLIP BROWSER · {state.Media.Count(m => !m.RemovedFromBrowser)}";
    }
    public async Task Transport(int channel, string action, double value = 0)
    {
        if (state is null) return;
        if (action == "Stop" && (Channel(channel).Status == "Playing" || Channel(channel).Emergency || state.Linked) && MessageBox.Show(this, state.Linked ? "Stop both linked channels?" : $"Stop channel {channel}?", "Confirm stop", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Send(new EngineCommand { Channel = channel, Action = action, Value = value, Confirm = action == "Stop" });
    }
    public async Task RundownAction(int channel, string action, RundownItem? item)
    {
        if (state is null) return;
        var p = Rundown(channel);
        if (new[] { "Cue", "Park", "Take", "Edit", "Up", "Down", "Rundown.Remove" }.Contains(action) && item is null) throw new InvalidOperationException("Select a rundown item first");
        if (action == "Preflight")
        {
            var report = await client.Preflight(p.Id); View(channel).SetPreflight(report); Render();
            var message = report.Issues.Count == 0 ? $"{report.CheckedItems} items checked. No missing files or invalid trims found." : string.Join("\n", report.Issues.Select(i => i.Title + ": " + i.Message));
            MessageBox.Show(this, message + "\n\nFile availability and metadata check only; run a rehearsal to verify audio and picture.", "Rundown preflight", MessageBoxButton.OK, report.Issues.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning); return;
        }
        if (action == "Rundown.Lock") { await Send(new EngineCommand { Channel = channel, Action = action, Enabled = !p.Locked }); return; }
        if (action == "AutoAdvance") { await Send(new EngineCommand { Channel = channel, Action = action, Enabled = !Channel(channel).AutoAdvance }); return; }
        if (action == "Park" && (Channel(channel).Status == "Playing" || Channel(channel).Emergency) && MessageBox.Show(this, "Park replaces the current output with the selected clip's first frame. Continue?", "Park on program", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (action == "Edit") { await EditItem(channel, item!, true); return; }
        if (action is "Rundown.Create" or "Rundown.Rename") { var name = PromptWindow.Ask(this, action == "Rundown.Create" ? "New rundown" : "Rename rundown", "Rundown name", action == "Rundown.Create" ? "Untitled bulletin" : p.Name); if (name is not null) await Send(new EngineCommand { Channel = channel, Action = action, Text = name }); return; }
        if (action is "Up" or "Down") { var index = p.Items.FindIndex(i => i.Id == item!.Id); await Send(new EngineCommand { Channel = channel, Action = "Rundown.Move", ItemId = item!.Id, Index = Math.Clamp(index + (action == "Up" ? -1 : 1), 0, p.Items.Count - 1) }); return; }
        if (action == "Export") { await SavePlaylist(channel); return; }
        if (action == "Import") { await LoadPlaylist(channel); return; }
        await Send(new EngineCommand { Channel = channel, Action = action, ItemId = item?.Id, Confirm = action == "Park" });
    }
    private async Task EditItem(int channel, RundownItem item, bool existing)
    {
        var media = state?.Media.FirstOrDefault(m => m.Id == item.MediaId); var dialog = new TrimWindow(Json.Copy(item), media) { Owner = this };
        if (dialog.ShowDialog() == true) await Send(new EngineCommand { Channel = channel, Action = existing ? "Rundown.Update" : "Rundown.Add", ItemId = existing ? item.Id : null, Item = dialog.Item });
    }
    private async void ImportMedia(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Multiselect = true, Filter = "Media files|*.dv;*.hdv;*.m2v;*.mts;*.ac3;*.mp4;*.mov;*.mxf;*.avi;*.ts;*.mkv;*.webm;*.mpg;*.m2ts;*.mp3;*.wav;*.aac;*.m4a;*.ogg;*.flac;*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.webp|All files|*.*" }; if (dialog.ShowDialog(this) == true) await ImportPaths(dialog.FileNames); }
    private async void ImportFolder(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Title = "Import media folder" }; if (dialog.ShowDialog(this) == true) await ImportPaths([dialog.FolderName]); }
    private async Task ImportPaths(IEnumerable<string> paths) { await Try(async () => { foreach (var path in paths) { StatusText.Text = "Indexing " + Path.GetFileName(path) + "…"; StatusText.Text = await client.Import(path); } await Poll(); }); }
    private void ClearClipSearch(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (IsInitialized) RenderLibrary(); }
    private void CategoryChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) RenderLibrary(); }
    private void SelectAllMedia(object sender, RoutedEventArgs e) { MediaGrid.UnselectAll(); foreach (var clip in MediaGrid.Items.Cast<MediaAsset>().Where(m => m.Kind != "Live")) MediaGrid.SelectedItems.Add(clip); StatusText.Text = $"{MediaGrid.SelectedItems.Count} clips selected. Drag them onto a program rundown or choose Add selected."; }
    private async void AddSelectedMedia(object sender, RoutedEventArgs e) => await Try(() => AddMediaToRundown(selectedChannel, SelectedBrowserMedia().Select(m => m.Id).ToArray()));
    private List<MediaAsset> SelectedBrowserMedia() => MediaGrid.Items.Cast<MediaAsset>().Where(m => MediaGrid.SelectedItems.Contains(m)).ToList();
    public async Task AddMediaToRundown(int target, IReadOnlyList<Guid> mediaIds, int index = -1) { if (mediaIds.Count == 0) throw new InvalidOperationException("Select one or more clips first"); if (!await EnsureRundownEditable(target)) return; await Send(new EngineCommand { Action = "Rundown.AddMedia", Channel = target, ItemIds = mediaIds.ToList(), Index = index }); StatusText.Text = $"Added {mediaIds.Count} clip(s) to Port {target} rundown."; }
    private async void TrimMedia(object sender, RoutedEventArgs e) => await Try(async () => { if (MediaGrid.SelectedItem is not MediaAsset m) throw new InvalidOperationException("Select a media asset first"); await EditItem(selectedChannel, PlayoutController.NewItem(m), false); });
    private async void MediaDoubleClick(object sender, MouseButtonEventArgs e) { if (RowAt(e.OriginalSource) is not null && MediaGrid.SelectedItem is MediaAsset media) { e.Handled = true; await Try(() => ActivateMedia(media)); } }
    private void MediaDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !DragReady(MediaGrid, e) || RowAt(e.OriginalSource) is not { } row || row.Item is not MediaAsset) return;
        // WPF may collapse an extended selection when the drag starts on a selected row.
        // Keep the mouse-down selection snapshot so dragging any selected row carries the whole group.
        var ids = dragMediaSelection.Length > 0 ? dragMediaSelection : SelectedBrowserMedia().Select(m => m.Id).ToArray();
        dragMediaSelection = [];
        if (ids.Length == 0) return;
        var payload = new DataObject("NewsPlay.MediaIds", JsonSerializer.Serialize(ids)); if (ids.Length == 1) payload.SetData("NewsPlay.Media", ids[0]);
        DragDrop.DoDragDrop(MediaGrid, payload, DragDropEffects.Copy);
    }
    private void FilesDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void FilesDropped(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await ImportPaths(files); }
    private async void LinkClicked(object sender, RoutedEventArgs e) => await Try(() => Send(new EngineCommand { Action = "Link", Enabled = !(state?.Linked ?? false) }));
    private void SafeClicked(object sender, RoutedEventArgs e) { ChannelOne.SetSafe(SafeCheck.IsChecked == true); ChannelTwo.SetSafe(SafeCheck.IsChecked == true); }
    private void LoadGraphics()
    {
        if (state is null || GraphicsChannel is null) return; var g = Channel(GraphicsChannel.SelectedIndex + 1).Graphics; LogoInput.Text = g.Logo; HeadlineInput.Text = g.Headline; SubtitleInput.Text = g.Subtitle; TickerInput.Text = g.Ticker; LogoOn.IsChecked = g.LogoOn; ClockOn.IsChecked = g.ClockOn; LowerOn.IsChecked = g.LowerOn; TickerOn.IsChecked = g.TickerOn; TickerSpeed.Text = g.TickerSpeed.ToString(); TickerColor.Text = g.Color;
    }
    private void GraphicsChannelChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) LoadGraphics(); }
    private async void ApplyGraphics(object sender, RoutedEventArgs e) => await Try(async () => { if (!int.TryParse(TickerSpeed.Text, out var speed)) throw new InvalidOperationException("Enter a numeric ticker speed"); _ = System.Windows.Media.ColorConverter.ConvertFromString(TickerColor.Text); await Send(new EngineCommand { Action = "Graphics", Channel = GraphicsChannel.SelectedIndex + 1, Graphics = new GraphicsState { Logo = LogoInput.Text, Headline = HeadlineInput.Text, Subtitle = SubtitleInput.Text, Ticker = TickerInput.Text, LogoOn = LogoOn.IsChecked == true, LowerOn = LowerOn.IsChecked == true, ClockOn = ClockOn.IsChecked == true, TickerOn = TickerOn.IsChecked == true, TickerSpeed = speed, Color = TickerColor.Text } }); StatusText.Text = "Graphics applied"; });
    private async void AddSlate(object sender, RoutedEventArgs e) => await Try(() => Send(new EngineCommand { Action = "Rundown.Add", Channel = selectedChannel, Item = new RundownItem { Kind = "Slate", Title = "PLEASE STAND BY", Out = 10 } }));
    private async void ScheduleSelected(object sender, RoutedEventArgs e) => await Try(async () => { var item = ChannelOne.SelectedItem ?? throw new InvalidOperationException("Select a rundown item first"); var value = PromptWindow.Ask(this, "Schedule a fixed-time take", "Local date and time (yyyy-MM-dd HH:mm:ss). This interrupts the channel.", DateTime.Now.AddMinutes(5).ToString("yyyy-MM-dd HH:mm:ss")); if (value is null) return; if (!DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var at)) throw new InvalidOperationException("Invalid date/time"); await Send(new EngineCommand { Action = "Schedule", Schedule = new ScheduledTake { Channel = selectedChannel, RundownId = Channel(selectedChannel).RundownId, ItemId = item.Id, Title = item.Title, At = at } }); });
    private async void RemoveSchedule(object sender, RoutedEventArgs e) { if (ScheduleGrid.SelectedItem is ScheduledTake selected) await Try(() => Send(new EngineCommand { Action = "Schedule.Remove", ItemId = selected.Id })); }
    private async void RefreshLogs(object sender, RoutedEventArgs e) => await Try(async () => LogsGrid.ItemsSource = await client.Logs());
    private async void ExportLogs(object sender, RoutedEventArgs e) => await Try(async () => { var dialog = new SaveFileDialog { Filter = "CSV log|*.csv", FileName = "NewsPlay-as-run.csv" }; if (dialog.ShowDialog(this) != true) return; static string Field(object? v) { var s = v?.ToString() ?? ""; if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; } var rows = await client.Logs(); var lines = new List<string> { "ID,Channel,Title,StartedAt,EndedAt,Seconds,Result,Output" }; lines.AddRange(rows.Select(r => string.Join(',', new object[] { r.Id, r.Channel, r.Title, r.StartedAt.ToString("O"), r.EndedAt.ToString("O"), r.PlayedSeconds.ToString(CultureInfo.InvariantCulture), r.Result, r.Output }.Select(Field)))); await File.WriteAllLinesAsync(dialog.FileName, lines, new UTF8Encoding(true)); StatusText.Text = "Log exported: " + dialog.FileName; });
    private async void ToolsChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == ToolsTabs && IsInitialized && state is not null && ToolsTabs.SelectedIndex == 3) await Try(async () => LogsGrid.ItemsSource = await client.Logs()); }
    private async void ApplyFormats(object sender, RoutedEventArgs e) => await Try(async () => { await Send(new EngineCommand { Action = "Format", Channel = 1, Text = FormatOne.SelectedItem as string });
#if !MCR_VARIANT
        await Send(new EngineCommand { Action = "Format", Channel = 2, Text = FormatTwo.SelectedItem as string });
#endif
    });
    private async void SetWatchFolder(object sender, RoutedEventArgs e) => await Try(async () => { var dialog = new OpenFolderDialog { Title = "Select ingest watch folder" }; if (dialog.ShowDialog(this) == true) WatchPath.Text = dialog.FolderName; await Send(new EngineCommand { Action = "WatchFolder", Path = WatchPath.Text }); });
    private void OpenOutput(object sender, RoutedEventArgs e) { var id = int.Parse((string)((Button)sender).Tag); var window = new OutputWindow(id); outputs.Add(window); window.Closed += (_, _) => outputs.Remove(window); window.Show(); if (state is not null) window.Update(state); }
    private async void DeleteRundown(object sender, RoutedEventArgs e) => await Try(async () => { var name = PromptWindow.Ask(this, "Delete unloaded rundown", "Enter the exact name of an unloaded rundown. Loaded rundowns are protected."); if (name is null) return; var p = state?.Rundowns.FirstOrDefault(p => p.Channel == selectedChannel && p.Name == name) ?? throw new InvalidOperationException("Rundown not found"); if (MessageBox.Show(this, $"Permanently delete {p.Name}?", "Delete rundown", MessageBoxButton.YesNo) == MessageBoxResult.Yes) await Send(new EngineCommand { Action = "Rundown.Delete", Channel = selectedChannel, RundownId = p.Id }); });
    private async void ShutdownEngine(object sender, RoutedEventArgs e) => await Try(async () => { if (MessageBox.Show(this,
#if MCR_VARIANT
        "Stop the independent VORTEX MCR engine and watchdog? Program must be stopped first.",
#else
        "Stop the engine and its watchdog? Both channels must be stopped.",
#endif
        "Engine shutdown", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; await client.Shutdown(); StatusText.Text = "Engine shut down. Restart the desktop application to start it again."; });
    private async void ShowAlarms(object sender, RoutedEventArgs e) { if (state is null) return; var text = state.Alarms.Count == 0 ? "No engine alarms." : string.Join("\n\n", state.Alarms.Take(20).Select(a => $"{a.At:HH:mm:ss} · CH {a.Channel}\n{a.Message}")); if (MessageBox.Show(this, text + "\n\nAcknowledge all alarms?", "Engine alarms", MessageBoxButton.YesNo) == MessageBoxResult.Yes) await Try(() => Send(new EngineCommand { Action = "Acknowledge" })); }
    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; var modifiers = Keyboard.Modifiers;
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or ComboBox && modifiers == ModifierKeys.None && !(key >= Key.F1 && key <= Key.F24)) return;
        if (!TranslateHotkey(ref key, ref modifiers)) return;
#if MCR_VARIANT
        if (modifiers == ModifierKeys.None && key == Key.F9) { e.Handled = true; SelectChannel(1); return; }
#else
        if ((modifiers == ModifierKeys.None && key is Key.F9 or Key.F10) || (modifiers == ModifierKeys.Control && key is Key.D1 or Key.D2 or Key.NumPad1 or Key.NumPad2))
        { e.Handled = true; SelectChannel(key is Key.F9 or Key.D1 or Key.NumPad1 ? 1 : 2); return; }
#endif
        if (key == Key.F12 && modifiers == ModifierKeys.None) { e.Handled = true; ShowShortcuts(); return; }
        if (e.OriginalSource is TextBox or System.Windows.Controls.Primitives.TextBoxBase or ComboBox || state is null || SourcePreview.IsKeyboardFocusWithin) return;
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.S)
        { e.Handled = true; await Try(() => Transport(selectedChannel, "Stop")); return; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            if (key == Key.B) { e.Handled = true; await Try(() => Send(new EngineCommand { Action = "Live.Back", Channel = selectedChannel })); }
            else if (key >= Key.D1 && key <= Key.D3)
            {
                var input = state.Media.Where(m => m.Kind == "Live").ElementAtOrDefault((int)key - (int)Key.D1);
                e.Handled = true;
                await Try(() => input is null ? throw new InvalidOperationException("Save this Live input in Input → Live Inputs first") : Send(new EngineCommand { Action = "Live.Take", Channel = selectedChannel, MediaId = input.Id }));
            }
            return;
        }
        if (modifiers == ModifierKeys.Control)
        {
#if MCR_VARIANT
            if (key is Key.D1 or Key.NumPad1) { e.Handled = true; SelectChannel(1); return; }
#else
            if (key is Key.D1 or Key.D2 or Key.NumPad1 or Key.NumPad2) { e.Handled = true; SelectChannel(key is Key.D1 or Key.NumPad1 ? 1 : 2); return; }
#endif
            var edit = key switch { Key.S => "Save", Key.O => "Load", Key.C => "Copy", Key.X => "Cut", Key.V => "Paste", Key.Z => "Undo", Key.Y => "Redo", _ => null };
            if (edit is not null) { e.Handled = true; await Try(() => PlaylistEdit(edit)); return; }
            if (key == Key.G) { View(selectedChannel).GoToCurrent(); e.Handled = true; return; }
            var listAction = key switch { Key.Q => "Cue", Key.I => "Import", Key.E => "Export", _ => null };
            if (listAction is not null) { e.Handled = true; await Try(() => RundownAction(selectedChannel, listAction, ChannelOne.SelectedItem)); }
            return;
        }
        if (modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down) { e.Handled = true; await Try(() => PlaylistEdit(key == Key.Up ? "Up" : "Down")); return; }
        if (modifiers != ModifierKeys.None) return;
        if (key is Key.Up or Key.Down) { e.Handled = true; View(selectedChannel).MoveSelection(key == Key.Up ? -1 : 1); return; }
        if (key is Key.L or Key.M or Key.A or Key.R)
        {
            e.Handled = true;
            await Try(async () =>
            {
                var c = Channel(selectedChannel);
                if (key == Key.R) { if (c.CurrentId is { } id) await Send(new EngineCommand { Channel = selectedChannel, Action = "Take", ItemId = id }); }
                else await Send(new EngineCommand { Channel = selectedChannel, Action = key == Key.L ? "LoopClip" : key == Key.M ? "Mute" : "AutoAdvance", Enabled = key == Key.L ? !c.LoopCurrent : key == Key.M ? !c.Mute : !c.AutoAdvance });
            }); return;
        }
        if (key == Key.Delete && MediaGrid.IsKeyboardFocusWithin) { e.Handled = true; await Try(RemoveBrowserClips); return; }
        if (key == Key.Delete && ChannelOne.RundownGrid.IsKeyboardFocusWithin) { e.Handled = true; await Try(() => PlaylistEdit("Delete")); return; }
        if (key >= Key.F1 && key <= Key.F8)
        {
            var offset = (int)key - (int)Key.F1; var channelId = offset < 4 ? 1 : 2; SelectChannel(channelId); e.Handled = true;
            await Try(async () =>
            {
                switch (offset % 4)
                {
                    case 0: await Transport(channelId, Channel(channelId).Status == "Playing" ? "Pause" : "Play"); break;
                    case 1: await RundownAction(channelId, "Park", ChannelOne.SelectedItem); break;
                    case 2: await RundownAction(channelId, "Take", ChannelOne.SelectedItem); break;
                    case 3: await Send(new EngineCommand { Channel = channelId, Action = "LoopPlayback", Enabled = !Channel(channelId).LoopPlayback }); break;
                }
            }); return;
        }
        if (SourcePreview.IsKeyboardFocusWithin) return;
        var action = key switch { Key.Space => Channel(selectedChannel).Status == "Playing" ? "Pause" : "Play", Key.PageDown => "Next", Key.PageUp => "Previous", Key.Left or Key.Right => "Step", Key.E => "Emergency", Key.K => "Pause", _ => null };
        if (action is null) return; e.Handled = true; await Try(() => Transport(selectedChannel, action, key == Key.Left ? -1 : 1));
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (smokeFile is null && state?.Channels.Any(c => c.Status == "Playing" || c.Emergency) == true && MessageBox.Show(this, "Close the operator console? File/network engine playback continues. Browser and NDI inputs stop and use signal-loss fallback. Fullscreen windows will close.", "Close console", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        e.Cancel = true; closing = true; stateTimer.Stop(); frameTimer.Stop();
        await SourcePreview.CloseAsync();
        liveInputsWindow?.Close(); browserSources?.Dispose();
        foreach (var window in toolWindows.Values.ToArray()) window.Close(); foreach (var output in outputs.ToArray()) output.Close(); foreach (var frame in frames) frame.Dispose(); client.Dispose();
        await Dispatcher.Yield(DispatcherPriority.Background); Close();
    }
}




