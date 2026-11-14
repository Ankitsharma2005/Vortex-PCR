using System.Text.Json;
using NewsPlay.Core;
using NewsPlay.Engine;

internal static class GpuDecodingChecks
{
    public static async Task Run(string folder)
    {
        LibVLCSharp.Shared.Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg"); Directory.CreateDirectory(ffmpeg);
        File.Copy(Path.GetFullPath("tools/ffmpeg/ffmpeg.exe"), Path.Combine(ffmpeg, "ffmpeg.exe"), true);
        var requests = new[]
        {
            new HardwareDecodeProbeRequest("Auto", "h264", 1920, 1080),
            new HardwareDecodeProbeRequest("D3D11VA", "h264", 1280, 720),
            new HardwareDecodeProbeRequest("D3D11VA", "hevc", 1280, 720),
            new HardwareDecodeProbeRequest("D3D11VA", "h264", 3840, 2160),
            new HardwareDecodeProbeRequest("D3D11VA", "hevc", 3840, 2160),
            new HardwareDecodeProbeRequest("DXVA2", "h264", 1920, 1080),
            new HardwareDecodeProbeRequest("CPU", "h264", 1920, 1080)
        };
        var results = new List<HardwareDecodeProbeResult>();
        foreach (var request in requests)
        {
            var result = await HardwareDecodeProbe.Run(request, folder, CancellationToken.None); results.Add(result);
            if (!result.Success || (request.Mode == "CPU" && result.HardwareUsed)) throw new Exception("GPU/CPU decode regression: " + JsonSerializer.Serialize(result));
            Console.WriteLine($"PASS {request.Mode} {request.Codec} {request.Width}x{request.Height}: frames={result.DecodedFrames}, hardware={result.HardwareUsed}, backend={result.Backend ?? "software fallback / no GPU confirmation"}");
        }
        var previous = Environment.GetEnvironmentVariable("NEWSPLAY_SOFTWARE_DECODE");
        try
        {
            Environment.SetEnvironmentVariable("NEWSPLAY_SOFTWARE_DECODE", "1");
            var forced = await HardwareDecodeProbe.Run(new("D3D11VA", "h264", 1280, 720), folder, CancellationToken.None); results.Add(forced);
            if (!forced.Success || forced.HardwareUsed) throw new Exception("Software override did not force CPU");
            Console.WriteLine("PASS software environment override produces frames without activating GPU decoding");
        }
        finally { Environment.SetEnvironmentVariable("NEWSPLAY_SOFTWARE_DECODE", previous); }
        File.WriteAllText(Path.Combine(folder, "gpu-decoding-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
    }
}
