using System.Diagnostics;
using NewsPlay.Core;

namespace NewsPlay.Engine;

internal static class HardwareEncoderProbe
{
    public static void Check(string executable, StreamProfile profile)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        string[] args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
            $"color=black:size={profile.Width}x{profile.Height}:rate={profile.Fps}",
            "-frames:v", "2", "-an", "-c:v", profile.VideoEncoder, "-pix_fmt", "yuv420p", "-f", "null", "-"];
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (profile.EncoderProfile != "auto") { start.ArgumentList.Insert(start.ArgumentList.Count - 3, "-profile:v"); start.ArgumentList.Insert(start.ArgumentList.Count - 3, profile.EncoderProfile); }
        if (profile.EncoderLevel != "auto") { start.ArgumentList.Insert(start.ArgumentList.Count - 3, "-level:v"); start.ArgumentList.Insert(start.ArgumentList.Count - 3, profile.EncoderLevel); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start encoder check");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(true); process.WaitForExit();
            Task.WhenAll(stderr, stdout).GetAwaiter().GetResult();
            throw new InvalidOperationException("GPU encoder check timed out. Check your graphics driver or select libx264 (CPU).");
        }
        Task.WhenAll(stderr, stdout).GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{profile.VideoEncoder} cannot encode this format on this PC. Check GPU/driver and FFmpeg support, or select libx264 (CPU).");
    }
}
