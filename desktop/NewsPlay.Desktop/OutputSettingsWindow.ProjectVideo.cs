using System.Windows;
using System.Windows.Controls;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed partial class OutputSettingsWindow
{
    private void AddProjectVideoPanel(TabControl tabs)
    {
        var panel = Panel(tabs, "Project Video Format");
        Label(panel, "Master production standard", true);
        Label(panel, "This sets the native production raster and cadence for both programs. DeckLink and streaming keep their own output formats. Stop both programs and all hardware/network outputs before applying.");
        var choice = new ComboBox { ItemsSource = ProjectVideoFormat.Supported, SelectedIndex = 4 };
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
        var report = new TextBlock { Text = "Check this PC before applying a format.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
        panel.Children.Add(choice); panel.Children.Add(description);
        var apply = new Button { Content = "Apply project video format", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left }; SettingsUi.Action(apply, "Apply");
        choice.SelectionChanged += (_, _) => { apply.IsEnabled = false; description.Text = (choice.SelectedItem as ProjectVideoFormat)?.Description ?? ""; };
        description.Text = ((ProjectVideoFormat)choice.SelectedItem).Description;
        Button(panel, "Check format compatibility", async () =>
        {
            var selected = (ProjectVideoFormat)choice.SelectedItem;
            var check = await client.CheckProjectVideo(selected.Id);
            report.Text = $"CPU conversion {check.GetProperty("cpuConversionMs").GetDouble():0.00} ms · budget {check.GetProperty("frameBudgetMs").GetDouble():0.00} ms\n" + check.GetProperty("message").GetString();
            apply.IsEnabled = Equals(choice.SelectedItem, selected) && check.GetProperty("supported").GetBoolean();
        });
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try { await client.ApplyProjectVideo(((ProjectVideoFormat)choice.SelectedItem).Id); feedback.Text = "Project production format applied and saved. Independent DeckLink/streaming formats are unchanged."; }
            catch (Exception ex) { feedback.Text = ex.Message; }
        };
        panel.Children.Add(apply); panel.Children.Add(report);
        Label(panel, "Interlaced production samples separate fields and preserves field order. Progressive outputs use the progressive field-time image. Low-resolution sources are scaled; they are not native 4K sources. Operator previews remain 960 × 540.");
        Loaded += async (_, _) =>
        {
            try
            {
                var state = await client.ProjectVideoStatus();
                if (state.TryGetProperty("selected", out var selected) && selected.ValueKind == System.Text.Json.JsonValueKind.String)
                    choice.SelectedItem = ProjectVideoFormat.Find(selected.GetString()!);
                report.Text = state.TryGetProperty("active", out var active) && active.ValueKind == System.Text.Json.JsonValueKind.String
                    ? "Active production format: " + active.GetString() : "Legacy project: preview-based processing is preserved until a project format is applied.";
                if (state.TryGetProperty("restoreError", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.String) report.Text += "\n" + error.GetString();
            }
            catch (Exception ex) { report.Text = ex.Message; }
        };
    }
    private void AddHardwareCompatibilityPanel(TabControl tabs)
    {
        var panel = Panel(tabs, "Hardware Compatibility");
        Label(panel, "GPU video decoding", true);
        var decodeMode = new ComboBox { ItemsSource = HardwareDecodeSettings.Modes, SelectedIndex = 0 };
        var testCodec = new ComboBox { ItemsSource = new[] { "h264", "hevc" }, SelectedIndex = 0 };
        var testSize = new ComboBox { ItemsSource = new[] { "1280x720", "1920x1080", "3840x2160" }, SelectedIndex = 1 };
        var decodeStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6), Text = "Auto prefers an available hardware decoder, with LibVLC CPU fallback. Settings take effect on the next media load." };
        panel.Children.Add(decodeMode);
        Button(panel, "Apply decoder preference", async () => { await client.ConfigureHardwareDecoding(new((string)decodeMode.SelectedItem)); decodeStatus.Text = "Decoder preference saved. Current playback continues unchanged; reload/cue the next media to use this preference."; });
        Label(panel, "Test codec and native source size"); panel.Children.Add(testCodec); panel.Children.Add(testSize);
        Button(panel, "Test selected decoder", async () =>
        {
            var dimensions = ((string)testSize.SelectedItem).Split('x');
            decodeStatus.Text = "Testing selected decoder on a local fixture…";
            var result = await client.TestHardwareDecoding(new((string)decodeMode.SelectedItem, (string)testCodec.SelectedItem, int.Parse(dimensions[0]), int.Parse(dimensions[1])));
            decodeStatus.Text = $"{result.Mode} · {result.Codec} · {result.Width}×{result.Height} · {result.DecodedFrames} frames\n" + (result.HardwareUsed ? "GPU confirmed: " + result.Backend : "GPU not active/confirmed for this test") + "\n" + result.Message;
        });
        Button(panel, "Refresh decoder observations", async () =>
        {
            var result = await client.HardwareDecodingStatus(); var observed = result.GetProperty("observations");
            decodeStatus.Text = "Saved preference: " + result.GetProperty("settings").GetProperty("mode").GetString() + "\nLast engine-wide hardware observation: " + observed.GetProperty("lastBackend").ToString() + "\n" + observed.GetProperty("scope").GetString() + "\n" + result.GetProperty("note").GetString();
            foreach (var port in result.GetProperty("ports").EnumerateArray()) if (port.GetProperty("softwareOverride").GetBoolean()) { decodeStatus.Text += "\nNEWSPLAY_SOFTWARE_DECODE=1 is forcing CPU decoding."; break; }
        });
        panel.Children.Add(decodeStatus);
        Loaded += async (_, _) => { try { var result = await client.HardwareDecodingStatus(); decodeMode.SelectedItem = result.GetProperty("settings").GetProperty("mode").GetString(); } catch { } };
        Label(panel, "Production resources and compatibility", true);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) }; panel.Children.Add(status);
        async Task Refresh()
        {
            var report = await client.ProjectVideoStatus();
            var hardware = await client.DeckLinkDevices();
            status.Text = $"CPU logical processors: {report.GetProperty("cpuLogicalProcessors").GetInt32()}\nProcess memory: {report.GetProperty("workingMemoryBytes").GetInt64() / 1048576d:0} MB\n" +
                report.GetProperty("gpuScaling").GetString() + "\n\n" + hardware.Message + "\n\n";
            if (report.TryGetProperty("resources", out var resources)) status.Text += $"CPU usage: {resources.GetProperty("cpuPercent").GetDouble():0.0}% · available RAM {resources.GetProperty("availableMemoryBytes").GetInt64() / 1073741824d:0.0} GB\n\n";
            foreach (var port in report.GetProperty("ports").EnumerateArray())
                status.Text += $"Source {port.GetProperty("sourceWidth").GetInt32()} × {port.GetProperty("sourceHeight").GetInt32()} · production {port.GetProperty("frames").GetInt64()} frames · missed deadlines {port.GetProperty("missedDeadlines").GetInt64()} · repeated frames {port.GetProperty("repeatedFrames").GetInt64()} · CPU raster {port.GetProperty("processingMs").GetDouble():0.00} ms\n";
            status.Text += "\nGPU encoding is checked against the selected encoder when starting a stream. GPU utilization telemetry is not available in this adapter. 4K60 real-time operation and DeckLink picture/audio require a test on the destination hardware.";
        }
        Button(panel, "Refresh hardware and processing status", Refresh);
    }
}
