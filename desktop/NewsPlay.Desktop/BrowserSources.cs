using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

// Browser sessions belong to the operator console, not to the input editor.
// Detaching the editor returns the same interactive browser to its rendering host.
public sealed class BrowserSources(EngineClient client) : IDisposable
{
    public NdiSources Ndi { get; } = new(client);
    public sealed class Session
    {
        public required Guid Id;
        public required WebView2 View;
        public required Window Host;
        public string Url = "";
        public string Status = "Starting browser";
        public bool Ready, Capturing, Disposed;
        public long Frames;
        public long CapturedFrames, ReplacedFrames;
        public byte[]? PendingJpeg;
        public bool Uploading;
        public readonly object FrameGate = new();
        public bool Screencast;
        public long LastPush;
        public long PushFrames;
        public bool PreviewAudio;
        public bool OnAir, PreviewOpen;
        public bool Standby;
        public SemaphoreSlim PageOperations = new(1, 1);
        public DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(20) };
        public Task? Initialization;
    }
    private readonly Dictionary<Guid, Session> sessions = [];
    private CoreWebView2Environment? environment;
    private Task<CoreWebView2Environment>? environmentTask;
    private bool disposed;
    private long syncVersion;
    public Session? Find(Guid id) => sessions.GetValueOrDefault(id);
    public async Task<Session> Ensure(Guid id, string url, bool preview = false)
    {
        if (disposed) throw new ObjectDisposedException(nameof(BrowserSources));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Enter a complete http:// or https:// browser URL");
        if (!sessions.TryGetValue(id, out var session))
        {
            if (sessions.Count >= 8) throw new InvalidOperationException("Maximum eight browser sessions per console");
            var view = new WebView2 { Width = 960, Height = 540 };
            var host = new Window { Width = 960, Height = 540, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, Content = view };
            session = new Session { Id = id, View = view, Host = host, PreviewOpen = preview }; sessions.Add(id, session); host.Show();
            session.Initialization = Initialize(session);
        }
        if (preview) session.PreviewOpen = true;
        try { await session.Initialization!; }
        catch { if (sessions.Remove(id)) { session.Disposed = true; session.Timer.Stop(); session.View.Dispose(); session.Host.Close(); } throw; }
        if (session.Url != url) { session.Url = url; session.Ready = false; session.View.CoreWebView2.Navigate(url); }
        return session;
    }
    private async Task Initialize(Session session)
    {
        try
        {
            // These are Chromium scheduling options for the hidden on-air browser host;
            // security features remain enabled. Without them, occluded windows can throttle video/compositor frames.
            var browserOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"
            };
            environmentTask ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(client.DataDirectory, "BrowserProfile"), browserOptions);
            environment ??= await environmentTask;
            await session.View.EnsureCoreWebView2Async(environment);
            var core = session.View.CoreWebView2;
            core.IsMuted = true; core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false; core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => { e.Cancel = true; session.Status = "Downloads are disabled in the playout browser"; };
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") core.Navigate(e.Uri); };
            core.NavigationStarting += (_, e) => { if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "about")) e.Cancel = true; session.Ready = false; session.Status = "Loading page…"; };
            core.NavigationCompleted += (_, e) => { session.Ready = e.IsSuccess; session.Status = e.IsSuccess ? "Interactive browser · audio follows on-air to Windows default output" : "Page load failed: " + e.WebErrorStatus; };
            core.ProcessFailed += (_, _) => { session.Ready = false; session.Status = "Browser process failed — reopen the source"; };
            core.GetDevToolsProtocolEventReceiver("Page.screencastFrame").DevToolsProtocolEventReceived += async (_, e) =>
            {
                try
                {
                    if(session.Disposed)return;
                    using var payload=JsonDocument.Parse(e.ParameterObjectAsJson);
                    var token=payload.RootElement.GetProperty("sessionId").GetInt32();
                    await core.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck",JsonSerializer.Serialize(new { sessionId=token }));
                    if(session.Disposed||session.Standby||!session.Ready)return;
                    session.LastPush=Environment.TickCount64;session.PushFrames++;
                    QueueFrame(session,Convert.FromBase64String(payload.RootElement.GetProperty("data").GetString()!));
                }
                catch(Exception ex){if(!session.Disposed)session.Status="Browser frame: "+ex.Message;}
            };
            await SetScreencast(session,true);
            session.Timer.Tick += async (_, _) =>
            {
                if (!session.Ready || session.Capturing || session.Disposed) return;
                if(session.Screencast&&Environment.TickCount64-session.LastPush<500)return;
                session.Capturing = true;
                try
                {
                    using var jpeg = new MemoryStream(); await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, jpeg);
                    if (!session.Disposed && !session.Standby)
                    {
                        QueueFrame(session,jpeg.ToArray());
                    }
                }
                catch (Exception ex) { session.Status = "Browser output: " + ex.Message; }
                finally { session.Capturing = false; }
            };
            session.Timer.Start();
        }
        catch (Exception ex) { session.Status = "WebView2 could not start: " + ex.Message; throw; }
    }
    private void QueueFrame(Session session,byte[] jpeg)
    {
        var start = false;
        lock (session.FrameGate)
        {
            Interlocked.Increment(ref session.CapturedFrames);
            if(session.PendingJpeg is not null)Interlocked.Increment(ref session.ReplacedFrames);
            session.PendingJpeg=jpeg;
            if(!session.Uploading){session.Uploading=true;start=true;}
        }
        if(start)_=UploadLatest(session);
    }
    private static async Task SetScreencast(Session session,bool enabled)
    {
        try
        {
            if(session.Disposed)return;
            await session.View.CoreWebView2.CallDevToolsProtocolMethodAsync(enabled?"Page.startScreencast":"Page.stopScreencast",enabled?"{\"format\":\"jpeg\",\"quality\":85,\"maxWidth\":960,\"maxHeight\":540,\"everyNthFrame\":1}":"{}");
            session.Screencast=enabled;session.LastPush=Environment.TickCount64;
        }
        catch{session.Screencast=false;}
    }
    private async Task UploadLatest(Session session)
    {
        while (true)
        {
            byte[]? jpeg;
            lock (session.FrameGate)
            {
                if(session.Disposed||session.Standby){session.PendingJpeg=null;session.Uploading=false;return;}
                jpeg=session.PendingJpeg;session.PendingJpeg=null;
                if(jpeg is null){session.Uploading=false;return;}
            }
            try { await client.BrowserFrame(session.Id, jpeg); Interlocked.Increment(ref session.Frames); }
            catch (Exception ex)
            {
                if (!session.Disposed) session.Status = "Browser upload: " + ex.Message;
                lock(session.FrameGate){session.PendingJpeg=null;session.Uploading=false;}
                return;
            }
        }
    }
    public void Attach(Session session, ContentControl panel)
    {
        if (panel.Content is System.Windows.Controls.Viewbox oldBox && oldBox.Child == session.View) return;
        Detach(panel);
        if (session.View.Parent is ContentControl prior) prior.Content = null;
        session.Host.Content = null; session.PreviewOpen = true; panel.Content = new System.Windows.Controls.Viewbox { Stretch = System.Windows.Media.Stretch.Uniform, Child = session.View };
    }
    public void Detach(ContentControl panel)
    {
        if (panel.Content is not System.Windows.Controls.Viewbox box || box.Child is not WebView2 view) return;
        box.Child = null; panel.Content = null;
        var session = sessions.Values.FirstOrDefault(s => s.View == view);
        if (session is not null && !session.Disposed) { session.PreviewOpen = false; session.PreviewAudio = false; session.Host.Content = view; }
    }
    private readonly Dictionary<Guid, long> revisions = [];
    public void Stop(Guid id, string reason = "operator")
    {
        if (!sessions.Remove(id, out var s)) return;
        if (Environment.GetEnvironmentVariable("NEWSPLAY_TRACE") == "1") File.AppendAllText(Path.Combine(client.DataDirectory, "browser-lifecycle.log"), $"{DateTimeOffset.Now:O} {id} Stop {reason} preview={s.PreviewOpen} air={s.OnAir}\n");
        s.Disposed = true; s.Timer.Stop();
        if (s.View.Parent is System.Windows.Controls.Viewbox box) box.Child = null;
        s.View.Dispose(); s.Host.Close();
    }    public async void Sync(EngineState state)
    {
        if (disposed) return;
        var version = ++syncVersion;
        Ndi.Sync(state);
        foreach (var asset in state.Media.Where(m => m.Live?.Type == "Browser").ToArray())
        {
            if (revisions.TryGetValue(asset.Id, out var revision) && revision != asset.LiveReleaseRevision && !LiveUsage.IsOnAir(state, asset.Id)) Stop(asset.Id, "release revision");
            revisions[asset.Id] = asset.LiveReleaseRevision;
            if (LiveUsage.IsOnAir(state, asset.Id))
                try { await Ensure(asset.Id, asset.Path); } catch { }
        }        foreach (var session in sessions.Values.ToArray())
        {
            if (disposed || version != syncVersion) return;
            if (session.Disposed || session.Initialization?.IsCompleted != true || session.View.CoreWebView2 is not { } core) continue;
            var asset = state.Media.FirstOrDefault(m => m.Id == session.Id);
            if ((asset is null && revisions.ContainsKey(session.Id)) || (asset is not null && asset.Live?.Type != "Browser")) { Stop(session.Id, "profile removed or type changed"); continue; }
            var onAir = LiveUsage.IsOnAir(state, session.Id);
            var referenced = state.Rundowns.SelectMany(p => p.Items).Any(i => i.MediaId == session.Id);
            if (!session.PreviewOpen && !onAir && !referenced) { Stop(session.Id, "unused"); continue; }
            SetStandby(session, !session.PreviewOpen && !onAir);
            var channels = state.Channels.Where(c => c.Status == "Playing" && !c.Emergency &&
                (c.LiveInputId == session.Id || (c.LiveInputId is null && state.Rundowns.FirstOrDefault(p => p.Id == c.RundownId)?.Items.Any(i => i.Id == c.CurrentId && i.MediaId == session.Id) == true))).ToArray();
            session.OnAir = channels.Length > 0;
            var audible = channels.Any(c => !c.Mute) && asset?.Live?.Mute != true;
            // Preview listening never overrides a muted on-air port.
            core.IsMuted = !(session.OnAir ? audible : session.PreviewAudio);
        }
    }
    public void SetPreviewAudio(Session session, bool enabled) { session.PreviewAudio = enabled; if (!session.OnAir && session.View.CoreWebView2 is { } core) core.IsMuted = !enabled; }
    private void SetStandby(Session session, bool standby)
    {
        if (session.Standby == standby) return;
        session.Standby = standby;
        if (standby) session.Timer.Stop(); else session.Timer.Start();
        _ = UpdatePage();
        async Task UpdatePage()
        {
            await session.PageOperations.WaitAsync();
            try
            {
                if (session.Disposed) return;
                await SetScreencast(session,!standby);
                // Preserve the DOM and player selection. Pause/resume accessible HTML media,
                // including same-origin frames, instead of navigating to the original URL again.
                var action = standby
                    ? "w.__newsplayHeldMedia??=[];for(const m of w.document.querySelectorAll('video,audio'))if(!m.paused&&!m.ended){w.__newsplayHeldMedia.push(m);m.pause();}"
                    : "for(const m of w.__newsplayHeldMedia??[])if(m.isConnected)m.play().catch(()=>{});w.__newsplayHeldMedia=[];";
                await session.View.CoreWebView2.ExecuteScriptAsync("(()=>{const visit=w=>{try{" + action + "for(let i=0;i<w.frames.length;i++)visit(w.frames[i]);}catch{}};visit(window);})()");
            }
            catch (Exception ex) { if (!session.Disposed) session.Status = "Page state: " + ex.Message; }
            finally { session.PageOperations.Release(); }
        }
    }
    public void Dispose()
    {
        Ndi.Dispose();
        disposed = true;
        foreach (var s in sessions.Values) { s.Disposed = true; s.Timer.Stop(); s.View.Dispose(); s.Host.Close(); }
        sessions.Clear();
    }
}




