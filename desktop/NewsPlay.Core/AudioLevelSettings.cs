namespace NewsPlay.Core;

public sealed record AudioLevelSettings
{
    public bool Enabled { get; init; }
    public string Mode { get; init; } = "RMS";
    public double TargetLufs { get; init; } = -23;
    public double TargetDb { get; init; } = -18;
    public double CeilingDb { get; init; } = -1;
    public double MaxBoostDb { get; init; } = 12;
    public void Validate()
    {
        if (Mode is not ("RMS" or "LUFS") || !double.IsFinite(TargetLufs) || TargetLufs < -36 || TargetLufs > -6) throw new InvalidOperationException("Choose RMS or LUFS and a target from -36 to -6 LUFS.");
        if (!double.IsFinite(TargetDb) || TargetDb < -36 || TargetDb > -6 ||
            !double.IsFinite(CeilingDb) || CeilingDb < -6 || CeilingDb > 0 ||
            !double.IsFinite(MaxBoostDb) || MaxBoostDb < 0 || MaxBoostDb > 24)
            throw new InvalidOperationException("Target: -36 to -6 dBFS; ceiling: -6 to 0 dBFS; maximum boost: 0 to 24 dB.");
    }
}

// Stereo-linked RMS leveler. No lookahead/buffering; ceiling is sample peak, not true peak.
public sealed class AudioLeveler
{
    private readonly object gate = new();
    private AudioLevelSettings settings = new();
    private double current = 1;
    private bool initialized;
    private LufsMeter lufs = new();
    public void ValidateRate(int rate) { lock (gate) if (settings.Enabled && settings.Mode == "LUFS" && rate != 48000) throw new InvalidOperationException("LUFS mode requires 48000 Hz PCM."); }
    public void Configure(AudioLevelSettings value) { value.Validate(); lock (gate) { settings = value with { }; initialized = false; current = 1; lufs = new(); } }
    public void Reset() { lock (gate) { initialized = false; current = 1; lufs = new(); } }
    public float[] Process(short[] input, int rate, float manualGain)
    {
        lock (gate)
        {
            var result = new float[input.Length];
            double energy = 0;
            foreach (var s in input) energy += (s / 32768.0) * (s / 32768.0);
            var rms = Math.Sqrt(energy / Math.Max(1, input.Length));
            var loudness = settings.Enabled && settings.Mode == "LUFS" ? lufs.Measure(input) : double.NegativeInfinity;
            var desired = current;
            if (settings.Enabled && settings.Mode == "RMS" && rms > Math.Pow(10, -50.0 / 20))
            {
                desired = Math.Min(Math.Pow(10, settings.MaxBoostDb / 20), Math.Pow(10, settings.TargetDb / 20) / rms);
                if (!initialized) { current = desired; initialized = true; }
            }
            if (settings.Enabled && settings.Mode == "LUFS" && loudness > -70 && rms > Math.Pow(10, -50.0 / 20))
                desired = Math.Min(Math.Pow(10, settings.MaxBoostDb / 20), Math.Pow(10, (settings.TargetLufs - loudness) / 20));
            var coefficient = Math.Exp(-1.0 / (Math.Max(1, rate) * (desired < current ? .05 : 1.5)));
            var ceiling = settings.Enabled ? Math.Pow(10, settings.CeilingDb / 20) : 1;
            for (var i = 0; i < input.Length; i += 2)
            {
                if (settings.Enabled) current = desired + coefficient * (current - desired);
                for (var ch = 0; ch < 2 && i + ch < input.Length; ch++)
                    result[i + ch] = (float)Math.Clamp(input[i + ch] / 32768.0 * manualGain * (settings.Enabled ? current : 1), -ceiling, ceiling);
            }
            return result;
        }
    }
}
