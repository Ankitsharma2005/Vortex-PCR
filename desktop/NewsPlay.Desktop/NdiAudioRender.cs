using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NewsPlay.Desktop;

internal sealed record NdiAudioRoute(int Port, string Device, int Rate, int Bits, float Gain);
internal sealed class NdiAudioRender : IDisposable
{
    public NdiAudioRoute Route { get; private set; }
    public int InputRate { get; }
    private readonly MMDevice device;
    private readonly WasapiOut output;
    private readonly BufferedWaveProvider buffer;
    private readonly VolumeSampleProvider volume;
    private readonly MediaFoundationResampler resampler;
    public NdiAudioRender(NdiAudioRoute route, int rate)
    {
        Route = route; InputRate = rate;
        using var enumerator = new MMDeviceEnumerator(); device = route.Device.Length == 0 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(route.Device);
        buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)) { BufferDuration = TimeSpan.FromMilliseconds(250), DiscardOnBufferOverflow = true };
        volume = new VolumeSampleProvider(buffer.ToSampleProvider()) { Volume = route.Gain };
        resampler = new MediaFoundationResampler(volume.ToWaveProvider(), new WaveFormat(route.Rate, route.Bits, 2)) { ResamplerQuality = 60 };
        output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
        try { output.Init(resampler); output.Play(); } catch { output.Dispose(); resampler.Dispose(); device.Dispose(); throw; }
    }
    public void Push(NdiAudioRoute route, byte[] bytes)
    {
        Route = route; volume.Volume = route.Gain;
        // Allow normal packet bursts. ClearBuffer discarded whole speech packets
        // at only 100 ms backlog; discard only excess oldest audio after a real stall.
        var limit = buffer.WaveFormat.AverageBytesPerSecond / 5;
        limit -= limit % buffer.WaveFormat.BlockAlign;
        var offset = Math.Max(0, bytes.Length - limit);
        var excess = Math.Max(0, buffer.BufferedBytes + bytes.Length - offset - limit);
        if (excess > 0) { var discard = new byte[excess]; buffer.Read(discard, 0, discard.Length); }
        buffer.AddSamples(bytes, offset, bytes.Length - offset);
    }
    public void Silence() { volume.Volume = 0; buffer.ClearBuffer(); }
    public void Dispose() { output.Dispose(); resampler.Dispose(); device.Dispose(); }
}
