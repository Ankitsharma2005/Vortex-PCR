using NewsPlay.Core;
using NewsPlay.Engine;
using LibVLCSharp.Shared;
using System.Text;

var passed = 0; var failed = 0;
Test("Auto audio balances loud and quiet stereo clips, limits peaks and preserves silence", () =>
{
    var level = new AudioLeveler(); level.Configure(new() { Enabled = true, MaxBoostDb = 24 });
    short[] Tone(double amplitude) => Enumerable.Range(0, 9600).Select(i => (short)(32767 * amplitude * Math.Sin((i / 2) * 2 * Math.PI * 1000 / 48000))).ToArray();
    double Rms(float[] samples) => Math.Sqrt(samples.Average(x => (double)x * x));
    var loud = level.Process(Tone(.8), 48000, 1); level.Reset(); var quiet = level.Process(Tone(.04), 48000, 1);
    Equal(true, Math.Abs(20 * Math.Log10(Rms(loud)) + 18) < .1);
    Equal(true, Math.Abs(Rms(loud) - Rms(quiet)) < .001);
    Equal(true, level.Process(new short[9600], 48000, 1).All(x => x == 0));
    Equal(true, level.Process(Tone(1), 48000, 100).All(x => Math.Abs(x) <= Math.Pow(10, -1.0 / 20) + .000001));
    level.Configure(new()); var source = Tone(.2); var bypass = level.Process(source, 48000, 1);
    Equal(true, bypass.Select((x, i) => x == source[i] / 32768f).All(x => x));
    Throws(() => level.Configure(new() { TargetDb = double.NaN }));
    var saved = Json.Copy(new ChannelState { AutoLevel = new() { Enabled = true, TargetDb = -20 } }); Equal(-20.0, saved.AutoLevel.TargetDb); Equal(true, saved.AutoLevel.Enabled);
});
Test("3D cube and mesh 3DL interpolate identity and reject incomplete/unsafe LUTs", () =>
{
    var cube="LUT_3D_SIZE 2\n"+string.Join("\n",Enumerable.Range(0,8).Select(i=>$"{i%2} {i/2%2} {i/4}"));
    var lut=CubeLut.Parse(cube,"identity.cube");var input=new System.Numerics.Vector3(.21f,.43f,.87f);var actual=lut.Sample(input);
    Equal(true,System.Numerics.Vector3.Distance(input,actual)<.00001);
    var mesh="0 1023\n"+string.Join("\n",Enumerable.Range(0,8).Select(i=>$"{i/4*1023} {i/2%2*1023} {i%2*1023}"));
    Equal(true,System.Numerics.Vector3.Distance(input,CubeLut.Parse(mesh,"identity.3dl").Sample(input))<.00001);
    Throws(()=>CubeLut.Parse("LUT_3D_SIZE 65\n0 0 0","bad.cube"));Throws(()=>CubeLut.Parse("LUT_1D_SIZE 2","bad.cube"));
    byte[] pixels=[52,105,222,71];var original=pixels.ToArray();new ColourTransform(new(){Lut=lut}).Apply(pixels,4,1,1);
    Equal(true,pixels.SequenceEqual(original));
});
Test("Advanced qualifier, curves and feathered mask affect only selected colour/area", () =>
{
    byte[] colours=[0,0,255,255,255,0,0,255];new ColourTransform(new(){Qualifier=true,QualifierHue=0,QualifierWidth=5,Hue=120}).Apply(colours,8,2,1);
    Equal(true,colours[1]>250&&colours[2]<5);Equal((byte)255,colours[4]);Equal((byte)0,colours[5]);
    var c=new ColourCorrection();c.Curves[1]=[0,0,0,0,0];byte[] red=[0,0,200,255];new ColourTransform(c).Apply(red,4,1,1);Equal((byte)0,red[2]);
    var area=Enumerable.Repeat(new byte[]{30,30,30,255},25).SelectMany(p=>p).ToArray();new ColourTransform(new(){Offset=.4,Mask="Ellipse",MaskWidth=.5,MaskHeight=.5}).Apply(area,area.Length,5,5);
    Equal((byte)30,area[0]);Equal(true,area[12*4]>100);Equal((byte)255,area[12*4+3]);
    Throws(()=>new ColourTransform(new(){White=.2,Black=.3}));Throws(()=>new ColourTransform(new(){QualifierSatMin=.8,QualifierSatMax=.2}));
});
Test("SDI nominal limiter clamps luma/chroma while preserving row padding",()=>
{
    byte[] raster=[0,0,255,255,77,88];BroadcastRange.ClampUyvy(raster,2,1,6);Equal(true,raster.SequenceEqual(new byte[]{16,16,240,235,77,88}));
});
Test("Hue versus luma adds equal channel offsets before gamut clipping",()=>
{
    var c=new ColourCorrection();c.HueCurves[2]=Enumerable.Repeat(.2,6).ToArray();byte[] pixel=[120,100,80,255];new ColourTransform(c).Apply(pixel,4,1,1);
    Equal(true,Math.Abs(pixel[0]-146)<=1&&Math.Abs(pixel[1]-126)<=1&&Math.Abs(pixel[2]-106)<=1);
});
Test("DeckLink audio timestamps share video timebase, delay trim and underrun recovery",()=>
{
    var t=new DeckLinkTimeline();t.Anchor(1000000,1001,30000);Equal(1602L,t.AudioTime(1000000));Equal(2082L,t.AudioTime(1010000));Equal(2562L,t.AudioTime(1000000,20));
    Equal(11L,DeckLinkTimeline.NextVideo(3,10000,1000));Equal(15L,DeckLinkTimeline.NextVideo(15,10000,1000));
});
Test("DeckLink bilinear upscale preserves endpoints and smooths intermediate pixels",()=>
{
    byte[] source=[0,0,0,255,255,255,255,255];var target=new byte[4*2*4];
    typeof(DeckLinkOutputService).GetMethod("Scale",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,[source,2,1,target,4,2,16]);
    Equal((byte)0,target[0]);Equal((byte)64,target[4]);Equal((byte)191,target[8]);Equal((byte)255,target[12]);Equal((byte)255,target[15]);
});
Test("Hardware PCM carries the original presentation timestamp",()=>
{
    using var output=new PcmAudioOutput();TimestampedPcm? packet=null;output.TimestampedSamples+=p=>packet=p;
    var pointer=System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
    try{System.Runtime.InteropServices.Marshal.Copy(new short[]{100,-100},0,pointer,2);output.Samples(pointer,1,1234567);Equal(1234567L,packet!.PresentationMicroseconds);Equal((short)-100,BitConverter.ToInt16(packet.Bytes,2));}
    finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer);}
});
Test("Advanced grade processes a 540p frame without changing alpha",()=>
{
    var pixels=new byte[960*540*4];for(var i=0;i<pixels.Length;i+=4){pixels[i]=64;pixels[i+1]=100;pixels[i+2]=150;pixels[i+3]=255;}
    var transform=new ColourTransform(new(){Lift=.05,Hue=5,Saturation=.8});transform.Apply(pixels,pixels.Length,960,540);
    var clock=System.Diagnostics.Stopwatch.StartNew();for(var i=0;i<30;i++)transform.Apply(pixels,pixels.Length,960,540);clock.Stop();
    Equal((byte)255,pixels[^1]);Console.WriteLine($"INFO Advanced CPU grade 960x540: {clock.Elapsed.TotalMilliseconds/30:0.00} ms/frame (not GPU or end-to-end latency)");
});
Test("Colour correction validates atomically and persists without moving on-air transport", () =>
{
    var t = new Fixture(); t.Add(1); t.Add(2); t.Command(1, "Play"); t.Command(2, "Play"); t.Advance(2);
    t.C.Execute(new EngineCommand { Action = "Media.Colour", MediaId = t.Media.Id, Colour = new() { Saturation = 0, Brightness = .1 } });
    Equal(0d, t.D1.Colour.Saturation); Equal(0d, t.D2.Colour.Saturation); Equal(2d, t.C.Channel(1).Position); Equal("Playing", t.C.Channel(1).Status);
    Throws(() => t.C.Execute(new EngineCommand { Action = "Media.Colour", MediaId = t.Media.Id, Colour = new() { Gamma = double.NaN } }));
    Equal(.1, t.C.Media(t.Media.Id).Colour.Brightness); Equal(0d, t.D1.Colour.Saturation);
    var restored = new Fixture(t.Store); Equal(.1, restored.C.Media(t.Media.Id).Colour.Brightness); Equal(0d, restored.D1.Colour.Saturation);
});
Test("Colour transform identity, bypass, monochrome, clipping and alpha preservation", () =>
{
    byte[] original = [20, 100, 240, 77]; var pixels = original.ToArray();
    new ColourTransform(new()).Apply(pixels, 4); Equal(true, pixels.SequenceEqual(original));
    new ColourTransform(new() { Enabled = false, Brightness = 1 }).Apply(pixels, 4); Equal(true, pixels.SequenceEqual(original));
    new ColourTransform(new() { Saturation = 0 }).Apply(pixels, 4); Equal(pixels[0], pixels[1]); Equal(pixels[1], pixels[2]); Equal((byte)77, pixels[3]);
    new ColourTransform(new() { Brightness = 1 }).Apply(pixels, 4); Equal((byte)255, pixels[0]);
    new ColourTransform(new() { Brightness = -1 }).Apply(pixels, 4); Equal((byte)0, pixels[2]);
    Throws(() => new ColourTransform(new() { Red = 3 }));
});
Test("Live profile deletion cannot be undone into dangling media references", () => { var t = new Fixture(); var input = Live(t); var liveItem = PlayoutController.NewItem(input); t.C.Execute(new EngineCommand { Action = "Live.Add", Item = liveItem }); t.Add(1); t.C.Execute(new EngineCommand { Action = "Live.Delete", MediaId = input.Id, Confirm = true }); t.C.Execute(new EngineCommand { Action = "Rundown.Undo" }); Equal(false, t.C.State.Rundowns.SelectMany(p => p.Items).Any(i => i.MediaId == input.Id)); });
Test("Deleting a live input requires confirmation and stops both references", () => { var t = new Fixture(); var input = Live(t); var item = PlayoutController.NewItem(input); t.C.Execute(new EngineCommand { Action = "Live.Add", Item = item }); t.C.Execute(new EngineCommand { Action = "Live.Take", Channel = 1, MediaId = input.Id }); Throws(() => t.C.Execute(new EngineCommand { Action = "Live.Delete", MediaId = input.Id })); Equal(true, t.C.State.Media.Any(m => m.Id == input.Id)); t.C.Execute(new EngineCommand { Action = "Live.Delete", MediaId = input.Id, Confirm = true }); Equal(false, t.C.State.Media.Any(m => m.Id == input.Id)); Equal(false, t.C.State.Rundowns.SelectMany(p => p.Items).Any(i => i.MediaId == input.Id)); Equal("Stopped", t.C.Channel(1).Status); Equal(false, t.C.Channel(1).LiveInputId.HasValue); });
Test("Live source release only advances after its last rundown reference is removed", () => { var t = new Fixture(); var input = Live(t); var a = PlayoutController.NewItem(input); var b = PlayoutController.NewItem(input); t.C.Execute(new EngineCommand { Action = "Live.Add", Item = a }); t.C.Execute(new EngineCommand { Action = "Live.Add", Item = b }); t.C.Execute(new EngineCommand { Action = "Rundown.Remove", ItemId = a.Id }); Equal(0L, t.C.Media(input.Id).LiveReleaseRevision); t.C.Execute(new EngineCommand { Action = "Rundown.Remove", ItemId = b.Id }); Equal(1L, t.C.Media(input.Id).LiveReleaseRevision); });
Test("Streaming validates protocol, bitrate and SRT mode without command interpolation", () => { var p = new StreamProfile { Protocol = "SRT listener", Url = "srt://127.0.0.1:9000?mode=caller&latency=200000" }; Equal(true, p.Destination().Contains("mode=listener")); Equal(false, p.Destination().Contains("mode=caller")); p.Url = "file:///C:/overwrite.ts"; Throws(p.Validate); p.Protocol = "RTMP"; p.Url = "rtmps://example.com/live/key"; p.Validate(); p.VideoKbps = -1; Throws(p.Validate); });
Test("DeckLink compatibility selects available interface and preserves out arguments", () => { var api = DeckLinkApiView.Find(new LegacyApiProbe(), typeof(IUnavailableApiProbe), typeof(ILegacyApiProbe))!; Equal(typeof(ILegacyApiProbe), api.Interface); object?[] args = [0L]; api.Call("Read", args); Equal(48000L, (long)args[0]!); });
Test("DeckLink compatibility preserves driver HRESULT instead of reflection wrapper", () => { var api = new DeckLinkApiView(new LegacyApiProbe(), typeof(ILegacyApiProbe)); try { api.Call("Fail"); throw new Exception("Expected driver rejection"); } catch (System.Runtime.InteropServices.COMException ex) { Equal(unchecked((int)0x80070057), ex.HResult); } });
Test("Embedded hardware PCM tap uses stereo Int16 gain and resets on pause", () => { using var audio = new PcmAudioOutput(); byte[]? captured = null; var resets = 0; audio.HardwareSamples += data => captured = data; audio.HardwareReset += () => resets++; var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(8); try { System.Runtime.InteropServices.Marshal.Copy(new short[] { 10000, -10000, 20000, -20000 }, 0, ptr, 4); audio.Gain(0, false); audio.Samples(ptr, 2); Equal(8, captured!.Length); Equal((short)10000, BitConverter.ToInt16(captured, 0)); Equal((short)-10000, BitConverter.ToInt16(captured, 2)); audio.Gain(0, true); audio.Samples(ptr, 2); Equal((short)0, BitConverter.ToInt16(captured!, 0)); audio.Pause(); Equal(true, resets >= 2); } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); } });
Test("Shared rundown preserves both lists and independent port cursors", () => { var t = new Fixture(); var a = t.Add(1); var b = t.Add(2); t.C.Execute(new EngineCommand { Action = "Rundown.Share" }); Equal(t.C.Channel(1).RundownId, t.C.Channel(2).RundownId); Equal(2, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); t.Command(1, "Take", a.Id); t.Command(2, "Take", b.Id); Equal(a.Id, t.C.Channel(1).CurrentId!.Value); Equal(b.Id, t.C.Channel(2).CurrentId!.Value); Throws(() => t.C.Execute(new EngineCommand { Action = "Rundown.RemoveMany", ItemIds = [b.Id] })); Equal(true, t.Store.Load()!.SharedRundown); });
Test("Shared rundown migration rejects playing output without mutation", () => { var t = new Fixture(); t.Add(1); t.Command(1, "Play"); Throws(() => t.C.Execute(new EngineCommand { Action = "Rundown.Share" })); Equal(false, t.C.State.SharedRundown); });
Test("Browser removal keeps on-air and rundown references and reimport restores same asset", () => { var t = new Fixture(); var item = t.Add(1); t.Command(1, "Play"); t.C.Execute(new EngineCommand { Action = "Media.Remove", ItemIds = [t.Media.Id] }); Equal(true, t.C.Media(t.Media.Id).RemovedFromBrowser); Equal("Playing", t.C.Channel(1).Status); Equal(item.Id, t.C.Channel(1).CurrentId!.Value); Equal(1, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); t.C.AddMedia(new MediaAsset { Path = t.Media.Path }); Equal(false, t.C.Media(t.Media.Id).RemovedFromBrowser); Equal(1, t.C.State.Media.Count); });
Test("Browser batch removal validates all selections before mutation", () => { var t = new Fixture(); Throws(() => t.C.Execute(new EngineCommand { Action = "Media.Remove", ItemIds = [t.Media.Id, Guid.NewGuid()] })); Equal(false, t.C.Media(t.Media.Id).RemovedFromBrowser); });
Test("Bulk paste preserves trims, uses new IDs and undoes as one edit", () => { var t = new Fixture(); var a = t.Add(1, 1, 3); var pasted = Json.Copy(a); t.C.Execute(new EngineCommand { Action = "Rundown.Paste", Items = [pasted, Json.Copy(pasted)], ItemId = a.Id }); var items = t.C.Playlist(t.C.Channel(1).RundownId).Items; Equal(3, items.Count); Equal(3, items.Select(i => i.Id).Distinct().Count()); Equal(1d, items[1].In); Equal(3d, items[1].Out); t.C.Execute(new EngineCommand { Action = "Rundown.Undo" }); Equal(1, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); });
Test("Bulk delete rejects mixed playing selection without deleting other clips", () => { var t = new Fixture(); var a = t.Add(1); var b = t.Add(1); t.Command(1, "Play"); Throws(() => t.C.Execute(new EngineCommand { Action = "Rundown.RemoveMany", ItemIds = [a.Id, b.Id] })); Equal(2, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); });
Test("Remove all requires stopped playback and can be undone", () => { var t = new Fixture(); t.Add(1); t.Add(1); t.Command(1, "Play"); Throws(() => t.C.Execute(new EngineCommand { Action = "Rundown.Clear" })); t.C.Execute(new EngineCommand { Action = "Stop", Confirm = true }); t.C.Execute(new EngineCommand { Action = "Rundown.Clear" }); Equal(0, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); t.C.Execute(new EngineCommand { Action = "Rundown.Undo" }); Equal(2, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); });
Test("PCM format validates atomically and persists independently per port", () => { var t = new Fixture(); Throws(() => t.C.Execute(new EngineCommand { Action = "AudioProfile", Value = 12345, Index = 24 })); Equal(48000, t.C.Channel(1).AudioSampleRate); t.C.Execute(new EngineCommand { Action = "AudioProfile", Value = 96000, Index = 24 }); var restored = new Fixture(t.Store); Equal(96000, restored.C.Channel(1).AudioSampleRate); Equal(24, restored.C.Channel(1).AudioBitDepth); Equal(48000, restored.C.Channel(2).AudioSampleRate); });
Test("SD HD and interlaced formats use correct frame rather than field rate", () => { Equal(25d, Timecode.Rate("480p25")); Equal(50d, Timecode.Rate("1080p50")); Equal(50d, Timecode.Rate("720p50")); Equal(25d, Timecode.Rate("1080i50")); Equal(30000d / 1001, Timecode.Rate("1080i59.94")); });
void Test(string name, Action test) { try { test(); Console.WriteLine("PASS " + name); passed++; } catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; } }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
Test("Browser profile accepts web URLs and rejects executable/file schemes", () => { var t = new Fixture(); var browser = new MediaAsset { Name = "Results", Path = "https://example.com/results", Live = new LiveInputSettings { Type = "Browser" } }; t.C.Execute(new EngineCommand { Action = "Live.Save", Asset = browser }); Equal("Live", t.C.Media(browser.Id).Kind); browser.Path = "javascript:alert(1)"; Throws(() => t.C.Execute(new EngineCommand { Action = "Live.Save", Asset = browser })); Equal("https://example.com/results", t.C.Media(browser.Id).Path); browser.Path = "file:///C:/secret"; Throws(() => t.C.Execute(new EngineCommand { Action = "Live.Save", Asset = browser })); });
MediaAsset Live(Fixture t, Guid? backup = null) { var m = new MediaAsset { Name = "Live feed", Path = "rtsp://127.0.0.1:8554/feed", Live = new LiveInputSettings { BackupMediaId = backup } }; t.C.Execute(new EngineCommand { Action = "Live.Save", Asset = m }); return t.C.Media(m.Id); }
Test("Live override returns to exact playlist position and preserves CG", () => { var t = new Fixture(); var item = t.Add(1); t.Command(1, "Play"); t.Advance(2); var input = Live(t); t.C.Channel(1).Graphics.Headline = "Keep CG"; t.C.Execute(new EngineCommand { Action = "Live.Take", MediaId = input.Id }); t.Advance(3); Equal(input.Id, t.C.Channel(1).LiveInputId!.Value); Equal("Keep CG", t.C.Channel(1).Graphics.Headline); t.Command(1, "Live.Back"); Equal(item.Id, t.C.Channel(1).CurrentId!.Value); Equal(2d, t.C.Channel(1).Position); Equal("Playing", t.C.Channel(1).Status); });
Test("Timed live item advances using wall time", () => { var t = new Fixture(); var input = Live(t); var item = PlayoutController.NewItem(input); item.Out = 2; t.C.Execute(new EngineCommand { Action = "Rundown.Add", Item = item }); var next = t.Add(1); t.Command(1, "Play"); t.Advance(2); Equal(next.Id, t.C.Channel(1).CurrentId!.Value); });
Test("Live no-frame timeout returns to playlist and alarms", () => { var t = new Fixture(); var item = t.Add(1); t.Command(1, "Play"); t.Advance(1); var input = Live(t); t.C.Execute(new EngineCommand { Action = "Live.Take", MediaId = input.Id }); t.Advance(9); Equal<Guid?>(null, t.C.Channel(1).LiveInputId); Equal(item.Id, t.C.Channel(1).CurrentId!.Value); Equal(1d, t.C.Channel(1).Position); Equal(1, t.C.State.Alarms.Count); });
Test("Live signal loss takes local backup clip", () => { var t = new Fixture(); t.Add(1); var input = Live(t, t.Media.Id); t.C.Execute(new EngineCommand { Action = "Live.Take", MediaId = input.Id }); t.Advance(9); Equal(t.Media.Id, t.C.Current(t.C.Channel(1))!.MediaId!.Value); Equal(-1, t.C.Current(t.C.Channel(1))!.Loops); Equal("Playing", t.C.Channel(1).Status); });
Test("Live settings reject unsupported adapters and unsafe URI schemes", () => { var t = new Fixture(); Throws(() => t.C.Execute(new EngineCommand { Action = "Live.Save", Asset = new MediaAsset { Name = "Bad", Path = "file:///C:/secret", Live = new LiveInputSettings() } })); Throws(() => t.C.Execute(new EngineCommand { Action = "Live.Save", Asset = new MediaAsset { Name = "Window", Path = "window://source", Live = new LiveInputSettings { Type = "Window capture" } } })); Equal(1, t.C.State.Media.Count); });
Test("HLS accepts web playlists and rejects local-file URLs", () => { var asset = new MediaAsset { Name = "HLS", Path = "https://localhost/live/index.m3u8", Live = new LiveInputSettings { Type = "HLS" } }; PlayoutController.ValidateLive(asset); Equal("Live", asset.Kind); asset.Path = "file:///C:/input.m3u8"; Throws(() => PlayoutController.ValidateLive(asset)); });
Test("Capture requires an explicit video device and persists audio selection", () => { var t = new Fixture(); var asset = new MediaAsset { Name = "Camera", Path = "dshow://", Live = new LiveInputSettings { Type = "USB / Webcam" } }; Throws(() => PlayoutController.ValidateLive(asset)); asset.Live.VideoDevice = "Test camera"; asset.Live.AudioDevice = "Test microphone"; t.C.Execute(new EngineCommand { Action = "Live.Save", Asset = asset }); var restored = new Fixture(t.Store); Equal("Test microphone", restored.C.Media(asset.Id).Live!.AudioDevice); });
Test("NDI requires a source name", () => { var asset = new MediaAsset { Name = "NDI", Path = "ndi://", Live = new LiveInputSettings { Type = "NDI" } }; Throws(() => PlayoutController.ValidateLive(asset)); asset.Path = "ndi://Test%20Sender"; PlayoutController.ValidateLive(asset); Equal("Live", asset.Kind); });
Test("SRT caller accepts stream parameters and rejects unsupported receiver modes", () => { var asset = new MediaAsset { Name = "SRT", Path = "srt://127.0.0.1:9000?mode=caller&latency=200", Live = new LiveInputSettings { Type = "SRT" } }; PlayoutController.ValidateLive(asset); asset.Path = "srt://127.0.0.1:9000?mode=listener"; Throws(() => PlayoutController.ValidateLive(asset)); });
Test("Live override survives restart with return point", () => { var t = new Fixture(); t.Add(1); t.Command(1, "Play"); t.Advance(2); var input = Live(t); t.C.Execute(new EngineCommand { Action = "Live.Take", MediaId = input.Id }); var recovered = new Fixture(t.Store); Equal(input.Id, recovered.C.Channel(1).LiveInputId!.Value); recovered.Command(1, "Live.Back"); Equal(2d, recovered.C.Channel(1).Position); });
Test("Stopped live override does not restart on air", () => { var t = new Fixture(); var input = Live(t); t.C.Execute(new EngineCommand { Action = "Live.Take", MediaId = input.Id }); t.C.Execute(new EngineCommand { Action = "Stop", Confirm = true }); var recovered = new Fixture(t.Store); Equal("Stopped", recovered.C.Channel(1).Status); Equal<Guid?>(null, recovered.C.Channel(1).LiveInputId); });
Test("Live scheduling binds inserted item and rejects invalid time without insertion", () => { var t = new Fixture(); var input = Live(t); var item = PlayoutController.NewItem(input); item.Out = 2; Throws(() => t.C.Execute(new EngineCommand { Action = "Live.Add", Item = item, Schedule = new ScheduledTake { At = t.Now.AddSeconds(-1) } })); Equal(0, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); t.C.Execute(new EngineCommand { Action = "Live.Add", Item = item, Schedule = new ScheduledTake { At = t.Now.AddSeconds(2) } }); Equal(t.C.Playlist(t.C.Channel(1).RundownId).Items[0].Id, t.C.State.Schedule[0].ItemId); });
Test("Loop clip repeats until explicit next and preserves queued destination", () => { var t = new Fixture(); var a = t.Add(1, 0, 1); var b = t.Add(1); t.Command(1, "Play"); t.C.Execute(new EngineCommand { Action = "LoopClip", Enabled = true }); t.Command(1, "Cue", b.Id); for (var n = 0; n < 20; n++) t.Advance(1); Equal(a.Id, t.C.Channel(1).CurrentId!.Value); Equal(21, t.C.Channel(1).Pass); t.Command(1, "Next"); Equal(b.Id, t.C.Channel(1).CurrentId!.Value); Equal(false, t.C.Channel(1).LoopCurrent); });
Test("Loop clip survives restart and remains independent per port", () => { var t = new Fixture(); t.Add(1, 0, 1); t.Add(2, 0, 1); t.Command(1, "Play"); t.Command(2, "Play"); t.C.Execute(new EngineCommand { Action = "LoopClip", Enabled = true }); var recovered = new Fixture(t.Store); Equal(true, recovered.C.Channel(1).LoopCurrent); Equal(false, recovered.C.Channel(2).LoopCurrent); recovered.Advance(1); Equal(2, recovered.C.Channel(1).Pass); Equal("Held", recovered.C.Channel(2).Status); });
Test("Break loop releases current-clip repeat", () => { var t = new Fixture(); t.Add(1, 0, 1); var b = t.Add(1); t.Command(1, "Play"); t.C.Execute(new EngineCommand { Action = "LoopClip", Enabled = true }); t.Command(1, "BreakLoop"); t.Advance(1); Equal(b.Id, t.C.Channel(1).CurrentId!.Value); Equal(false, t.C.Channel(1).LoopCurrent); });
Test("Two independent native channel controllers", () => { var t = new Fixture(); t.Add(1); t.Add(2); t.Command(1, "Play"); t.Command(2, "Play"); t.Advance(2); t.Command(1, "Pause"); t.Advance(1); Equal(2d, t.C.Channel(1).Position); Equal(3d, t.C.Channel(2).Position); });
Test("Trim IN/OUT and finite loop advances to next", () => { var t = new Fixture(); var a = t.Add(1, 2, 4, 2); var b = t.Add(1); t.Command(1, "Take", a.Id); Equal(2d, t.C.Channel(1).Position); t.Advance(2); Equal(2, t.C.Channel(1).Pass); t.Advance(2); Equal(b.Id, t.C.Channel(1).CurrentId!.Value); Equal("Playing", t.C.Channel(1).Status); Equal(2, t.Store.Rows.Count); });
Test("1000 software loops remain inside trim", () => { var t = new Fixture(); t.Add(1, 2, 3, -1); t.Command(1, "Play"); for (var n = 0; n < 1000; n++) t.Advance(1); Equal(1001, t.C.Channel(1).Pass); Equal(2d, t.C.Channel(1).Position); });
Test("Break loop follows next item after current pass", () => { var t = new Fixture(); t.Add(1, 0, 1, -1); var b = t.Add(1); t.Command(1, "Play"); t.Advance(1); t.Command(1, "BreakLoop"); t.Advance(1); Equal(b.Id, t.C.Channel(1).CurrentId!.Value); });
Test("Manual next item cues without playing", () => { var t = new Fixture(); t.Add(1, 0, 1); var b = t.Add(1); b.StartMode = "Manual"; t.Command(1, "Play"); t.Advance(1); Equal("Cued", t.C.Channel(1).Status); });
Test("100 item rundown reaches final held frame", () => { var t = new Fixture(); for (var n = 0; n < 100; n++) t.Add(1, 0, 1); t.Command(1, "Play"); for (var n = 0; n < 100; n++) t.Advance(1); Equal("Held", t.C.Channel(1).Status); });
Test("Emergency preserves position and resumes audio decoder", () => { var t = new Fixture(); t.Add(1); t.Command(1, "Play"); t.Advance(2); t.Command(1, "Emergency"); t.Advance(20); Equal(2d, t.C.Channel(1).Position); t.Command(1, "Return"); t.Advance(1); Equal(3d, t.C.Channel(1).Position); });
Test("Missing media raises alarm and standby", () => { var t = new Fixture(); t.Media.Path = "missing"; t.Add(1); t.Command(1, "Play"); Equal("Error", t.C.Channel(1).Status); Equal(true, t.C.Channel(1).Emergency); Equal(1, t.C.State.Alarms.Count); });
Test("Playing rundown item is protected", () => { var t = new Fixture(); var a = t.Add(1); t.Command(1, "Play"); Throws(() => t.Command(1, "Rundown.Remove", a.Id)); Throws(() => t.C.Execute(new EngineCommand { Action = "Rundown.Update", ItemId = a.Id, Item = Json.Copy(a) })); });
Test("Undo and redo are non-destructive", () => { var t = new Fixture(); t.Add(1); t.Add(1); t.Command(1, "Rundown.Undo"); Equal(1, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); t.Command(1, "Rundown.Redo"); Equal(2, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); });
Test("Linked play validates both channels before mutation", () => { var t = new Fixture(); t.Add(1); t.C.State.Linked = true; Throws(() => t.Command(1, "Play")); Equal("Stopped", t.C.Channel(1).Status); t.Add(2); t.Command(1, "Play"); Equal("Playing", t.C.Channel(2).Status); Throws(() => t.Command(1, "Stop")); Equal("Playing", t.C.Channel(1).Status); });
Test("Invalid trims, passes and gains are rejected", () => { var t = new Fixture(); Throws(() => t.Add(1, 5, 3)); Throws(() => t.Add(1, 0, 11)); Throws(() => t.Add(1, 0, 1, 0)); Throws(() => t.C.Execute(new EngineCommand { Action = "Gain", Value = double.NaN })); });
Test("Unknown media frame rate cannot crash timecode rendering", () => { Equal("00:00:01:00", Timecode.Format(1, 0)); Equal("00:00:01:00", Timecode.Format(1, double.NaN)); });
Test("Scheduled take fires once and missed take alarms", () => { var t = new Fixture(); var a = t.Add(1); t.C.Execute(new EngineCommand { Action = "Schedule", Schedule = new ScheduledTake { Channel = 1, RundownId = t.C.Channel(1).RundownId, ItemId = a.Id, At = t.Now.AddSeconds(2) } }); t.Advance(2); Equal("Playing", t.C.Channel(1).Status); Equal(true, t.C.State.Schedule[0].Fired); });
Test("Checkpoint restores active decoder position", () => { var t = new Fixture(); t.Add(1); t.Command(1, "Play"); t.Advance(2); t.C.Save(); var recovered = new Fixture(t.Store); Equal(2d, recovered.C.Channel(1).Position); Equal("Playing", recovered.C.Channel(1).Status); Equal("Engine restart", t.Store.Rows.Last().Result); });
Test("SQLite transactional state and as-run survive reopening", () => { var root = Path.Combine(Path.GetTempPath(), "NewsPlay-test-" + Guid.NewGuid().ToString("N")); using (var db = new SqliteStore(root)) { var s = EngineState.Create(); s.Channels[0].Position = 42; db.Save(s); } using (var db = new SqliteStore(root)) Equal(42d, db.Load()!.Channels[0].Position); });

