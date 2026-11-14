using NAudio.Wave;
using NewsPlay.Engine;

long clock = 920000;
var audio = new TimestampedAudioBuffer(new WaveFormat(48000, 16, 2), () => clock, () => 80000);
byte[] packet = Enumerable.Repeat((byte)37, 1920).ToArray();
byte[] target = new byte[1920];
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
audio.Add(packet, 1000000); audio.Read(target, 0, target.Length);
Check(target.SequenceEqual(packet), "On-time stereo packet");
audio.Add(packet, 1100000); audio.Read(target, 0, target.Length);
Check(target.All(b => b == 0), "Future audio is held");
clock = 1020000; audio.Read(target, 0, target.Length);
Check(target.SequenceEqual(packet), "Held audio reaches its timestamp");
audio.Add(packet, 1000000); audio.Read(target, 0, target.Length);
Check(target.All(b => b == 0), "Expired audio is discarded");
audio.Add(packet, 1100000); audio.Clear(); audio.Read(target, 0, target.Length);
Check(target.All(b => b == 0), "Seek/flush clears queued audio");
audio.SetDelay(100000); clock = 1120000; audio.Add(packet, 1100000); audio.Read(target, 0, target.Length);
Check(target.SequenceEqual(packet), "Video output delay shifts audio presentation");
audio.Clear(); audio.SetDelay(0);
for (long frame = 0; frame < 720000; frame++)
{
    var pts = 2000000 + frame * 10000;
    clock = pts - 80000; audio.Add(packet, pts); audio.Read(target, 0, target.Length);
    if (!target.SequenceEqual(packet)) throw new Exception("Simulated two-hour clock timeline failed");
}
Console.WriteLine("PASS two-hour simulated timestamp timeline (not a hardware soak test)");
long latency = 20000;
clock = 980000;
var measured = new TimestampedAudioBuffer(new WaveFormat(48000, 16, 2), () => clock, () => latency);
measured.Add(packet, 1000000); measured.Read(target, 0, target.Length);
Check(target.SequenceEqual(packet), "Measured 20ms device queue");
latency = 140000; clock = 960000;
measured.Add(packet, 1100000); measured.Read(target, 0, target.Length);
Check(target.SequenceEqual(packet), "Changed USB device queue uses measured latency");
Check(Math.Abs(measured.SubmittedSeconds - .02) < .000001, "Submitted render duration counts hardware-format-independent source frames");
measured.Clear();
Check(Math.Abs(measured.SubmittedSeconds - .02) < .000001, "Flush preserves running hardware clock epoch");
measured.ResetRenderer();
Check(measured.SubmittedSeconds == 0, "Stopped renderer resets submitted clock epoch");
latency = 0; clock = 2000000; measured.Add(packet, clock); measured.Read(target, 0, target.Length);
Check(target.SequenceEqual(packet), "First post-seek packet has no stale device delay");
short[] Tone(double amplitude) => Enumerable.Range(0, 96000).Select(i => (short)(32767 * amplitude * Math.Sin(2 * Math.PI * 1000 * (i / 2) / 48000))).ToArray();
var meter = new NewsPlay.Core.LufsMeter();
Check(double.IsNegativeInfinity(meter.Measure(new short[96000])), "LUFS digital silence");
var reading = meter.Measure(Tone(.1));
Check(Math.Abs(reading + 20) < .2, "Stereo 1 kHz calibration near -20 LUFS");
var normalizer = new NewsPlay.Core.AudioLeveler();
normalizer.Configure(new() { Enabled = true, Mode = "LUFS", TargetLufs = -23, MaxBoostDb = 24 });
float[] leveled = [];
for (var i = 0; i < 20; i++) leveled = normalizer.Process(Tone(.02), 48000, 1);
var measuredOutput = new NewsPlay.Core.LufsMeter().Measure(leveled.Select(x => (short)(x * 32767)).ToArray());
Check(Math.Abs(measuredOutput + 23) < .3, "LUFS leveling converges to -23 on steady tone");
var rejected = false; try { normalizer.ValidateRate(44100); } catch (InvalidOperationException) { rejected = true; }
Check(rejected, "LUFS rejects unsupported sample rate");
clock = 1000000;
var advance = new TimestampedAudioBuffer(new WaveFormat(48000, 16, 2), () => clock);
advance.SetDelay(-100000); advance.Add(packet, 1100000); advance.Read(target, 0, target.Length);
Check(target.SequenceEqual(packet), "Negative calibration trim advances audio");
var delayed = new TimestampedAudioBuffer(new WaveFormat(48000, 16, 2), () => clock);
delayed.SetDelay(100000); delayed.Add(packet, 1000000); delayed.Read(target, 0, target.Length);
Check(target.All(x => x == 0) && delayed.HeldFrames == 480, "Early audio is held for positive video delay");
clock += 100000; delayed.Read(target, 0, target.Length);
Check(target.SequenceEqual(packet), "Held audio releases at delayed video time");
var outputMeter = new RenderedAudioMeter();
byte[] peakPcm = new byte[1920];
for (var i = 0; i < peakPcm.Length; i += 4) { peakPcm[i + 1] = 64; peakPcm[i + 3] = 32; }
outputMeter.Submit(peakPcm, 0, peakPcm.Length, 48000, 16, 2000000);
Check(outputMeter.Levels(1900000) == (0d, 0d), "Meter does not show audio before queued playback time");
Check(outputMeter.Levels(2000000) == (.5d, .25d), "Output meter preserves stereo peaks at playback time");
Check(outputMeter.Levels(2200000) == (0d, 0d), "Stale output meter decays to zero");
outputMeter.Clear(); Check(outputMeter.Levels(2000000) == (0d, 0d), "Meter flush clears pending peaks");
long jitterClock = 10000000;
var continuous = new TimestampedAudioBuffer(new WaveFormat(48000, 16, 2), () => jitterClock);
var tonePacket = new byte[1920]; var toneOut = new byte[1920];
for (int block = 0; block < 1000; block++)
{
    for (int frame = 0; frame < 480; frame++)
    {
        var sample = (short)(16000 * Math.Sin((block * 480L + frame) * 2 * Math.PI * 1003 / 48000));
        for (int channel = 0; channel < 2; channel++) { var at = frame * 4 + channel * 2; tonePacket[at] = (byte)sample; tonePacket[at + 1] = (byte)(sample >> 8); }
    }
    var pts = 10000000 + block * 10000L;
    jitterClock = pts + (block % 2 == 0 ? -2500 : 2500);
    continuous.Add(tonePacket.ToArray(), pts); continuous.Read(toneOut, 0, toneOut.Length);
    if (!toneOut.SequenceEqual(tonePacket)) throw new Exception("Device clock jitter spliced the continuous stereo waveform");
}
Check(continuous.TrimmedFrames == 0 && continuous.HeldFrames == 0 && continuous.StarvedFrames == 0, "10 seconds of continuous PCM survives ±2.5 ms clock jitter without sample cuts or inserted silence");
continuous.Read(toneOut, 0, toneOut.Length);
Check(continuous.StarvedFrames == 480 && toneOut.All(b => b == 0), "Source starvation is reported separately from intentional timestamp holds");
