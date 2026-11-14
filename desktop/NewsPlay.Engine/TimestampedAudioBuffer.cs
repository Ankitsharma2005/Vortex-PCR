using NAudio.Wave;

namespace NewsPlay.Engine;

// Reconcile each WASAPI read with the decoder clock rather than accumulating
// an indefinitely growing FIFO when the two devices run at different rates.
public sealed class TimestampedAudioBuffer(WaveFormat format, Func<long> clock, Func<long>? renderLatency = null) : IWaveProvider
{
    private readonly object gate = new();
    public RenderedAudioMeter Meter { get; } = new();
    private readonly Queue<(byte[] Data, long Pts)> packets = new();
    private int offset, queuedBytes;
    private long delayUs, submittedBytes, trimmedFrames, heldFrames, starvedFrames;
    private bool receivedSamples;
    public long TrimmedFrames => Interlocked.Read(ref trimmedFrames);
    public long HeldFrames => Interlocked.Read(ref heldFrames);
    public long StarvedFrames => Interlocked.Read(ref starvedFrames);
    public double SubmittedSeconds => Interlocked.Read(ref submittedBytes) / (double)format.AverageBytesPerSecond;
    public WaveFormat WaveFormat => format;
    public void SetDelay(long microseconds) => Interlocked.Exchange(ref delayUs, Math.Clamp(microseconds, -500000, 1000000));
    public void Clear() { lock (gate) { packets.Clear(); Meter.Clear(); offset = queuedBytes = 0; receivedSamples = false; } }
    // Call only after the renderer has stopped/reset its hardware clock.
    public void ResetRenderer() { Clear(); Interlocked.Exchange(ref submittedBytes, 0); }
    public void Add(byte[] data, long pts)
    {
        if (data.Length == 0 || data.Length % format.BlockAlign != 0) return;
        lock (gate)
        {
            packets.Enqueue((data, pts > 0 ? pts : clock())); queuedBytes += data.Length;
            receivedSamples = true;
            while (queuedBytes > format.AverageBytesPerSecond * 2 && packets.Count > 1)
            { queuedBytes -= packets.Dequeue().Data.Length; offset = 0; }
        }
    }
    public int Read(byte[] buffer, int destination, int count)
    {
        Array.Clear(buffer, destination, count);
        var frames = count / format.BlockAlign;
        // Use the render device position to measure queued audio, not a fixed latency guess.
        var audibleAt = clock() + (renderLatency?.Invoke() ?? 0);
        var now = audibleAt - Interlocked.Read(ref delayUs);
        lock (gate)
        {
            var written = 0;
            while (written < frames && packets.TryPeek(out var packet))
            {
                var due = packet.Pts + offset * 1000000L / format.AverageBytesPerSecond;
                var wanted = now + written * 1000000L / format.SampleRate;
                var difference = (due - wanted) * format.SampleRate / 1000000L;
                // Device-position polling and callback dispatch can jitter several
                // milliseconds. Do not splice a continuous waveform for that jitter.
                var tolerance = format.SampleRate / 200; // 5 ms; does not permit growing FIFO drift.
                if (difference > tolerance)
                { var hold = (int)Math.Min(frames - written, difference); Interlocked.Add(ref heldFrames, hold); written += hold; continue; }
                if (difference < -tolerance)
                    { var skip = (int)Math.Min((packet.Data.Length - offset) / format.BlockAlign, -difference); Interlocked.Add(ref trimmedFrames, skip); offset += skip * format.BlockAlign; }
                var copy = Math.Min((frames - written) * format.BlockAlign, packet.Data.Length - offset);
                Buffer.BlockCopy(packet.Data, offset, buffer, destination + written * format.BlockAlign, copy);
                written += copy / format.BlockAlign; offset += copy;
                if (offset == packet.Data.Length) { packets.Dequeue(); queuedBytes -= packet.Data.Length; offset = 0; }
            }
            if (receivedSamples && written < frames) Interlocked.Add(ref starvedFrames, frames - written);
        }
        Meter.Submit(buffer, destination, count, format.SampleRate, format.BitsPerSample, audibleAt);
        Interlocked.Add(ref submittedBytes, count);
        return count;
    }
}
