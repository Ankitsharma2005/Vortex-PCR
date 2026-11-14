using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LibVLCSharp.Shared;
using NewsPlay.Core;
using NewsPlay.Engine;

internal static class EncodingChecks
{
    private static Process Start(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }
    private static async Task<string> Run(string executable, string[] arguments, int seconds = 30)
    {
        using var process = Start(executable, arguments);
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds)); }
        catch { try { process.Kill(true); } catch { } throw; }
        if (process.ExitCode != 0) throw new Exception(await stderr);
        await stderr; return await stdout;
    }
    public static async Task<int> RunAll(string folder, bool rtmpsOnly = false)
    {
        int passed = 0;
        var ffmpeg = Path.GetFullPath("tools/ffmpeg/ffmpeg.exe"); var ffprobe = Path.GetFullPath("tools/ffmpeg/ffprobe.exe");
        foreach (var codec in rtmpsOnly ? Array.Empty<string>() : new[] { "libx264", "libx265" })
        foreach (var rate in new[] { 25, 30, 50, 60 })
        {
            var file = Path.Combine(folder, $"uhd-{codec}-{rate}.mkv");
            await Run(ffmpeg, ["-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=size=3840x2160:rate={rate}", "-frames:v", "2", "-c:v", codec, "-preset", "ultrafast", "-threads", "4", "-pix_fmt", "yuv420p", file]);
            var json = await Run(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,codec_name,r_frame_rate", "-of", "json", file]);
            using var document = JsonDocument.Parse(json); var stream = document.RootElement.GetProperty("streams")[0];
            if (stream.GetProperty("width").GetInt32() != 3840 || stream.GetProperty("height").GetInt32() != 2160 || stream.GetProperty("r_frame_rate").GetString() != rate + "/1") throw new Exception("UHD encoder metadata mismatch");
            await Run(ffmpeg, ["-v", "error", "-i", file, "-frames:v", "2", "-f", "null", "-"]);
            Console.WriteLine($"PASS actual {codec} UHD {rate} fps format encode/decode metadata (2 frames, not real-time certification)"); passed++;
        }
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg"); Directory.CreateDirectory(bundled); File.Copy(ffmpeg, Path.Combine(bundled, "ffmpeg.exe"), true);
        using var vlc = new LibVLC("--quiet", "--no-video-title-show");
        using var one = new VlcDecoder(vlc, "ProjectNetwork" + Environment.ProcessId, 1); using var two = new VlcDecoder(vlc, "ProjectNetwork" + Environment.ProcessId, 2);
        one.ConfigureProject(ProjectVideoFormat.Find("1080i50")); one.Load(new MediaAsset { Name = "Network fixture", Kind = "Still", Path = Path.Combine(folder, "native-3840x2160.bmp"), Width = 3840, Height = 2160 }, 0, true); one.SetAudio(0, true);
        using var service = new StreamingService(one, two);
        foreach (var protocol in rtmpsOnly ? new[] { "RTMPS" } : new[] { "UDP", "SRT caller", "RTMP", "RTMPS" })
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var url = protocol switch { "UDP" => "udp://127.0.0.1:29310", "SRT caller" => "srt://127.0.0.1:29311?latency=20000", "RTMP" => "rtmp://127.0.0.1:29312/live/test", _ => "rtmps://127.0.0.1:29313/live/test" };
            var input = protocol == "SRT caller" ? url + "&mode=listener" : protocol == "RTMPS" ? "rtmp://127.0.0.1:29314/live/test" : url;
            var receiverArgs = new List<string> { "-hide_banner", "-loglevel", "error" };
            if (protocol is "RTMP" or "RTMPS") receiverArgs.AddRange(["-listen", "1"]);
            receiverArgs.AddRange(["-i", input, "-map", "0:v:0", "-map", "0:a:0", "-frames:v", "3", "-f", "null", "-"]);
            using var receiver = Start(ffmpeg, receiverArgs); var errors = receiver.StandardError.ReadToEndAsync(); var output = receiver.StandardOutput.ReadToEndAsync();
            TcpListener? tlsListener = null; Task? tls = null;
            X509Certificate2? certificate = null;
            var certificatePath = Path.Combine(folder, "loopback-ca.pem");
            if (protocol == "RTMPS")
            {
                using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
                await File.WriteAllTextAsync(certificatePath, generated.ExportCertificatePem(), cancel.Token);
                certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                tlsListener = new TcpListener(IPAddress.Loopback, 29313); tlsListener.Start();
                tls = Task.Run(async () =>
                {
                    using var incoming = await tlsListener.AcceptTcpClientAsync(cancel.Token);
                    using var ssl = new SslStream(incoming.GetStream()); await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancel.Token);
                    using var backend = new TcpClient(); await backend.ConnectAsync(IPAddress.Loopback, 29314, cancel.Token); var wire = backend.GetStream();
                    var completed = await Task.WhenAny(ssl.CopyToAsync(wire, cancel.Token), wire.CopyToAsync(ssl, cancel.Token)); await completed;
                }, cancel.Token);
            }
            Guid id = Guid.NewGuid();
            try
            {
                await Task.Delay(300, cancel.Token);
                service.Start(new StreamProfile { Id = id, Name = "Loopback " + protocol, Protocol = protocol, Url = url, Width = 1280, Height = 720, Fps = 25, TlsCaFile = protocol == "RTMPS" ? certificatePath : "" });
                await receiver.WaitForExitAsync(cancel.Token);
                if (receiver.ExitCode != 0) throw new Exception(await errors + " TLS: " + tls?.Status + " " + tls?.Exception);
                Console.WriteLine("PASS actual independent 1080i50 project → 720p25 " + protocol + " encoder/receiver loopback"); passed++;
            }
            catch (OperationCanceledException)
            {
                var state = service.Status().FirstOrDefault(s => s.Id == id);
                throw new Exception(protocol + " loopback timeout; encoder: " + state?.State + " " + state?.Message + "; TLS task: " + tls?.Status + " " + tls?.Exception);
            }
            finally
            {
                await service.Stop(id); cancel.Cancel(); tlsListener?.Stop();
                try { if (!receiver.HasExited) receiver.Kill(true); } catch { }
                try { if (tls is not null) await tls; await errors; await output; } catch { }
                certificate?.Dispose();
            }
        }
        return passed;
    }
}
