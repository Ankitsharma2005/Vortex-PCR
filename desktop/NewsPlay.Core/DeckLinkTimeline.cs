namespace NewsPlay.Core;

public sealed class DeckLinkTimeline
{
    private long videoClock, outputSamples;
    private double correctionSamples;
    public bool Ready { get; private set; }
    public void Reset() { Ready = false; videoClock = outputSamples = 0; correctionSamples = 0; }
    public void Anchor(long sourceClockMicroseconds, long displayTime, long timescale)
    {
        if (sourceClockMicroseconds <= 0 || timescale <= 0) return;
        var target = displayTime * (48000d / timescale);
        if (!Ready)
        {
            videoClock = sourceClockMicroseconds; outputSamples = (long)Math.Round(target); Ready = true;
            return;
        }
        // Display callbacks and frame-rate conversion jitter by part of a frame.
        // Track sustained hardware clock drift without moving PCM timestamps by
        // an entire video frame on every callback. Seek/load resets the anchor.
        var predicted = outputSamples + (sourceClockMicroseconds - videoClock) * .048 + correctionSamples;
        correctionSamples += Math.Clamp((target - predicted) / 128, -2, 2);
    }
    public long AudioTime(long pts, int delayMs = 0) => !Ready ? throw new InvalidOperationException("Video clock not anchored") : outputSamples + (long)Math.Round((pts - videoClock) * .048 + correctionSamples) + delayMs * 48L;
    public static long NextVideo(long sequence, long hardwareTime, long duration) => Math.Max(sequence, hardwareTime / duration + 1);
    // Trim samples already played or submitted, including after a video-clock correction.
    public static int SkipSamples(long packetTime, int offset, int remaining, long hardwareTime, long submittedUntil)
        => (int)Math.Min(remaining, Math.Max(0, Math.Max(hardwareTime, submittedUntil) - (packetTime + offset)));
}
public sealed record TimestampedPcm(byte[] Bytes, long PresentationMicroseconds);
