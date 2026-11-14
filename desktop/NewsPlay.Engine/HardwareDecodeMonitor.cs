using LibVLCSharp.Shared;

namespace NewsPlay.Engine;

// LibVLC's managed event has no player identifier. Report engine-wide evidence,
// never mislabel a global log message as proof for a particular program port.
public sealed class HardwareDecodeMonitor : IDisposable
{
    private readonly LibVLC vlc;
    private readonly object gate = new();
    private string? lastBackend;
    private DateTimeOffset? lastObserved;
    private long observations;
    public HardwareDecodeMonitor(LibVLC vlc) { this.vlc = vlc; vlc.Log += Observe; }
    private void Observe(object? sender, LogEventArgs e)
    {
        if (!string.Equals(e.Module, "avcodec", StringComparison.OrdinalIgnoreCase) || !e.Message.StartsWith("Using ", StringComparison.Ordinal) || !e.Message.EndsWith(" for hardware decoding", StringComparison.Ordinal)) return;
        lock (gate) { lastBackend = e.Message[6..^22]; lastObserved = DateTimeOffset.UtcNow; observations++; }
    }
    public object Status { get { lock (gate) return new { scope = "engine-wide historical observations, not per-player activity", lastBackend, lastObservedUtc = lastObserved, observations }; } }
    public void Dispose() => vlc.Log -= Observe;
}
