using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
namespace NewsPlay.Desktop;

public sealed class StereoMeter : FrameworkElement
{
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private double targetL, targetR, shownL, shownR, peakL, peakR;
    private DateTime holdL, holdR;
    private string caption = "PCM";
    private bool endpointMix;
    public long LastFastUpdate { get; private set; }
    public void FastLevels(double left, double right, string label = "PCM") { LastFastUpdate = Environment.TickCount64; Levels(left, right, label); }
    private readonly System.Diagnostics.Stopwatch animationClock = System.Diagnostics.Stopwatch.StartNew();
    private long lastTick;
    private MMDeviceEnumerator? devices;
    private MMDevice? device;
    public StereoMeter()
    {
        MinWidth = 58;
        Loaded += (_, _) => timer.Start(); Unloaded += (_, _) => { timer.Stop(); device?.Dispose(); device = null; devices?.Dispose(); devices = null; };
        timer.Tick += (_, _) => { if (endpointMix) ReadEndpoint(); var now = animationClock.ElapsedMilliseconds; var dt = Math.Clamp((now - lastTick) / 1000d, 0, .1); lastTick = now; shownL += (targetL - shownL) * (1 - Math.Exp(-dt / (targetL > shownL ? .012 : .18))); shownR += (targetR - shownR) * (1 - Math.Exp(-dt / (targetR > shownR ? .012 : .18))); InvalidateVisual(); };
        ToolTip = "Stereo audio · −60 to 0 dBFS · peak hold · red indicates near clipping";
    }
    public void EndpointMix(bool enabled) { endpointMix = enabled; ToolTip = enabled ? "Windows default output MIX, including other apps. Browser/NDI direct audio is not isolated by program port." : "This port's decoded PCM audio, after gain/mute · −60 to 0 dBFS"; }
    private void ReadEndpoint()
    {
        try { devices ??= new MMDeviceEnumerator(); device ??= devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); var peaks = device.AudioMeterInformation.PeakValues; Levels(peaks.Count > 0 ? peaks[0] : 0, peaks.Count > 1 ? peaks[1] : 0, "WIN MIX"); }
        catch { device?.Dispose(); device = null; Levels(0, 0, "NO OUT"); }
    }
    public void Levels(double left, double right, string label = "PCM")
    {
        targetL = Db(left); targetR = Db(right); caption = label;
        if (targetL >= peakL || DateTime.UtcNow > holdL) { peakL = targetL; holdL = DateTime.UtcNow.AddSeconds(1.2); }
        if (targetR >= peakR || DateTime.UtcNow > holdR) { peakR = targetR; holdR = DateTime.UtcNow.AddSeconds(1.2); }
    }
    private static double Db(double level) => double.IsFinite(level) && level > .001 ? Math.Clamp((20 * Math.Log10(level) + 60) / 60, 0, 1) : 0;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var h = Math.Max(40, ActualHeight - 44); var x = Math.Max(0, (ActualWidth - 38) / 2);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(15, 20, 28)), null, new Rect(0, 0, ActualWidth, ActualHeight), 6, 6);
        Text(dc, "L     R", x, 5, Brushes.LightSteelBlue, 9);
        for (var ch = 0; ch < 2; ch++)
        {
            var value = ch == 0 ? shownL : shownR;
            for (var i = 0; i < 30; i++)
            {
                var lit = value * 30 > i; var color = i >= 28 ? Color.FromRgb(255, 76, 91) : i >= 23 ? Color.FromRgb(255, 205, 76) : Color.FromRgb(64, 224, 163);
                dc.DrawRoundedRectangle(new SolidColorBrush(lit ? color : Color.FromRgb(32, 43, 50)), null, new Rect(x + ch * 20, 22 + h - (i + 1) * h / 30, 15, Math.Max(1, h / 30 - 2)), 1, 1);
            }
            var peak = ch == 0 ? peakL : peakR;
            if (peak > .01) dc.DrawRectangle(Brushes.White, null, new Rect(x + ch * 20, 22 + h * (1 - peak), 15, 1.5));
        }
        Text(dc, caption, 5, ActualHeight - 17, Brushes.SlateGray, 8);
    }
    private void Text(DrawingContext dc, string text, double x, double y, Brush brush, double size) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
}
