using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LibVLCSharp.Shared;
using NewsPlay.Core;
using NewsPlay.Engine;

var data = Path.GetFullPath(Environment.GetEnvironmentVariable("NEWSPLAY_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewsPlay"));
Directory.CreateDirectory(data);
var instance = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..12];
if (args.Contains("--supervise"))
{
    using var watchdog = new Mutex(true, $"Local\\NewsPlay.{instance}.Watchdog", out var acquired);
    if (!acquired) return;
    var failures = new Queue<DateTimeOffset>();
    while (!File.Exists(Path.Combine(data, "shutdown.request")))
    {
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(typeof(PlayoutController).Assembly.Location.Replace("NewsPlay.Core.dll", "NewsPlay.Engine.dll"));
        psi.ArgumentList.Add("--serve");
        using var child = Process.Start(psi)!;
        async Task Capture(StreamReader reader, string file) { while (await reader.ReadLineAsync() is { } line) await File.AppendAllTextAsync(Path.Combine(data, file), DateTimeOffset.Now.ToString("O") + " " + line + Environment.NewLine); }
        var standardOutput = Capture(child.StandardOutput, "engine-console.log"); var standardError = Capture(child.StandardError, "engine-stderr.log");
        await child.WaitForExitAsync(); await Task.WhenAll(standardOutput, standardError);
        if (File.Exists(Path.Combine(data, "shutdown.request"))) break;
        failures.Enqueue(DateTimeOffset.Now); while (failures.Count > 0 && DateTimeOffset.Now - failures.Peek() > TimeSpan.FromMinutes(1)) failures.Dequeue();
        if (failures.Count > 5) { File.AppendAllText(Path.Combine(data, "engine-errors.log"), "Repeated engine failure; watchdog stopped.\n"); break; }
        await Task.Delay(1000);
    }
    return;
}
using var singleton = new Mutex(true, $"Local\\NewsPlay.{instance}.Engine", out var ownsEngine);
if (!ownsEngine) return;
try
{
    LibVLCSharp.Shared.Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
    using var vlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--quiet");
    using var d1 = new VlcDecoder(vlc, instance, 1); using var d2 = new VlcDecoder(vlc, instance, 2); using var editDecoder = new VlcDecoder(vlc, instance, 3);
    using var sourceDecoder = new VlcDecoder(vlc, instance, 4);
    using var colourDecoder = new VlcDecoder(vlc, instance, 5);
    using var livePreviews = new LivePreviewService(vlc, instance);
    using var db = new SqliteStore(data); var gate = new object();
    var controller = new PlayoutController(db, new Dictionary<int, IDecoder> { [1] = d1, [2] = d2 }); var probe = new MediaProbe(vlc);
    var keyFile = Path.Combine(data, "engine.key");
    if (!File.Exists(keyFile)) File.WriteAllText(keyFile, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    var key = File.ReadAllText(keyFile).Trim();
    var builder = WebApplication.CreateSlimBuilder(args.Where(a => !a.StartsWith("--")).ToArray());
    builder.WebHost.UseUrls("http://127.0.0.1:" + (Environment.GetEnvironmentVariable("NEWSPLAY_PORT") ?? "17890")); builder.Logging.ClearProviders();
    var app = builder.Build();
    var colourPreviewActivity = DateTimeOffset.MinValue;
    Guid? colourPreviewMedia = null;
    Guid? colourLiveMedia = null;
    Guid? colourSession = null;
    void EndColourPreview()
    {
        foreach(var decoder in new[] { d1, d2 }) { decoder.EnableRawPreview(false); if (decoder.LoadedMediaId == colourLiveMedia && controller.State.Media.FirstOrDefault(m => m.Id == colourLiveMedia) is { } media) decoder.SetColour(media.Colour); }
        colourDecoder.EnableRawPreview(false); colourDecoder.Stop(); colourPreviewMedia = null; colourLiveMedia = null;
    }
    app.MapPost("/colour-source", (EngineCommand cmd) =>
    {
        lock (gate)
        {
            if(cmd.Action is not ("Load" or "Adjust" or "Stop")) throw new InvalidOperationException("Unknown colour preview command");
            if(cmd.Action is "Stop" or "Adjust" && cmd.ItemId != colourSession) return Results.Json(new { slot = 0 });
            if(cmd.Action == "Load" && cmd.ItemId != colourSession) { EndColourPreview(); colourSession = cmd.ItemId; }
            colourPreviewActivity = DateTimeOffset.UtcNow;
            if (cmd.Action == "Stop") { EndColourPreview(); return Results.Json(new { slot = 0 }); }
            var asset = controller.Media(cmd.MediaId);
            if(cmd.Action == "Adjust")
            {
                var correction = cmd.Colour ?? throw new InvalidOperationException("Colour settings required"); correction.Validate();
                foreach(var decoder in new[] { d1, d2 }) if(decoder.HasMedia && decoder.LoadedMediaId == asset.Id) decoder.SetColour(cmd.Enabled ? correction : asset.Colour);
                colourLiveMedia = cmd.Enabled ? asset.Id : null; return Results.Json(new { slot = 0 });
            }
            var active = controller.State.Channels.Where(ch => ch.Status == "Playing" && !ch.Emergency).Select(ch => ch.Id == 1 ? d1 : d2).FirstOrDefault(d => d.HasMedia && d.LoadedMediaId == asset.Id);
            if (active is not null) { active.EnableRawPreview(true); colourDecoder.Stop(); return Results.Json(new { slot = active == d1 ? 21 : 22 }); }
            if (asset.Live?.Type is "DeckLink" or "USB / Webcam") throw new InvalidOperationException("Take this capture source on a port first to monitor colour without opening the device twice");
            if (colourPreviewMedia != asset.Id || !colourDecoder.HasMedia || colourDecoder.Ended)
            {
                var copy = Json.Copy(asset); copy.Colour = new() { Enabled = false }; colourDecoder.EnableRawPreview(true); colourDecoder.SetAudio(0, true); colourDecoder.Load(copy, 0, true); colourDecoder.SetAudio(0, true); colourPreviewMedia = asset.Id;
            }
            return Results.Json(new { slot = 25 });
        }
    });
    var previewAudioFile = Path.Combine(data, "preview-audio.json");
    EngineCommand previewAudio = File.Exists(previewAudioFile) ? System.Text.Json.JsonSerializer.Deserialize<EngineCommand>(File.ReadAllText(previewAudioFile), Json.Options) ?? new() : new() { Value = 48000, Index = 16 };
    try { sourceDecoder.ConfigureAudio(previewAudio.Text ?? "", (int)previewAudio.Value, previewAudio.Index); editDecoder.ConfigureAudio(previewAudio.Text ?? "", (int)previewAudio.Value, previewAudio.Index); }
    catch { previewAudio = new() { Value = 48000, Index = 16 }; sourceDecoder.ConfigureAudio("", 48000, 16); editDecoder.ConfigureAudio("", 48000, 16); }
    controller.State.PreviewAudio = new() { DeviceId = previewAudio.Text ?? "", SampleRate = (int)previewAudio.Value, BitDepth = previewAudio.Index };
    app.MapGet("/preview-audio", () => Results.Json(previewAudio, Json.Options));
    app.MapPost("/preview-audio", (EngineCommand cmd) =>
    {
        lock (gate)
        {
            if (cmd.Value is not (32000 or 44100 or 48000 or 88200 or 96000 or 176400 or 192000) || cmd.Index is not (16 or 24 or 32)) throw new InvalidOperationException("Unsupported PCM format");
            if (!string.IsNullOrEmpty(cmd.Text) && !PcmAudioOutput.Devices().Any(d => d.Id == cmd.Text)) throw new InvalidOperationException("Preview audio device unavailable");
            sourceDecoder.Stop(); editDecoder.Stop(); sourceDecoder.ConfigureAudio(cmd.Text ?? "", (int)cmd.Value, cmd.Index); editDecoder.ConfigureAudio(cmd.Text ?? "", (int)cmd.Value, cmd.Index);
            previewAudio = cmd; controller.State.PreviewAudio = new() { DeviceId = cmd.Text ?? "", SampleRate = (int)cmd.Value, BitDepth = cmd.Index }; var temporary = previewAudioFile + ".tmp"; File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(previewAudio, Json.Options)); File.Move(temporary, previewAudioFile, true); return Results.Ok();
        }
    });
    var browserFrames = new BrowserFrameHub();
    app.Use(async (context, next) =>
    {
        var browserStream = context.Request.Path.StartsWithSegments("/browser/stream") && context.Request.Query["key"] == key;
        if (context.Request.Headers["X-NewsPlay-Key"] != key && !browserStream) { context.Response.StatusCode = 401; return; }
        var trace = Environment.GetEnvironmentVariable("NEWSPLAY_TRACE") == "1";
        if (trace) Console.WriteLine("HTTP start " + context.Request.Path);
        try { await next(context); if (trace) Console.WriteLine("HTTP done " + context.Request.Path); }
        catch (Exception ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    });
    app.MapGet("/state", () => { lock (gate) return Results.Json(Json.Copy(controller.State), Json.Options); });
    app.MapGet("/audio-sync", () => new[] { d1.AudioSyncStatus, d2.AudioSyncStatus });
    app.MapGet("/audio-levels", () => new[] { new[] { d1.AudioLeft, d1.AudioRight }, new[] { d2.AudioLeft, d2.AudioRight } });
    using var deckLink = new DeckLinkOutputService(d1, d2);
    using var streaming = new StreamingService(d1, d2);
    app.MapGet("/stream/status", () => Results.Json(streaming.Status(), Json.Options));
    app.MapPost("/stream/start", (StreamProfile profile) => { streaming.Start(profile); return Results.Ok(); });
    app.MapPost("/stream/stop", async (StreamProfile profile) => { await streaming.Stop(profile.Id); return Results.Ok(); });
    app.MapGet("/decklink/devices", async () => Results.Json(await Task.Run(DeckLinkOutputService.Scan), Json.Options));
    app.MapGet("/decklink/status", () => Results.Json(deckLink.Status(), Json.Options));
    app.MapPost("/decklink/start", async (DeckLinkRoute route) =>
    {
        lock (gate)
        {
            var channel = controller.Channel(route.Port);
            if (channel.Status == "Playing" || channel.Emergency) throw new InvalidOperationException("Pause or stop this program before enabling DeckLink output");
            if (route.Audio && channel.AudioSampleRate != 48000) throw new InvalidOperationException("Set program audio to 48000 Hz first");
        }
        await deckLink.Start(route); return Results.Json(deckLink.Status(), Json.Options);
    });
    app.MapPost("/decklink/stop", async (DeckLinkRoute route) => { await deckLink.Stop(route.Port); return Results.Ok(); });
    app.MapPost("/browser/frame/{id:guid}", (Guid id, HttpContext context) => browserFrames.Upload(id, context));
    app.MapGet("/browser/stream/{id:guid}", (Guid id, HttpContext context) => browserFrames.Stream(id, context));
    app.MapGet("/live/status", () => { lock (gate) return Results.Json(livePreviews.Status(controller.State), Json.Options); });
    app.MapPost("/live/preview", (EngineCommand cmd) =>
    {
        lock (gate)
        {
            var id = cmd.MediaId ?? throw new InvalidOperationException("Select an input");
            switch (cmd.Action)
            {
                case "Start": livePreviews.Start(controller.Media(id)); break;
                case "Stop": livePreviews.Stop(id); break;
                case "Audio": livePreviews.Audio(id, cmd.Enabled, cmd.Value); break;
                default: throw new InvalidOperationException("Unknown live preview command");
            }
            return Results.Ok();
        }
    });
    app.MapGet("/health", () => Results.Json(new { pid = Environment.ProcessId, instance, decoder = "LibVLC", deckLink = false, memoryMb = Process.GetCurrentProcess().WorkingSet64 / 1048576 }));
    app.MapGet("/logs", () => { lock (gate) return Results.Json(db.Logs(), Json.Options); });
    app.MapGet("/audio-devices/{channel:int}", (int channel) => { lock (gate) return Results.Json(controller.AudioDevices(channel), Json.Options); });
    app.MapGet("/preflight/{rundownId:guid}", (Guid rundownId) => { lock (gate) return Results.Json(controller.Preflight(rundownId), Json.Options); });
    var previewCleanup = new List<Action>();
    void MapPreview(string route, VlcDecoder decoder)
    {
        long revision = 0;
        Guid? loadedId = null; long loadedRelease = 0;
        ColourCorrection? previewColour = null;
        previewCleanup.Add(() => { var media = controller.State.Media.FirstOrDefault(m => m.Id == loadedId); if (media is not null && !ReferenceEquals(previewColour, media.Colour)) { decoder.SetColour(media.Colour); previewColour = media.Colour; } });
        previewCleanup.Add(() => { if (loadedId is { } id && !controller.State.Media.Any(m => m.Id == id && m.LiveReleaseRevision == loadedRelease)) { revision++; decoder.Stop(); loadedId = null; } });
        app.MapGet(route, () => { lock (gate) return Results.Json(new { position = decoder.Position, ended = decoder.Ended, error = decoder.Error, audioLeft = decoder.AudioLeft, audioRight = decoder.AudioRight }); });
        app.MapPost(route, async (EngineCommand cmd) =>
        {
            long request, firstFrame = 0; bool waitForPicture = false;
            lock (gate)
            {
                request = ++revision;
                switch (cmd.Action)
                {
                    case "Load":
                        var asset = controller.Media(cmd.MediaId);
                        loadedId = asset.Id; loadedRelease = asset.LiveReleaseRevision;
                        PlayoutController.Number(cmd.Value, 0, asset.Duration);
                        firstFrame = decoder.Frames; decoder.SetAudio(0, true); decoder.Load(asset, cmd.Value, true); decoder.SetAudio(0, true); waitForPicture = asset.Kind != "Audio"; break;
                    case "Play": decoder.SetAudio(0, cmd.Enabled); decoder.Play(); break;
                    case "Mute": decoder.SetAudio(0, cmd.Enabled); break;
                    case "Pause": decoder.Pause(); break;
                    case "Seek": decoder.Seek(Math.Max(0, cmd.Value)); break;
                    case "Stop": decoder.Stop(); break;
                    default: throw new InvalidOperationException("Unknown preview command");
                }
            }
            if (cmd.Action == "Load")
            {
                // Prime a muted first frame; native start-paused may never publish one on a fresh decoder.
                // Never block the engine tick, and never pause a newer request.
                var deadline = Stopwatch.StartNew();
                while (waitForPicture && deadline.Elapsed < TimeSpan.FromSeconds(3))
                {
                    lock (gate) { if (request != revision || decoder.Frames > firstFrame || decoder.Error is not null) break; }
                    await Task.Delay(20);
                }
                lock (gate)
                {
                    if (request == revision)
                    {
                        decoder.Pause(); decoder.Seek(cmd.Value);
                        if (decoder.Error is { } error) throw new InvalidOperationException(error);
                        if (waitForPicture && decoder.Frames <= firstFrame) throw new InvalidOperationException("Preview decoder did not produce a picture");
                    }
                }
            }
            return Results.Ok();
        });
    }
    MapPreview("/preview", editDecoder); MapPreview("/source-preview", sourceDecoder);
    app.MapPost("/command", (EngineCommand cmd) => { lock (gate) {
        var previous = controller.State.Media.Where(m => m.Kind == "Live").ToDictionary(m => m.Id, m => m.LiveReleaseRevision);
        controller.Execute(cmd); livePreviews.Sync(controller.State); foreach (var cleanup in previewCleanup) cleanup();
        foreach (var prior in previous) if (!controller.State.Media.Any(m => m.Id == prior.Key && m.LiveReleaseRevision == prior.Value) && !LiveUsage.IsOnAir(controller.State, prior.Key)) browserFrames.Remove(prior.Key);
        return Results.Json(Json.Copy(controller.State), Json.Options);
    } });
    app.MapPost("/import", async (EngineCommand cmd) =>
    {
        var source = cmd.Path ?? throw new InvalidOperationException("Path required");
        var paths = Directory.Exists(source) ? Directory.EnumerateFiles(source).Where(p => MediaProbe.Extensions.Contains(Path.GetExtension(p))).ToArray() : [source];
        var errors = new List<string>(); var imported = 0;
        foreach (var file in paths)
        {
            try { var media = await probe.Inspect(file); lock (gate) controller.AddMedia(media); imported++; }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        return Results.Json(new { imported, errors });
    });
    app.MapPost("/shutdown", () => { lock (gate) { if (controller.State.Channels.Any(c => c.Status == "Playing" || c.Emergency)) return Results.BadRequest(new { error = "Stop both channels before shutting down engine" }); controller.Save(); File.WriteAllText(Path.Combine(data, "shutdown.request"), "operator"); app.Lifetime.StopApplication(); return Results.Ok(); } });
    var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20)); var clock = Stopwatch.StartNew(); var previous = clock.Elapsed.TotalSeconds; var saved = previous;
    var ticker = Task.Run(async () => { try { while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping)) { var time = clock.Elapsed.TotalSeconds; lock (gate) { controller.Tick(time - previous); if (colourPreviewActivity != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - colourPreviewActivity > TimeSpan.FromSeconds(10)) { EndColourPreview(); colourPreviewActivity = DateTimeOffset.MinValue; } if (time - saved >= 1) { controller.Save(); saved = time; } } previous = time; } } catch (OperationCanceledException) { } catch (Exception ex) { File.AppendAllText(Path.Combine(data, "engine-errors.log"), ex + "\n"); Environment.Exit(1); } });
    var observed = new Dictionary<string, (long, DateTime)>();
    var watch = Task.Run(async () => { try { while (!app.Lifetime.ApplicationStopping.IsCancellationRequested) { await Task.Delay(5000, app.Lifetime.ApplicationStopping); string folder; lock (gate) folder = controller.State.WatchFolder; if (!Directory.Exists(folder)) continue; foreach (var file in Directory.EnumerateFiles(folder).Where(p => MediaProbe.Extensions.Contains(Path.GetExtension(p)))) { lock (gate) { if (controller.State.Media.Any(m => string.Equals(m.Path, file, StringComparison.OrdinalIgnoreCase))) continue; } var info = new FileInfo(file); var stamp = (info.Length, info.LastWriteTimeUtc); if (!observed.TryGetValue(file, out var prior) || prior != stamp) { observed[file] = stamp; continue; } try { var media = await probe.Inspect(file); lock (gate) controller.AddMedia(media); observed.Remove(file); } catch (Exception ex) { lock (gate) controller.Alarm(ex.Message); observed.Remove(file); } } } } catch (OperationCanceledException) { } catch (Exception ex) { lock (gate) controller.Alarm("Watch folder: " + ex.Message); } });
    var liveMonitor = Task.Run(async () =>
    {
        try { while (!app.Lifetime.ApplicationStopping.IsCancellationRequested) { await Task.Delay(1000, app.Lifetime.ApplicationStopping); lock (gate) livePreviews.Tick(); } }
        catch (OperationCanceledException) { }
    });
    await app.RunAsync(); timer.Dispose(); await Task.WhenAll(ticker, watch, liveMonitor); lock (gate) controller.Save();
}
catch (Exception ex) { File.AppendAllText(Path.Combine(data, "engine-errors.log"), DateTimeOffset.Now + " " + ex + "\n"); throw; }
