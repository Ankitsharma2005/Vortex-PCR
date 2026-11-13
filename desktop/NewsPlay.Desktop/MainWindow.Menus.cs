using System.Windows;
using System.Windows.Controls;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
#if !MCR_VARIANT
    // The VORTEX navigation rail is collapsed in PCR, but its shared XAML
    // event hookups still need a PCR-side handler at compile time.
    private void McrNavigate(object sender, RoutedEventArgs e) { }
#endif
    private LiveInputsWindow? liveInputsWindow;
    private Window? cgWindow;
    private void OpenAdvancedCg(object sender, RoutedEventArgs e)
    {
        CgSurface.Enabled = true;
        if (cgWindow is not null) { cgWindow.Activate(); return; }
        var surface = new CgSurface(selectedChannel, true);
        cgWindow = new Window { Title = "NewsPlay · Advanced CG", Width = 1400, Height = 900, Content = surface, Owner = this };
        cgWindow.Closed += (_, _) => { surface.Dispose(); cgWindow = null; };
        cgWindow.Show(); Render();
    }
    private BrowserSources? browserSources;
    private BrowserSources BrowserInputs => browserSources ??= new BrowserSources(client);
    private void OpenLiveInputs(object sender, RoutedEventArgs e)
    {
        if (liveInputsWindow is not null) { liveInputsWindow.Activate(); return; }
        liveInputsWindow = new LiveInputsWindow(client, Send, selectedChannel, BrowserInputs) { Owner = this };
        liveInputsWindow.Closed += (_, _) => liveInputsWindow = null;
        liveInputsWindow.Show();
    }
    private async void OpenToolMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out var index)) return;
        if (toolWindows.TryGetValue(index, out var existing)) { existing.Activate(); return; }
        var tab = (TabItem)ToolsTabs.Items[index]; var content = tab.Content; tab.Content = null;
        var window = new Window { Style = (Style)Application.Current.Resources[typeof(Window)], Title = "NewsPlay · " + tab.Header, Width = 820, Height = 430, MinWidth = 650, MinHeight = 340, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content, Padding = new Thickness(16) };
        toolWindows[index] = window;
        window.Closed += (_, _) => { window.Content = null; tab.Content = content; toolWindows.Remove(index); };
        if (index == 0) { GraphicsChannel.SelectedIndex = selectedChannel - 1; LoadGraphics(); }
        window.Show();
        if (index == 3) await Try(async () => LogsGrid.ItemsSource = await client.Logs());
    }
    private async void MenuAction(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string action }) return;
        await Try(async () =>
        {
            switch (action)
            {
                case "Reconnect": await client.Connect(); await Poll(); break;
                case "Program1": SelectChannel(1); break;
                case "Program2": SelectChannel(2); break;
                case "Link": await Send(new EngineCommand { Action = "Link", Enabled = !(state?.Linked ?? false) }); break;
                case "Connection":
                    var s = await client.GetState();
#if MCR_VARIANT
                    Info("Connection", "Local MCR engine connected\n\nSingle Program: " + s.Channels.Single(c => c.Id == 1).Status + " · " + s.Channels.Single(c => c.Id == 1).Format + "\n\nState and logs: " + client.DataDirectory);
#else
                    Info("Connection", "Local engine connected\n\n" + string.Join("\n", s.Channels.Select(c => $"Program {c.Id}: {c.Status} · {c.Format}")) + "\n\nState and logs: " + client.DataDirectory + "\n\nDeckLink, NDI and NRCS connections are not implemented.");
#endif
                    break;
                case "Input": Info("Input sources", "Local media / watch folder: available from Input → Import media and Tools → Engine settings.\n\nSource preview is independent of program playback.\n\nDeckLink SDI/HDMI capture: not implemented.\nNDI input: not implemented.\nHDMI/DP ports on a normal graphics card are outputs, not capture inputs.\n\nLive input settings require a supported capture device and adapter integration."); break;
                case "Output": new OutputSettingsWindow(client, Send, StartDisplayOutputs, StopDisplayOutputs, selectedChannel) { Owner = this }.ShowDialog(); break;
                case "Streaming": new StreamingWindow(client) { Owner = this }.Show(); break;
                case "StopOutputs": StopDisplayOutputs(); break;
                case "Shortcuts": ShowShortcuts(); break;
                case "Help":
#if MCR_VARIANT
                    Info("VORTEX MCR operator help", "Import clips → add them to the Master Playlist → load a clip in Preview → CUT Preview to Program.\n\nF1 Play/Pause · F2 Park · F3 Play Now · F4 Loop playlist\nCtrl+Q Cue · Ctrl+G Locate current · F12 Shortcuts\n\nLive Switcher, CG Studio, Streaming, Scheduler, Reports and System tools are in the VORTEX navigation rail. Preview is an operator monitor; MCR has one Program output."); break;
#else
                    Info("Operator help", "Import clips → Add to Program → select story → Park → Still/Continue.\n\nCue queues the next story without interrupting output. Take starts the selected story immediately.\n\nF1 Still/Continue · F2 Park · F3 Take · F4 Loop\nCtrl+Q Cue · Ctrl+G Current\n\nCG controls are in the CG menu. Display selection and audio routing are in Output. Choose multiple displays to mirror the same program.\n\nEsc leaves fullscreen output; closing the console closes its display outputs.\n\nOne program is visible at a time. Saved rundowns or F5–F8 can select Program 2; its engine is independent."); break;
#endif
            }
        });
    }
    private void Info(string title, string message)
    {
        var w = new Window { Style = (Style)Application.Current.Resources[typeof(Window)], Title = "NewsPlay · " + title, Width = 640, Height = 410, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        w.Content = new ScrollViewer { Margin = new Thickness(24), Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 } }; w.ShowDialog();
    }
    private void StopDisplayOutputs() { foreach (var o in outputs.ToArray()) o.Close(); }
    private void StartDisplayOutputs(DisplayOutputProfile profile)
    {
        var displays = DisplayDevices.All();
        if (profile.Displays.Count == 0 || profile.Displays.Any(id => !displays.Any(d => d.Id == id))) throw new InvalidOperationException("Selected display is no longer connected");
        var created = new List<OutputWindow>();
        try
        {
            foreach (var id in profile.Displays)
            {
                var window = new OutputWindow(profile.Channel); created.Add(window); window.SetFit(profile.Fit); window.Show(); DisplayDevices.Place(window, displays.Single(d => d.Id == id));
                if (state is not null) window.Update(state); window.SetFrame(frames[profile.Channel - 1].Image);
            }
        }
        catch { foreach (var w in created) w.Close(); throw; }
        StopDisplayOutputs();
        foreach (var window in created) { outputs.Add(window); window.Closed += (_, _) => outputs.Remove(window); }
    }
}


