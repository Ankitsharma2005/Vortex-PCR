using LibVLCSharp.Shared;
using NewsPlay.Core;

namespace NewsPlay.Engine;

public sealed class LivePreviewService(LibVLC vlc, string instance) : IDisposable
{
    private HardwareDecodeSettings hardwareDecoding = new();
    public void ConfigureHardwareDecoding(HardwareDecodeSettings settings)
    {
        settings.Validate(); hardwareDecoding = settings;
        foreach (var input in inputs.Values) input.Decoder.ConfigureHardwareDecoding(settings);
    }
    private sealed class Input
    {
        public required MediaAsset Asset;
        public required VlcDecoder Decoder;
        public int Slot;
        public long Frames;
        public DateTimeOffset LastFrame = DateTimeOffset.UtcNow, Sample = DateTimeOffset.UtcNow, Retry = DateTimeOffset.UtcNow;
        public double Fps;
        public int Retries;
        public bool Muted = true;
        public double Gain;
        public string? Error;
    }
    private readonly Dictionary<Guid, Input> inputs = [];
    public void Start(MediaAsset asset)
    {
        PlayoutController.ValidateLive(asset);
        if (inputs.ContainsKey(asset.Id)) return;
        if (inputs.Count >= 8) throw new InvalidOperationException("Maximum eight monitored live inputs");
        var slot = Enumerable.Range(10, 8).First(s => inputs.Values.All(i => i.Slot != s));
        var input = new Input { Asset = Json.Copy(asset), Decoder = new VlcDecoder(vlc, instance, slot), Slot = slot };
        input.Decoder.ConfigureHardwareDecoding(hardwareDecoding);
        try { input.Decoder.SetAudio(0, true); input.Decoder.Load(asset, 0, true); input.Decoder.SetAudio(0, true); inputs.Add(asset.Id, input); }
        catch { input.Decoder.Dispose(); throw; }
    }
    public void Stop(Guid id) { if (inputs.Remove(id, out var input)) input.Decoder.Dispose(); }
    public void Sync(EngineState state)
    {
        foreach (var input in inputs.Values.ToArray())
            if (!state.Media.Any(m => m.Id == input.Asset.Id && m.LiveReleaseRevision == input.Asset.LiveReleaseRevision)) Stop(input.Asset.Id);
    }
    public void Audio(Guid id, bool mute, double gain)
    {
        PlayoutController.Number(gain, -60, 6);
        if (!inputs.TryGetValue(id, out var input)) throw new InvalidOperationException("Start preview first");
        input.Muted = mute; input.Gain = gain; input.Decoder.SetAudio(gain, mute);
    }
    public void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var input in inputs.Values)
        {
            var frames = input.Decoder.Frames;
            if (frames != input.Frames)
            {
                input.Fps = (frames - input.Frames) / Math.Max(.001, (now - input.Sample).TotalSeconds);
                input.LastFrame = now; input.Error = null;
            }
            input.Frames = frames; input.Sample = now;
            if ((input.Decoder.Error is not null || input.Decoder.Ended || (now - input.LastFrame).TotalSeconds > input.Asset.Live!.SignalTimeoutSeconds) && (now - input.Retry).TotalSeconds >= 5)
            {
                input.Retry = now; input.Retries++; input.Error = "Reconnecting — no fresh video";
                try { input.Decoder.Load(input.Asset, 0, true); input.Decoder.SetAudio(input.Gain, input.Muted); }
                catch { input.Error = "Reconnect failed"; }
            }
        }
    }
    public object Status(EngineState state) => inputs.Values.Select(i => new
    {
        id = i.Asset.Id, name = i.Asset.Name, slot = i.Slot,
        signal = i.Decoder.Error is null && !i.Decoder.Ended && i.Frames > 0 && (DateTimeOffset.UtcNow - i.LastFrame).TotalSeconds < i.Asset.Live!.SignalTimeoutSeconds ? "Signal OK" : "No Signal",
        width = i.Decoder.SourceWidth, height = i.Decoder.SourceHeight, observedFps = Math.Round(i.Fps, 1), cacheMs = i.Asset.Live!.CacheMs,
        retries = i.Retries, muted = i.Muted, message = i.Error,
        audioTrack = i.Decoder.AudioTrack,
        onAir = state.Channels.Where(c => c.Status == "Playing" && (c.LiveInputId == i.Asset.Id || (c.LiveInputId is null && state.Rundowns.Single(p => p.Id == c.RundownId).Items.Any(r => r.Id == c.CurrentId && r.MediaId == i.Asset.Id)))).Select(c => c.Id).ToArray()
    }).ToArray();
    public void Dispose() { foreach (var input in inputs.Values) input.Decoder.Dispose(); inputs.Clear(); }
}
