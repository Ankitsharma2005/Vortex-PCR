using System.Text.Json;

namespace NewsPlay.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public static T Copy<T>(T source) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source, Options), Options)!;
}
public sealed class MediaAsset
{
    public ColourCorrection Colour { get; set; } = new();
    public long LiveReleaseRevision { get; set; }
    public bool RemovedFromBrowser { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Clip";
    public double Duration { get; set; }
    public double Fps { get; set; } = 25;
    public string Codec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int AudioTracks { get; set; }
    public string Category { get; set; } = "Package";
    public string Tags { get; set; } = "";
    public string Warning { get; set; } = "";
    public LiveInputSettings? Live { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;
    public string DurationText => Timecode.Format(Duration, Fps);
    public string Details => $"{Codec}  ·  {Width}×{Height}  ·  {Fps:0.##} fps  ·  {AudioTracks} audio";
}
public sealed class RundownItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? MediaId { get; set; }
    public string Kind { get; set; } = "Clip";
    public string Title { get; set; } = "Standby";
    public string Reporter { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Color { get; set; } = "#527CE5";
    public double In { get; set; }
    public double Out { get; set; } = 10;
    public int Loops { get; set; } = 1;
    public string StartMode { get; set; } = "Auto";
    public double GainDb { get; set; }
    public double Duration => Out - In;
    public string DurationText => Timecode.Format(Duration);
    public string InText => Timecode.Format(In);
    public string LoopText => Loops == -1 ? "∞" : Loops.ToString();
    public string Status { get; set; } = "READY";
}
public sealed class Rundown
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Channel { get; set; }
    public string Name { get; set; } = "Untitled rundown";
    public bool Loop { get; set; }
    public bool Locked { get; set; }
    public List<RundownItem> Items { get; set; } = [];
    public string TotalText => Timecode.Format(Items.Sum(i => i.Duration * Math.Max(1, i.Loops))) + (Loop || Items.Any(i => i.Loops < 0) ? " + ∞" : "");
    public override string ToString() => Name;
}
public sealed class GraphicsState
{
    public string Logo { get; set; } = "NEWSPLAY";
    public bool LogoOn { get; set; } = true;
    public bool ClockOn { get; set; } = true;
    public bool LowerOn { get; set; }
    public string Headline { get; set; } = "Your headline here";
    public string Subtitle { get; set; } = "LIVE • NEWS DESK";
    public bool TickerOn { get; set; }
    public string Ticker { get; set; } = "Welcome to NewsPlay";
    public int TickerSpeed { get; set; } = 70;
    public string Color { get; set; } = "#C92D42";
}
public sealed class ChannelState
{
    public int Id { get; set; }
    public string Name { get; set; } = "PROGRAM";
    public Guid RundownId { get; set; }
    public Guid? CurrentId { get; set; }
    public Guid? QueuedId { get; set; }
    public bool AutoAdvance { get; set; } = true;
    public string Status { get; set; } = "Stopped";
    public double Position { get; set; }
    public int Pass { get; set; } = 1;
    public bool BreakLoop { get; set; }
    public bool LoopCurrent { get; set; }
    public bool LoopPlayback { get; set; }
    public Guid? LiveInputId { get; set; }
    public ResumePoint? PlaylistReturn { get; set; }
    public bool Mute { get; set; }
    public double GainDb { get; set; }
    public double Rate { get; set; } = 1;
    public string Format { get; set; } = "1080p25";
    public string EndMode { get; set; } = "Auto";
    public string AudioDeviceId { get; set; } = "";
    public AudioLevelSettings AutoLevel { get; set; } = new();
    public int AudioSampleRate { get; set; } = 48000;
    public int AudioBitDepth { get; set; } = 16;
    public double AudioLeft { get; set; }
    public double AudioRight { get; set; }
    public bool Emergency { get; set; }
    public ResumePoint? Interrupted { get; set; }
    public GraphicsState Graphics { get; set; } = new();
    public Guid? RunId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public double PlayedSeconds { get; set; }
    public long DecodedFrames { get; set; }
    public double Fps => Timecode.Rate(Format);
}
public sealed record ResumePoint(Guid? ItemId, double Position, string Status, int Pass);
public sealed record AudioDeviceOption(string Id, string Name);
public sealed record PreflightIssue(Guid ItemId, string Title, string Message);
public sealed record PreflightReport(Guid RundownId, DateTimeOffset CheckedAt, int CheckedItems, List<PreflightIssue> Issues);
public sealed record Alarm(Guid Id, DateTimeOffset At, int? Channel, string Message, bool Acknowledged = false);
public sealed record AsRun(Guid Id, int Channel, Guid ItemId, string Title, DateTimeOffset StartedAt, DateTimeOffset EndedAt, double PlayedSeconds, double EndPosition, string Result, string Output = "Native software decoder");
public sealed class ScheduledTake
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Channel { get; set; }
    public Guid RundownId { get; set; }
    public Guid ItemId { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public bool Fired { get; set; }
    public string StatusText => Fired ? "Processed" : "Pending";
}
public sealed class EngineState
{
    // Null preserves the legacy processing path for projects saved before this feature.
    public string? ProjectVideoFormat { get; set; }
    public HardwareDecodeSettings HardwareDecoding { get; set; } = new();
    public List<DeckLinkRoute> DeckLinkRoutes { get; set; } = [];
    public List<StreamProfile> StreamProfiles { get; set; } = [];
    public int Version { get; set; } = 1;
    public List<MediaAsset> Media { get; set; } = [];
    public List<Rundown> Rundowns { get; set; } = [];
    public List<ChannelState> Channels { get; set; } = [];
    public List<Alarm> Alarms { get; set; } = [];
    public List<ScheduledTake> Schedule { get; set; } = [];
    public List<Guid?> Carts { get; set; } = Enumerable.Repeat<Guid?>(null, 48).ToList();
    public bool Linked { get; set; }
    public bool SharedRundown { get; set; }
    public string WatchFolder { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
    public string OutputStatus { get; set; } = "Native software playback · DeckLink adapter not installed";
    public AudioRouteSettings PreviewAudio { get; set; } = new();
    public static EngineState Create()
    {
        var s = new EngineState();
        for (var id = 1; id <= 2; id++)
        {
            var p = new Rundown { Channel = id, Name = id == 1 ? "Main bulletin" : "Secondary rundown" };
            s.Rundowns.Add(p); s.Channels.Add(new ChannelState { Id = id, Name = id == 1 ? "PROGRAM" : "SECONDARY", RundownId = p.Id });
        }
        return s;
    }
}
public sealed class AudioRouteSettings
{
    public string DeviceId { get; set; } = "";
    public int SampleRate { get; set; } = 48000;
    public int BitDepth { get; set; } = 16;
}
public sealed class EngineCommand
{
    public AudioLevelSettings? AutoLevel { get; set; }
    public ColourCorrection? Colour { get; set; }
    public string Action { get; set; } = "";
    public int Channel { get; set; } = 1;
    public Guid? RundownId { get; set; }
    public Guid? ItemId { get; set; }
    public Guid? MediaId { get; set; }
    public string? Text { get; set; }
    public string? Path { get; set; }
    public double Value { get; set; }
    public bool Enabled { get; set; }
    public bool Confirm { get; set; }
    public int Index { get; set; }
    public RundownItem? Item { get; set; }
    public Rundown? Rundown { get; set; }
    public GraphicsState? Graphics { get; set; }
    public ScheduledTake? Schedule { get; set; }
    public MediaAsset? Asset { get; set; }
    public List<Guid> ItemIds { get; set; } = [];
    public List<RundownItem> Items { get; set; } = [];
}
public sealed class LiveInputSettings
{
    public string VideoDevice { get; set; } = "";
    public string AudioDevice { get; set; } = "none";
    public string Type { get; set; } = "RTSP";
    public int CacheMs { get; set; } = 500;
    public bool Deinterlace { get; set; } = true;
    public double GainDb { get; set; }
    public bool Mute { get; set; }
    public double SignalTimeoutSeconds { get; set; } = 8;
    public Guid? BackupMediaId { get; set; }
}
public static class Timecode
{
    public static readonly string[] Formats = ["480p25", "480p29.97", "480p30", "480p50", "480p59.94", "480p60", "480i59.94", "576p25", "576p50", "576i50", "720p23.976", "720p24", "720p25", "720p29.97", "720p30", "720p50", "720p59.94", "720p60", "1080p23.976", "1080p24", "1080p25", "1080p29.97", "1080p30", "1080p50", "1080p59.94", "1080p60", "1080i50", "1080i59.94", "1080i60", "2160p23.976", "2160p24", "2160p25", "2160p29.97", "2160p30", "2160p50", "2160p59.94", "2160p60", "2160p100", "2160p119.88", "2160p120", "4320p23.976", "4320p24", "4320p25", "4320p29.97", "4320p30", "4320p50", "4320p59.94", "4320p60", "4320p100", "4320p119.88", "4320p120", "PAL", "NTSC"];
    public static double Rate(string format)
    {
        if (format == "PAL") return 25; if (format == "NTSC") return 30000d / 1001;
        var separator = Math.Max(format.IndexOf('p'), format.IndexOf('i'));
        if (separator < 0 || !double.TryParse(format[(separator + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate)) return 25;
        if (rate == 23.976) rate = 24000d / 1001; else if (rate == 29.97) rate = 30000d / 1001; else if (rate == 59.94) rate = 60000d / 1001; else if (rate == 119.88) rate = 120000d / 1001;
        return format[separator] == 'i' ? rate / 2 : rate;
    }
    public static string Format(double seconds, double fps = 25)
    {
        if (!double.IsFinite(fps) || fps < 1 || fps > 240) fps = 25;
        var frames = (long)Math.Floor(Math.Max(0, double.IsFinite(seconds) ? seconds : 0) * fps + 0.00001);
        var rate = (int)Math.Round(fps);
        return $"{frames / (rate * 3600):00}:{frames / (rate * 60) % 60:00}:{frames / rate % 60:00}:{frames % rate:00}";
    }
}
