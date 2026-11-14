using System.Diagnostics;
using LibVLCSharp.Shared;
using NewsPlay.Core;

namespace NewsPlay.Engine;

public static class HardwareDecodeProbe
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    public static async Task<HardwareDecodeProbeResult> Run(HardwareDecodeProbeRequest request, string dataDirectory, CancellationToken cancellationToken)
    {
        var settings = new HardwareDecodeSettings(request.Mode); settings.Validate();
        if (request.Codec is not ("h264" or "hevc") || (request.Width, request.Height) is not ((1280, 720) or (1920, 1080) or (3840, 2160))) throw new InvalidOperationException("Choose H.264/HEVC and 720p, 1080p or UHD for the decoder test");
        var encoder = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(encoder)) throw new InvalidOperationException("Bundled FFmpeg is needed to create the GPU decoder test fixture");
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Another GPU decoder test is running. Wait for it to finish before retrying.");
        try
        {
            var folder = Path.Combine(dataDirectory, "hardware-decode-probe"); Directory.CreateDirectory(folder);
            var fixture = Path.Combine(folder, $"{request.Codec}-{request.Width}x{request.Height}.mkv");
            if (!File.Exists(fixture))
            {
                var start = new ProcessStartInfo(encoder) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var arg in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=size={request.Width}x{request.Height}:rate=25", "-frames:v", "10", "-an", "-c:v", request.Codec == "h264" ? "libx264" : "libx265", "-preset", "ultrafast", "-threads", "2", "-pix_fmt", "yuv420p", fixture + ".tmp.mkv" }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start decoder fixture encoder");
                var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
                try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
                catch { try { process.Kill(true); await process.WaitForExitAsync(); } catch { } await Task.WhenAll(stderr, stdout); throw; }
                await Task.WhenAll(stderr, stdout);
                if (process.ExitCode != 0) throw new InvalidOperationException("Decoder fixture encoding failed. Check FFmpeg H.264/HEVC support.");
                File.Move(fixture + ".tmp.mkv", fixture, true);
            }
            using var vlc = new LibVLC("--quiet", "--no-video-title-show");
            string? backend = null;
            // This LibVLC instance has exactly one player, so this evidence is per-test.
            vlc.Log += (_, e) => { if (e.Module == "avcodec" && e.Message.StartsWith("Using ", StringComparison.Ordinal) && e.Message.EndsWith(" for hardware decoding", StringComparison.Ordinal)) Interlocked.Exchange(ref backend, e.Message[6..^22]); };
            using var decoder = new VlcDecoder(vlc, "HardwareDecodeProbe" + Guid.NewGuid().ToString("N"), 1);
            decoder.ConfigureHardwareDecoding(settings);
            decoder.ConfigureProject(ProjectVideoFormat.Supported.First(f => f.Width == request.Width && f.Height == request.Height && f.Fps == 25 && !f.Interlaced));
            decoder.SetAudio(0, true);
            decoder.Load(new MediaAsset { Kind = "Clip", Name = "Hardware decoding test", Path = fixture, Width = request.Width, Height = request.Height }, 0, true);
            var timeout = Stopwatch.StartNew();
            while (decoder.Frames < 3 && decoder.Error is null && !decoder.Ended && timeout.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20, cancellationToken);
            var count = decoder.Frames; var observed = Volatile.Read(ref backend); decoder.Stop();
            var success = count >= 2;
            var forcedCpu = settings.VlcBackend(Environment.GetEnvironmentVariable("NEWSPLAY_SOFTWARE_DECODE") == "1") == "none";
            return new(success, request.Mode, request.Codec, request.Width, request.Height, success && observed is not null, observed, count,
                !success ? "Decoder test did not produce enough frames. Check graphics drivers and media support." : observed is not null
                ? "Hardware decoding confirmed by LibVLC and native production frames received. This short test does not certify sustained playback, GPU scaling or 4K60."
                : forcedCpu ? "CPU decoding produced native production frames. GPU decoding was explicitly disabled."
                : "Playback produced native production frames, but GPU decoding was not confirmed. LibVLC CPU fallback remains available for unsupported devices/codecs.");
        }
        finally { gate.Release(); }
    }
}
