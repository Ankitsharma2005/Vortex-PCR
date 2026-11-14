using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NewsPlay.Core;

namespace NewsPlay.Engine;

// A separate WASAPI render client for each program/preview decoder.
public sealed class PcmAudioOutput : IDisposable
{
    private readonly object gate = new();
    private MMDevice? device;
    private WasapiOut? output;
    private TimestampedAudioBuffer? buffer;
    public Func<long> Clock { get; set; } = () => Environment.TickCount64 * 1000;
    private long videoDelay, measuredQueue;
    public object SyncStatus => new { renderBufferMs = 80, videoDelayMs = Interlocked.Read(ref videoDelay) / 1000d, renderQueueMs = Interlocked.Read(ref measuredQueue) / 1000d, trimmedFrames = buffer?.TrimmedFrames ?? 0, heldFrames = buffer?.HeldFrames ?? 0, starvedFrames = buffer?.StarvedFrames ?? 0, callbackErrors = Interlocked.Read(ref callbackErrors) };
    private long callbackErrors;
    public void SetVideoDelay(long delay) { Interlocked.Exchange(ref videoDelay, delay); buffer?.SetDelay(delay); }
    private string endpoint = "";
    private int rate = 48000, bits = 16;
    private volatile float gain = 1;
    private readonly AudioLeveler leveler = new();
    public void SetAutoLevel(AudioLevelSettings settings) { if (settings.Enabled && settings.Mode == "LUFS" && rate != 48000) throw new InvalidOperationException("Set PCM to 48000 Hz before enabling LUFS mode."); leveler.Configure(settings); }
    private volatile float left, right;
    private long lastSamples;
    public event Action<byte[]>? HardwareSamples;
    public event Action<TimestampedPcm>? TimestampedSamples;
    public event Action? HardwareReset;
    public double Left => buffer?.Meter.Levels(Clock()).Left ?? 0;
    public double Right => buffer?.Meter.Levels(Clock()).Right ?? 0;
    public static IReadOnlyList<AudioDeviceOption> Devices()
    {
        using var enumerator = new MMDeviceEnumerator(); var result = new List<AudioDeviceOption>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) { using (endpoint) result.Add(new(endpoint.ID, endpoint.FriendlyName)); }
        return result;
    }
    public void Configure(string id, int sampleRate, int bitDepth)
    {
        leveler.ValidateRate(sampleRate);
        if (sampleRate is not (32000 or 44100 or 48000 or 88200 or 96000 or 176400 or 192000) || bitDepth is not (16 or 24 or 32)) throw new InvalidOperationException("Choose a supported PCM sample rate and bit depth");
        using var enumerator = new MMDeviceEnumerator();
        using var candidate = id.Length == 0 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
        if (candidate.State != DeviceState.Active) throw new InvalidOperationException("Audio endpoint is unavailable");
        lock (gate) { Release(); endpoint = id; rate = sampleRate; bits = bitDepth; }
    }
    public void Start()
    {
        lock (gate)
        {
            if (output is not null) { output.Play(); return; }
            using var enumerator = new MMDeviceEnumerator();
            device = endpoint.Length == 0 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(endpoint);
            buffer = new TimestampedAudioBuffer(new WaveFormat(rate, bits, 2), () => Clock(), RenderLatency); buffer.SetDelay(Interlocked.Read(ref videoDelay));
            // Restore the stable shared-mode buffer. RenderLatency still measures
            // queued samples, so stability does not depend on a fixed A/V offset.
            try { output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80); output.Init(buffer); output.Play(); }
            catch { Release(); throw; }
        }
    }
    private long RenderLatency()
    {
        var renderer = output; var source = buffer;
        if (renderer is null || source is null) return 0;
        var playedSeconds = renderer.GetPosition() / (double)renderer.OutputWaveFormat.AverageBytesPerSecond;
        var latency = (long)(Math.Clamp(source.SubmittedSeconds - playedSeconds, 0, 2) * 1000000); Interlocked.Exchange(ref measuredQueue, latency); return latency;
    }
    public void Samples(IntPtr pointer, uint frames, long pts = 0)
    {
        // Never allow managed exceptions to cross a native libVLC callback.
        try
        {
            if (frames == 0 || frames > 192000) return;
            // VLC 3.0 amem emits signed 16-bit PCM even if its public API accepts FL32.
            // Read exactly two Int16 samples per frame; treating this as float overreads native memory.
            var samples = new short[checked((int)frames * 2)]; Marshal.Copy(pointer, samples, 0, samples.Length);
            var processed = leveler.Process(samples, rate, gain);
            var size = bits / 8; var bytes = new byte[samples.Length * size]; float l = 0, r = 0;
            for (var i = 0; i < samples.Length; i++)
            {
                var sample = processed[i];
                if (i % 2 == 0) l = Math.Max(l, Math.Abs(sample)); else r = Math.Max(r, Math.Abs(sample));
                var value = bits == 16 ? (int)(sample * 32767) : bits == 24 ? (int)(sample * 8388607) : (int)((double)sample * int.MaxValue);
                for (var b = 0; b < size; b++) bytes[i * size + b] = (byte)(value >> (8 * b));
            }
            left = l; right = r; Interlocked.Exchange(ref lastSamples, Environment.TickCount64);
            if (HardwareSamples is not null || TimestampedSamples is not null)
            {
                var pcm = new byte[samples.Length * 2];
                for (var i = 0; i < samples.Length; i++) { var value = (short)Math.Clamp((int)(processed[i] * 32768), short.MinValue, short.MaxValue); pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8); }
                HardwareSamples?.Invoke(pcm); TimestampedSamples?.Invoke(new(pcm, pts));
            }
            lock (gate) { buffer?.Add(bytes, pts); }
        }
        catch { Interlocked.Increment(ref callbackErrors); }
    }
    public void Gain(double db, bool mute) { gain = mute ? 0 : (float)Math.Pow(10, db / 20); if (mute) { left = right = 0; Flush(); } }
    public void Pause() { HardwareReset?.Invoke(); lock (gate) { output?.Stop(); buffer?.ResetRenderer(); Interlocked.Exchange(ref measuredQueue, 0); left = right = 0; } }
    public void Resume() { lock (gate) output?.Play(); }
    public void Flush()
    {
        leveler.Reset(); HardwareReset?.Invoke();
        lock (gate)
        {
            var playing = output?.PlaybackState == PlaybackState.Playing;
            output?.Stop(); buffer?.ResetRenderer(); Interlocked.Exchange(ref measuredQueue, 0); left = right = 0;
            if (playing) output?.Play();
        }
    }
    private void Release() { output?.Dispose(); output = null; device?.Dispose(); device = null; buffer = null; left = right = 0; }
    public void Dispose() { lock (gate) Release(); }
}
