using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using NewsPlay.Core;

namespace NewsPlay.Engine;

public sealed class VlcDecoder : IDecoder
{
    public const int MaxWidth = 960, MaxHeight = 540, Capacity = 64 + MaxWidth * MaxHeight * 4;
    public const int ProductionMaxWidth = 3840, ProductionMaxHeight = 2160;
    private ProductionVideoPipeline? production;
    public ProjectVideoFormat? ProductionFormat => production?.Format;
    public object ProductionStatus => new { format = production?.Format, sourceWidth = SourceWidth, sourceHeight = SourceHeight,
        sourceNativeRaster = production is not null && SourceWidth >= production.Format.Width && SourceHeight >= production.Format.Height,
        frames = production?.Produced ?? 0, missedDeadlines = production?.MissedDeadlines ?? 0, repeatedFrames = production?.RepeatedFrames ?? 0, processingMs = production?.ProcessingMs ?? 0 };
    public void ConfigureProject(ProjectVideoFormat? format)
    {
        if (HasMedia) throw new InvalidOperationException("Stop and unload both programs before changing the production format");
        var next = format is null ? null : new ProductionVideoPipeline(format, () => vlc.Clock);
        var previous = production; production = next; previous?.Dispose();
        managed = new byte[format?.FrameBytes ?? MaxWidth * MaxHeight * 4];
    }
    public bool CopyProductionFrame(byte[] target, out int w, out int h, out long clock, bool interlaced = false, bool lowerFieldFirst = false)
    {
        if (production is not null) return production.Copy(target, out w, out h, out clock, interlaced, lowerFieldFirst);
        return CopyHardwareFrame(target, out w, out h, out clock);
    }
    private readonly LibVLC vlc;
    private readonly MediaPlayer player;
    private readonly PcmAudioOutput audio = new();
    private HardwareDecodeSettings hardwareDecoding = new();
    private string activeDecodeBackend = "not loaded";
    public void ConfigureHardwareDecoding(HardwareDecodeSettings settings) { settings.Validate(); Volatile.Write(ref hardwareDecoding, settings); }
    public object HardwareDecodeStatus => new { requestedNextLoad = Volatile.Read(ref hardwareDecoding).Mode, activeRequestedBackend = activeDecodeBackend, softwareOverride = Environment.GetEnvironmentVariable("NEWSPLAY_SOFTWARE_DECODE") == "1", hasMedia = HasMedia, frames = Frames };
    private readonly MediaPlayer.LibVLCAudioPlayCb audioPlay;
    private readonly MediaPlayer.LibVLCAudioPauseCb audioPause;
    private readonly MediaPlayer.LibVLCAudioResumeCb audioResume;
    private readonly MediaPlayer.LibVLCAudioFlushCb audioFlush;
    private int audioRate = 48000, audioBits = 16;
    private string audioDevice = "";
    public object AudioSyncStatus => audio.SyncStatus;
    public double AudioLeft => audio.Left;
    public double AudioRight => audio.Right;
    public int AudioSampleRate => audioRate;
    public long ClockMicroseconds => vlc.Clock;
    public void SetSoundCardVideoDelay(long microseconds) => audio.SetVideoDelay(microseconds);
    public event Action<byte[]> HardwareAudio { add => audio.HardwareSamples += value; remove => audio.HardwareSamples -= value; }
    public event Action<TimestampedPcm> TimestampedAudio { add => audio.TimestampedSamples += value; remove => audio.TimestampedSamples -= value; }
    public event Action HardwareAudioReset { add => audio.HardwareReset += value; remove => audio.HardwareReset -= value; }
    public bool CopyHardwareFrame(byte[] target, out int w, out int h)
        => CopyHardwareFrame(target, out w, out h, out _);
    public bool CopyHardwareFrame(byte[] target, out int w, out int h, out long clock)
    {
        w = h = 0; clock = 0;
        if (!HasMedia) return true;
        bool held;
        try { held = frameLock.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
        if (!held) return false;
        try { w = view.ReadInt32(8); h = view.ReadInt32(12); if (w < 1 || h < 1 || w > MaxWidth || h > MaxHeight) { w = h = 0; return true; } Marshal.Copy(IntPtr.Add(view.SafeMemoryMappedViewHandle.DangerousGetHandle(), checked((int)view.PointerOffset + 64)), target, 0, w * h * 4); clock = view.ReadInt64(48); return true; }
        finally { frameLock.ReleaseMutex(); }
    }
    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private readonly Mutex frameLock;
    private readonly MemoryMappedFile scratch;
    private readonly MemoryMappedViewAccessor scratchView;
    private readonly IntPtr buffer;
    private byte[] managed = new byte[MaxWidth * MaxHeight * 4];
    private readonly byte[] previewPixels = new byte[MaxWidth * MaxHeight * 4];
    private ColourTransform colour = new(new ColourCorrection());
    private readonly RawPreview? rawPreview;
    public Guid LoadedMediaId { get; private set; }
    public void EnableRawPreview(bool enabled) { if (rawPreview is not null) rawPreview.Enabled = enabled; }
    public void SetColour(ColourCorrection correction) => Volatile.Write(ref colour, new ColourTransform(correction));
    private readonly MediaPlayer.LibVLCVideoLockCb lockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb displayCb;
    private readonly MediaPlayer.LibVLCVideoFormatCb formatCb;
    private long frames;
    private int width = MaxWidth, height = MaxHeight;
    private volatile bool ended;
    private volatile string? error;
    public bool HasMedia { get; private set; }
    public double Position => Math.Max(0, player.Time / 1000d);
    public bool Ended => ended;
    public string? Error => error;
    public long Frames => Interlocked.Read(ref frames);
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    public int AudioTrack => player.AudioTrack;
    public VlcDecoder(LibVLC vlc, string instance, int channel)
    {
        this.vlc = vlc; audio.Clock = () => vlc.Clock;
        if (channel is 1 or 2 or 5) rawPreview = new RawPreview(instance, channel + 20);
        // Page-aligned scratch memory satisfies VLC's 32-byte alignment requirement.
        // Pad scanlines for SIMD converters even when visible height is not a multiple of 32.
        scratch = MemoryMappedFile.CreateNew(null, channel is 1 or 2 ? ProductionMaxWidth * 2176L * 4 : MaxWidth * 576 * 4);
        scratchView = scratch.CreateViewAccessor();
        buffer = scratchView.SafeMemoryMappedViewHandle.DangerousGetHandle();
        map = MemoryMappedFile.CreateOrOpen($"Local\\NewsPlay.{instance}.CH{channel}", Capacity);
        view = map.CreateViewAccessor(); frameLock = new Mutex(false, $"Local\\NewsPlay.{instance}.CH{channel}.Lock");
        // Configure the media directly: MediaPlayer.EnableHardwareDecoding would
        // append its own D3D11VA option at Play and override Auto/DXVA2/CPU choices.
        player = new MediaPlayer(vlc);
        audioPlay = (_, samples, count, pts) => audio.Samples(samples, count, pts);
        audioPause = (_, _) => audio.Pause(); audioResume = (_, _) => audio.Resume(); audioFlush = (_, _) => audio.Flush();
        player.SetAudioCallbacks(audioPlay, audioPause, audioResume, audioFlush, null);
        player.SetAudioFormat("S16N", (uint)audioRate, 2);
        lockCb = (_, planes) => { Marshal.WriteIntPtr(planes, buffer); return IntPtr.Zero; };
        displayCb = (_, _) => Publish();
        player.SetVideoCallbacks(lockCb, null, displayCb);
        formatCb = (ref IntPtr opaque, IntPtr chroma, ref uint videoWidth, ref uint videoHeight, ref uint pitch, ref uint lines) =>
        {
            SourceWidth = (int)videoWidth; SourceHeight = (int)videoHeight;
            if (production is not null)
            {
                var ratio = Math.Min((double)production.Format.Width / videoWidth, (double)production.Format.Height / videoHeight);
                // Never call an upscaled source native; retain original source dimensions in status.
                width = Math.Max(8, (int)(videoWidth * ratio) / 8 * 8); height = Math.Max(2, (int)(videoHeight * ratio) / 2 * 2);
            }
            Marshal.Copy(System.Text.Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
            videoWidth = (uint)width; videoHeight = (uint)height;
            pitch = (uint)(width * 4); lines = (uint)((height + 31) & ~31);
            return 1;
        };
        player.SetVideoFormatCallbacks(formatCb, null);
        player.EndReached += (_, _) => ended = true;
        player.EncounteredError += (_, _) => error = "Decoder could not play this media. Check file integrity and codec support.";
    }
    private void Publish()
    {
        var presentationClock = vlc.Clock;
        var held = false;
        try
        {
            var size = width * height * 4;
            Marshal.Copy(buffer, managed, 0, size);
            var pw = width; var ph = height;
            byte[] preview = managed;
            if (production is not null)
            {
                var ratio = Math.Min((double)MaxWidth / width, (double)MaxHeight / height);
                pw = Math.Max(2, (int)(width * ratio) / 2 * 2); ph = Math.Max(2, (int)(height * ratio) / 2 * 2);
                if (rawPreview?.Enabled == true) VideoRaster.Scale(managed, width, height, previewPixels, pw, ph); preview = previewPixels;
            }
            rawPreview?.Publish(preview, pw, ph, LoadedMediaId);
            Volatile.Read(ref colour).Apply(managed, size, width, height);
            production?.Submit(managed, width, height, presentationClock);
            if (production is not null) VideoRaster.Scale(managed, width, height, previewPixels, pw, ph);
            try { held = frameLock.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) return;
            Marshal.Copy(preview, 0, IntPtr.Add(view.SafeMemoryMappedViewHandle.DangerousGetHandle(), checked((int)view.PointerOffset + 64)), pw * ph * 4);
            view.Write(8, pw); view.Write(12, ph); view.Write(16, pw * 4); view.Write(48, presentationClock); view.Write(0, Interlocked.Increment(ref frames));
        }
        catch { /* Frame publication failure must not unwind into native VLC code. */ }
        finally { if (held) frameLock.ReleaseMutex(); }
    }
    public void Load(MediaAsset asset, double position, bool play)
    {
        if (Environment.GetEnvironmentVariable("NEWSPLAY_TRACE") == "1") Console.WriteLine("Decoder: loading " + asset.Name);
        player.Stop(); LoadedMediaId = asset.Id; SetColour(asset.Colour); audio.Flush(); audio.Start(); ended = false; error = null; HasMedia = true;
        var ratio = asset.Width > 0 && asset.Height > 0 ? (double)asset.Width / asset.Height : 16d / 9;
        var maxW = production?.Format.Width ?? MaxWidth; var maxH = production?.Format.Height ?? MaxHeight;
        width = (int)Math.Min(maxW, maxH * ratio); width = Math.Max(8, width / 8 * 8);
        height = Math.Max(2, Math.Min(maxH, (int)(width / ratio)) / 2 * 2);
        var location = asset.Path;
        if (asset.Live?.Type is "Browser" or "NDI")
        {
            var data = Environment.GetEnvironmentVariable("NEWSPLAY_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewsPlay");
            var key = File.ReadAllText(Path.Combine(data, "engine.key")).Trim();
            location = "http://127.0.0.1:" + (Environment.GetEnvironmentVariable("NEWSPLAY_PORT") ?? "17890") + "/browser/stream/" + asset.Id + "?key=" + Uri.EscapeDataString(key);
        }
        using var media = new Media(vlc, location, asset.Kind == "Live" ? FromType.FromLocation : FromType.FromPath);
        activeDecodeBackend = Volatile.Read(ref hardwareDecoding).VlcBackend(Environment.GetEnvironmentVariable("NEWSPLAY_SOFTWARE_DECODE") == "1");
        media.AddOption(":avcodec-hw=" + activeDecodeBackend);
        if (production is not null) { media.AddOption(":deinterlace=-1"); media.AddOption(":deinterlace-mode=yadif2x"); }
        if (asset.Live?.Type is "Browser" or "NDI") media.AddOption(":demux=mjpeg");
        if (asset.Live is { } live)
        {
            if (live.Type is "USB / Webcam" or "DeckLink")
            {
                if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "plugins", "access", "libdshow_plugin.dll"))) throw new InvalidOperationException("DirectShow capture plugin is missing. Rebuild with the verified VLC capture package.");
                media.AddOption(":dshow-vdev=" + live.VideoDevice);
                media.AddOption(":dshow-adev=" + live.AudioDevice);
            }
            var buffering = live.Type == "Browser" ? 0 : live.Type == "NDI" ? 25 : live.CacheMs; media.AddOption(":network-caching=" + buffering); media.AddOption(":live-caching=" + buffering);
            media.AddOption(":screen-fps=25"); media.AddOption(":rtsp-tcp");
            if (live.Deinterlace) { media.AddOption(":deinterlace=1"); media.AddOption(":deinterlace-mode=yadif"); }
        }
        else { media.AddOption(":file-caching=250"); media.AddOption(":start-time=" + position.ToString("0.000", CultureInfo.InvariantCulture)); }
        if (!play) media.AddOption(":start-paused");
        if (asset.Kind == "Still") media.AddOption(":image-duration=-1");
        if (!player.Play(media)) throw new InvalidOperationException("Unable to start decoder: " + asset.Name);
        if (Environment.GetEnvironmentVariable("NEWSPLAY_TRACE") == "1") Console.WriteLine("Decoder: loaded " + asset.Name);
    }
    public void Play() { if (HasMedia) player.SetPause(false); }
    public void Pause() { if (HasMedia) player.SetPause(true); }
    public void Stop() { player.Stop(); audio.Pause(); production?.Clear(); HasMedia = false; ended = false; error = null; }
    public void Seek(double seconds) { if (HasMedia) { audio.Flush(); player.Time = (long)(seconds * 1000); } ended = false; }
    public void SetAudio(double gainDb, bool mute) => audio.Gain(gainDb, mute);
    public IReadOnlyList<AudioDeviceOption> AudioDevices() => PcmAudioOutput.Devices();
    // libvlc explicitly accepts NULL to select the default endpoint.
    public void SetAudioDevice(string id) { audio.Configure(id, audioRate, audioBits); audioDevice = id; }
    public void SetAudioFormat(int rate, int bits) { audio.Configure(audioDevice, rate, bits); audioRate = rate; audioBits = bits; player.SetAudioFormat("S16N", (uint)rate, 2); }
    public void SetAutoLevel(AudioLevelSettings settings) => audio.SetAutoLevel(settings);
    public void ConfigureAudio(string id, int rate, int bits) { audio.Configure(id, rate, bits); audioDevice = id; audioRate = rate; audioBits = bits; player.SetAudioFormat("S16N", (uint)rate, 2); }
    public void SetRate(double rate) { if (player.SetRate((float)rate) == -1) throw new InvalidOperationException("Decoder does not support this playback rate"); }
    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("NEWSPLAY_TRACE") == "1") Console.WriteLine("Decoder: stopping");
        player.Stop();
        if (Environment.GetEnvironmentVariable("NEWSPLAY_TRACE") == "1") Console.WriteLine("Decoder: disposing player");
        player.Dispose();
        production?.Dispose();
        audio.Dispose();
        if (Environment.GetEnvironmentVariable("NEWSPLAY_TRACE") == "1") Console.WriteLine("Decoder: disposing buffers");
        frameLock.Dispose(); view.Dispose(); map.Dispose(); scratchView.Dispose(); scratch.Dispose();
        rawPreview?.Dispose();
    }
}