Test("PCR cue queues selected item without interrupting current decoder", () => { var t = new Fixture(); var a = t.Add(1, 0, 4); t.Add(1); var c = t.Add(1); t.Command(1, "Take", a.Id); t.Advance(1); t.Command(1, "Cue", c.Id); Equal(a.Id, t.C.Channel(1).CurrentId!.Value); Equal(1d, t.C.Channel(1).Position); Equal("Playing", t.C.Channel(1).Status); t.Advance(3); Equal(c.Id, t.C.Channel(1).CurrentId!.Value); Equal<Guid?>(null, t.C.Channel(1).QueuedId); });
Test("PCR manual mode parks next and continue plays it", () => { var t = new Fixture(); t.Add(1, 0, 2); var b = t.Add(1); t.C.Execute(new EngineCommand { Action = "AutoAdvance", Enabled = false }); t.Command(1, "Play"); t.Advance(2); Equal(b.Id, t.C.Channel(1).CurrentId!.Value); Equal("Cued", t.C.Channel(1).Status); t.Advance(2); Equal(0d, t.C.Channel(1).Position); t.Command(1, "Play"); t.Advance(1); Equal(1d, t.C.Channel(1).Position); });
Test("Park requires confirmation while output is playing", () => { var t = new Fixture(); var a = t.Add(1); var b = t.Add(1, 2, 5); t.Command(1, "Take", a.Id); Throws(() => t.Command(1, "Park", b.Id)); Equal(a.Id, t.C.Channel(1).CurrentId!.Value); t.C.Execute(new EngineCommand { Action = "Park", ItemId = b.Id, Confirm = true }); Equal("Cued", t.C.Channel(1).Status); Equal(2d, t.C.Channel(1).Position); });
Test("Rundown lock rejects edits but permits cue and take", () => { var t = new Fixture(); var a = t.Add(1); t.C.Execute(new EngineCommand { Action = "Rundown.Lock", Enabled = true }); Throws(() => t.Add(1)); Throws(() => t.Command(1, "Rundown.Remove", a.Id)); Throws(() => t.Command(1, "Rundown.Undo")); t.Command(1, "Cue", a.Id); t.Command(1, "Take", a.Id); Equal("Playing", t.C.Channel(1).Status); });
Test("PCR protects imminent next from deletion and edits", () => { var t = new Fixture(); t.Add(1, 0, 10); var b = t.Add(1); var c = t.Add(1); t.Command(1, "Play"); t.Advance(6); Throws(() => t.Command(1, "Rundown.Remove", b.Id)); Throws(() => t.C.Execute(new EngineCommand { Action = "Rundown.Update", ItemId = b.Id, Item = Json.Copy(b) })); Throws(() => t.C.Execute(new EngineCommand { Action = "Rundown.Move", ItemId = c.Id, Index = 1 })); Equal(3, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); });
Test("Preflight reports missing files without interrupting output", () => { var t = new Fixture(); var a = t.Add(1); t.Command(1, "Play"); t.Advance(1); t.Media.Path = "missing"; var r = t.C.Preflight(t.C.Channel(1).RundownId); Equal(1, r.Issues.Count); Equal(a.Id, r.Issues[0].ItemId); Equal("Playing", t.C.Channel(1).Status); Equal(1d, t.C.Channel(1).Position); });
Test("Missing manual take preserves current program", () => { var t = new Fixture(); var a = t.Add(1); var b = t.Add(1); t.Command(1, "Play"); t.Advance(1); t.Media.Path = "missing"; Throws(() => t.Command(1, "Take", b.Id)); Equal(a.Id, t.C.Channel(1).CurrentId!.Value); Equal("Playing", t.C.Channel(1).Status); Equal(1d, t.C.Channel(1).Position); });
Test("Removing queued item clears queue and resumes normal order", () => { var t = new Fixture(); var a = t.Add(1); var b = t.Add(1); t.Command(1, "Cue", b.Id); t.Command(1, "Rundown.Remove", b.Id); Equal<Guid?>(null, t.C.Channel(1).QueuedId); t.Command(1, "Play"); Equal(a.Id, t.C.Channel(1).CurrentId!.Value); });
Test("Queued item and PCR mode survive a saved checkpoint", () => { var t = new Fixture(); t.Add(1); var b = t.Add(1); t.Command(1, "Play"); t.Command(1, "Cue", b.Id); t.C.Execute(new EngineCommand { Action = "AutoAdvance", Enabled = false }); t.C.Execute(new EngineCommand { Action = "Rundown.Lock", Enabled = true }); var restored = new Fixture(t.Store); Equal(b.Id, restored.C.Channel(1).QueuedId!.Value); Equal(false, restored.C.Channel(1).AutoAdvance); Equal(true, restored.C.Playlist(restored.C.Channel(1).RundownId).Locked); });
Test("Missing auto-next enters alarm state without throwing out of tick", () => { var t = new Fixture(); t.Add(1, 0, 1); t.Add(1); t.Command(1, "Play"); t.Media.Path = "missing"; t.Advance(1); Equal("Error", t.C.Channel(1).Status); Equal(1, t.C.State.Alarms.Count); });

