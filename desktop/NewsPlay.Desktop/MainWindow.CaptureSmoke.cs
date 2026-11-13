using System.IO;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
    private async Task TestCaptureWorkflow()
    {
        if (!Environment.GetCommandLineArgs().Contains("--capture-smoke")) return;
        var camera = CaptureDevices.Video().FirstOrDefault(n => n.Contains("Camera", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("No camera detected for capture test");
        var microphone = CaptureDevices.Audio().FirstOrDefault(n => n.Contains("Microphone", StringComparison.OrdinalIgnoreCase) && !n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) ?? "none";
        var asset = new MediaAsset { Name = "Camera integration fixture", Path = "dshow://", Live = new LiveInputSettings { Type = "USB / Webcam", VideoDevice = camera, AudioDevice = microphone, SignalTimeoutSeconds = 15 } };
        await Send(new EngineCommand { Action = "Live.Save", Asset = asset });
        try
        {
            await client.LivePreview(new EngineCommand { Action = "Start", MediaId = asset.Id });
            LivePreviewStatus? status = null;
            for (var i = 0; i < 120; i++) { await Task.Delay(100); status = (await client.LiveStatus()).FirstOrDefault(s => s.Id == asset.Id); if (status?.Signal == "Signal OK") break; }
            if (status?.Signal != "Signal OK") throw new InvalidOperationException("Camera preview has no decoded signal");
            await client.LivePreview(new EngineCommand { Action = "Stop", MediaId = asset.Id });
            await Send(new EngineCommand { Action = "Mute", Channel = 1, Enabled = true });
            var before = Channel(1).DecodedFrames;
            await Send(new EngineCommand { Action = "Live.Take", Channel = 1, MediaId = asset.Id });
            for (var i = 0; i < 100; i++) { await Task.Delay(100); state = await client.GetState(); if (Channel(1).DecodedFrames > before + 3) break; }
            if (Channel(1).DecodedFrames <= before + 3) throw new InvalidOperationException("Camera frames did not reach program");
            await Send(new EngineCommand { Action = "Live.Back", Channel = 1 });
            await File.WriteAllTextAsync(smokeFile + ".capture.result.txt", $"PASS: detected camera, real DirectShow preview {status.Width}×{status.Height}, program Take and Back. Audio track id {status.AudioTrack}; microphone selected: {microphone != "none"}. No camera images recorded.");
        }
        finally { await client.LivePreview(new EngineCommand { Action = "Stop", MediaId = asset.Id }); await Send(new EngineCommand { Action = "Mute", Channel = 1, Enabled = false }); }
    }
}
