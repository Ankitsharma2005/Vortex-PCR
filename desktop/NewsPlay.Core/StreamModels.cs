namespace NewsPlay.Core;

public sealed class StreamProfile
{
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
    public string VideoEncoder { get; set; } = "libx264";
    public string AudioCodec { get; set; } = "aac";
    public void Validate()
    {
        if (VideoEncoder is not ("libx264" or "h264_nvenc" or "h264_qsv" or "h264_amf")) throw new InvalidOperationException("Unsupported video encoder");
        if (AudioCodec is not ("aac" or "ac3")) throw new InvalidOperationException("Choose AAC or AC3 audio");
        if (Protocol == "RTMP" && AudioCodec != "aac") throw new InvalidOperationException("RTMP requires AAC audio. Use SRT or UDP for AC3.");
        if (Port is not (1 or 2) || string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("Choose a name and program port");
        if ((Width, Height) is not ((640, 360) or (960, 540) or (1280, 720) or (1920, 1080)) || Fps is not (25 or 30 or 50 or 60)) throw new InvalidOperationException("Unsupported encoder format");
        if (VideoKbps is < 200 or > 50000 || AudioKbps is < 64 or > 320) throw new InvalidOperationException("Invalid encoder bitrate");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || Url.Any(char.IsControl)) throw new InvalidOperationException("Enter a complete destination URL");
        if (Protocol == "RTMP" && uri.Scheme is "rtmp" or "rtmps") return;
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
public sealed record StreamStatus(Guid Id, string Name, int Port, string State, long Frames, string Message);
