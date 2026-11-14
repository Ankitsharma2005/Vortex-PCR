using NewsPlay.Core;

namespace NewsPlay.Engine;

public static class StreamingArguments
{
    public static IReadOnlyList<string> Build(StreamProfile profile, int width, int height, string videoPipe, string audioPipe)
    {
        profile.Validate();
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-thread_queue_size", "8", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{width}x{height}", "-framerate", profile.Fps.ToString(), "-probesize", "32", "-analyzeduration", "0", "-i", videoPipe,
            "-thread_queue_size", "64", "-f", "s16le", "-ar", "48000", "-ac", "2", "-probesize", "32", "-analyzeduration", "0", "-i", audioPipe, "-c:v", profile.VideoEncoder };
        if (profile.VideoEncoder is "libx264" or "libx265") { args.AddRange(["-preset", "veryfast", "-tune", "zerolatency"]); }
        if (profile.EncoderProfile != "auto") args.AddRange(["-profile:v", profile.EncoderProfile]);
        if (profile.EncoderLevel != "auto") args.AddRange(["-level:v", profile.EncoderLevel]);
        if (profile.TlsCaFile.Length > 0 && new Uri(profile.Url).Scheme == "rtmps") args.AddRange(["-ca_file", profile.TlsCaFile]);
        args.AddRange(["-pix_fmt", "yuv420p", "-vf", $"scale={profile.Width}:{profile.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={profile.Width}:{profile.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1",
            "-b:v", profile.VideoKbps + "k", "-maxrate", profile.VideoKbps + "k", "-bufsize", profile.VideoKbps * 2 + "k", "-g", (profile.Fps * profile.KeyframeSeconds).ToString(),
            "-c:a", profile.AudioCodec, "-b:a", profile.AudioKbps + "k", "-ar", profile.AudioSampleRate.ToString(), "-ac", profile.AudioChannels.ToString(),
            "-progress", "pipe:1", "-f", profile.Protocol is "RTMP" or "RTMPS" ? "flv" : "mpegts", profile.Destination()]);
        return args;
    }
}