Test("Imminent next cannot be displaced by insertion", () => { var t = new Fixture(); var a = t.Add(1, 0, 10); t.Add(1); t.Command(1, "Play"); t.Advance(6); Throws(() => t.C.Execute(new EngineCommand { Action = "Rundown.Add", ItemId = a.Id, MediaId = t.Media.Id })); Equal(2, t.C.Playlist(t.C.Channel(1).RundownId).Items.Count); });
Test("Recovery preserves loop pass and requested break", () => { var t = new Fixture(); t.Add(1, 0, 1, 3); t.Command(1, "Play"); t.Advance(1); t.Command(1, "BreakLoop"); var restored = new Fixture(t.Store); Equal(2, restored.C.Channel(1).Pass); Equal(true, restored.C.Channel(1).BreakLoop); });

Test("Audio device rejects unavailable endpoint without mutating state", () => { var t = new Fixture(); Throws(() => t.C.Execute(new EngineCommand { Action = "AudioDevice", Text = "unavailable-device" })); Equal("", t.C.Channel(1).AudioDeviceId); });
Test("Audio endpoint changes are protected during playing and persist at rest", () => { var t = new Fixture(); t.Add(1); t.Command(1, "Play"); Throws(() => t.C.Execute(new EngineCommand { Action = "AudioDevice", Text = "" })); t.Command(1, "Pause"); t.C.Execute(new EngineCommand { Action = "AudioDevice", Text = "" }); var restored = new Fixture(t.Store); Equal("Paused", restored.C.Channel(1).Status); Equal("", restored.C.Channel(1).AudioDeviceId); });

