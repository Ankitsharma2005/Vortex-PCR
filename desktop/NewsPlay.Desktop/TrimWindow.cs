using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class TrimWindow : Window
{
    public RundownItem Item { get; }
    private readonly EngineClient client = new();
    private readonly FrameReader frame = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly Image image = new() { Stretch = System.Windows.Media.Stretch.Uniform };
    private readonly Slider scrub = new();
    private readonly TextBlock time = new() { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 17 };
    private readonly ComboBox mode = new() { ItemsSource = new[] { "Auto", "Manual", "Floating" } };
    private bool polling;
    private double position;
    private readonly MediaAsset? media;
    public TrimWindow(RundownItem item, MediaAsset? media)
    {
        Style = (Style)Application.Current.Resources[typeof(Window)];
        Item = item; this.media = media; Title = "NewsPlay · Non-destructive trim"; Width = 970; Height = 640; MinWidth = 800; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(20) }; root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Grid { Margin = new Thickness(0, 0, 18, 0) }; left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); left.RowDefinitions.Add(new RowDefinition()); left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(55) });
        left.Children.Add(new TextBlock { Text = "SOURCE MONITOR / TRIM", FontWeight = FontWeights.Bold, FontSize = 13 });
        var preview = new Border { Background = System.Windows.Media.Brushes.Black, Child = image }; Grid.SetRow(preview, 1); left.Children.Add(preview);
        scrub.Minimum = 0; scrub.Maximum = media?.Duration ?? item.Out; scrub.Value = item.In; scrub.PreviewMouseLeftButtonUp += async (_, _) => await Safe(async () => { position = scrub.Value; await client.Preview(new EngineCommand { Action = "Seek", Value = position }); }); Grid.SetRow(scrub, 2); left.Children.Add(scrub);
        var controls = new StackPanel { Orientation = Orientation.Horizontal }; AddButton(controls, "▶ Play", async () => await client.Preview(new EngineCommand { Action = "Play" })); AddButton(controls, "Ⅱ Pause", async () => await client.Preview(new EngineCommand { Action = "Pause" })); AddButton(controls, "−1f", () => Step(-1)); AddButton(controls, "+1f", () => Step(1)); controls.Children.Add(time); Grid.SetRow(controls, 3); left.Children.Add(controls);
        var help = new TextBlock { Text = "I / O: mark IN / OUT  ·  ← / →: step  ·  J / K / L: seek / pause / play\nTrim is metadata only. Decoder seeking is not guaranteed frame accurate.", TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), FontSize = 10 }; Grid.SetRow(help, 4); left.Children.Add(help); root.Children.Add(left);
        var right = new StackPanel(); Grid.SetColumn(right, 1);
        AddField(right, "title", "Story slug", item.Title); var mark = new StackPanel { Orientation = Orientation.Horizontal }; AddButton(mark, "I · Mark IN", () => { fields["in"].Text = position.ToString("0.000", CultureInfo.InvariantCulture); return Task.CompletedTask; }); AddButton(mark, "O · Mark OUT", () => { fields["out"].Text = position.ToString("0.000", CultureInfo.InvariantCulture); return Task.CompletedTask; });
        AddField(right, "in", "Mark IN · seconds", item.In.ToString(CultureInfo.InvariantCulture)); AddField(right, "out", "Mark OUT · seconds", item.Out.ToString(CultureInfo.InvariantCulture)); right.Children.Add(mark);
        AddField(right, "loops", "Loop passes · −1 for infinite", item.Loops.ToString()); AddField(right, "gain", "Audio gain · dB", item.GainDb.ToString(CultureInfo.InvariantCulture));
        mode.SelectedItem = item.StartMode; right.Children.Add(mode); AddField(right, "reporter", "Reporter / anchor", item.Reporter); AddField(right, "notes", "Notes", item.Notes);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; var cancel = new Button { Content = "Cancel", IsCancel = true }; buttons.Children.Add(cancel); var save = new Button { Content = "Save trim", IsDefault = true }; save.Click += (_, _) => Save(); buttons.Children.Add(save); right.Children.Add(buttons);
        root.Children.Add(right); Content = root;
        Loaded += async (_, _) => await Safe(async () => { await client.Attach(); if (media is not null) await client.Preview(new EngineCommand { Action = "Load", MediaId = media.Id, Value = item.In }); position = item.In; timer.Start(); });
        timer.Tick += async (_, _) => { if (polling) return; polling = true; try { if (frame.Read(client.Instance, 3)) image.Source = frame.Image; position = media is not null ? await client.PreviewPosition() : scrub.Value; if (!scrub.IsMouseCaptureWithin) scrub.Value = position; time.Text = "  " + Timecode.Format(position, media?.Fps ?? 25); } catch { } finally { polling = false; } };
        Closed += async (_, _) => { timer.Stop(); frame.Dispose(); try { await client.Preview(new EngineCommand { Action = "Stop" }); } catch { } client.Dispose(); };
        PreviewKeyDown += async (_, e) => { if (e.OriginalSource is TextBox or ComboBox) return; await Safe(async () => { switch (e.Key) { case Key.I: fields["in"].Text = position.ToString("0.000", CultureInfo.InvariantCulture); break; case Key.O: fields["out"].Text = position.ToString("0.000", CultureInfo.InvariantCulture); break; case Key.Left: await Step(-1); break; case Key.Right: await Step(1); break; case Key.J: await Step(-25); break; case Key.K: await client.Preview(new EngineCommand { Action = "Pause" }); break; case Key.L: await client.Preview(new EngineCommand { Action = "Play" }); break; } }); };
    }
    private void AddField(Panel panel, string key, string title, string value) { panel.Children.Add(new TextBlock { Text = title, FontSize = 10, Margin = new Thickness(4, 5, 4, 0), Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush") }); var box = new TextBox { Text = value }; fields[key] = box; panel.Children.Add(box); }
    private void AddButton(Panel panel, string title, Func<Task> action) { var b = new Button { Content = title }; b.Click += async (_, _) => await Safe(action); panel.Children.Add(b); }
    private async Task Safe(Func<Task> action) { try { await action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Trim editor"); } }
    private async Task Step(int frames) { await client.Preview(new EngineCommand { Action = "Pause" }); position = Math.Clamp(position + frames / (media?.Fps ?? 25), 0, media?.Duration ?? Item.Out); await client.Preview(new EngineCommand { Action = "Seek", Value = position }); }
    private void Save()
    {
        try
        {
            var start = double.Parse(fields["in"].Text, CultureInfo.InvariantCulture); var end = double.Parse(fields["out"].Text, CultureInfo.InvariantCulture); var loops = int.Parse(fields["loops"].Text); var gain = double.Parse(fields["gain"].Text, CultureInfo.InvariantCulture);
            PlayoutController.Number(start, 0, media?.Duration ?? 86400); PlayoutController.Number(end, start + .001, media?.Duration ?? 86400); PlayoutController.Number(gain, -60, 12); if (loops != -1 && loops < 1) throw new InvalidOperationException("Passes must be 1 or more, or −1");
            Item.Title = fields["title"].Text; Item.In = start; Item.Out = end; Item.Loops = loops; Item.GainDb = gain; Item.Reporter = fields["reporter"].Text; Item.Notes = fields["notes"].Text; Item.StartMode = mode.SelectedItem as string ?? "Auto"; DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Invalid trim"); }
    }
}
