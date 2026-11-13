using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class BrowserPreview : UserControl, IDisposable
{
    private readonly EngineClient client = new();
    private readonly FrameReader frame = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer renderTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Image picture = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock title = new() { Text = "Select a clip, then Preview", FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock counter = new() { Text = "00:00:00:00", FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = Brushes.LightGreen };
    private readonly Slider seek = new() { Minimum = 0, Maximum = 1 };
    private readonly CheckBox loop = new() { Content = "Loop", FontSize = 10 };
    private readonly CheckBox mute = new() { Content = "Mute preview", IsChecked = true, FontSize = 10 };
    private readonly SemaphoreSlim operations = new(1, 1);
    private MediaAsset? media;
    private bool polling, playing, disposed;
    private double position;
    public bool HasFrame => frame.Image is not null;
    public BrowserPreview()
    {
        Style = (Style)Application.Current.Resources[typeof(UserControl)];
        var root = new Grid();
        foreach (var h in new[] { new GridLength(22), new GridLength(1, GridUnitType.Star), new GridLength(18), new GridLength(30), new GridLength(26) }) root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(title);
        var monitor = new Border { Background = Brushes.Black, Child = picture }; Grid.SetRow(monitor, 1); root.Children.Add(monitor);
        Grid.SetRow(seek, 2); root.Children.Add(seek);
        var buttons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5 }; Grid.SetRow(buttons, 3); root.Children.Add(buttons);
        Add(buttons, "|◀", () => SeekTo(0)); Add(buttons, "−1s", () => SeekTo(Math.Max(0, position - 1))); Add(buttons, "Play", Play); Add(buttons, "Pause", Pause); Add(buttons, "+1s", () => SeekTo(Math.Min(media?.Duration ?? 0, position + 1)));
        var bottom = new DockPanel(); bottom.Children.Add(loop); bottom.Children.Add(mute); counter.HorizontalAlignment = HorizontalAlignment.Right; bottom.Children.Add(counter); Grid.SetRow(bottom, 4); root.Children.Add(bottom);
        Content = root;
        seek.PreviewMouseLeftButtonUp += async (_, _) => await Safe(() => SeekTo(seek.Value));
        mute.Click += async (_, _) => await Safe(async () => { if (media is not null) await client.SourcePreview(new EngineCommand { Action = "Mute", Enabled = mute.IsChecked == true }); });
        timer.Tick += async (_, _) => await Poll();
        renderTimer.Tick += (_, _) => { if (!disposed && media is not null && frame.Read(client.Instance, 4)) picture.Source = frame.Image; };
    }
    private void Add(Panel parent, string text, Func<Task> action) { var button = new Button { Content = text, Padding = new Thickness(2), FontSize = 10 }; button.Click += async (_, _) => await Safe(action); parent.Children.Add(button); }
    private async Task Safe(Func<Task> action) { await operations.WaitAsync(); try { if (!disposed) await action(); } catch (Exception ex) { title.Text = ex.Message; } finally { operations.Release(); } }
    public async Task Load(MediaAsset asset)
    {
        await operations.WaitAsync();
        try
        {
            if (disposed) return;
            await client.Attach(); await client.SourcePreview(new EngineCommand { Action = "Load", MediaId = asset.Id });
            media = asset; playing = false; position = 0; seek.Maximum = Math.Max(.001, asset.Duration); seek.Value = 0; title.Text = asset.Name; picture.Source = null; timer.Start(); renderTimer.Start();
        }
        finally { operations.Release(); }
    }
    private async Task Play()
    {
        if (media is null) return;
        var status = await client.SourceStatus();
        if (status.Ended) await client.SourcePreview(new EngineCommand { Action = "Load", MediaId = media.Id });
        await client.SourcePreview(new EngineCommand { Action = "Play", Enabled = mute.IsChecked == true }); playing = true;
    }
    private async Task Pause() { if (media is null) return; await client.SourcePreview(new EngineCommand { Action = "Pause" }); playing = false; }
    private async Task SeekTo(double seconds)
    {
        if (media is null) return;
        var status = await client.SourceStatus();
        if (status.Ended) { await client.SourcePreview(new EngineCommand { Action = "Load", MediaId = media.Id, Value = seconds }); playing = false; }
        else await client.SourcePreview(new EngineCommand { Action = "Seek", Value = seconds });
        position = seconds;
    }
    private async Task Poll()
    {
        if (polling || disposed || media is null || !await operations.WaitAsync(0)) return;
        polling = true;
        try
        {
            var status = await client.SourceStatus(); if (disposed) return;
            position = status.Position; if (!seek.IsMouseCaptureWithin) seek.Value = position;
            counter.Text = Timecode.Format(position, media.Fps);
            if (status.Error is not null) { title.Text = status.Error; playing = false; }
            else if (status.Ended && playing) { playing = false; if (loop.IsChecked == true) await Play(); }
        }
        catch (Exception ex) { title.Text = "Preview unavailable: " + ex.Message; }
        finally { polling = false; operations.Release(); }
    }
    public Task CloseAsync()
    {
        if (disposed) return Task.CompletedTask; disposed = true; timer.Stop(); renderTimer.Stop(); frame.Dispose();
        return StopPreview();
    }
    public void Dispose() => _ = CloseAsync();
    private async Task StopPreview() { await operations.WaitAsync(); try { if (media is not null) await client.SourcePreview(new EngineCommand { Action = "Stop" }); } catch { } finally { client.Dispose(); operations.Release(); } }
}