if (args.Contains("--native"))
{
    try
    {
        Console.WriteLine("NATIVE: Writing deterministic AVI fixture");
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEWSPLAY_TESTDIR") ?? Path.Combine(AppContext.BaseDirectory, "test-media")); Directory.CreateDirectory(root); var file = Path.Combine(root, "Native-Test.avi"); AviFixture.Write(file);
        Console.WriteLine("NATIVE: Loading libvlc DLL");
        LibVLCSharp.Shared.Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
        Console.WriteLine("NATIVE: Creating libvlc instance");
        using var vlc = new LibVLC("--no-video-title-show", "--verbose=2");
        vlc.Log += (_, e) => { if (e.Level >= LogLevel.Error) Console.WriteLine("VLC " + e.Message); };
        Console.WriteLine("NATIVE: Probing AVI");
        var probe = new MediaProbe(vlc); var media = await probe.Inspect(file); Equal("Clip", media.Kind); if (media.Duration < 1.5 || media.Width != 160) throw new Exception("Native media probe returned invalid metadata");
        Console.WriteLine("NATIVE: Starting two frame-buffer decoders");
        using var first = new VlcDecoder(vlc, "Test" + Environment.ProcessId, 1); using var second = new VlcDecoder(vlc, "Test" + Environment.ProcessId, 2);
        first.Load(media, 0, true); second.Load(media, .2, true); first.SetAudio(0, true); second.SetAudio(0, true);
        await Task.Delay(900); Console.WriteLine($"NATIVE: Frames {first.Frames}, {second.Frames}; positions {first.Position}, {second.Position}"); if (first.Frames < 2 || second.Frames < 2) throw new Exception($"Decoded frame counts too low: {first.Frames}, {second.Frames}");
        first.SetColour(new() { Saturation = 0 });
        await Task.Delay(150);
        var corrected = new byte[VlcDecoder.MaxWidth * VlcDecoder.MaxHeight * 4];
        if (!first.CopyHardwareFrame(corrected, out var cw, out var ch) || cw == 0) throw new Exception("No corrected output frame");
        for (var pixel = 0; pixel < cw * ch * 4; pixel += 4) if (corrected[pixel] != corrected[pixel + 1] || corrected[pixel + 1] != corrected[pixel + 2]) throw new Exception("Colour correction missing from hardware/program buffer");
        first.SetColour(new() { Enabled = false, Saturation = 0 }); await Task.Delay(150);
        var bypassRestored = false;
        for (var attempt = 0; attempt < 30 && !bypassRestored; attempt++) { if (first.CopyHardwareFrame(corrected, out cw, out ch) && cw > 0) bypassRestored = Enumerable.Range(0, cw * ch).Any(p => corrected[p * 4] != corrected[p * 4 + 2]); if (!bypassRestored) await Task.Delay(10); }
        if (!bypassRestored) throw new Exception("Bypass did not restore colour");
        Console.WriteLine("PASS Native on-air colour adjustment and bypass reach shared hardware/program frame without reload."); passed++;
        first.Pause(); var before = first.Position; var secondBefore = second.Frames; await Task.Delay(450); if (Math.Abs(first.Position - before) > .2 || second.Frames <= secondBefore) throw new Exception("Channel independence failed");
        Console.WriteLine($"PASS Actual LibVLC dual AVI decoding: CH1 {first.Frames} frames, CH2 {second.Frames} frames; metadata {media.Width}×{media.Height}, {media.Duration:0.00}s"); passed++;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            first.Stop(); second.Stop(); first.Load(media, .1, true); second.Load(media, .2, true);
            GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(500);
            if (first.Error is not null || second.Error is not null) throw new Exception("Decoder error during repeated loads");
        }
        Console.WriteLine("PASS Repeated native stop/load under forced garbage collection."); passed++;
    }
    catch (Exception ex) { Console.WriteLine("FAIL Native decoding: " + ex); failed++; }
}
Console.WriteLine($"\n{passed} passed; {failed} failed."); Environment.ExitCode = failed > 0 ? 1 : 0;

