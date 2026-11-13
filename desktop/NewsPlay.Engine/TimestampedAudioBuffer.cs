using NAudio.Wave;

namespace NewsPlay.Engine;

// Reconcile each WASAPI read with the decoder clock rather than accumulating
// an indefinitely growing FIFO when the two devices run at different rates.
public sealed class TimestampedAudioBuffer(WaveFormat format, Func<long> clock) : IWaveProvider
{
    private readonly object gate = new();
    private readonly Queue<(byte[] Data, long Pts)> packets = new();
    private int offset, queuedBytes;
    private long delayUs;
    public WaveFormat WaveFormat => format;
    public void SetDelay(long microseconds) => Interlocked.Exchange(ref delayUs, Math.Clamp(microseconds, 0, 1000000));
    public void Clear() { lock (gate) { packets.Clear(); offset = queuedBytes = 0; } }
    public void Add(byte[] data, long pts)
    {
        if (data.Length % format.BlockAlign != 0) return;
        lock (gate)
        {
            packets.Enqueue((data, pts > 0 ? pts : clock())); queuedBytes += data.Length;
            while (queuedBytes > format.AverageBytesPerSecond * 2 && packets.Count > 1)
            { queuedBytes -= packets.Dequeue().Data.Length; offset = 0; }
        }
    }
    public int Read(byte[] buffer, int destination, int count)
    {
        Array.Clear(buffer, destination, count);
        var frames = count / format.BlockAlign;
        // WASAPI shared renderer is configured with an 80 ms buffer. This is
        // a latency estimate, not a measurement of the physical DAC delay.
        var now = clock() + 80000 - Interlocked.Read(ref delayUs);
        lock (gate)
        {
            var written = 0;
            while (written < frames && packets.TryPeek(out var packet))
            {
                var due = packet.Pts + offset * 1000000L / format.AverageBytesPerSecond;
                var wanted = now + written * 1000000L / format.SampleRate;
                var difference = (due - wanted) * format.SampleRate / 1000000L;
                // One millisecond deadband avoids correcting clock-query jitter.
                if (difference > format.SampleRate / 1000)
                { written += (int)Math.Min(frames - written, difference); continue; }
                if (difference < -format.SampleRate / 1000)
                    offset += (int)Math.Min((packet.Data.Length - offset) / format.BlockAlign, -difference) * format.BlockAlign;
                var copy = Math.Min((frames - written) * format.BlockAlign, packet.Data.Length - offset);
                Buffer.BlockCopy(packet.Data, offset, buffer, destination + written * format.BlockAlign, copy);
                written += copy / format.BlockAlign; offset += copy;
                if (offset == packet.Data.Length) { packets.Dequeue(); queuedBytes -= packet.Data.Length; offset = 0; }
            }
        }
        return count;
    }
}
