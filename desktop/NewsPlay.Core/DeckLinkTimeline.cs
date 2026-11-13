namespace NewsPlay.Core;

public sealed class DeckLinkTimeline
{
    private long videoClock, outputSamples;
    public bool Ready { get; private set; }
    public void Anchor(long sourceClockMicroseconds, long displayTime, long timescale)
    {
        if (sourceClockMicroseconds <= 0 || timescale <= 0) return;
        videoClock = sourceClockMicroseconds; outputSamples = (long)Math.Round(displayTime * (48000d / timescale)); Ready = true;
    }
    public long AudioTime(long pts, int delayMs = 0) => !Ready ? throw new InvalidOperationException("Video clock not anchored") : outputSamples + (long)Math.Round((pts - videoClock) * .048) + delayMs * 48L;
    public static long NextVideo(long sequence, long hardwareTime, long duration) => Math.Max(sequence, hardwareTime / duration + 1);
}
public sealed record TimestampedPcm(byte[] Bytes, long PresentationMicroseconds);
