using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
    private Dictionary<string, string>? hotkeys;
#if MCR_VARIANT
    private static readonly (string Gesture, string Action)[] KeyActions = [
        ("Space", "Play / pause Program"), ("Up", "Select previous playlist clip"), ("Down", "Select next playlist clip"), ("F9", "Select Program"),
        ("PageDown", "Next clip"), ("PageUp", "Previous clip"), ("L", "Loop clip"), ("R", "Restart clip"),
        ("M", "Mute Program"), ("A", "Auto / manual"), ("Ctrl+Shift+S", "Stop Program"),
        ("F1", "Play / pause Program"), ("F2", "Park selected clip"), ("F3", "Play now"), ("F4", "Toggle playlist loop"),
        ("Ctrl+Q", "Cue selected"), ("Ctrl+G", "Locate current"), ("Ctrl+S", "Save playlist"), ("Ctrl+O", "Load playlist"),
        ("Ctrl+C", "Copy"), ("Ctrl+X", "Cut"), ("Ctrl+V", "Paste"), ("Ctrl+Z", "Undo"), ("Ctrl+Y", "Redo"),
        ("Ctrl+I", "Import rundown"), ("Ctrl+E", "Export rundown"), ("Alt+Up", "Move up"), ("Alt+Down", "Move down"),
        ("Delete", "Remove selected"), ("Ctrl+Alt+D1", "Live 1"), ("Ctrl+Alt+D2", "Live 2"), ("Ctrl+Alt+D3", "Live 3"),
        ("Ctrl+Alt+B", "Back to playlist"), ("Left", "Frame backward"), ("Right", "Frame forward"), ("E", "Standby"), ("K", "Pause"), ("F12", "Shortcut help")
    ];
#else
    private static readonly (string Gesture, string Action)[] KeyActions = [
        ("Space", "Play / pause selected port"), ("Up", "Select previous rundown clip"), ("Down", "Select next rundown clip"), ("F9", "Select Port 1"), ("F10", "Select Port 2"),
        ("Ctrl+D1", "Select Port 1 (alternate)"), ("Ctrl+D2", "Select Port 2 (alternate)"),
        ("Ctrl+NumPad1", "Select Port 1 (keypad)"), ("Ctrl+NumPad2", "Select Port 2 (keypad)"),
        ("PageDown", "Next clip"), ("PageUp", "Previous clip"), ("L", "Loop clip"), ("R", "Restart clip"),
        ("M", "Mute"), ("A", "Auto / manual"), ("Ctrl+Shift+S", "Stop"),
        ("F1", "Play / pause Port 1"), ("F5", "Play / pause Port 2"), ("F2", "Park Port 1"), ("F6", "Park Port 2"),
        ("F3", "Play now Port 1"), ("F7", "Play now Port 2"), ("F4", "Toggle Loop Playback on Port 1"), ("F8", "Toggle Loop Playback on Port 2"),
        ("Ctrl+Q", "Cue selected"), ("Ctrl+G", "Locate current"), ("Ctrl+S", "Save playlist"), ("Ctrl+O", "Load playlist"),
        ("Ctrl+C", "Copy"), ("Ctrl+X", "Cut"), ("Ctrl+V", "Paste"), ("Ctrl+Z", "Undo"), ("Ctrl+Y", "Redo"),
        ("Ctrl+I", "Import rundown"), ("Ctrl+E", "Export rundown"), ("Alt+Up", "Move up"), ("Alt+Down", "Move down"),
        ("Delete", "Remove selected"), ("Ctrl+Alt+D1", "Live 1"), ("Ctrl+Alt+D2", "Live 2"), ("Ctrl+Alt+D3", "Live 3"),
        ("Ctrl+Alt+B", "Back to playlist"), ("Left", "Frame backward"), ("Right", "Frame forward"), ("E", "Standby"), ("K", "Pause"), ("F12", "Shortcut help")
    ];
