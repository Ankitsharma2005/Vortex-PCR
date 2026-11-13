using NAudio.Wave;
using NewsPlay.Engine;

long clock = 920000;
var audio = new TimestampedAudioBuffer(new WaveFormat(48000, 16, 2), () => clock);
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
