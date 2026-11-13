using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace NewsPlay.Desktop;

// Measures the actual Windows render endpoint, including all apps routed to it.
// It must not be presented as a source-isolated or embedded SDI meter.
public sealed class OutputAudioMeter : StackPanel
{
    private readonly TextBlock status = new() { FontSize = 10, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar left = Bar(), right = Bar();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private MMDeviceEnumerator? devices;
    private MMDevice? device;
    private DateTime nextRefresh;
    public OutputAudioMeter()
    {
        Margin = new Thickness(6);
        Children.Add(new TextBlock { Text = "WINDOWS OUTPUT AUDIO · L/R", FontSize = 10 });
        Children.Add(left); Children.Add(right); Children.Add(status);
        ToolTip = "Actual Windows default output mix, including other apps. Not a per-port or SDI embedded meter. Scale −60 to 0 dBFS.";
        Loaded += (_, _) => timer.Start();
        Unloaded += (_, _) => { timer.Stop(); device?.Dispose(); device = null; devices?.Dispose(); devices = null; };
        timer.Tick += (_, _) => Read();
    }
    private static ProgressBar Bar() => new() { Minimum = 0, Maximum = 60, Height = 7, Margin = new Thickness(0, 2, 0, 0), Background = Brushes.Black, Foreground = Brushes.LimeGreen };
    private static double Db(float value) => value <= .001f ? -60 : Math.Clamp(20 * Math.Log10(value), -60, 0);
    private void Read()
    {
        try
        {
            devices ??= new MMDeviceEnumerator();
            if (DateTime.UtcNow >= nextRefresh || device is null)
            {
                var current = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (current.ID != device?.ID) { device?.Dispose(); device = current; } else current.Dispose();
                nextRefresh = DateTime.UtcNow.AddSeconds(2);
            }
            var peaks = device!.AudioMeterInformation.PeakValues;
            var l = Db(peaks.Count > 0 ? peaks[0] : 0); var r = Db(peaks.Count > 1 ? peaks[1] : peaks.Count > 0 ? peaks[0] : 0);
            Set(left, l); Set(right, r);
            status.Text = $"L {l:0.0} / R {r:0.0} dBFS" + (device.AudioEndpointVolume.Mute ? " · OUTPUT MUTED" : "");
            status.ToolTip = device.FriendlyName;
        }
        catch { left.Value = right.Value = 0; status.Text = "Audio endpoint unavailable"; device?.Dispose(); device = null; }
    }
    private static void Set(ProgressBar bar, double db) { bar.Value = db + 60; bar.Foreground = db > -3 ? Brushes.OrangeRed : db > -12 ? Brushes.Gold : Brushes.LimeGreen; }
}
