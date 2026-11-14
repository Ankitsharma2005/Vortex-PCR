using System.Diagnostics;
using NewsPlay.Core;
using NewsPlay.Engine;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (InvalidOperationException) { Check(true, name); return; } throw new Exception(name); }
foreach (var format in ProjectVideoFormat.Supported)
{
    var input = new byte[format.FrameBytes]; var output = new byte[format.FrameBytes];
    for (int i = 0; i < input.Length; i += 4) { input[i] = (byte)(i / 4 % 251); input[i + 1] = (byte)(i / 4 / format.Width % 253); input[i + 2] = 173; input[i + 3] = 255; }
    using var pipeline = new ProductionVideoPipeline(format, () => 1000000);
    pipeline.Submit(input, format.Width, format.Height, 1000000);
    var timeout = Stopwatch.StartNew(); bool matched = false;
    while (timeout.Elapsed < TimeSpan.FromSeconds(5))
    {
        await Task.Delay(25);
        pipeline.Copy(output, out var w, out var h, out var clock, format.Interlaced);
        if (clock > 0 && output.AsSpan().SequenceEqual(input)) { matched = w == format.Width && h == format.Height; break; }
    }
    Check(matched, format.Id + " paced production preserves full raster pixels (including native UHD detail)");
    Console.WriteLine($"INFO {format.Id}: raster CPU {pipeline.ProcessingMs:0.00} ms, missed {pipeline.MissedDeadlines}; synthetic test, not decode/output certification");
}
Reject(() => ProjectVideoFormat.Find("4320p60"), "8K production rejected");
byte[] first = Enumerable.Repeat((byte)20, 2 * 4 * 4).ToArray(), second = Enumerable.Repeat((byte)180, first.Length).ToArray();
var woven = new byte[first.Length]; VideoRaster.CopyField(first, woven, 2, 4, 0); VideoRaster.CopyField(second, woven, 2, 4, 1);
Check(woven[0] == 20 && woven[8] == 180 && woven[16] == 20 && woven[24] == 180, "Upper-first weave keeps two time-separated fields");
var scaled = new byte[4 * 8 * 4]; VideoRaster.Scale(woven, 2, 4, scaled, 4, 8, true);
Check(Enumerable.Range(0, 8).All(y => scaled[y * 16] == (y % 2 == 0 ? 20 : 180)), "Interlaced scale never blends opposite fields");
var bob = new byte[first.Length]; VideoRaster.Bob(woven, bob, 2, 4, 1);
Check(bob.All(b => b == 180), "Deinterlace selects the correct field");
var source = Enumerable.Repeat((byte)160, 16 * 9 * 4).ToArray(); var letterbox = new byte[720 * 576 * 4]; VideoRaster.Scale(source, 16, 9, letterbox, 720, 576);
Check(letterbox[0] == 0 && letterbox[(288 * 720 + 360) * 4] == 160, "Aspect ratio conversion letterboxes rather than stretching");
var state = EngineState.Create(); state.ProjectVideoFormat = "2160p25"; state.DeckLinkRoutes.Add(new() { Port = 1, Mode = 123, AudioChannels = 8, AudioLeftChannel = 3, AudioRightChannel = 4 });
state.StreamProfiles.Add(new() { Width = 3840, Height = 2160, VideoCodec = "hevc", VideoEncoder = "libx265", Protocol = "SRT caller", Url = "", Fps = 25 });
var restored = Json.Copy(state);
Check(restored.ProjectVideoFormat == "2160p25" && restored.DeckLinkRoutes[0].AudioLeftChannel == 3 && restored.StreamProfiles[0].VideoCodec == "hevc", "Project and independent output settings survive JSON persistence");
var legacy = System.Text.Json.JsonSerializer.Deserialize<EngineState>("{\"version\":1}", Json.Options)!;
Check(legacy.ProjectVideoFormat is null && legacy.StreamProfiles.Count == 0, "Legacy project safely retains legacy processing");
var savedFolder = Path.Combine(AppContext.BaseDirectory, "project-video-save-check"); Directory.CreateDirectory(savedFolder);
using (var store = new SqliteStore(savedFolder)) store.Save(state);
using (var store = new SqliteStore(savedFolder)) Check(store.Load()!.ProjectVideoFormat == "2160p25" && store.Load()!.StreamProfiles[0].Width == 3840, "SQLite project save and reopen preserves independent formats");
foreach (var protocol in new[] { "RTMP", "RTMPS", "SRT caller", "SRT listener", "UDP" })
{
    var url = protocol switch { "RTMP" => "rtmp://example.com/live/key", "RTMPS" => "rtmps://example.com/live/key", "UDP" => "udp://example.com:9000", _ => "srt://example.com:9000" };
    var profile = new StreamProfile { Protocol = protocol, Url = url, Width = 3840, Height = 2160, Fps = 60, KeyframeSeconds = 3, AudioSampleRate = 44100, AudioChannels = 1 };
    var encoderArgs = StreamingArguments.Build(profile, 3840, 2160, "video-pipe", "audio-pipe").ToList();
    Check(encoderArgs[encoderArgs.IndexOf("-video_size") + 1] == "3840x2160" && encoderArgs[encoderArgs.IndexOf("-g") + 1] == "180" && encoderArgs.Contains("44100"), protocol + " UHD independent streaming arguments");
    Check(encoderArgs.IndexOf("-preset") > encoderArgs.IndexOf("-i"), "Encoder options follow inputs");
    if (protocol.StartsWith("SRT") || protocol == "UDP") { profile.VideoCodec = "hevc"; profile.VideoEncoder = "libx265"; profile.Validate(); Check(StreamingArguments.Build(profile, 1920, 1080, "v", "a").Contains("libx265"), protocol + " HEVC configuration"); }
    else { profile.VideoCodec = "hevc"; profile.VideoEncoder = "libx265"; Reject(profile.Validate, "Unsupported HEVC RTMP rejected"); }
}
var badLevel = new StreamProfile { Url = "rtmp://example.com/live", Width = 3840, Height = 2160, Fps = 60, EncoderLevel = "3.1" };
Reject(badLevel.Validate, "Low H.264 level rejects UHD60");
if (Environment.GetCommandLineArgs().Contains("--native"))
{
    LibVLCSharp.Shared.Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
    var bmp = Path.Combine(savedFolder, "native-3840x2160.bmp");
    using (var writer = new BinaryWriter(File.Create(bmp)))
    {
        const int w = 3840, h = 2160, bytes = w * h * 4;
        writer.Write((ushort)0x4d42); writer.Write(54 + bytes); writer.Write(0); writer.Write(54); writer.Write(40); writer.Write(w); writer.Write(-h); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0); writer.Write(bytes); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        var row = new byte[w * 4];
        for (int y = 0; y < h; y++) { for (int x = 0; x < w; x++) { row[x * 4] = (byte)x; row[x * 4 + 1] = (byte)y; row[x * 4 + 2] = 127; row[x * 4 + 3] = 255; } writer.Write(row); }
    }
    using var vlc = new LibVLCSharp.Shared.LibVLC("--quiet", "--no-video-title-show");
    using var decoder = new VlcDecoder(vlc, "ProjectVideoNative" + Environment.ProcessId, 1);
    decoder.ConfigureProject(ProjectVideoFormat.Find("2160p25"));
    decoder.SetAudio(0, true); decoder.Load(new MediaAsset { Name = "Native UHD raster test", Kind = "Still", Path = bmp, Width = 3840, Height = 2160 }, 0, true);
    var frame = new byte[3840 * 2160 * 4]; var watch = Stopwatch.StartNew(); bool valid = false;
    while (watch.Elapsed < TimeSpan.FromSeconds(10))
    {
        await Task.Delay(50); decoder.CopyProductionFrame(frame, out var width, out var height, out var pts);
        var at = (2101 * 3840 + 3801) * 4;
        if (pts > 0 && width == 3840 && height == 2160 && frame[at] == (byte)(3801 % 256) && frame[at + 1] == (byte)(2101 % 256) && frame[at + 2] == 127) { valid = true; break; }
    }
    Check(valid && decoder.SourceWidth == 3840 && decoder.SourceHeight == 2160, "Actual LibVLC decode to native UHD production preserves one-pixel detail");
    decoder.Stop();
}
Console.WriteLine($"{passed} project video checks passed. Physical DeckLink/GPU/network receivers were not tested.");
if (Environment.GetCommandLineArgs().Contains("--encoding")) { passed += await EncodingChecks.RunAll(savedFolder); Console.WriteLine($"{passed} checks passed including local encoding/network loopback. No physical DeckLink/GPU certification."); }
if (Environment.GetCommandLineArgs().Contains("--rtmps-only")) { passed += await EncodingChecks.RunAll(savedFolder, true); Console.WriteLine($"{passed} checks passed including RTMPS loopback."); }
if (Environment.GetCommandLineArgs().Contains("--capabilities"))
{
    var reports = ProjectVideoFormat.Supported.Select(VideoFormatProbe.Check).ToArray();
    File.WriteAllText(Path.Combine(savedFolder, "capabilities.json"), System.Text.Json.JsonSerializer.Serialize(reports, new System.Text.Json.JsonSerializerOptions(Json.Options) { WriteIndented = true }));
    foreach (var report in reports) Console.WriteLine($"CAPABILITY {report.Format}: enabled={report.Supported}, CPU={report.CpuConversionMs:0.00} ms, budget={report.FrameBudgetMs:0.00} ms");
}
if (Environment.GetCommandLineArgs().Contains("--gpu")) await GpuDecodingChecks.Run(savedFolder);
