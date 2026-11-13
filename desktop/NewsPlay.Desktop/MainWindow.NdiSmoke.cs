using System.IO;
using System.Runtime.InteropServices;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
    private async Task TestNdiWorkflow()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ndi-smoke")) return;
        NdiNative.Check();
        using var cancel = new CancellationTokenSource();
        var senderName = "NewsPlay test " + Guid.NewGuid().ToString("N")[..8];
        var allocatedName = Marshal.StringToCoTaskMemUTF8(senderName);
        var settings = new NdiNative.SenderSettings { Name = allocatedName };
        var sender = NdiNative.NDIlib_send_create(ref settings);
        Marshal.FreeCoTaskMem(allocatedName);
        if (sender == IntPtr.Zero) throw new InvalidOperationException("NDI test sender did not start");
        var task = Task.Run(async () =>
        {
            var pixels = new byte[160 * 90 * 4]; var samples = new float[1920 * 2];
            for (var i = 0; i < 1920; i++) samples[i] = samples[i + 1920] = (float)Math.Sin(i * Math.PI * 2 * 1000 / 48000) * .01f;
            var videoPin = GCHandle.Alloc(pixels, GCHandleType.Pinned); var audioPin = GCHandle.Alloc(samples, GCHandleType.Pinned);
            try
            {
                var frame = new NdiNative.Video { Width = 160, Height = 90, FourCC = 0x58524742, RateN = 25, RateD = 1, Aspect = 16f / 9, Format = 1, Timecode = long.MaxValue, Data = videoPin.AddrOfPinnedObject(), Stride = 160 * 4 };
                var audio = new NdiNative.Audio { Rate = 48000, Channels = 2, Samples = 1920, Timecode = long.MaxValue, Data = audioPin.AddrOfPinnedObject(), Stride = 1920 * 4 };
                byte phase = 0;
                while (!cancel.IsCancellationRequested)
                {
                    for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = phase; pixels[i + 2] = 30; pixels[i + 3] = 255; }
                    phase += 3;
                    NdiNative.NDIlib_send_send_video_v2(sender, ref frame); NdiNative.NDIlib_send_send_audio_v2(sender, ref audio);
                    await Task.Delay(40, cancel.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally { videoPin.Free(); audioPin.Free(); }
        });
        Guid? id = null;
        try
        {
            var found = await NdiNative.Discover();
            var name = found.FirstOrDefault(n => n.Contains(senderName)) ?? throw new InvalidOperationException("NDI discovery did not find the local sender");
            var asset = new MediaAsset { Name = "NDI integration fixture", Path = "ndi://" + Uri.EscapeDataString(name), Live = new LiveInputSettings { Type = "NDI", SignalTimeoutSeconds = 15 } };
            await Send(new EngineCommand { Action = "Live.Save", Asset = asset }); id = asset.Id;
            BrowserInputs.Ndi.Monitor(asset);
            for (var i = 0; i < 100 && !BrowserInputs.Ndi.HasAudioVideo(asset.Id); i++) await Task.Delay(100);
            if (!BrowserInputs.Ndi.HasAudioVideo(asset.Id)) throw new InvalidOperationException("NDI receiver did not get audio/video: " + BrowserInputs.Ndi.Status(asset.Id));
            var before = Channel(1).DecodedFrames;
            await Send(new EngineCommand { Action = "Live.Take", Channel = 1, MediaId = asset.Id });
            for (var i = 0; i < 70; i++) { await Task.Delay(100); state = await client.GetState(); if (Channel(1).DecodedFrames > before + 3) break; }
            if (Channel(1).DecodedFrames <= before + 3 || Channel(1).Status != "Playing") throw new InvalidOperationException("NDI frames did not reach program");
            for (var i = 0; i < 30 && !BrowserInputs.Ndi.HasAudioRender(asset.Id); i++) await Task.Delay(100);
            if (!BrowserInputs.Ndi.HasAudioRender(asset.Id)) throw new InvalidOperationException("NDI audio render route did not start: " + BrowserInputs.Ndi.Status(asset.Id));
            await Send(new EngineCommand { Action = "Live.Back", Channel = 1 });
            BrowserInputs.Ndi.Unmonitor(asset.Id);
            if (BrowserInputs.Ndi.Status(asset.Id) != "NDI not started") throw new InvalidOperationException("Unused NDI receiver was retained");
            await File.WriteAllTextAsync(smokeFile + ".ndi.result.txt", "PASS: actual NDI runtime sender discovery, received video and stereo PCM audio, native program frame decoding, playlist return. Loopback network test; remote NDI/HX and lip-sync not certified.");
        }
        finally { if (id is { } source) BrowserInputs.Ndi.Stop(source); cancel.Cancel(); await task; NdiNative.NDIlib_send_destroy(sender); }
    }
}

