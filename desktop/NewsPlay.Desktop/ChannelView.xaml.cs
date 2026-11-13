using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public partial class ChannelView : UserControl
{
    public int ChannelId { get; set; }
    public MainWindow Host { get; set; } = null!;
    private bool updating;
    private CgSurface? advancedCg;
    internal CgSurface? AdvancedCgSurface => advancedCg;
    private string signature = "", choices = "", tickerSignature = "";
    private ChannelState? channel;
    private bool transportBusy;
    private bool countdownWarning;
    private PreflightReport? preflight;
    private string preflightSignature = "";
    public void SetPreflight(PreflightReport report) { preflight = report; preflightSignature = ""; signature = ""; }
    public void GoToCurrent() { var row = RundownGrid.Items.Cast<RundownItem>().FirstOrDefault(i => i.Id == channel?.CurrentId); if (row is not null) { RundownGrid.SelectedItem = row; RundownGrid.ScrollIntoView(row); RundownGrid.Focus(); } }
    public Guid? SelectedItemId => (RundownGrid.SelectedItem as RundownItem)?.Id;
    public RundownItem? SelectedItem => RundownGrid.SelectedItem as RundownItem;
    public void MoveSelection(int direction)
    {
        var items = RundownGrid.Items.Cast<RundownItem>().ToArray(); if (items.Length == 0) return;
        var anchor = SelectedItemId ?? channel?.CurrentId;
        var index = anchor is { } id ? Array.FindIndex(items, item => item.Id == id) : -1;
        if (index < 0) index = direction > 0 ? -1 : items.Length;
        var target = items[Math.Clamp(index + Math.Sign(direction), 0, items.Length - 1)];
        RundownGrid.SelectedItem = target; RundownGrid.ScrollIntoView(target); RundownGrid.Focus();
    }
    public ChannelView()
    {
        InitializeComponent(); ConfigureClipActions();
#if MCR_VARIANT
        SlateTitle.Text = "VORTEX MCR"; LogoText.Text = "VORTEX";
#endif
        PreviewMouseDown += (_, _) => Host?.SelectChannel(ChannelId);
    }
    public FrameworkElement DetachRundown() { OuterLayout.Children.Remove(RundownPanel); return RundownPanel; }
    public void SetSelected(bool selected) => OuterBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected ? "#7198E3" : "#303C50"));
    public void Update(EngineState state)
    {
        if (!CgSurface.Enabled && advancedCg is not null) { Monitor.Children.Remove(advancedCg); advancedCg.Dispose(); advancedCg = null; }
        if (CgSurface.Enabled && advancedCg is null) { advancedCg = new CgSurface(ChannelId); Monitor.Children.Add(advancedCg); }
        updating = true;
        try
        {
            channel = state.Channels.Single(c => c.Id == ChannelId); var p = state.Rundowns.Single(p => p.Id == channel.RundownId); var item = p.Items.FirstOrDefault(i => i.Id == channel.CurrentId);
            PlayPauseButton.Content = channel.Status == "Playing" ? "⏸" : "▶";
            PlayPauseButton.ToolTip = channel.Status == "Playing" ? "Pause current playback" : channel.Status == "Paused" ? "Resume current playback" : "Play selected clip";
            PlayPauseButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(channel.Status == "Playing" ? "#9A6826" : "#23775C"));
            if (channel.LiveInputId is { } live) item = PlayoutController.NewItem(state.Media.Single(m => m.Id == live));
            PlayPauseButton.IsEnabled = !transportBusy && !(channel.Status == "Playing" && item?.Kind == "Live");
            if (!PlayPauseButton.IsEnabled && item?.Kind == "Live") PlayPauseButton.ToolTip = "Live input cannot pause; use Stop or Back to Playlist";
            var next = p.Items.FirstOrDefault(i => i.Id == channel.QueuedId);
            if (next is null) { var nextIndex = p.Items.FindIndex(i => i.Id == channel.CurrentId) + 1; if (nextIndex >= p.Items.Count && p.Loop) nextIndex = 0; next = p.Items.ElementAtOrDefault(nextIndex); }
            NextText.Text = channel.LoopPlayback ? "LOOP PLAYBACK · CURRENT CLIP" : channel.Status == "Playing" && (channel.LoopCurrent || item?.Loops == -1) && !channel.BreakLoop ? "LOOPING · NEXT ON BREAK" : (channel.QueuedId is null ? "NEXT  " : "CUED NEXT  ") + (next?.Title ?? "END OF RUNDOWN");
            NextText.ToolTip = NextText.Text;
            AutoButton.Content = channel.AutoAdvance ? "AUTO" : "MANUAL"; AutoButton.Background = channel.AutoAdvance ? new SolidColorBrush(Color.FromRgb(31, 89, 72)) : new SolidColorBrush(Color.FromRgb(106, 77, 35));
            LockButton.Content = p.Locked ? "Unlock" : "Lock";
            var remaining = Math.Max(0, (item?.Out ?? 0) - channel.Position);
            AutoBarButton.Content = channel.AutoAdvance ? "AUTO ON" : "MANUAL";
            AutoBarButton.Background = channel.AutoAdvance ? Brushes.DarkGreen : Brushes.DarkGoldenrod;
            var progress = item is null || channel.LiveInputId is not null ? 0 : Math.Clamp((channel.Position - item.In) / Math.Max(.001, item.Duration) * 100, 0, 100);
            AutoProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new DoubleAnimation(progress, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            AutoProgress.Foreground = new LinearGradientBrush(channel.AutoAdvance ? Color.FromRgb(33, 115, 105) : Color.FromRgb(126, 85, 32), channel.AutoAdvance ? Color.FromRgb(93, 235, 182) : Color.FromRgb(244, 192, 79), 0);
            AutoProgressText.Text = channel.Emergency ? "STANDBY" : channel.LiveInputId is not null ? "LIVE · BACK TO PLAYLIST TO RETURN" : item is null ? "READY · SELECT A CLIP" : channel.Status != "Playing" ? channel.Status.ToUpperInvariant() : channel.LoopPlayback ? "LOOP PLAYBACK · " + Timecode.Format(remaining, channel.Fps) : !channel.BreakLoop && (channel.LoopCurrent || item.Loops == -1 || channel.Pass < item.Loops) ? "LOOPING · " + Timecode.Format(remaining, channel.Fps) : channel.EndMode != "Auto" ? channel.EndMode.ToUpperInvariant() + " AT END · " + Timecode.Format(remaining, channel.Fps) : next is null ? "END OF LIST · " + Timecode.Format(remaining, channel.Fps) : (channel.AutoAdvance && next.StartMode != "Manual" ? "AUTO NEXT IN " : "PARK NEXT IN ") + Timecode.Format(remaining, channel.Fps);
            var warning = channel.Status == "Playing" && channel.LiveInputId is null && item?.Kind != "Live" && remaining > 0 && remaining <= 10;
            if (warning != countdownWarning)
            {
                countdownWarning = warning;
                var brush = new SolidColorBrush(Color.FromRgb(24, 35, 48)); RemainingBackground.Background = brush;
                if (warning) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromRgb(75, 18, 30), Color.FromRgb(211, 38, 60), TimeSpan.FromMilliseconds(450)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                RemainingText.BeginAnimation(OpacityProperty, warning ? new DoubleAnimation(1, .45, TimeSpan.FromMilliseconds(450)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever } : null);
            }
            RemainingText.Foreground = warning ? Brushes.White : new SolidColorBrush(Color.FromRgb(169, 195, 233));
            RemainingText.ToolTip = channel.LoopPlayback ? "Current pass remaining; Loop Playback is active" : channel.LoopCurrent && !channel.BreakLoop ? "Current pass remaining; Loop Clip is active" : item?.Loops == -1 && !channel.BreakLoop ? "Current pass remaining; item loop is active" : "Current pass remaining";
            var checkSignature = System.Text.Json.JsonSerializer.Serialize(p.Items, Json.Options);
            if (preflight is not null && (preflight.RundownId != p.Id || (preflightSignature != "" && preflightSignature != checkSignature))) preflight = null;
            if (preflight is not null) preflightSignature = checkSignature;
            PreflightButton.Content = preflight is null ? "Check media" : preflight.Issues.Count == 0 ? "Media checked" : $"{preflight.Issues.Count} issues";
            PreflightButton.ToolTip = preflight is null ? "Check file availability and trim metadata before air" : $"Checked at {preflight.CheckedAt:HH:mm:ss}; click to recheck";
#if MCR_VARIANT
            ChannelTitle.Text = ChannelId == 1 ? "PROGRAM" : "DISABLED";
#else
            ChannelTitle.Text = $"PORT {ChannelId}   ·   F{ChannelId + 8}";
#endif
            StatusBadge.Text = channel.Status == "Cued" ? "PARKED" : channel.Status.ToUpperInvariant(); StatusBadge.Foreground = new SolidColorBrush(channel.Status == "Playing" ? Color.FromRgb(104, 221, 126) : Color.FromRgb(213, 199, 122)); FormatLabel.Text = channel.Format;
            DurationText.Text = Timecode.Format(item?.Duration ?? 0, channel.Fps);
            ElapsedText.Text = Timecode.Format(channel.Position - (item?.In ?? 0), channel.Fps); RemainingText.Text = "−" + Timecode.Format((item?.Out ?? 0) - channel.Position, channel.Fps); NowTitle.Text = item?.Title ?? "No clip cued";
            if (channel.LiveInputId is not null) { StatusBadge.Text = "LIVE"; DurationText.Text = "LIVE"; RemainingText.Text = "MANUAL"; }
            if (!PositionSlider.IsMouseCaptureWithin) { PositionSlider.Minimum = item?.In ?? 0; PositionSlider.Maximum = item?.Out ?? 1; PositionSlider.Value = channel.Position; } PositionSlider.IsEnabled = item is not null && !channel.Emergency;
            MuteCheck.IsChecked = channel.Mute; if (!GainSlider.IsMouseCaptureWithin) GainSlider.Value = channel.GainDb; GainLabel.Text = $"{channel.GainDb:0} dB";
            if (item?.Kind == "Live") PositionSlider.IsEnabled = false;
            EndMode.SelectedIndex = channel.EndMode switch { "Hold" => 1, "Black" => 2, _ => 0 }; LoopList.IsChecked = channel.LoopPlayback; LoopClip.IsChecked = channel.LoopCurrent;
            DecoderLabel.Text = $"{channel.DecodedFrames:N0} frames";
            PortMeter.Levels(channel.AudioLeft, channel.AudioRight);
            var asset = state.Media.FirstOrDefault(m => m.Id == item?.MediaId);
            PortMeter.EndpointMix(channel.Status == "Playing" && !channel.Emergency && asset?.Live?.Type == "Browser");
            if (asset?.Live?.Type == "NDI") { var levels = Host.NdiLevels(asset.Id, ChannelId); PortMeter.Levels(levels.Left, levels.Right, "NDI PCM"); }
            LoopIcon.Background = channel.LoopPlayback || channel.LoopCurrent ? Brushes.SeaGreen : new SolidColorBrush(Color.FromRgb(45, 50, 61));
            var newChoices = string.Join('|', state.Rundowns.Select(p => p.Id + p.Name)); if (choices != newChoices) { choices = newChoices; RundownSelect.ItemsSource = state.Rundowns.Where(p => state.SharedRundown || p.Channel == ChannelId).ToList(); } RundownSelect.SelectedValue = p.Id;
            var newSignature = System.Text.Json.JsonSerializer.Serialize(p, Json.Options) + channel.CurrentId + channel.Status + channel.QueuedId + channel.LoopCurrent + channel.LoopPlayback + channel.LiveInputId + preflight?.CheckedAt;
            if (newSignature != signature)
            {
                signature = newSignature; var selected = SelectedItemId; var rows = Json.Copy(p.Items); var current = rows.FindIndex(i => i.Id == channel.CurrentId);
                for (var i = 0; i < rows.Count; i++) rows[i].Status = i == current ? channel.LiveInputId is not null ? "INTERRUPTED / LIVE" : channel.Status.ToUpperInvariant() : preflight?.Issues.Any(issue => issue.ItemId == rows[i].Id) == true ? "MISSING / INVALID" : rows[i].Id == next?.Id ? "NEXT" : "READY";
                RundownGrid.ItemsSource = rows; RundownGrid.SelectedItem = rows.FirstOrDefault(i => i.Id == selected);
            }
            TotalText.Text = p.TotalText;
            var showSlate = channel.Emergency || channel.Status is "Stopped" or "Error" || item is null || item.Kind is "Slate" or "Audio";
            Slate.Visibility = showSlate ? Visibility.Visible : Visibility.Collapsed;
            SlateTitle.Text = channel.Emergency ? "PLEASE STAND BY" : item?.Kind == "Slate" ? item.Title : item?.Kind == "Audio" ? "AUDIO PLAYOUT" : "NEWSPLAY";
            SlateSubtitle.Text = channel.Emergency ? "WE WILL RETURN SHORTLY" : item?.Title ?? "READY WHEN YOU ARE";
            var g = channel.Graphics; LogoText.Text = g.Logo; LogoBox.Visibility = g.LogoOn && !CgSurface.Enabled ? Visibility.Visible : Visibility.Collapsed;
            LowerBox.Visibility = g.LowerOn && !CgSurface.Enabled ? Visibility.Visible : Visibility.Collapsed; Headline.Text = g.Headline; Subtitle.Text = g.Subtitle;
            PreviewClock.Visibility = g.ClockOn && !CgSurface.Enabled ? Visibility.Visible : Visibility.Collapsed; PreviewClock.Text = DateTime.Now.ToString("HH:mm:ss");
            TickerBox.Visibility = g.TickerOn && !CgSurface.Enabled ? Visibility.Visible : Visibility.Collapsed;
            var ts = $"{g.Ticker}|{g.TickerSpeed}|{g.TickerOn}|{g.Color}|{(int)Monitor.ActualWidth}";
            if (tickerSignature != ts)
            {
                tickerSignature = ts; TickerText.Text = g.Ticker;
                try { TickerBox.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(g.Color)); } catch { }
                TickerText.Measure(new Size(double.PositiveInfinity, 30)); var width = Math.Max(1, Monitor.ActualWidth); var textWidth = TickerText.DesiredSize.Width;
                var animation = new DoubleAnimation(width, -textWidth, TimeSpan.FromSeconds((width + textWidth) / Math.Max(10, g.TickerSpeed))) { RepeatBehavior = RepeatBehavior.Forever };
                TickerText.BeginAnimation(Canvas.LeftProperty, g.TickerOn ? animation : null);
            }
        }
        finally { updating = false; }
    }
    public void SetFrame(ImageSource? image) => Preview.Source = image;
    public void SetSafe(bool enabled) => SafeArea.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    private async void IconTransport(object sender, RoutedEventArgs e)
    {
        if (transportBusy || channel is null || sender is not Button { Tag: string action }) return;
        transportBusy = true;
        PlayPauseButton.IsEnabled = false;
        try { await Host.Try(async () =>
        {
            if (action == "Loop") await Host.Send(new EngineCommand { Action = "LoopClip", Channel = ChannelId, Enabled = !channel.LoopCurrent });
            else if (action == "Repeat") { if (channel.CurrentId is { } current) await Host.Send(new EngineCommand { Action = "Take", Channel = ChannelId, ItemId = current }); }
            else if (channel.Status == "Playing") await Host.Transport(ChannelId, "Pause");
            else if (channel.Status == "Paused") await Host.Transport(ChannelId, "Play");
            else if (SelectedItem is { } selected && selected.Id != channel.CurrentId) await Host.Send(new EngineCommand { Action = "Take", Channel = ChannelId, ItemId = selected.Id });
            else await Host.Transport(ChannelId, "Play");
        }); }
        finally { transportBusy = false; PlayPauseButton.IsEnabled = true; }
    }
    private async void Transport(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        await Host.Try(async () => { var value = action == "StepBack" ? -1 : 1; if (action == "Still") action = channel?.Status == "Playing" ? "Pause" : "Play"; if (action.StartsWith("Step")) action = "Step"; await Host.Transport(ChannelId, action, value); });
    }
    private async void ListAction(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        await Host.Try(() => Host.RundownAction(ChannelId, action, SelectedItem));
    }
    private async void SeekReleased(object sender, MouseButtonEventArgs e) => await Host.Try(() => Host.Send(new EngineCommand { Channel = ChannelId, Action = "Seek", Value = PositionSlider.Value }));
    private async void GainReleased(object sender, MouseButtonEventArgs e) => await Host.Try(() => Host.Send(new EngineCommand { Channel = ChannelId, Action = "Gain", Value = Math.Round(GainSlider.Value) }));
    private async void MuteClicked(object sender, RoutedEventArgs e) => await Host.Try(() => Host.Send(new EngineCommand { Channel = ChannelId, Action = "Mute", Enabled = MuteCheck.IsChecked == true }));
    private async void LoopPlaybackClicked(object sender, RoutedEventArgs e) => await Host.Try(() => Host.Send(new EngineCommand { Channel = ChannelId, Action = "LoopPlayback", Enabled = LoopList.IsChecked == true }));
    private async void EndChanged(object sender, SelectionChangedEventArgs e) { if (!updating && channel is not null && EndMode.SelectedItem is ComboBoxItem { Content: string value }) await Host.Try(() => Host.Send(new EngineCommand { Channel = ChannelId, Action = "EndMode", Text = value })); }
    private async void RundownChanged(object sender, SelectionChangedEventArgs e) { if (!updating && channel is not null && RundownSelect.SelectedValue is Guid id && id != channel.RundownId) await Host.Try(() => Host.Send(new EngineCommand { Channel = ChannelId, Action = "Rundown.Load", RundownId = id })); }
    private async void EditDoubleClick(object sender, MouseButtonEventArgs e) { if (MainWindow.RowAt(e.OriginalSource) is not null && SelectedItem is { } item) { e.Handled = true; await Host.Try(() => Host.ActivateItem(ChannelId, item)); } }
    private void ItemSelected(object sender, SelectionChangedEventArgs e) { }
    private void BeginDrag(object sender, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed && MainWindow.DragReady(RundownGrid, e) && SelectedItem is { } item && MainWindow.RowAt(e.OriginalSource) is not null) DragDrop.DoDragDrop(RundownGrid, new DataObject("NewsPlay.Item", $"{ChannelId}|{item.Id}"), DragDropEffects.Move | DragDropEffects.Copy); }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("NewsPlay.MediaIds")) e.Effects = IsInsideRundown(e.OriginalSource) ? DragDropEffects.Copy : DragDropEffects.None;
        else if (e.Data.GetDataPresent("NewsPlay.Media")) e.Effects = DragDropEffects.Copy;
        else if (e.Data.GetData("NewsPlay.Item") is string raw && int.TryParse(raw.Split('|').FirstOrDefault(), out var source)) e.Effects = source == ChannelId ? DragDropEffects.Move : DragDropEffects.Copy;
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; await Host.Try(async () =>
        {
            if (e.Data.GetData("NewsPlay.MediaIds") is string encoded)
            {
                if (!IsInsideRundown(e.OriginalSource)) return;
                var ids = System.Text.Json.JsonSerializer.Deserialize<Guid[]>(encoded) ?? [];
                var target = e.OriginalSource as DependencyObject; while (target is not null && target is not DataGridRow) target = VisualTreeHelper.GetParent(target);
                var index = target is DataGridRow row ? row.GetIndex() : -1; await Host.AddMediaToRundown(ChannelId, ids, index);
            }
            else if (e.Data.GetData("NewsPlay.Media") is Guid mediaId)
            {
                if (!IsInsideRundown(e.OriginalSource)) { await Host.DropToPort(ChannelId, e.Data); return; }
                var target = e.OriginalSource as DependencyObject; while (target is not null && target is not DataGridRow) target = VisualTreeHelper.GetParent(target);
                var index = target is DataGridRow row ? row.GetIndex() : -1; await Host.AddMediaToRundown(ChannelId, [mediaId], index);
            }
            else if (e.Data.GetData("NewsPlay.Item") is string raw)
            {
                var parts = raw.Split('|');
                if (int.Parse(parts[0]) != ChannelId) { await Host.DropToPort(ChannelId, e.Data); return; }
                var source = e.OriginalSource as DependencyObject; while (source is not null && source is not DataGridRow) source = VisualTreeHelper.GetParent(source);
                if (source is DataGridRow row)
                {
                    var index = row.GetIndex();
                    if (await Host.EnsureRundownEditable(ChannelId)) await Host.Send(new EngineCommand { Action = "Rundown.Move", Channel = ChannelId, ItemId = Guid.Parse(parts[1]), Index = index });
                }
            }
        });
    }
    private bool IsInsideRundown(object source)
    {
        var node = source as DependencyObject;
        while (node is not null && node != RundownGrid) node = VisualTreeHelper.GetParent(node);
        return node == RundownGrid;
    }
}



