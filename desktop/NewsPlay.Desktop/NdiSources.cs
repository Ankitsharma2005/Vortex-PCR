using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class NdiSources(EngineClient client) : IDisposable
{
    private sealed class Session
    {
        public required string Path;
        public CancellationTokenSource Cancel = new();
        public Task? Worker;
        public volatile bool Preview, OnAir;
        public volatile string Status = "Connecting NDI";
        public volatile NdiAudioRoute[] Routes = [];
        public volatile float Left, Right;
        public long VideoFrames, AudioSamples;
        public int RenderCount;
    }
    private readonly Dictionary<Guid, Session> sessions = [];
    private readonly HashSet<Guid> monitored = [];
    private readonly Dictionary<Guid, long> revisions = [];
    public void Monitor(MediaAsset asset) { Ensure(asset); monitored.Add(asset.Id); }
    public void Unmonitor(Guid id) { monitored.Remove(id); if (sessions.TryGetValue(id, out var session)) { session.Preview = false; if (!session.OnAir) Stop(id); } }
    public string Status(Guid id) => sessions.TryGetValue(id, out var session) ? session.Status : "NDI not started";
    public (double Left, double Right) Levels(Guid id, int port) { if (!sessions.TryGetValue(id, out var session)) return (0, 0); var gain = session.Routes.FirstOrDefault(r => r.Port == port)?.Gain ?? 0; return (session.Left * gain, session.Right * gain); }
    internal bool HasAudioVideo(Guid id) => sessions.TryGetValue(id, out var session) && Interlocked.Read(ref session.VideoFrames) > 2 && Interlocked.Read(ref session.AudioSamples) > 0;
    internal bool HasAudioRender(Guid id) => sessions.TryGetValue(id, out var session) && Volatile.Read(ref session.RenderCount) > 0;
    public void Ensure(MediaAsset asset)
    {
        if (sessions.TryGetValue(asset.Id, out var old))
        {
            if (old.Path == asset.Path && old.Worker?.IsCompleted != true) return;
            old.Cancel.Cancel(); sessions.Remove(asset.Id);
        }
        if (sessions.Count >= 8) throw new InvalidOperationException("Maximum eight NDI receivers");
        NdiNative.Check();
        var session = new Session { Path = asset.Path }; sessions.Add(asset.Id, session);
        session.Worker = Task.Run(() => Receive(asset.Id, session));
    }
    public void ToggleListen(Guid id)
    {
        if (!sessions.TryGetValue(id, out var session)) throw new InvalidOperationException("Start NDI input first");
        session.Preview = !session.Preview;
    }
    public void Sync(EngineState state)
    {
        foreach (var id in sessions.Keys.ToArray())
        {
            var asset = state.Media.FirstOrDefault(m => m.Id == id);
            if (asset is null || (revisions.TryGetValue(id, out var revision) && revision != asset.LiveReleaseRevision && !LiveUsage.IsOnAir(state, id)) || (!monitored.Contains(id) && !LiveUsage.IsOnAir(state, id))) Stop(id);
        }
        foreach (var asset in state.Media.Where(m => m.Live?.Type == "NDI")) revisions[asset.Id] = asset.LiveReleaseRevision;
        foreach (var asset in state.Media.Where(m => m.Live?.Type == "NDI").Take(8))
        {
            // Discovery/Save explicitly creates sessions. On-air takes also recover after restart.
            var channels = state.Channels.Where(c => c.Status == "Playing" && !c.Emergency && (c.LiveInputId == asset.Id || (c.LiveInputId is null && state.Rundowns.FirstOrDefault(p => p.Id == c.RundownId)?.Items.Any(i => i.Id == c.CurrentId && i.MediaId == asset.Id) == true))).ToArray();
            if (channels.Length > 0 && !sessions.ContainsKey(asset.Id)) { try { Ensure(asset); } catch { continue; } }
            if (!sessions.TryGetValue(asset.Id, out var session)) continue;
            session.OnAir = channels.Length > 0;
            session.Routes = channels.Length > 0 ? channels.Select(c => new NdiAudioRoute(c.Id, c.AudioDeviceId, c.AudioSampleRate, c.AudioBitDepth, c.Mute || asset.Live!.Mute ? 0 : (float)Math.Min(2, Math.Pow(10, (c.GainDb + asset.Live.GainDb) / 20)))).ToArray() : session.Preview ? new[] { new NdiAudioRoute(0, state.PreviewAudio.DeviceId, state.PreviewAudio.SampleRate, state.PreviewAudio.BitDepth, 1) } : [];
        }
    }
    private async Task Receive(Guid id, Session session)
    {
        IntPtr receiver = IntPtr.Zero, sourceName = IntPtr.Zero;
        var outputs = new Dictionary<int, NdiAudioRender>();
        var lastVideo = DateTime.MinValue;
        var pendingVideo = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
        var upload = Task.Run(async () =>
        {
            try
            {
                await foreach (var jpeg in pendingVideo.Reader.ReadAllAsync(session.Cancel.Token))
                {
                    await client.BrowserFrame(id, jpeg, session.Cancel.Token);
                    Interlocked.Increment(ref session.VideoFrames);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { session.Status = "NDI frame upload: " + ex.Message; session.Cancel.Cancel(); }
        });
        try
        {
            sourceName = Marshal.StringToCoTaskMemUTF8(Uri.UnescapeDataString(session.Path[6..]));
            var settings = new NdiNative.ReceiverSettings { Source = new NdiNative.Source { Name = sourceName }, Color = 0, Bandwidth = 100, Fields = false };
            receiver = NdiNative.NDIlib_recv_create_v3(ref settings);
            if (receiver == IntPtr.Zero) throw new InvalidOperationException("NDI receiver initialization failed");
            while (!session.Cancel.IsCancellationRequested)
            {
                var video = new NdiNative.Video(); var audio = new NdiNative.Audio();
                var kind = NdiNative.NDIlib_recv_capture_v2(receiver, ref video, ref audio, IntPtr.Zero, 100);
                if (kind == 1)
                {
                    try
                    {
                        if (DateTime.UtcNow - lastVideo < TimeSpan.FromMilliseconds(30)) continue;
                        if (video.Width < 2 || video.Width > 8192 || video.Height < 2 || video.Height > 4320 || video.Stride < video.Width * 4 || video.Stride > 65536 || video.Data == IntPtr.Zero) throw new InvalidOperationException("Unsupported NDI frame layout");
                        if (video.FourCC is not (0x41524742 or 0x58524742)) throw new InvalidOperationException("NDI did not return requested BGRX/BGRA format");
                        var size = checked(video.Stride * video.Height); var pixels = new byte[size]; Marshal.Copy(video.Data, pixels, 0, size);
                        BitmapSource picture = BitmapSource.Create(video.Width, video.Height, 96, 96, PixelFormats.Bgr32, null, pixels, video.Stride);
                        var scale = Math.Min(1, Math.Min(960d / video.Width, 540d / video.Height));
                        if (scale < 1) picture = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
                        var encoder = new JpegBitmapEncoder { QualityLevel = 85 }; encoder.Frames.Add(BitmapFrame.Create(picture));
                        using var stream = new MemoryStream(); encoder.Save(stream);
                        // Network upload must not block the native receiver's audio capture.
                        pendingVideo.Writer.TryWrite(stream.ToArray()); lastVideo = DateTime.UtcNow;
                        session.Status = $"NDI {video.Width}×{video.Height} · video bridge up to 30 fps · independent program/preview audio routes";
                    }
                    finally { NdiNative.NDIlib_recv_free_video_v2(receiver, ref video); }
                }
                else if (kind == 2)
                {
                    try
                    {
                        if (audio.Data == IntPtr.Zero || audio.Samples <= 0 || audio.Samples > 192000 || audio.Rate < 8000 || audio.Rate > 192000 || audio.Channels < 1 || audio.Channels > 64 || audio.Stride < audio.Samples * 4) continue;
                        var left = new float[audio.Samples]; var right = new float[audio.Samples];
                        Marshal.Copy(audio.Data, left, 0, left.Length); Marshal.Copy(audio.Data + (audio.Channels > 1 ? audio.Stride : 0), right, 0, right.Length);
                        var interleaved = new float[audio.Samples * 2];
                        for (var i = 0; i < left.Length; i++) { interleaved[2 * i] = float.IsFinite(left[i]) ? left[i] : 0; interleaved[2 * i + 1] = float.IsFinite(right[i]) ? right[i] : 0; }
                        var bytes = new byte[interleaved.Length * 4]; Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
                        session.Left = left.Select(Math.Abs).DefaultIfEmpty(0).Max(); session.Right = right.Select(Math.Abs).DefaultIfEmpty(0).Max();
                        var routes = session.Routes;
                        foreach (var removed in outputs.Keys.Where(p => routes.All(r => r.Port != p)).ToArray()) { outputs[removed].Dispose(); outputs.Remove(removed); }
                        foreach (var route in routes)
                        {
                            if (outputs.TryGetValue(route.Port, out var current) && (current.InputRate != audio.Rate || current.Route.Device != route.Device || current.Route.Rate != route.Rate || current.Route.Bits != route.Bits)) { current.Dispose(); outputs.Remove(route.Port); }
                            if (!outputs.TryGetValue(route.Port, out current)) { current = new NdiAudioRender(route, audio.Rate); outputs.Add(route.Port, current); }
                            current.Push(route, bytes);
                        }
                        Volatile.Write(ref session.RenderCount, outputs.Count);
                        Interlocked.Add(ref session.AudioSamples, audio.Samples);
                    }
                    finally { NdiNative.NDIlib_recv_free_audio_v2(receiver, ref audio); }
                }
                else if (kind == 4) throw new InvalidOperationException("NDI receiver disconnected; restart preview");
                else if (DateTime.UtcNow - lastVideo > TimeSpan.FromSeconds(3)) session.Status = "No NDI video signal · waiting for source";
            }
        }
        catch (Exception ex) { session.Status = ex.Message; }
        finally { session.Cancel.Cancel(); pendingVideo.Writer.TryComplete(); await upload; Volatile.Write(ref session.RenderCount, 0); session.Left = session.Right = 0; foreach (var render in outputs.Values) render.Dispose(); if (receiver != IntPtr.Zero) NdiNative.NDIlib_recv_destroy(receiver); if (sourceName != IntPtr.Zero) Marshal.FreeCoTaskMem(sourceName); }
    }
    public void Stop(Guid id) { monitored.Remove(id); if (sessions.Remove(id, out var session)) { session.Routes = []; session.Cancel.Cancel(); } }
    public void Dispose() { foreach (var id in sessions.Keys.ToArray()) Stop(id); }
}

