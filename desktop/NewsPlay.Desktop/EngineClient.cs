using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public sealed class EngineClient : IDisposable
{
#if MCR_VARIANT
    public string DataDirectory { get; } = Path.GetFullPath(Environment.GetEnvironmentVariable("VORTEX_MCR_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewsPlayMCR"));
#else
    public string DataDirectory { get; } = Path.GetFullPath(Environment.GetEnvironmentVariable("NEWSPLAY_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewsPlay"));
#endif
    public string Instance => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..12];
#if MCR_VARIANT
    private readonly HttpClient http = new() { BaseAddress = new Uri("http://127.0.0.1:" + (Environment.GetEnvironmentVariable("VORTEX_MCR_PORT") ?? "17990")), Timeout = TimeSpan.FromSeconds(30) };
#else
    private readonly HttpClient http = new() { BaseAddress = new Uri("http://127.0.0.1:" + (Environment.GetEnvironmentVariable("NEWSPLAY_PORT") ?? "17890")), Timeout = TimeSpan.FromSeconds(30) };
#endif
    public async Task Connect()
    {
        Directory.CreateDirectory(DataDirectory); await LoadKey();
        try { await GetState(); return; } catch { }
#if MCR_VARIANT
        var configuredAssembly = Environment.GetEnvironmentVariable("VORTEX_MCR_ENGINE_ASSEMBLY");
        var engineExe = Path.Combine(AppContext.BaseDirectory, "engine", "Vortex.MCR.Engine.exe");
        if (!File.Exists(engineExe)) engineExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NewsPlay.MCR.Engine", "bin", "Debug", "net8.0-windows", "Vortex.MCR.Engine.exe"));
        if (!string.IsNullOrWhiteSpace(configuredAssembly))
        {
            if (!File.Exists(configuredAssembly)) throw new FileNotFoundException("Build the independent VORTEX MCR engine before running from source", configuredAssembly);
            var host = Environment.GetEnvironmentVariable("VORTEX_MCR_DOTNET_HOST") ?? "dotnet";
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(configuredAssembly) };
            start.ArgumentList.Add(configuredAssembly); start.ArgumentList.Add("--supervise");
            var shutdownFile = Path.Combine(DataDirectory, "shutdown.request"); if (File.Exists(shutdownFile)) File.Delete(shutdownFile);
            Process.Start(start);
        }
        else
        {
            if (!File.Exists(engineExe)) throw new FileNotFoundException("Independent VORTEX MCR engine executable not found", engineExe);
            var shutdownFile = Path.Combine(DataDirectory, "shutdown.request"); if (File.Exists(shutdownFile)) File.Delete(shutdownFile);
            Process.Start(new ProcessStartInfo(engineExe, "--supervise") { WorkingDirectory = Path.GetDirectoryName(engineExe), UseShellExecute = false, CreateNoWindow = true });
        }
        for (var i = 0; i < 80; i++) { await Task.Delay(250); await LoadKey(); try { await GetState(); return; } catch { } }
        throw new InvalidOperationException("Independent VORTEX MCR engine did not start; check engine-errors.log");
#else
        if (Environment.GetEnvironmentVariable("NEWSPLAY_ENGINE_ASSEMBLY") is { Length: > 0 } assembly)
        {
            if (!File.Exists(assembly)) throw new FileNotFoundException("Build the engine DLL before running from source", assembly);
            var host = Environment.GetEnvironmentVariable("NEWSPLAY_DOTNET_HOST") ?? "dotnet";
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(assembly) };
            var shutdownFile = Path.Combine(DataDirectory, "shutdown.request"); if (File.Exists(shutdownFile)) File.Delete(shutdownFile);
            start.ArgumentList.Add(assembly); start.ArgumentList.Add("--supervise"); Process.Start(start);
            for (var i = 0; i < 120; i++) { await Task.Delay(250); await LoadKey(); try { await GetState(); return; } catch { } }
            throw new InvalidOperationException("Source engine did not start; check engine-errors.log");
        }
        var engine = Path.Combine(AppContext.BaseDirectory, "engine", "NewsPlay.Engine.exe");
        if (!File.Exists(engine)) engine = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NewsPlay.Engine", "bin", "Debug", "net8.0-windows", "NewsPlay.Engine.exe"));
        if (!File.Exists(engine)) throw new FileNotFoundException("Engine executable not found. Run desktop/build.ps1 to build the desktop package.", engine);
        var shutdown = Path.Combine(DataDirectory, "shutdown.request"); if (File.Exists(shutdown)) File.Delete(shutdown);
        Process.Start(new ProcessStartInfo(engine, "--supervise") { WorkingDirectory = Path.GetDirectoryName(engine), UseShellExecute = false, CreateNoWindow = true });
        for (var i = 0; i < 60; i++) { await Task.Delay(250); await LoadKey(); try { await GetState(); return; } catch { } }
        throw new InvalidOperationException("Engine did not start. Check " + Path.Combine(DataDirectory, "engine-errors.log"));
