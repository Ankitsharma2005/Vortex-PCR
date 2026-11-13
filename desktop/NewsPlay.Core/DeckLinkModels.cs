namespace NewsPlay.Core;

public sealed record DeckLinkMode(long Id, string Name, int Width, int Height, long Duration, long Timescale);
public sealed record DeckLinkDeviceInfo(string Id, string Name, bool Capture, bool Playback, List<string> Connections, List<DeckLinkMode> Modes, string Api = "", string Issue = "");
public sealed record DeckLinkScan(string Message, List<DeckLinkDeviceInfo> Devices);
public sealed class DeckLinkRoute
{
    public int Port { get; set; } = 1;
    public string DeviceId { get; set; } = "";
    public string Connection { get; set; } = "SDI";
    public long Mode { get; set; }
    public bool Audio { get; set; } = true;
    public int AudioDelayMs { get; set; }
    public bool LegalRange { get; set; } = true;
}
public sealed record DeckLinkStatus(int Port, string DeviceId, string Message, bool Running, long Frames, long AudioFrames);
