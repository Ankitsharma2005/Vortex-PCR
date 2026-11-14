namespace NewsPlay.Engine;

public sealed class RenderedAudioMeter
{
    private readonly object gate = new();
    private readonly Queue<(long Due, double L, double R)> peaks = new();
    private double left, right;
    private long lastDue;
    public void Clear() { lock (gate) { peaks.Clear(); left = right = 0; lastDue = 0; } }
    public void Submit(byte[] pcm, int offset, int count, int rate, int bits, long due)
    {
        var divisor = Math.Pow(2, bits - 1);
        var bytes = bits / 8; var align = bytes * 2; var frames = count / align;
        var window = Math.Max(1, rate / 100);
        lock (gate)
        {
            for (var start = 0; start < frames; start += window)
            {
                double l = 0, r = 0;
                for (var frame = start; frame < Math.Min(frames, start + window); frame++)
                    for (var channel = 0; channel < 2; channel++)
                    {
                        var at = offset + frame * align + channel * bytes;
                        int value = 0;
                        for (var b = 0; b < bytes; b++) value |= pcm[at + b] << (b * 8);
                        value = (value << (32 - bits)) >> (32 - bits);
                        var amplitude = Math.Abs(value / divisor);
                        if (channel == 0) l = Math.Max(l, amplitude); else r = Math.Max(r, amplitude);
                    }
                peaks.Enqueue((due + start * 1000000L / rate, l, r));
            }
            while (peaks.Count > 400) peaks.Dequeue();
        }
    }
    public (double Left, double Right) Levels(long now)
    {
        lock (gate)
        {
            while (peaks.TryPeek(out var peak) && peak.Due <= now)
            { peaks.Dequeue(); left = peak.L; right = peak.R; lastDue = peak.Due; }
            return now - lastDue > 100000 ? (0, 0) : (left, right);
        }
    }
}