sealed class MemoryStore : IStateStore
{
    private EngineState? state; public List<AsRun> Rows { get; } = [];
    public EngineState? Load() => state is null ? null : Json.Copy(state);
    public void Save(EngineState value) => state = Json.Copy(value);
    public void Log(AsRun row) => Rows.Add(row);
    public void Audit(string action, string detail) { }
}
sealed class FakeDecoder : IDecoder
{
    public ColourCorrection Colour { get; private set; } = new();
    public void SetColour(ColourCorrection correction) => Colour = Json.Copy(correction);
    public bool HasMedia { get; private set; } public double Position { get; private set; } public bool Ended => false; public string? Error => null; public long Frames => 0; private bool playing;
    public void Load(MediaAsset media, double position, bool play) { SetColour(media.Colour); HasMedia = true; Position = position; playing = play; }
    public void Play() => playing = true;
    public void Pause() => playing = false;
    public void Stop() { playing = false; HasMedia = false; }
    public void Seek(double seconds) => Position = seconds;
    public void SetAudio(double gainDb, bool mute) { }
    public void SetRate(double rate) { }
    public void Advance(double seconds) { if (playing) Position += seconds; }
    public void Dispose() { }
}
sealed class Fixture
{
    public readonly PlayoutController C; public readonly MemoryStore Store; public readonly FakeDecoder D1 = new(), D2 = new(); public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T10:00:00Z"); public MediaAsset Media;
    public Fixture(MemoryStore? store = null) { Store = store ?? new MemoryStore(); C = new PlayoutController(Store, new Dictionary<int, IDecoder> { [1] = D1, [2] = D2 }, () => Now, p => p != "missing"); Media = C.State.Media.FirstOrDefault() ?? new MediaAsset { Name = "Clip", Path = "test", Duration = 10 }; C.AddMedia(Media); }
    public RundownItem Add(int channel, double start = 0, double end = 10, int loops = 1) { C.Execute(new EngineCommand { Action = "Rundown.Add", Channel = channel, Item = new RundownItem { MediaId = Media.Id, In = start, Out = end, Loops = loops } }); return C.Playlist(C.Channel(channel).RundownId).Items.Last(); }
    public void Command(int channel, string action, Guid? item = null) => C.Execute(new EngineCommand { Channel = channel, Action = action, ItemId = item });
    public void Advance(double seconds) { Now += TimeSpan.FromSeconds(seconds); D1.Advance(seconds); D2.Advance(seconds); C.Tick(seconds); }
}
static class AviFixture
{
    public static void Write(string file)
    {
        const int width = 160, height = 90, fps = 25, count = 75, size = width * height * 3;
        using var stream = File.Create(file); using var w = new BinaryWriter(stream);
        void Four(string s) => w.Write(Encoding.ASCII.GetBytes(s));
        long Chunk(string name) { Four(name); var p = stream.Position; w.Write(0); return p; }
        void End(long position) { var end = stream.Position; stream.Position = position; w.Write((int)(end - position - 4)); stream.Position = end; if ((end & 1) != 0) w.Write((byte)0); }
        var riff = Chunk("RIFF"); Four("AVI "); var hdrl = Chunk("LIST"); Four("hdrl"); var avih = Chunk("avih"); w.Write(1000000 / fps); w.Write(size * fps); w.Write(0); w.Write(0x10); w.Write(count); w.Write(0); w.Write(1); w.Write(size); w.Write(width); w.Write(height); w.Write(new byte[16]); End(avih);
        var strl = Chunk("LIST"); Four("strl"); var strh = Chunk("strh"); Four("vids"); Four("DIB "); w.Write(0); w.Write((short)0); w.Write((short)0); w.Write(0); w.Write(1); w.Write(fps); w.Write(0); w.Write(count); w.Write(size); w.Write(-1); w.Write(0); w.Write((short)0); w.Write((short)0); w.Write((short)width); w.Write((short)height); End(strh);
        var strf = Chunk("strf"); w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24); w.Write(0); w.Write(size); w.Write(0); w.Write(0); w.Write(0); w.Write(0); End(strf); End(strl); End(hdrl);
        var movi = Chunk("LIST"); Four("movi"); var origin = stream.Position - 4; var offsets = new List<int>();
        for (var frame = 0; frame < count; frame++) { offsets.Add((int)(stream.Position - origin)); var chunk = Chunk("00db"); for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) { w.Write((byte)(60 + y)); w.Write((byte)(30 + (x + frame * 3) % 180)); w.Write((byte)(20 + frame)); } End(chunk); }
        End(movi); var idx = Chunk("idx1"); foreach (var offset in offsets) { Four("00db"); w.Write(0x10); w.Write(offset); w.Write(size); } End(idx); End(riff);
    }
}