#endif
    private string HotkeyPath => Path.Combine(client.DataDirectory, "hotkeys.json");
    private static string Gesture(Key key, ModifierKeys mods) => (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + key;
    private static (Key Key, ModifierKeys Mods) ParseGesture(string text)
    {
        var parts = text.Split('+'); var mods = ModifierKeys.None;
        foreach (var part in parts.SkipLast(1)) mods |= part switch { "Ctrl" => ModifierKeys.Control, "Alt" => ModifierKeys.Alt, "Shift" => ModifierKeys.Shift, _ => throw new InvalidOperationException("Unknown modifier") };
        return (Enum.Parse<Key>(parts[^1]), mods);
    }
    private Dictionary<string, string> Hotkeys
    {
        get
        {
            if (hotkeys is not null) return hotkeys;
            hotkeys = KeyActions.ToDictionary(x => x.Gesture, x => x.Gesture);
            try
            {
                if (File.Exists(HotkeyPath))
                {
                    var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(HotkeyPath))!;
                    var candidate = new Dictionary<string, string>(hotkeys);
                    foreach (var pair in saved.Where(p => candidate.ContainsKey(p.Key))) { if (pair.Value.Length > 0) ParseGesture(pair.Value); candidate[pair.Key] = pair.Value; }
                    if (candidate.Values.Where(v => v.Length > 0).Distinct().Count() == candidate.Values.Count(v => v.Length > 0)) hotkeys = candidate;
                }
            } catch { StatusText.Text = "Invalid hotkey settings; defaults restored."; }
            return hotkeys;
        }
    }
    private bool TranslateHotkey(ref Key key, ref ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Windows)) return false;
        var actual = Gesture(key, modifiers);
        var match = Hotkeys.FirstOrDefault(p => p.Value == actual);
        if (match.Key is null) return false;
        (key, modifiers) = ParseGesture(match.Key); return true;
    }
    private void EditHotkeys(object sender, RoutedEventArgs e) => ShowHotkeyEditor();
    private void ShowHotkeyEditor()
    {
        var draft = new Dictionary<string, string>(Hotkeys); var fields = new Dictionary<string, TextBox>();
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Click a key field and press your shortcut. Clear disables that binding. Changes apply after Save. Shortcuts work in the main console; text editing keeps its own keys.", TextWrapping = TextWrapping.Wrap });
        var dialog = new Window { Owner = this, Title = "Custom keyboard shortcuts", Width = 690, Height = 740, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel(); dialog.Content = root; var footer = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var entry in KeyActions)
        {
            var row = new DockPanel { Margin = new Thickness(0, 5, 0, 0) }; var clear = new Button { Content = "Clear" }; DockPanel.SetDock(clear, Dock.Right); row.Children.Add(clear);
            var field = new TextBox { Text = draft[entry.Gesture], IsReadOnly = true, Width = 170 }; DockPanel.SetDock(field, Dock.Right); row.Children.Add(field); fields[entry.Gesture] = field;
            row.Children.Add(new TextBlock { Text = entry.Action, VerticalAlignment = VerticalAlignment.Center }); panel.Children.Add(row);
            clear.Click += (_, _) => { draft[entry.Gesture] = ""; field.Text = ""; };
            field.PreviewKeyDown += (_, args) =>
            {
                args.Handled = true; var key = args.Key == Key.System ? args.SystemKey : args.Key; var mods = Keyboard.Modifiers;
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin || mods.HasFlag(ModifierKeys.Windows) || (mods == ModifierKeys.Alt && key is Key.F4 or Key.Tab) || key is Key.Escape or Key.Tab) return;
                draft[entry.Gesture] = Gesture(key, mods); field.Text = draft[entry.Gesture];
            };
        }
        var reset = new Button { Content = "Restore defaults" }; footer.Children.Add(reset); reset.Click += (_, _) => { foreach (var entry in KeyActions) { draft[entry.Gesture] = entry.Gesture; fields[entry.Gesture].Text = entry.Gesture; } };
        var save = new Button { Content = "Save shortcuts" }; footer.Children.Add(save); save.Click += (_, _) =>
        {
            var duplicate = draft.Where(p => p.Value.Length > 0).GroupBy(p => p.Value).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null) { MessageBox.Show(dialog, "Shortcut already assigned: " + duplicate.Key + ". Clear or change the conflicting binding."); return; }
            try { Directory.CreateDirectory(client.DataDirectory); File.WriteAllText(HotkeyPath + ".tmp", JsonSerializer.Serialize(draft)); File.Move(HotkeyPath + ".tmp", HotkeyPath, true); hotkeys = draft; dialog.Close(); }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message); }
        };
        dialog.ShowDialog();
    }
}
