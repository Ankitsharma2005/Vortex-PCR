using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using NewsPlay.Core;

namespace NewsPlay.Engine;

public sealed class StreamingService(VlcDecoder one, VlcDecoder two) : IDisposable
{
    private readonly Dictionary<Guid, Session> sessions = [];
    private readonly object gate = new();
    public StreamStatus[] Status() { lock (gate) return sessions.Values.Select(s => s.Status).ToArray(); }
    public void Start(StreamProfile profile)
    {
        profile.Validate();
        if (profile.TlsCaFile.Length > 0 && !File.Exists(profile.TlsCaFile)) throw new InvalidOperationException("Selected TLS CA certificate file is unavailable on this PC");
        var decoder = profile.Port == 1 ? one : two;
        if (profile.Width > 1920 && decoder.ProductionFormat is null) throw new InvalidOperationException("Select and validate a project production format before UHD streaming");
        if (profile.Width > 1920)
        {
            var compatibility = VideoFormatProbe.Check(new ProjectVideoFormat("Streaming output", profile.Width, profile.Height, profile.Fps));
            if (!compatibility.Supported) throw new InvalidOperationException("UHD streaming conversion is not enabled on this PC: " + compatibility.Message);
        }
        if (decoder.AudioSampleRate != 48000) throw new InvalidOperationException("Set program audio to 48000 Hz before streaming");
        var executable = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("Bundled FFmpeg encoder is missing");
        lock (gate)
        {
            if (sessions.ContainsKey(profile.Id)) throw new InvalidOperationException("Stop this destination before changing it");
            if (sessions.Count >= 4) throw new InvalidOperationException("Maximum four simultaneous stream destinations");
            HardwareEncoderProbe.Check(executable, profile);
            var session = new Session(Json.Copy(profile), decoder, executable); sessions.Add(profile.Id, session);
        }
    }
    public async Task Stop(Guid id) { Session? session; lock (gate) sessions.TryGetValue(id, out session); if (session is null) return; await session.Stop(); lock (gate) sessions.Remove(id); }
    public void Dispose() { foreach (var status in Status()) Stop(status.Id).GetAwaiter().GetResult(); }
    private sealed class Session
    {
        private readonly StreamProfile profile;
        private readonly VlcDecoder decoder;
        private readonly CancellationTokenSource cancel = new();
        private readonly ConcurrentQueue<byte[]> audio = new();
        private readonly Process process;
        private readonly NamedPipeServerStream videoPipe, audioPipe;
        private readonly Task worker;
        private volatile string state = "Connecting", message = "Waiting for encoder / destination";
        private long frames, droppedFrames;
        private int audioResetVersion;
        private readonly int inputWidth, inputHeight;
        public StreamStatus Status => new(profile.Id, profile.Name, profile.Port, state, Interlocked.Read(ref frames), message, Interlocked.Read(ref droppedFrames), $"{profile.Width}x{profile.Height}p{profile.Fps} · {profile.VideoCodec} · production {inputWidth}x{inputHeight} · source {decoder.SourceWidth}x{decoder.SourceHeight}" + (inputWidth < profile.Width || inputHeight < profile.Height || decoder.SourceWidth < profile.Width || decoder.SourceHeight < profile.Height ? " · upscaled / source below output raster" : ""));
        public Session(StreamProfile profile, VlcDecoder decoder, string executable)
        {
            this.profile = profile; this.decoder = decoder;
            inputWidth = decoder.ProductionFormat?.Width ?? 960; inputHeight = decoder.ProductionFormat?.Height ?? 540;
            var token = "NewsPlayStream" + Guid.NewGuid().ToString("N");
            videoPipe = new(token + "v", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            audioPipe = new(token + "a", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in StreamingArguments.Build(profile, inputWidth, inputHeight, @"\\.\pipe\" + token + "v", @"\\.\pipe\" + token + "a")) start.ArgumentList.Add(arg);
            try { process = Process.Start(start)!; }
            catch { videoPipe.Dispose(); audioPipe.Dispose(); throw; }
            decoder.HardwareAudio += Samples; decoder.HardwareAudioReset += Reset;
            worker = Task.Run(Run);
        }
        private void Samples(byte[] data) { audio.Enqueue(data); while (audio.Count > 32) audio.TryDequeue(out _); }
        private void Reset() { while (audio.TryDequeue(out _)) { } Interlocked.Increment(ref audioResetVersion); }
        private async Task Run()
        {
            async Task DrainErrors() { while (await process.StandardError.ReadLineAsync() is not null) { } }
            async Task Progress()
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                    if (line.StartsWith("frame=") && long.TryParse(line[6..].Trim(), out var count) && count > 0)
                    { Interlocked.Exchange(ref frames, count); state = "Encoding"; message = profile.VideoEncoder + " output progressing; verify receiver. Native PCM audio; desktop CG is not embedded."; }
            }
            var errorReader = DrainErrors(); var outputReader = Progress();
            Task? video = null, sound = null;
            try
            {
                video = PumpVideo(); sound = PumpAudio(); var exit = process.WaitForExitAsync(cancel.Token);
                var completed = await Task.WhenAny(video, sound, exit);
                await completed;
                if (!cancel.IsCancellationRequested) { state = "Failed"; message = "Encoder exited or destination disconnected. Check URL/key, network and receiver; Stop then Start to retry."; }
            }
            catch (OperationCanceledException) { }
            catch { state = "Failed"; message = "Encoder connection failed. Check destination and selected audio format."; }
            finally
            {
                cancel.Cancel(); decoder.HardwareAudio -= Samples; decoder.HardwareAudioReset -= Reset;
                try { if (!process.HasExited) process.Kill(true); } catch { }
                videoPipe.Dispose(); audioPipe.Dispose();
                try { if (video is not null && sound is not null) await Task.WhenAll(video, sound); } catch { }
                try { await process.WaitForExitAsync(); await errorReader; await outputReader; } catch { }
                Reset();
            }
        }
        private async Task Pace(Stopwatch clock, long tick)
        {
            var delay = tick * 1000d / profile.Fps - clock.Elapsed.TotalMilliseconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), cancel.Token);
        }
        private async Task PumpVideo()
        {
            await videoPipe.WaitForConnectionAsync(cancel.Token);
            var source = new byte[inputWidth * inputHeight * 4]; var target = new byte[source.Length]; var clock = Stopwatch.StartNew(); long tick = 0;
            while (!cancel.IsCancellationRequested)
            {
                if (decoder.CopyProductionFrame(source, out var w, out var h, out _)) VideoRaster.Scale(source, w, h, target, inputWidth, inputHeight);
                await videoPipe.WriteAsync(target, cancel.Token);
                await Pace(clock, ++tick);
                if (clock.Elapsed.TotalMilliseconds - tick * 1000d / profile.Fps > 1000) { var current = (long)(clock.Elapsed.TotalSeconds * profile.Fps); Interlocked.Add(ref droppedFrames, current - tick); tick = current; }
            }
        }
        private async Task PumpAudio()
        {
            await audioPipe.WaitForConnectionAsync(cancel.Token);
            var version = Volatile.Read(ref audioResetVersion);
            var buffer = new byte[48000 / profile.Fps * 4]; byte[]? pending = null; var offset = 0; var clock = Stopwatch.StartNew(); long tick = 0;
            while (!cancel.IsCancellationRequested)
            {
                if (decoder.AudioSampleRate != 48000) throw new InvalidOperationException("Audio rate changed");
                var currentVersion = Volatile.Read(ref audioResetVersion);
                if (version != currentVersion) { pending = null; offset = 0; version = currentVersion; }
                Array.Clear(buffer); var filled = 0;
                while (filled < buffer.Length)
                {
                    if (pending is null && !audio.TryDequeue(out pending)) break;
                    var copy = Math.Min(buffer.Length - filled, pending!.Length - offset);
                    Buffer.BlockCopy(pending, offset, buffer, filled, copy); filled += copy; offset += copy;
                    if (offset == pending.Length) { pending = null; offset = 0; }
                }
                await audioPipe.WriteAsync(buffer, cancel.Token); await Pace(clock, ++tick);
            }
        }
        public async Task Stop() { cancel.Cancel(); try { if (!process.HasExited) process.Kill(true); } catch { } await worker; process.Dispose(); }
    }
}
