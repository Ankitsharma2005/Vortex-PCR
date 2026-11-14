namespace NewsPlay.Core;

public sealed record DeckLinkMode(long Id, string Name, int Width, int Height, long Duration, long Timescale, bool Interlaced = false, bool LowerFieldFirst = false)
{
    public string Description => $"{Name} · {Width}×{Height} · {(Duration > 0 ? Timescale / (double)Duration : 0):0.###} fps · " + (Interlaced ? (LowerFieldFirst ? "interlaced, lower field first" : "interlaced, upper field first") : "progressive");
}
public sealed record DeckLinkDeviceInfo(string Id, string Name, bool Capture, bool Playback, List<string> Connections, List<DeckLinkMode> Modes, string Api = "", string Issue = "");
public sealed record DeckLinkScan(string Message, List<DeckLinkDeviceInfo> Devices);
public sealed class DeckLinkRoute
{
    public int Port { get; set; } = 1;
    public string DeviceId { get; set; } = "";
    public string Connection { get; set; } = "SDI";
    public long Mode { get; set; }
    public bool Audio { get; set; } = true;
    public int AudioChannels { get; set; } = 2;
    public int AudioLeftChannel { get; set; } = 1;
    public int AudioRightChannel { get; set; } = 2;
    public int AudioDelayMs { get; set; }
    public bool LegalRange { get; set; } = true;
    public void ValidateAudio()
    {
        if (AudioChannels is not (2 or 8 or 16) || AudioLeftChannel < 1 || AudioRightChannel < 1 || AudioLeftChannel > AudioChannels || AudioRightChannel > AudioChannels || AudioLeftChannel == AudioRightChannel)
            throw new InvalidOperationException("Choose distinct left/right channels within a 2, 8 or 16 channel embedded output");
    }
    public TimestampedPcm RouteAudio(TimestampedPcm packet)
    {
        ValidateAudio();
        if (packet.Bytes.Length % 4 != 0) throw new InvalidOperationException("Embedded input must be stereo 16-bit PCM");
        if (AudioChannels == 2 && AudioLeftChannel == 1 && AudioRightChannel == 2) return packet;
        var stride = AudioChannels * 2; var mapped = new byte[packet.Bytes.Length / 4 * stride];
        for (int i = 0; i < packet.Bytes.Length / 4; i++) { Buffer.BlockCopy(packet.Bytes, i * 4, mapped, i * stride + (AudioLeftChannel - 1) * 2, 2); Buffer.BlockCopy(packet.Bytes, i * 4 + 2, mapped, i * stride + (AudioRightChannel - 1) * 2, 2); }
        return new(mapped, packet.PresentationMicroseconds);
    }
}
public sealed record DeckLinkStatus(int Port, string DeviceId, string Message, bool Running, long Frames, long AudioFrames);
