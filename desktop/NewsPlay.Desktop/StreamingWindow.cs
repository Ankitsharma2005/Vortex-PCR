using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class StreamingWindow : Window
{
    private readonly EngineClient client;
    private readonly ListBox destinations = new() { DisplayMemberPath = "Name", MinWidth = 220 };
    private readonly TextBox name = new() { Text = "Stream 1" }, url = new(), bitrate = new() { Text = "4000" };
    private readonly ComboBox audioCodec = new() { ItemsSource = new[] { "aac", "ac3" }, SelectedIndex = 0 };
    private readonly ComboBox videoEncoder = new() { ItemsSource = new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf" }, SelectedIndex = 0 };
    private readonly PasswordBox key = new();
    private readonly ComboBox platform = new() { ItemsSource = new[] { "Custom RTMP", "YouTube", "Facebook Live", "X / Twitter", "SRT caller", "SRT listener", "UDP" }, SelectedIndex = 0 };
#if MCR_VARIANT
    private readonly ComboBox port = new() { ItemsSource = new[] { "Program" }, SelectedIndex = 0 };
#else
    private readonly ComboBox port = new() { ItemsSource = new[] { "Port 1", "Port 2" }, SelectedIndex = 0 };
#endif
    private readonly ComboBox size = new() { ItemsSource = new[] { "640x360", "960x540", "1280x720", "1920x1080" }, SelectedIndex = 2 };
    private readonly ComboBox fps = new() { ItemsSource = new[] { 25, 30, 50, 60 }, SelectedIndex = 0 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool polling;
    public StreamingWindow(EngineClient client)
    {
        this.client = client; Title = "NewsPlay · Streaming encoder"; Width = 900; Height = 730; Style = (Style)Application.Current.Resources[typeof(Window)];
        var grid = new Grid { Margin = new Thickness(16) }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(240) }); grid.ColumnDefinitions.Add(new()); Content = grid; grid.Children.Add(destinations);
        var panel = new StackPanel { Margin = new Thickness(15, 0, 0, 0) }; var scroll = new ScrollViewer { Content = panel }; Grid.SetColumn(scroll, 1); grid.Children.Add(scroll);
        void Field(string label, Control control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) }); panel.Children.Add(control); }
        void Button(string text, Func<Task> action) { var b = new Button { Content = text }; b.Click += async (_, _) => { try { await action(); await Refresh(); } catch (Exception ex) { status.Text = ex.Message; } }; panel.Children.Add(b); }
        Field("Destination name", name); Field("Platform / protocol", platform); Field("Program", port); Field("Server URL (use the ingest URL supplied by your platform)", url); Field("Stream key (optional; appended to RTMP URL)", key); Field("H.264 encoder · CPU / NVIDIA NVENC / Intel QSV / AMD AMF", videoEncoder); Field("Encoded size", size); Field("Frame rate", fps); Field("Video bitrate kbps · H.264 / AVC", bitrate); Field("Audio codec · 128 kbps stereo (AC3: SRT / UDP only)", audioCodec);
        Button("Start new destination", async () =>
        {
            var selected = (string)platform.SelectedItem; var dimensions = ((string)size.SelectedItem).Split('x');
            var protocol = selected.StartsWith("SRT") || selected == "UDP" ? selected : "RTMP";
            await client.StreamStart(new StreamProfile { Name = name.Text.Trim(), Port = port.SelectedIndex + 1, Protocol = protocol, Url = url.Text.Trim().TrimEnd('/') + (protocol == "RTMP" && key.Password.Length > 0 ? "/" + key.Password.Trim() : ""), Width = int.Parse(dimensions[0]), Height = int.Parse(dimensions[1]), Fps = (int)fps.SelectedItem, VideoKbps = int.Parse(bitrate.Text), AudioCodec = (string)audioCodec.SelectedItem, VideoEncoder = (string)videoEncoder.SelectedItem });
            key.Clear();
        });
        Button("Stop selected destination", async () => { if (destinations.SelectedItem is StreamStatus selected) await client.StreamStop(selected.Id); });
        Button("Stop all streams", async () => { foreach (var s in await client.StreamStatus()) await client.StreamStop(s.Id); });
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock { Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap, Text = "Up to four independent destinations. Platform account must have live ingest access. SRT listener waits for a receiver. URLs/keys are not saved in profiles or logs. Streams continue if this window closes; use Stop before closing NewsPlay.\n\nInput is the 960×540 software program buffer (larger sizes are scaled), with native 48 kHz stereo PCM. Browser/NDI bridge audio and desktop CG overlays are not embedded in this encoder. Verify the receiver before going live." });
        timer.Tick += async (_, _) => { if (polling) return; polling = true; try { await Refresh(); } catch (Exception ex) { status.Text = ex.Message; } finally { polling = false; } };
        Loaded += (_, _) => timer.Start(); Closed += (_, _) => timer.Stop();
    }
    private async Task Refresh()
    {
        var id = (destinations.SelectedItem as StreamStatus)?.Id; var states = await client.StreamStatus(); destinations.ItemsSource = states; destinations.SelectedItem = states.FirstOrDefault(s => s.Id == id) ?? states.FirstOrDefault();
        status.Text = string.Join("\n", states.Select(s => $"{s.Name} · Port {s.Port} · {s.State} · {s.Frames} frames\n{s.Message}"));
    }
}
