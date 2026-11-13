using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
    private const string ClipClipboard = "NewsPlay.RundownItems.v1";
    private void AddFileMenu(Menu menu)
    {
        var file = new MenuItem { Header = "_File" }; menu.Items.Insert(0, file);
        void Entry(string label, string action, string? shortcut = null)
        {
            var item = new MenuItem { Header = label, InputGestureText = shortcut ?? "" };
            item.Click += async (_, _) => await Try(() => PlaylistEdit(action)); file.Items.Add(item);
        }
        Entry("Save playlist…", "Save", "Ctrl+S"); Entry("Load playlist…", "Load", "Ctrl+O"); file.Items.Add(new Separator());
        Entry("Copy clips", "Copy", "Ctrl+C"); Entry("Cut clips", "Cut", "Ctrl+X"); Entry("Paste clips after selection", "Paste", "Ctrl+V");
        Entry("Move clip up", "Up", "Alt+↑"); Entry("Move clip down", "Down", "Alt+↓");
        Entry("Delete selected clips", "Delete", "Delete"); Entry("Remove all clips…", "Clear");
        file.Items.Add(new Separator()); Entry("Undo", "Undo", "Ctrl+Z"); Entry("Redo", "Redo", "Ctrl+Y");
    }
    private List<RundownItem> SelectedClips() => Rundown(selectedChannel).Items.Where(i => ChannelOne.RundownGrid.SelectedItems.Cast<RundownItem>().Any(s => s.Id == i.Id)).ToList();
    private async Task PlaylistEdit(string action)
    {
        if (state is null) return;
        var clips = SelectedClips();
        if (action is "Save" or "Load") { await RundownAction(selectedChannel, action == "Save" ? "Export" : "Import", null); return; }
        if (action is "Copy" or "Cut")
        {
            if (clips.Count == 0) throw new InvalidOperationException("Select one or more playlist clips");
            Clipboard.SetData(ClipClipboard, JsonSerializer.Serialize(clips, Json.Options));
            if (action == "Copy") { StatusText.Text = $"Copied {clips.Count} clip(s)"; return; }
        }
        if (!await EnsureRundownEditable(selectedChannel)) return;
        switch (action)
        {
            case "Cut": case "Delete":
                if (clips.Count == 0) throw new InvalidOperationException("Select clips first");
                await Send(new EngineCommand { Action = "Rundown.RemoveMany", Channel = selectedChannel, ItemIds = clips.Select(c => c.Id).ToList() }); break;
            case "Paste":
                if (Clipboard.GetData(ClipClipboard) is not string content || content.Length > 10000000) throw new InvalidOperationException("Copy playlist clips before pasting");
                var pasted = JsonSerializer.Deserialize<List<RundownItem>>(content, Json.Options) ?? [];
                await Send(new EngineCommand { Action = "Rundown.Paste", Channel = selectedChannel, ItemId = clips.LastOrDefault()?.Id, Items = pasted }); break;
            case "Clear":
                if (MessageBox.Show(this, "Remove all clips from this playlist? You can undo while stopped.", "Clear playlist", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                await Send(new EngineCommand { Action = "Rundown.Clear", Channel = selectedChannel }); break;
            case "Up": case "Down": await RundownAction(selectedChannel, action, clips.FirstOrDefault()); break;
            case "Undo": case "Redo": await Send(new EngineCommand { Action = "Rundown." + action, Channel = selectedChannel }); break;
        }
    }
    private sealed class PlaylistFile
    {
        public int Version { get; set; } = 1;
        public Rundown Rundown { get; set; } = new();
        public List<MediaAsset> Media { get; set; } = [];
    }
    private async Task SavePlaylist(int channel)
    {
        var p = Rundown(channel); var dialog = new SaveFileDialog { Filter = "NewsPlay playlist|*.json", FileName = p.Name + ".json" };
        if (dialog.ShowDialog(this) != true) return;
        var file = new PlaylistFile { Rundown = Json.Copy(p), Media = state!.Media.Where(m => p.Items.Any(i => i.MediaId == m.Id)).Select(Json.Copy).ToList() };
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(file, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
    }
    private async Task LoadPlaylist(int channel)
    {
        if (Channel(channel).Status == "Playing" || Channel(channel).Emergency) throw new InvalidOperationException("Stop this port before loading a playlist");
        var dialog = new OpenFileDialog { Filter = "NewsPlay playlist|*.json" }; if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > 10000000) throw new InvalidOperationException("Playlist file is too large");
        var text = await File.ReadAllTextAsync(dialog.FileName); using var document = JsonDocument.Parse(text);
        Rundown list;
        if (document.RootElement.TryGetProperty("rundown", out _))
        {
            var file = JsonSerializer.Deserialize<PlaylistFile>(text, Json.Options) ?? throw new InvalidOperationException("Invalid playlist");
            if (file.Version != 1) throw new InvalidOperationException("Unsupported playlist version"); list = file.Rundown;
            foreach (var asset in file.Media)
            {
                var matching = state!.Media.FirstOrDefault(m => m.Path.Equals(asset.Path, StringComparison.OrdinalIgnoreCase) && m.Kind == asset.Kind);
                if (matching is null)
                {
                    if (asset.Kind == "Live") { var copied = Json.Copy(asset); copied.Id = Guid.NewGuid(); await Send(new EngineCommand { Action = "Live.Save", Asset = copied }); matching = state!.Media.Single(m => m.Id == copied.Id); }
                    else { if (!File.Exists(asset.Path)) throw new InvalidOperationException("Media missing: " + asset.Name); await client.Import(asset.Path); state = await client.GetState(); matching = state.Media.FirstOrDefault(m => m.Path.Equals(asset.Path, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Could not import " + asset.Name); }
                }
                foreach (var item in list.Items.Where(i => i.MediaId == asset.Id)) item.MediaId = matching.Id;
            }
        }
        else list = JsonSerializer.Deserialize<Rundown>(text, Json.Options) ?? throw new InvalidOperationException("Invalid playlist");
        foreach (var item in list.Items) if (item.MediaId is { } id && state!.Media.All(m => m.Id != id)) throw new InvalidOperationException("Playlist references unavailable media; import its source files first");
        var previous = state!.Rundowns.Select(p => p.Id).ToHashSet();
        await Send(new EngineCommand { Action = "Rundown.Import", Channel = channel, Rundown = list });
        var imported = state!.Rundowns.Single(p => !previous.Contains(p.Id)); await Send(new EngineCommand { Action = "Rundown.Load", Channel = channel, RundownId = imported.Id });
    }
}

