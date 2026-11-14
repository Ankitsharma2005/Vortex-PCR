namespace NewsPlay.Core;

public sealed class StreamProfile
{
    public static string IngestUrl(string protocol, string server, string key)
    {
        var address = server.Trim(); var secret = key.Trim();
        if (protocol is not ("RTMP" or "RTMPS") || secret.Length == 0) return address;
        if (secret.Any(char.IsControl)) throw new InvalidOperationException("Stream key contains invalid characters");
        var suffix = address.IndexOfAny(['?', '#']);
        return suffix < 0 ? address.TrimEnd('/') + "/" + secret : address[..suffix].TrimEnd('/') + "/" + secret + address[suffix..];
    }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Stream";
    public int Port { get; set; } = 1;
    public string Protocol { get; set; } = "RTMP";
    public string Url { get; set; } = "";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 25;
    public int VideoKbps { get; set; } = 4000;
    public int AudioKbps { get; set; } = 128;
    public int AudioSampleRate { get; set; } = 48000;
    public int AudioChannels { get; set; } = 2;
    public int KeyframeSeconds { get; set; } = 2;
    public string VideoCodec { get; set; } = "h264";
    public string EncoderProfile { get; set; } = "auto";
    public string EncoderLevel { get; set; } = "auto";
    public string TlsCaFile { get; set; } = "";
    public string VideoEncoder { get; set; } = "libx264";
    public string AudioCodec { get; set; } = "aac";
    public void Validate()
    {
        var supportedEncoders = VideoCodec switch { "h264" => new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf" }, "hevc" => new[] { "libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf" }, _ => [] };
        if (!supportedEncoders.Contains(VideoEncoder)) throw new InvalidOperationException("Unsupported video codec/encoder combination");
        if (EncoderProfile != "auto" && !(VideoCodec == "h264" ? new[] { "baseline", "main", "high" } : new[] { "main" }).Contains(EncoderProfile)) throw new InvalidOperationException("Unsupported encoder profile");
        if (!new[] { "auto", "3.1", "4.0", "4.1", "4.2", "5.0", "5.1", "5.2", "6.0", "6.1", "6.2" }.Contains(EncoderLevel)) throw new InvalidOperationException("Unsupported encoder level");
        if (EncoderLevel != "auto")
        {
            var limits = VideoCodec == "h264" ? EncoderLevel switch
            {
                "3.1" => (3600L, 108000L), "4.0" or "4.1" => (8192L, 245760L), "4.2" => (8704L, 522240L),
                "5.0" => (22080L, 589824L), "5.1" => (36864L, 983040L), "5.2" => (36864L, 2073600L), _ => (139264L, 16711680L)
            } : EncoderLevel switch
            {
                "3.1" => (983040L, 33177600L), "4.0" => (2228224L, 66846720L), "4.1" => (2228224L, 133693440L), "4.2" => (0L, 0L),
                "5.0" => (8912896L, 267386880L), "5.1" => (8912896L, 534773760L), "5.2" => (8912896L, 1069547520L), _ => (35651584L, 4278190080L)
            };
            var units = VideoCodec == "h264" ? ((Width + 15L) / 16) * ((Height + 15L) / 16) : Width * (long)Height;
            if (units > limits.Item1 || units * Fps > limits.Item2) throw new InvalidOperationException("Encoder level is too low for this resolution/FPS. Use auto or a compatible higher level.");
        }
        if (Protocol is "RTMP" or "RTMPS" && VideoCodec != "h264") throw new InvalidOperationException("This RTMP adapter supports H.264/AAC. Use SRT or UDP for HEVC.");
        if (KeyframeSeconds is < 1 or > 10 || AudioSampleRate is not (32000 or 44100 or 48000) || AudioChannels is not (1 or 2)) throw new InvalidOperationException("Unsupported keyframe interval or streaming audio format");
        if (AudioCodec is not ("aac" or "ac3")) throw new InvalidOperationException("Choose AAC or AC3 audio");
        if (Protocol is "RTMP" or "RTMPS" && AudioCodec != "aac") throw new InvalidOperationException("RTMP requires AAC audio. Use SRT or UDP for AC3.");
        if (Port is not (1 or 2) || string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("Choose a name and program port");
        if ((Width, Height) is not ((640, 360) or (720, 576) or (960, 540) or (1280, 720) or (1920, 1080) or (3840, 2160)) || Fps is not (25 or 30 or 50 or 60)) throw new InvalidOperationException("Unsupported encoder format");
        if (VideoKbps is < 200 or > 50000 || AudioKbps is < 64 or > 320) throw new InvalidOperationException("Invalid encoder bitrate");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || Url.Any(char.IsControl)) throw new InvalidOperationException("Enter a complete destination URL");
        if (Protocol == "RTMP" && uri.Scheme is "rtmp" or "rtmps" || Protocol == "RTMPS" && uri.Scheme == "rtmps") return;
        if (Protocol == "UDP" && uri.Scheme == "udp" && uri.Port > 0) return;
        if (Protocol is "SRT caller" or "SRT listener" && uri.Scheme == "srt" && uri.Port > 0) return;
        throw new InvalidOperationException("Destination URL does not match protocol");
    }
    public string Destination()
    {
        Validate();
        if (!Protocol.StartsWith("SRT")) return Url;
        var builder = new UriBuilder(Url);
        var query = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.Split('=')[0].Equals("mode", StringComparison.OrdinalIgnoreCase));
        builder.Query = string.Join("&", query.Append("mode=" + (Protocol == "SRT listener" ? "listener" : "caller")));
        return builder.Uri.AbsoluteUri;
    }
}
public sealed record StreamStatus(Guid Id, string Name, int Port, string State, long Frames, string Message, long DroppedFrames = 0, string Format = "");
