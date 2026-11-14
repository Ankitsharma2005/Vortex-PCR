using System.Windows.Threading;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer audioMeterTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool audioMeterPolling;
    private void StartAudioMeters()
    {
        audioMeterTimer.Tick += async (_, _) =>
        {
            if (audioMeterPolling || closing || !connected) return;
            audioMeterPolling = true;
            try
            {
                var levels = await client.AudioLevels();
                if (closing || state is null) return;
                foreach (var channel in state.Channels)
                {
                    var index = channel.Id - 1;
                    if (index < 0 || index >= levels.Length || levels[index].Length < 2) continue;
                    var rundown = state.Rundowns.FirstOrDefault(r => r.Id == channel.RundownId);
                    var item = rundown?.Items.FirstOrDefault(i => i.Id == channel.CurrentId);
                    var asset = state.Media.FirstOrDefault(m => m.Id == item?.MediaId);
                    if (asset?.Live?.Type == "Browser") continue; // Endpoint mix is sampled by the meter.
                    if (asset?.Live?.Type == "NDI")
                    {
                        var ndi = NdiLevels(asset.Id, channel.Id);
                        View(channel.Id).PortMeter.FastLevels(ndi.Left, ndi.Right, "NDI PCM");
                    }
                    else View(channel.Id).PortMeter.FastLevels(levels[index][0], levels[index][1], "OUT PCM");
                }
            }
            catch { /* The normal state refresh remains the fallback during reconnect. */ }
            finally { audioMeterPolling = false; }
        };
        audioMeterTimer.Start();
    }
}
