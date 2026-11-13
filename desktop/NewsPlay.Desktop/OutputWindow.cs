using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class OutputWindow : Window
{
    public int ChannelId { get; }
    private readonly Image picture = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock slate = new() { Text = "NEWSPLAY", FontSize = 45, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border slateBox;
    private readonly TextBlock logo = new() { FontSize = 24, FontWeight = FontWeights.Bold, Padding = new Thickness(15, 7, 15, 7), Background = Brushes.Crimson, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(50, 35, 50, 0) };
    private readonly TextBlock lower = new() { FontSize = 30, Padding = new Thickness(20, 10, 20, 10), Background = new SolidColorBrush(Color.FromArgb(230, 20, 35, 55)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(60, 0, 60, 90) };
    private readonly TextBlock crawl = new() { FontSize = 22, Padding = new Thickness(0, 7, 0, 0) };
    private readonly Border tickerBox;
    private readonly TextBlock clock = new() { FontFamily = new FontFamily("Consolas"), FontSize = 22, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(190, 15, 20, 30)), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(40, 0, 40, 90) };
    private string tickerKey = "";
    private CgSurface? advancedCg;
    public OutputWindow(int channel)
    {
        Style = (Style)Application.Current.Resources[typeof(Window)];
        ChannelId = channel; Title = $"NewsPlay CH{channel} · Software monitor"; Width = 960; Height = 580; Background = Brushes.Black;
        var grid = new Grid { ClipToBounds = true }; grid.Children.Add(picture); slateBox = new Border { Background = new SolidColorBrush(Color.FromRgb(12, 22, 38)), Child = slate }; grid.Children.Add(slateBox); grid.Children.Add(logo); grid.Children.Add(lower); tickerBox = new Border { Height = 42, Background = Brushes.Crimson, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 20), ClipToBounds = true, Child = new Canvas { Children = { crawl } } }; grid.Children.Add(tickerBox); Content = grid;
        grid.Children.Add(clock);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; Width = 960; Height = 580; } if (e.Key == Key.F11) Fullscreen(); }; MouseDoubleClick += (_, _) => Fullscreen();
    }
    private void Fullscreen() { WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
    public void SetFit(string fit) => picture.Stretch = fit switch { "Fill" => Stretch.UniformToFill, "Stretch" => Stretch.Fill, _ => Stretch.Uniform };
    public void SetFrame(ImageSource? image) => picture.Source = image;
    public void Update(EngineState state)
    {
        if (!CgSurface.Enabled && advancedCg is not null) { ((Grid)Content).Children.Remove(advancedCg); advancedCg.Dispose(); advancedCg = null; }
        if (CgSurface.Enabled && advancedCg is null) { advancedCg = new CgSurface(ChannelId); ((Grid)Content).Children.Add(advancedCg); Closed += (_, _) => advancedCg?.Dispose(); }
        var c = state.Channels.Single(c => c.Id == ChannelId); var item = state.Rundowns.Single(p => p.Id == c.RundownId).Items.FirstOrDefault(i => i.Id == c.CurrentId);
        if (c.LiveInputId is { } live) item = PlayoutController.NewItem(state.Media.Single(m => m.Id == live));
        slateBox.Visibility = c.Emergency || c.Status is "Stopped" or "Error" || item is null || item.Kind is "Slate" or "Audio" ? Visibility.Visible : Visibility.Collapsed; slate.Text = c.Emergency ? "PLEASE STAND BY" : item?.Kind is "Slate" or "Audio" ? item.Title : "NEWSPLAY";
        var g = c.Graphics; logo.Text = g.Logo; logo.Visibility = g.LogoOn && !CgSurface.Enabled ? Visibility.Visible : Visibility.Collapsed; lower.Text = g.Headline + "\n" + g.Subtitle; lower.Visibility = g.LowerOn && !CgSurface.Enabled ? Visibility.Visible : Visibility.Collapsed; tickerBox.Visibility = g.TickerOn && !CgSurface.Enabled ? Visibility.Visible : Visibility.Collapsed;
        clock.Text = DateTime.Now.ToString("HH:mm:ss"); clock.Visibility = g.ClockOn && !CgSurface.Enabled ? Visibility.Visible : Visibility.Collapsed;
        var key = g.Ticker + g.TickerSpeed + g.TickerOn + (int)ActualWidth;
        if (key != tickerKey) { tickerKey = key; crawl.Text = g.Ticker; crawl.Measure(new Size(double.PositiveInfinity, 42)); var width = Math.Max(1, ActualWidth); crawl.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(width, -crawl.DesiredSize.Width, TimeSpan.FromSeconds((width + crawl.DesiredSize.Width) / Math.Max(10, g.TickerSpeed))) { RepeatBehavior = RepeatBehavior.Forever }); }
        try { tickerBox.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(g.Color)); } catch { }
    }
}


