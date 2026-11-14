using System.Diagnostics;
using NewsPlay.Core;

namespace NewsPlay.Engine;

public sealed record VideoFormatCompatibility(string Format, bool Supported, string Message, double CpuConversionMs, double FrameBudgetMs, long AvailableMemoryBytes, bool HardwareVerified = false);

public static class VideoFormatProbe
{
    public static VideoFormatCompatibility Check(ProjectVideoFormat format)
    {
        var memory = VideoResourceMonitor.AvailableMemory;
        var required = format.FrameBytes * 32L + 256L * 1024 * 1024;
        var budget = 1000d / format.FieldRate;
        if (!Environment.Is64BitProcess || memory < required)
            return new(format.Id, false, "Insufficient available memory for two production ports. A 64-bit process is required.", 0, budget, memory);
        var source = new byte[format.FrameBytes]; var copy = new byte[format.FrameBytes]; var preview = new byte[960 * 540 * 4];
        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var clock = Stopwatch.StartNew();
            // Budget both ports, decoder publication, production copy and preview conversion.
            for (int port = 0; port < 2; port++)
            {
                for (int pass = 0; pass < 4; pass++) Buffer.BlockCopy(source, 0, copy, 0, source.Length);
                VideoRaster.Scale(copy, format.Width, format.Height, preview, 960, 540);
                if (format.Interlaced || format.Fps >= 50) { VideoRaster.CopyField(source, copy, format.Width, format.Height, 0); VideoRaster.CopyField(source, copy, format.Width, format.Height, 1); }
            }
            if (i > 0) times.Add(clock.Elapsed.TotalMilliseconds);
        }
        var measured = times.Max(); var supported = measured < budget * .8;
        return new(format.Id, supported, supported
            ? "CPU raster/preview conversion passed the local admission check. Decode, effects and simultaneous outputs still require a real-time soak test. GPU decode is requested when available; production raster conversion uses the CPU. DeckLink/encoder support is validated separately."
            : "CPU conversion exceeded the frame budget. Choose a lower resolution or frame rate on this PC.", measured, budget, memory);
    }
}
