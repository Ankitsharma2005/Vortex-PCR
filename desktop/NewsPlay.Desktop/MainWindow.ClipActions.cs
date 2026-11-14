using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
    private bool dualPort;
    public (double Left, double Right) NdiLevels(Guid id, int port) => browserSources?.Ndi.Levels(id, port) ?? (0, 0);
    private static readonly Dictionary<UIElement, Point> dragStarts = new();
    public static DataGridRow? RowAt(object source)
    {
        var node = source as DependencyObject;
        while (node is not null && node is not DataGridRow)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as DataGridRow;
    }
    public static void TrackDrag(DataGrid grid) => grid.PreviewMouseLeftButtonDown += (_, e) => dragStarts[grid] = e.GetPosition(grid);
    public static bool DragReady(UIElement grid, MouseEventArgs e)
    {
        if (!dragStarts.TryGetValue(grid, out var start)) return false;
        var delta = e.GetPosition(grid) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
        dragStarts.Remove(grid); return true;
    }
    private void InitializeClipWorkspace()
    {
        var topMenu = (Menu)((DockPanel)((Grid)Content).Children[0]).Children[0];
        AddFileMenu(topMenu); SafeCheck.Visibility = Visibility.Collapsed;
        var outputMenu = topMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Output");
        var standby = new MenuItem { Header = "Standby selected port" }; standby.Click += async (_, _) => await Try(() => Transport(selectedChannel, "Emergency"));
        var back = new MenuItem { Header = "Return from standby" }; back.Click += async (_, _) => await Try(() => Transport(selectedChannel, "Return")); outputMenu.Items.Add(new Separator()); outputMenu.Items.Add(standby); outputMenu.Items.Add(back);
        var inputMenu = topMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Input");
        var liveMenu = new MenuItem { Header = "Live Inputs…" }; liveMenu.Click += OpenLiveInputs;
        inputMenu.Items.Insert(0, liveMenu);
        TrackDrag(MediaGrid);
        MediaGrid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var row = RowAt(e.OriginalSource);
            dragMediaSelection = row?.Item is MediaAsset && row.IsSelected ? SelectedBrowserMedia().Select(m => m.Id).ToArray() : [];
        };
        MediaGrid.PreviewMouseRightButtonDown += (_, e) => { if (RowAt(e.OriginalSource) is { } row) row.IsSelected = true; else MediaGrid.UnselectAll(); };
        MediaGrid.ContextMenu = new ContextMenu();
        MediaGrid.ContextMenuOpening += (_, e) =>
        {
            if (MediaGrid.SelectedItem is not MediaAsset media) { e.Handled = true; return; }
            var menu = MediaGrid.ContextMenu!; menu.Items.Clear();
#if MCR_VARIANT
            AddAction(menu, "Preview / Play Now…", () => ActivateMedia(media));
#else
            AddAction(menu, dualPort ? "Play now / cue to port…" : "Play", () => ActivateMedia(media));
#endif
            AddAction(menu, "Preview on source", () => SourcePreview.Load(media));
            AddAction(menu, "Colour correction…", () => OpenColour(media.Id));
            AddAction(menu, "Trim / properties", () => EditItem(selectedChannel, PlayoutController.NewItem(media), false));
            menu.Items.Add(new Separator());
            AddAction(menu, "Remove from Clip Browser", RemoveBrowserClips);
        };
        var file = Path.Combine(client.DataDirectory, "workspace-mode.txt");
        var savedDual = false;
        try { savedDual = smokeFile is null && File.Exists(file) && File.ReadAllText(file).Trim().Equals("dual", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) { StatusText.Text = "Workspace preference could not be read: " + ex.Message; }
        SetPortMode(savedDual, false);
    }
    private void SingleModeClicked(object sender, RoutedEventArgs e) => SetPortMode(false, true);
    private void DualModeClicked(object sender, RoutedEventArgs e) => SetPortMode(true, true);
    private void SetPortMode(bool dual, bool persist)
    {
        SingleModeMenu.Header = "Single Playout Mode";
        DualModeMenu.Header = "Dual Playout Mode";
        dualPort = dual; SingleModeMenu.IsChecked = !dual; DualModeMenu.IsChecked = dual;
        SecondDeckColumn.Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(ChannelTwo, dual ? 1 : 0);
        DeckRow.Height = new GridLength(dual ? 446 : 526);
        ChannelOne.SetCompact(dual); ChannelTwo.SetCompact(dual);
#if MCR_VARIANT
        PortOneRundown.Header = "MASTER PLAYLIST · SINGLE PROGRAM";
#else
        PortOneRundown.Header = "SHARED RUNDOWN · PORT 1 + PORT 2";
#endif
        SelectChannel(selectedChannel);
        if (persist)
        {
            try { Directory.CreateDirectory(client.DataDirectory); File.WriteAllText(Path.Combine(client.DataDirectory, "workspace-mode.txt"), dual ? "dual" : "single"); }
            catch (Exception ex) { StatusText.Text = "Mode changed, but preference could not be saved: " + ex.Message; }
        }
    }
    private (int Port, string Action)? ChooseClipDestination()
    {
#if MCR_VARIANT
        var mcrDialog = new Window { Owner = this, Title = "Preview or Program", Width = 500, Height = 230, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var mcrPanel = new StackPanel { Margin = new Thickness(16) };
        mcrPanel.Children.Add(new TextBlock { Text = "Preview the selected clip in the source monitor, or take it directly to the single Program output.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        (int Port, string Action)? mcrResult = null;
        var mcrButtons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var (port, action, label) in new[] { (0, "Preview", "◉  Preview source"), (1, "Take", "▶  Play Now on Program") })
        {
            var button = new Button { Content = label, Height = 58, FontSize = 14, Margin = new Thickness(4), Background = action == "Take" ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.SlateGray };
            button.Click += (_, _) => { mcrResult = (port, action); mcrDialog.DialogResult = true; };
            mcrButtons.Children.Add(button);
        }
        mcrPanel.Children.Add(mcrButtons);
        mcrPanel.Children.Add(new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Width = 90 });
        mcrDialog.Content = mcrPanel; mcrDialog.ShowDialog(); return mcrResult;
#else
        var dialog = new Window { Owner = this, Title = "Play or cue clip", Width = 490, Height = 260, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Play Now immediately replaces playback on the selected port.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        (int Port, string Action)? result = null;
        foreach (var action in new[] { "Take", "Cue" })
        {
            var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            foreach (var port in new[] { 1, 2 })
            {
                var button = new Button { Content = action == "Take" ? $"▶  Play Now on Port {port}" : $"Cue on Port {port}", Height = 46, FontSize = 14 };
                if (action == "Take") button.Background = System.Windows.Media.Brushes.SeaGreen;
                button.Click += (_, _) => { result = (port, action); dialog.DialogResult = true; };
                row.Children.Add(button);
            }
            panel.Children.Add(row);
        }
        panel.Children.Add(new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Width = 90 });
        dialog.Content = panel; dialog.ShowDialog(); return result;
#endif
    }
    public async Task ActivateItem(int sourcePort, RundownItem item)
    {
#if MCR_VARIANT
        var mcrChoice = ChooseClipDestination();
        if (mcrChoice is { Action: "Preview" }) { await PreviewMcrItem(item); return; }
        if (mcrChoice is { Action: "Take" }) { await RouteClip(1, sourcePort, item, null, "Take"); return; }
#else
        var choice = dualPort ? ChooseClipDestination() : (Port: sourcePort, Action: "Take");
        if (choice is { } destination) await RouteClip(destination.Port, sourcePort, item, null, destination.Action);
#endif
    }
    private async Task ActivateMedia(MediaAsset media)
    {
#if MCR_VARIANT
        var mcrChoice = ChooseClipDestination();
        if (mcrChoice is { Action: "Preview" }) { await SourcePreview.Load(media); mcrPreviewMedia = media; mcrPreviewItem = null; return; }
        if (mcrChoice is { Action: "Take" }) { await RouteClip(1, null, null, media.Id, "Take"); return; }
#else
        var choice = dualPort ? ChooseClipDestination() : (Port: selectedChannel, Action: "Take");
        if (choice is { } destination) await RouteClip(destination.Port, null, null, media.Id, destination.Action);
#endif
    }
    private async Task RouteClip(int target, int? source, RundownItem? item, Guid? media, string action)
    {
        Guid id;
        if (item is not null && (source == target || state!.SharedRundown && Rundown(target).Items.Any(i => i.Id == item.Id))) id = item.Id;
        else
        {
            if (!await EnsureRundownEditable(target)) return;
            var prior = Rundown(target).Items.Select(i => i.Id).ToHashSet();
            await Send(new EngineCommand { Action = "Rundown.Add", Channel = target, MediaId = media, Item = item is null ? null : Json.Copy(item) });
            id = Rundown(target).Items.Single(i => !prior.Contains(i.Id)).Id;
        }
        await Send(new EngineCommand { Action = action, Channel = target, ItemId = id }); SelectChannel(target);
    }
    public async Task<bool> EnsureRundownEditable(int port)
    {
        // Refresh before deciding: the engine may have restored a saved lock state.
        state = await client.GetState();
        Render();
        var rundown = Rundown(port);
        if (!rundown.Locked) return true;
        if (MessageBox.Show(this, $"Port {port}: '{rundown.Name}' is locked.\n\nUnlock this rundown and continue the clip operation?", "Unlock rundown", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            StatusText.Text = $"Port {port}: clip operation cancelled; rundown remains locked.";
            return false;
        }
        await Send(new EngineCommand { Action = "Rundown.Lock", Channel = port, RundownId = rundown.Id, Enabled = false });
        return true;
    }
    public async Task DropToPort(int target, IDataObject data)
    {
        if (data.GetData("NewsPlay.Media") is Guid media) await RouteClip(target, null, null, media, "Cue");
        else if (data.GetData("NewsPlay.Item") is string raw)
        {
            var parts = raw.Split('|'); var source = int.Parse(parts[0]); var id = Guid.Parse(parts[1]);
            var item = Rundown(source).Items.Single(i => i.Id == id);
            await RouteClip(target, source, item, null, "Cue");
        }
    }
    public void AddAction(ContextMenu menu, string title, Func<Task> action)
    {
        var entry = new MenuItem { Header = title }; entry.Click += async (_, _) => await Try(action); menu.Items.Add(entry);
    }
    public void PopulateClipMenu(ContextMenu menu, int port, RundownItem item)
    {
        menu.Items.Clear();
#if MCR_VARIANT
        AddAction(menu, "Preview / Play Now…", () => ActivateItem(port, item));
#else
        AddAction(menu, dualPort ? "Play now / cue to port…" : "Play", () => ActivateItem(port, item));
#endif
        AddAction(menu, "Loop clip — toggle", async () =>
        {
            if (Channel(port).CurrentId == item.Id) await Send(new EngineCommand { Action = "LoopClip", Channel = port, Enabled = !Channel(port).LoopCurrent });
            else { var copy = Json.Copy(item); copy.Loops = copy.Loops == -1 ? 1 : -1; await Send(new EngineCommand { Action = "Rundown.Update", Channel = port, ItemId = item.Id, Item = copy }); }
        });
        AddAction(menu, "Schedule…", async () =>
        {
            var value = PromptWindow.Ask(this, "Schedule clip", "Local date/time (yyyy-MM-dd HH:mm:ss). Interrupts this port at the scheduled time.", DateTime.Now.AddMinutes(5).ToString("yyyy-MM-dd HH:mm:ss"));
            if (value is null) return;
            if (!DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var at)) throw new InvalidOperationException("Invalid date/time");
            await Send(new EngineCommand { Action = "Schedule", Schedule = new ScheduledTake { Channel = port, RundownId = Channel(port).RundownId, ItemId = item.Id, Title = item.Title, At = at } });
        });
        AddAction(menu, "Advance — take this clip now", () => Send(new EngineCommand { Action = "Take", Channel = port, ItemId = item.Id }));
        AddAction(menu, "Preview on source", async () => { var media = state!.Media.SingleOrDefault(m => m.Id == item.MediaId) ?? throw new InvalidOperationException("This item has no source media"); await SourcePreview.Load(media); });
        AddAction(menu, "Trim / properties", () => RundownAction(port, "Edit", item));
        AddAction(menu, "Colour correction…", () => OpenColour(item.MediaId));
        menu.Items.Add(new Separator());
        foreach (var action in new[] { "Copy", "Cut", "Paste", "Delete", "Up", "Down", "Clear" })
        {
            var selectedAction = action; AddAction(menu, action == "Clear" ? "Remove all clips…" : action, () => { SelectChannel(port); return PlaylistEdit(selectedAction); });
        }
    }
}