#endif
    }
    private Task LoadKey()
    {
        var path = Path.Combine(DataDirectory, "engine.key");
        if (File.Exists(path))
        {
#if MCR_VARIANT
            const string header = "X-Vortex-MCR-Key";
#else
            const string header = "X-NewsPlay-Key";
#endif
            http.DefaultRequestHeaders.Remove(header); http.DefaultRequestHeaders.Add(header, File.ReadAllText(path).Trim());
        }
        return Task.CompletedTask;
    }
    public async Task<EngineState> GetState() => (await http.GetFromJsonAsync<EngineState>("/state", Json.Options))!;
    public async Task<EngineState> Send(EngineCommand command)
    {
        var response = await http.PostAsJsonAsync("/command", command, Json.Options); await Check(response); return (await response.Content.ReadFromJsonAsync<EngineState>(Json.Options))!;
    }
    public async Task<string> Import(string path) { var response = await http.PostAsJsonAsync("/import", new EngineCommand { Path = path }, Json.Options); await Check(response); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement; return $"{root.GetProperty("imported").GetInt32()} imported" + (root.GetProperty("errors").GetArrayLength() > 0 ? "; " + string.Join("; ", root.GetProperty("errors").EnumerateArray().Select(e => e.GetString())) : ""); }
    public async Task<List<AsRun>> Logs() => await http.GetFromJsonAsync<List<AsRun>>("/logs", Json.Options) ?? [];
    public async Task<List<AudioDeviceOption>> AudioDevices(int channel) => await http.GetFromJsonAsync<List<AudioDeviceOption>>("/audio-devices/" + channel, Json.Options) ?? [];
    public async Task<DeckLinkScan> DeckLinkDevices() => (await http.GetFromJsonAsync<DeckLinkScan>("/decklink/devices", Json.Options))!;
    public async Task<DeckLinkStatus[]> DeckLinkStatus() => await http.GetFromJsonAsync<DeckLinkStatus[]>("/decklink/status", Json.Options) ?? [];
    public async Task DeckLinkStart(DeckLinkRoute route) { using var response = await http.PostAsJsonAsync("/decklink/start", route, Json.Options); await Check(response); }
    public async Task DeckLinkStop(int port) { using var response = await http.PostAsJsonAsync("/decklink/stop", new DeckLinkRoute { Port = port }, Json.Options); await Check(response); }
    public async Task<EngineCommand> PreviewAudio() => await http.GetFromJsonAsync<EngineCommand>("/preview-audio", Json.Options) ?? new() { Value = 48000, Index = 16 };
    public async Task SetPreviewAudio(EngineCommand command) { using var response = await http.PostAsJsonAsync("/preview-audio", command, Json.Options); await Check(response); }
    public async Task<PreflightReport> Preflight(Guid rundownId) => (await http.GetFromJsonAsync<PreflightReport>("/preflight/" + rundownId, Json.Options))!;
    public async Task Preview(EngineCommand command) { var response = await http.PostAsJsonAsync("/preview", command, Json.Options); await Check(response); }
    public async Task SourcePreview(EngineCommand command) { var response = await http.PostAsJsonAsync("/source-preview", command, Json.Options); await Check(response); }
    public async Task<SourcePreviewState> SourceStatus() => (await http.GetFromJsonAsync<SourcePreviewState>("/source-preview", Json.Options))!;
    public async Task<double> PreviewPosition() { using var json = JsonDocument.Parse(await http.GetStringAsync("/preview")); return json.RootElement.GetProperty("position").GetDouble(); }
    public async Task Attach() => await LoadKey();
    public async Task<int> ColourSource(EngineCommand command) { using var response = await http.PostAsJsonAsync("/colour-source", command, Json.Options); await Check(response); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("slot").GetInt32(); }
    public async Task<StreamStatus[]> StreamStatus() => await http.GetFromJsonAsync<StreamStatus[]>("/stream/status", Json.Options) ?? [];
    public async Task StreamStart(StreamProfile profile) { using var response = await http.PostAsJsonAsync("/stream/start", profile, Json.Options); await Check(response); }
    public async Task StreamStop(Guid id) { using var response = await http.PostAsJsonAsync("/stream/stop", new StreamProfile { Id = id }, Json.Options); await Check(response); }
    public async Task<List<LivePreviewStatus>> LiveStatus() => await http.GetFromJsonAsync<List<LivePreviewStatus>>("/live/status", Json.Options) ?? [];
    public async Task BrowserFrame(Guid id, byte[] jpeg) { using var body = new ByteArrayContent(jpeg); body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg"); using var response = await http.PostAsync("/browser/frame/" + id, body); await Check(response); }
    public async Task LivePreview(EngineCommand command) { var response = await http.PostAsJsonAsync("/live/preview", command, Json.Options); await Check(response); }
    public async Task Shutdown() { var response = await http.PostAsync("/shutdown", null); await Check(response); }
    private static async Task Check(HttpResponseMessage response) { if (response.IsSuccessStatusCode) return; var raw = await response.Content.ReadAsStringAsync(); try { using var json = JsonDocument.Parse(raw); throw new InvalidOperationException(json.RootElement.GetProperty("error").GetString()); } catch (JsonException) { throw new InvalidOperationException(raw); } }
    public void Dispose() => http.Dispose();
}
public sealed record SourcePreviewState(double Position, bool Ended, string? Error, double AudioLeft = 0, double AudioRight = 0);
public sealed record LivePreviewStatus(Guid Id, string Name, int Slot, string Signal, int Width, int Height, double ObservedFps, int CacheMs, int Retries, bool Muted, string? Message, int[] OnAir, int AudioTrack = -1);
