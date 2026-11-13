using System.Windows;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
    private async Task RemoveBrowserClips()
    {
        var ids = MediaGrid.SelectedItems.Cast<MediaAsset>().Select(m => m.Id).ToList();
        if (ids.Count == 0) { StatusText.Text = "Select clips in the Clip Browser first."; return; }
        await Send(new EngineCommand { Action = "Media.Remove", ItemIds = ids });
        StatusText.Text = $"Removed {ids.Count} clip(s) from Clip Browser. Disk files and rundown entries are kept. Import again to restore.";
    }
    private async void RemoveBrowserClicked(object sender, RoutedEventArgs e) => await Try(RemoveBrowserClips);
}
