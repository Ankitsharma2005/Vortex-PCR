using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DeckLinkAPI;
using NewsPlay.Core;

namespace NewsPlay.Engine;

// SDK 16 interfaces, generated from the vendor IDL. All COM work stays on MTA workers.
public sealed class DeckLinkOutputService : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<int, Session> sessions = [];
    private readonly Dictionary<int, VlcDecoder> decoders;
    public DeckLinkOutputService(VlcDecoder one, VlcDecoder two) => decoders = new() { [1] = one, [2] = two };
    internal static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static string Identity(IDeckLink device) => DeckLinkCompatibility.Identity(device);
    private static _BMDVideoConnection Connection(string name) => name switch { "Device default" => 0, "SDI" => _BMDVideoConnection.bmdVideoConnectionSDI, "HDMI" => _BMDVideoConnection.bmdVideoConnectionHDMI, _ => throw new InvalidOperationException("Select Device default, SDI or HDMI") };
    public static DeckLinkScan Scan()
    {
        IDeckLinkIterator? iterator = null; var devices = new List<DeckLinkDeviceInfo>(); var issues = new List<string>();
        try
        {
            iterator = DeckLinkCompatibility.Iterator();
            while (true)
            {
                iterator.Next(out var device); if (device is null) break;
                string name = "DeckLink device", id = "";
                try
                {
                    device.GetDisplayName(out name); id = Identity(device);
                    var support = DeckLinkCompatibility.Attribute(device, _BMDDeckLinkAttributeID.BMDDeckLinkVideoIOSupport);
                    var connections = DeckLinkCompatibility.Attribute(device, _BMDDeckLinkAttributeID.BMDDeckLinkVideoOutputConnections) ?? 0;
                    var hasOutput = DeckLinkApiView.Find(device, DeckLinkCompatibility.OutputInterfaces) is not null;
                    var playback = hasOutput && (support is null || (support & (long)_BMDVideoIOSupport.bmdDeviceSupportsPlayback) != 0);
                    var connectors = playback ? new List<string> { "Device default" } : [];
                    connectors.AddRange(new[] { "SDI", "HDMI" }.Where(c => (connections & (long)Connection(c)) != 0));
                    var modes = new List<DeckLinkMode>(); var api = "";
                    if (playback)
                    {
                        var output = new CompatibleDeckLinkOutput(device); api = output.ApiName;
                        output.GetDisplayModeIterator(out var modeIterator);
                        try
                        {
                            while (true)
                            {
                                modeIterator.Next(out var mode); if (mode is null) break;
                                try
                                {
                                    if (mode.GetWidth() > 1920 || mode.GetHeight() > 1080) continue;
                                    output.DoesSupportVideoMode(0, mode.GetDisplayMode(), _BMDPixelFormat.bmdFormat8BitYUV, _BMDVideoOutputConversionMode.bmdNoVideoOutputConversion, _BMDSupportedVideoModeFlags.bmdSupportedVideoModeDefault, out _, out var supported);
                                    if (supported == 0) continue;
                                    mode.GetName(out var label); mode.GetFrameRate(out var duration, out var scale);
                                    modes.Add(new((long)mode.GetDisplayMode(), label, mode.GetWidth(), mode.GetHeight(), duration, scale));
                                }
                                catch (COMException ex) { issues.Add(name + ": mode query 0x" + ex.HResult.ToString("X8")); }
                                finally { Release(mode); }
                            }
                        }
                        finally { Release(modeIterator); }
                    }
                    var issue = !playback ? "No active output capability; check the card profile. Capture-only cards cannot output." : modes.Count == 0 ? "Driver reports no supported SD/HD output modes in this profile." : "";
                    devices.Add(new(id, name, support.HasValue && (support.Value & (long)_BMDVideoIOSupport.bmdDeviceSupportsCapture) != 0, playback, connectors, modes, api, issue));
                }
                catch (Exception ex) { issues.Add(name + ": " + ex.Message); devices.Add(new(id, name, false, false, [], [], "", ex.Message)); }
                finally { Release(device); }
            }
            return new((devices.Count == 0 ? "Driver found; no DeckLink devices detected." : "Select a device and hardware format. Device default keeps the connector configured in Desktop Video Setup. Active API is detected automatically.") + (issues.Count == 0 ? "" : "\n" + string.Join("\n", issues.Distinct())), devices);
        }
        catch (Exception ex) { return new($"DeckLink discovery: {ex.Message} (0x{ex.HResult:X8})", devices); }
        finally { Release(iterator); }
    }
    public DeckLinkStatus[] Status() { lock (gate) return sessions.Values.Select(s => s.Status).ToArray(); }
    public async Task Start(DeckLinkRoute route)
    {
        PlayoutController.Number(route.AudioDelayMs, -500, 500);
        Session session;
        lock (gate)
        {
            if (!decoders.ContainsKey(route.Port)) throw new InvalidOperationException("Select port 1 or 2");
            if (sessions.ContainsKey(route.Port)) throw new InvalidOperationException("Stop this port's DeckLink route before changing it");
            if (sessions.Values.Any(s => s.Route.DeviceId == route.DeviceId)) throw new InvalidOperationException("This DeckLink subdevice is already assigned to another port");
            session = new Session(route, decoders[route.Port]); sessions.Add(route.Port, session);
        }
        session.Start();
        try { await session.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { session.Cancel.Cancel(); throw; }
    }
    public async Task Stop(int port)
    {
        Session? session; lock (gate) sessions.TryGetValue(port, out session);
        if (session is null) return;
        session.Cancel.Cancel();
        await session.Done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        lock (gate) sessions.Remove(port);
    }
    public void Dispose() { foreach (var status in Status()) { try { Stop(status.Port).GetAwaiter().GetResult(); } catch { } } }

    private sealed class Session
    {
        public readonly DeckLinkRoute Route;
        public readonly CancellationTokenSource Cancel = new();
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly VlcDecoder decoder;
        private readonly Queue<TimestampedPcm> audio = new();
        private readonly object audioGate = new();
        private int queuedSamples;
        private long underruns, droppedAudio;
        private long frames, audioFrames;
        private volatile string message = "Starting…";
        private volatile bool running;
        private int resetAudio;
        public DeckLinkStatus Status => new(Route.Port, Route.DeviceId, message + (running ? $" · underruns {Interlocked.Read(ref underruns)} · audio drops {Interlocked.Read(ref droppedAudio)}" : ""), running, Interlocked.Read(ref frames), Interlocked.Read(ref audioFrames));
        public Session(DeckLinkRoute route, VlcDecoder decoder) { Route = route; this.decoder = decoder; }
        public void Start() { var worker = new Thread(Run) { IsBackground = true, Name = $"DeckLink output {Route.Port}" }; worker.SetApartmentState(ApartmentState.MTA); worker.Start(); }
        private void Samples(TimestampedPcm packet)
        {
            if(packet.Bytes.Length / 4 > 24000) { Interlocked.Increment(ref droppedAudio); return; }
            lock(audioGate) { audio.Enqueue(packet); queuedSamples += packet.Bytes.Length / 4; while(queuedSamples > 24000 && audio.Count > 1) { queuedSamples -= audio.Dequeue().Bytes.Length / 4; Interlocked.Increment(ref droppedAudio); } }
        }
        private bool Dequeue(out TimestampedPcm? packet) { lock(audioGate) { if(audio.Count == 0) { packet=null; return false; } packet=audio.Dequeue();queuedSamples-=packet.Bytes.Length/4;return true; } }
        private void Reset() { lock(audioGate) { audio.Clear(); queuedSamples=0; } Interlocked.Exchange(ref resetAudio, 1); }
        private void Run()
        {
            IDeckLinkIterator? iterator = null; IDeckLink? device = null; CompatibleDeckLinkOutput? output = null;
            IDeckLinkDisplayMode? mode = null; object? rgb = null;
            DeckLinkApiView? converter = null; var pool = new List<object>();
            bool videoEnabled = false, audioEnabled = false, started = false;
            DeckLinkApiView? configuration = null; long oldConnection = 0; bool restoreConnection = false;
            try
            {
                iterator = DeckLinkCompatibility.Iterator();
                while (true) { iterator.Next(out var candidate); if (candidate is null) break; if (Identity(candidate) == Route.DeviceId) { device = candidate; break; } Release(candidate); }
                if (device is null) throw new InvalidOperationException("Selected DeckLink subdevice is no longer connected");
                output = new CompatibleDeckLinkOutput(device); var connector = Connection(Route.Connection);
                output.DoesSupportVideoMode(connector, (_BMDDisplayMode)Route.Mode, _BMDPixelFormat.bmdFormat8BitYUV, _BMDVideoOutputConversionMode.bmdNoVideoOutputConversion, _BMDSupportedVideoModeFlags.bmdSupportedVideoModeDefault, out var actual, out var supported);
                if (supported == 0 || (long)actual != Route.Mode) throw new InvalidOperationException("Card/profile does not support this connector and video mode");
                output.GetDisplayMode(actual, out mode); var width = mode.GetWidth(); var height = mode.GetHeight();
                if (width > 1920 || height > 1080 || width < 2 || height < 2) throw new InvalidOperationException("This adapter supports SD/HD modes through 1080; UHD is not enabled");
                mode.GetFrameRate(out var duration, out var timescale);
                if (connector != 0)
                {
                    configuration = DeckLinkCompatibility.Configuration(device) ?? throw new InvalidOperationException("Driver has no connector configuration interface. Choose Device default and set SDI/HDMI in Desktop Video Setup.");
                    object?[] args = [_BMDDeckLinkConfigurationID.bmdDeckLinkConfigVideoOutputConnection, 0L];
                    try { configuration.Call("GetInt", args); oldConnection = (long)args[1]!; }
                    catch (COMException) { throw new InvalidOperationException("Connector is fixed or managed by Desktop Video Setup. Choose Device default."); }
                    // A fixed output may already drive SDI and HDMI together; do not force a setter.
                    if ((oldConnection & (long)connector) == 0)
                    {
                        configuration.Call("SetInt", _BMDDeckLinkConfigurationID.bmdDeckLinkConfigVideoOutputConnection, (long)connector); restoreConnection = true;
                    }
                }
                output.EnableVideoOutput(actual, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault); videoEnabled = true;
                output.RowBytesForPixelFormat(_BMDPixelFormat.bmdFormat8BitBGRA, width, out var rgbStride);
                output.RowBytesForPixelFormat(_BMDPixelFormat.bmdFormat8BitYUV, width, out var yuvStride);
                output.CreateVideoFrame(width, height, rgbStride, _BMDPixelFormat.bmdFormat8BitBGRA, _BMDFrameFlags.bmdFrameFlagDefault, out rgb);
                for (var i = 0; i < 6; i++) { output.CreateVideoFrame(width, height, yuvStride, _BMDPixelFormat.bmdFormat8BitYUV, _BMDFrameFlags.bmdFrameFlagDefault, out var frame); pool.Add(frame); }
                var tagged=DeckLinkCompatibility.TagColour(rgb,height>576);foreach(var frame in pool)tagged=DeckLinkCompatibility.TagColour(frame,height>576)&&tagged;
                converter = DeckLinkCompatibility.Converter(output.LegacyFrames);
                if (Route.Audio)
                {
                    if (decoder.AudioSampleRate != 48000) throw new InvalidOperationException("Set this program audio profile to 48000 Hz before enabling embedded SDI audio");
                    output.EnableAudioOutput(_BMDAudioSampleRate.bmdAudioSampleRate48kHz, _BMDAudioSampleType.bmdAudioSampleType16bitInteger, 2, _BMDAudioOutputStreamType.bmdAudioOutputStreamTimestamped);
                    audioEnabled = true; decoder.TimestampedAudio += Samples; decoder.HardwareAudioReset += Reset;
                }
                var source = new byte[VlcDecoder.MaxWidth * VlcDecoder.MaxHeight * 4]; var scaled = new byte[rgbStride * height];
                long sequence = 0, sourceClock = 0, anchoredClock = 0; int poolIndex = 0, sw = 0, sh = 0; var timeline = new DeckLinkTimeline();
                long scaledClock = -1; int scaledWidth = -1, scaledHeight = -1;
                void Schedule()
                {
                    if (decoder.CopyHardwareFrame(source, out var nextWidth, out var nextHeight, out var clock)) { sw = nextWidth; sh = nextHeight; sourceClock = clock; }
                    if (sourceClock != scaledClock || sw != scaledWidth || sh != scaledHeight)
                    {
                        Scale(source, sw, sh, scaled, width, height, rgbStride);
                        scaledClock = sourceClock; scaledWidth = sw; scaledHeight = sh;
                    }
                    DeckLinkCompatibility.WriteFrame(rgb, scaled);
                    var target = pool[poolIndex++ % pool.Count]; converter.Call("ConvertFrame", rgb, target);
                    if(Route.LegalRange)DeckLinkCompatibility.LegalizeFrame(target,width,height,yuvStride);
                    var displayTime = sequence++ * duration;
                    output.ScheduleVideoFrame(target, displayTime, duration, timescale);
                    if(sourceClock > 0 && sourceClock != anchoredClock)
                    {
                        timeline.Anchor(sourceClock, displayTime, timescale); anchoredClock = sourceClock;
                        // Update only for a fresh image. A held/paused frame must not grow the audio delay.
                        var hardwareTime = started ? output.ScheduledTime(timescale) : 0;
                        decoder.SetSoundCardVideoDelay((long)((displayTime - hardwareTime) * 1000000d / timescale) + decoder.ClockMicroseconds - sourceClock + Route.AudioDelayMs * 1000L);
                    }
                    Interlocked.Increment(ref frames);
                }
                for (var i = 0; i < 3; i++) Schedule();
                Cancel.Token.ThrowIfCancellationRequested();
                output.StartScheduledPlayback(0, timescale, 1); started = true; running = true;
                message = "Running · " + output.ApiName + " · 8-bit SDR · " + (tagged?(height>576?"Rec.709 tagged":"Rec.601 tagged"):"driver colour metadata default") + (Route.LegalRange?" · Y16–235 C16–240":"") + " · timestamped PCM/video · audio trim " + Route.AudioDelayMs + " ms"; Ready.TrySetResult();
                TimestampedPcm? pending = null; int offset = 0; long pendingTime = 0, submittedUntil = 0;
                while (!Cancel.IsCancellationRequested)
                {
                    if (Interlocked.Exchange(ref resetAudio, 0) != 0 && audioEnabled)
                    {
                        output.FlushBufferedAudioSamples(); pending = null; offset = 0; submittedUntil = 0;
                        timeline.Reset(); anchoredClock = sourceClock; decoder.SetSoundCardVideoDelay(0);
                    }
                    output.GetBufferedVideoFrameCount(out var count);
                    if(count == 0) { sequence = DeckLinkTimeline.NextVideo(sequence, output.ScheduledTime(timescale), duration); Interlocked.Increment(ref underruns); }
                    if (count < 3) Schedule();
                    if (audioEnabled)
                    {
                        if (decoder.AudioSampleRate != 48000) throw new InvalidOperationException("Audio sample rate changed; restart DeckLink after restoring 48000 Hz");
                        output.GetBufferedAudioSampleFrameCount(out var buffered);
                        if (timeline.Ready && buffered < 4800 && (pending is not null || Dequeue(out pending)))
                        {
                            if(offset == 0) pendingTime = timeline.AudioTime(pending!.PresentationMicroseconds > 0 ? pending.PresentationMicroseconds : decoder.ClockMicroseconds, Route.AudioDelayMs);
                            var hardwareNow = output.ScheduledTime(48000);
                            offset += DeckLinkTimeline.SkipSamples(pendingTime, offset / 4, (pending!.Bytes.Length - offset) / 4, hardwareNow, submittedUntil) * 4;
                            if(offset == pending.Bytes.Length) { pending=null;offset=0;Interlocked.Increment(ref droppedAudio);continue; }
                            var handle = GCHandle.Alloc(pending.Bytes, GCHandleType.Pinned);
                            try { output.ScheduleAudioSamples(handle.AddrOfPinnedObject() + offset, (uint)((pending.Bytes.Length - offset) / 4), pendingTime + offset / 4, 48000, out var written); offset += checked((int)written * 4); if (written > 0) submittedUntil = pendingTime + offset / 4; Interlocked.Add(ref audioFrames, written); if (offset == pending.Bytes.Length) { pending = null; offset = 0; } }
                            finally { handle.Free(); }
                        }
                    }
                    Cancel.Token.WaitHandle.WaitOne(2);
                }
                message = "Stopped";
            }
            catch (Exception ex) { message = $"DeckLink error: {ex.Message} (0x{ex.HResult:X8})"; Ready.TrySetException(new InvalidOperationException(message, ex)); }
            finally
            {
                decoder.SetSoundCardVideoDelay(0);
                running = false; decoder.TimestampedAudio -= Samples; decoder.HardwareAudioReset -= Reset;
                if (started) { try { output!.StopScheduledPlayback(0, out _, 0); } catch { } }
                if (audioEnabled) { try { output!.DisableAudioOutput(); } catch { } }
                if (videoEnabled) { try { output!.DisableVideoOutput(); } catch { } }
                if (restoreConnection) { try { configuration!.Call("SetInt", _BMDDeckLinkConfigurationID.bmdDeckLinkConfigVideoOutputConnection, oldConnection); } catch { } }
                foreach (var frame in pool) Release(frame); Release(rgb); Release(converter?.Instance); Release(mode); Release(device); Release(iterator); Done.TrySetResult();
            }
        }
    }
    // Letterbox software-preview frames into the card's selected hardware raster.
    internal static void Scale(byte[] source, int sw, int sh, byte[] target, int width, int height, int stride)
    {
        if (sw == width && sh == height && stride == width * 4) { Buffer.BlockCopy(source, 0, target, 0, checked(stride * height)); return; }
        Array.Clear(target); if (sw < 1 || sh < 1) return;
        var scale = Math.Min((double)width / sw, (double)height / sh); var w = Math.Max(1, (int)(sw * scale)); var h = Math.Max(1, (int)(sh * scale));
        var left = (width - w) / 2; var top = (height - h) / 2;
        var offsets = new int[w]; var weights = new int[w];
        for(var x=0;x<w;x++) { var sx=Math.Clamp((x+.5)*sw/w-.5,0,sw-1);offsets[x]=(int)sx;weights[x]=(int)((sx-offsets[x])*256); }
        void ScaleRow(int y)
        {
            var sy=Math.Clamp((y+.5)*sh/h-.5,0,sh-1);var y0=(int)sy;var y1=Math.Min(sh-1,y0+1);var fy=(int)((sy-y0)*256);
            for(var x=0;x<w;x++)
            {
                var x0=offsets[x];var x1=Math.Min(sw-1,x0+1);var fx=weights[x];var a=(y0*sw+x0)*4;var b=(y0*sw+x1)*4;var c=(y1*sw+x0)*4;var d=(y1*sw+x1)*4;var to=(y+top)*stride+(x+left)*4;
                for(var channel=0;channel<3;channel++){var upper=source[a+channel]*(256-fx)+source[b+channel]*fx;var lower=source[c+channel]*(256-fx)+source[d+channel]*fx;target[to+channel]=(byte)((upper*(256-fy)+lower*fy+32768)>>16);}target[to+3]=255;
            }
        }
        if (w * h >= 1280 * 720) Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, ScaleRow);
        else for (var y = 0; y < h; y++) ScaleRow(y);
    }
}

