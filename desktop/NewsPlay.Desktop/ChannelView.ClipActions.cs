using System.Windows;
using System.Windows.Controls;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public partial class ChannelView
{
    public void SetCompact(bool compact)
    {
        PreviewRow.Height = new GridLength(compact ? 220 : 350);
        if (TransportPanel.Parent is Panel parent) parent.Children.Remove(TransportPanel);
        TransportButtons.Rows = 1;
        TransportButtons.Columns = 6;
        TransportRow.Height = new GridLength(compact ? 64 : 0);
        PreviewTransportRow.Height = new GridLength(compact ? 0 : 50);
        TimingColumn.Width = new GridLength(compact ? 170 : 205);
        Grid.SetRow(TransportPanel, compact ? 6 : 1);
        Grid.SetColumn(TransportPanel, 0);
        TransportPanel.VerticalAlignment = VerticalAlignment.Stretch;
        TransportPanel.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        TransportPanel.Width = compact ? double.NaN : 540;
        foreach (var button in TransportButtons.Children.OfType<Button>())
        {
            button.Height = compact ? 48 : 34;
            button.FontSize = compact ? 25 : 21;
            button.Padding = new Thickness(compact ? 4 : 2);
        }
        (compact ? DeckLayout : PreviewLayout).Children.Add(TransportPanel);
    }
    private void ConfigureClipActions()
    {
        MainWindow.TrackDrag(RundownGrid);
        // The shared rundown does not change the independently selected program port.
        RundownGrid.PreviewMouseRightButtonDown += (_, e) => { if (MainWindow.RowAt(e.OriginalSource) is { } row) row.IsSelected = true; else RundownGrid.UnselectAll(); };
        RundownGrid.ContextMenu = new ContextMenu();
        RundownGrid.ContextMenuOpening += (_, e) => { if (SelectedItem is not { } item) { e.Handled = true; return; } Host.SelectChannel(ChannelId); Host.PopulateClipMenu(RundownGrid.ContextMenu!, ChannelId, item); };
        OuterBorder.AllowDrop = true;
        OuterBorder.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("NewsPlay.Media") || e.Data.GetDataPresent("NewsPlay.Item") ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        OuterBorder.Drop += async (_, e) => { e.Handled = true; await Host.Try(() => Host.DropToPort(ChannelId, e.Data)); };
        OuterBorder.ToolTip = "Drop a clip here to cue this port. TAKE starts the selected clip.";
    }
    private async void LoopClipClicked(object sender, RoutedEventArgs e) => await Host.Try(() => Host.Send(new EngineCommand { Action = "LoopClip", Channel = ChannelId, Enabled = LoopClip.IsChecked == true }));
}


