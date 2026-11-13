using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using NewsPlay.Core;

namespace NewsPlay.Desktop;

public partial class MainWindow
{
    private async Task TestBrowserWorkflow()
    {
        if (!Environment.GetCommandLineArgs().Contains("--browser-smoke")) return;
        using var cancel = new CancellationTokenSource();
        var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/";
        var serve = Task.Run(async () =>
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    using var connection = await server.AcceptTcpClientAsync(cancel.Token);
                    try
                    {
                    using var stream = connection.GetStream(); var request = new byte[4096]; await stream.ReadAsync(request, cancel.Token);
                    var html = Encoding.UTF8.GetBytes("<!doctype html><body style='background:#16354f;color:white;font:28px Arial;padding:20px'><h1 id='result'>Browser source ready</h1><input id='entry' style='font-size:28px'><button id='go' style='font-size:28px' onclick=\"document.getElementById('result').textContent=document.getElementById('entry').value;document.body.style.background='#216339'\">Update</button><p>Interactive browser playout test</p></body>");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n"), cancel.Token); await stream.WriteAsync(html, cancel.Token);
                    }
                    catch (IOException) { /* Browser may cancel speculative/favicon requests. Keep serving the fixture. */ }
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (cancel.IsCancellationRequested) { }
        });
        var editor = new LiveInputsWindow(client, Send, 1, BrowserInputs) { Owner = this, Left = -18000, Top = -18000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            editor.Show(); await Task.Delay(800);
            var session = await editor.TestBrowserSave(url);
            for (var i = 0; i < 150 && session.Frames < 2; i++) await Task.Delay(100);
            if (session.Frames < 2) throw new InvalidOperationException("Browser capture failed: " + session.Status + $" disposed={session.Disposed} ready={session.Ready} capturing={session.Capturing} timer={session.Timer.IsEnabled}");
            await session.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('entry').focus()");
            await session.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"Keyboard and mouse verified\"}");
            var bounds = await session.View.CoreWebView2.ExecuteScriptAsync("(()=>{let r=document.getElementById('go').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()");
            using var xy = JsonDocument.Parse(bounds); var x = xy.RootElement.GetProperty("x").GetDouble(); var y = xy.RootElement.GetProperty("y").GetDouble();
            foreach (var type in new[] { "mousePressed", "mouseReleased" }) await session.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = "left", clickCount = 1 }));
            if (!(await session.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('result').textContent")).Contains("Keyboard and mouse verified")) throw new InvalidOperationException("Browser keyboard/mouse interaction failed");
            await editor.TestBrowserResave();
            if (BrowserInputs.Find(session.Id) != session) throw new InvalidOperationException("Saving existing profile replaced browser session");
            await Task.Delay(500);
            await using (var shot = File.Create(smokeFile + ".browser.png")) await session.View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);
            editor.Close(); await Task.Delay(800); if (BrowserInputs.Find(session.Id) != session || !session.Standby) throw new InvalidOperationException("Saved browser lost before TAKE"); state = await client.GetState(); var item = Rundown(1).Items.Single(i => i.MediaId == session.Id);
            await Send(new EngineCommand { Action = "Take", Channel = 1, ItemId = item.Id });
            if (!(await session.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('result').textContent")).Contains("Keyboard and mouse verified")) throw new InvalidOperationException("TAKE reset selected browser content");
            var before = Channel(1).DecodedFrames;
            for (var i = 0; i < 100; i++) { await Task.Delay(100); state = await client.GetState(); if (Channel(1).DecodedFrames > before + 3) break; }
            if (Channel(1).DecodedFrames <= before + 3 || Channel(1).Status != "Playing") throw new InvalidOperationException("Browser playlist did not decode to program");
            BrowserInputs.Sync(state);
            if (session.View.CoreWebView2.IsMuted) throw new InvalidOperationException("Browser remained muted after TAKE");
            await session.View.CoreWebView2.ExecuteScriptAsync("(()=>{const c=document.createElement('canvas');c.width=640;c.height=180;document.body.append(c);const ctx=c.getContext('2d');window.motionFrames=0;const draw=()=>{motionFrames++;ctx.fillStyle='#182738';ctx.fillRect(0,0,640,180);ctx.fillStyle='#50e899';ctx.fillRect((motionFrames*7)%600,50,40,70);requestAnimationFrame(draw)};draw();})()");
            var capturedStart=session.Frames;var decodedStart=Channel(1).DecodedFrames;var measurement=System.Diagnostics.Stopwatch.StartNew();await Task.Delay(2500);state=await client.GetState();measurement.Stop();
            var captureRate=(session.Frames-capturedStart)/measurement.Elapsed.TotalSeconds;var decodeRate=(Channel(1).DecodedFrames-decodedStart)/measurement.Elapsed.TotalSeconds;
            if(session.Frames<=capturedStart+2||Channel(1).DecodedFrames<=decodedStart+2)throw new InvalidOperationException("Animated browser source stalled");
            await File.WriteAllTextAsync(smokeFile+".browser-performance.txt",$"Animated local browser: {captureRate:0.0} uploaded fps; {decodeRate:0.0} decoded fps; latest-frame replacements {session.ReplacedFrames}. This is a local smoke measurement, not 50 fps certification.");
            await session.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = "window.testAudio=new AudioContext();window.testTone=testAudio.createOscillator();window.testGain=testAudio.createGain();testGain.gain.value=.01;testTone.connect(testGain);testGain.connect(testAudio.destination);testTone.start();testAudio.resume();", userGesture = true }));
            for (var i = 0; i < 30 && !session.View.CoreWebView2.IsDocumentPlayingAudio; i++) await Task.Delay(100);
            if (!session.View.CoreWebView2.IsDocumentPlayingAudio) throw new InvalidOperationException("Browser test audio did not start");
            await Send(new EngineCommand { Action = "Mute", Channel = 1, Enabled = true });
            if (!session.View.CoreWebView2.IsMuted) throw new InvalidOperationException("Port mute did not mute browser");
            await Send(new EngineCommand { Action = "Mute", Channel = 1, Enabled = false });
            if (session.View.CoreWebView2.IsMuted) throw new InvalidOperationException("Browser did not unmute with port");
            UpdateFrames(); UpdateLayout(); CaptureWindow(this, smokeFile + ".browser-program.png");
            using (var anonymous = new System.Net.Http.HttpClient())
            {
                using var denied = await anonymous.GetAsync("http://127.0.0.1:" + (Environment.GetEnvironmentVariable("NEWSPLAY_PORT") ?? "17890") + "/browser/stream/" + session.Id);
                if (denied.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException("Browser stream accepted unauthenticated request");
            }
            editor.Close(); var captured = session.Frames; await Task.Delay(800);
            if (session.Frames <= captured) throw new InvalidOperationException("Closing editor stopped browser source");
            await Send(new EngineCommand { Action = "Stop", Channel = 1, Confirm = true });
            await Task.Delay(500);
            if (BrowserInputs.Find(session.Id) != session || !session.Standby) throw new InvalidOperationException("STOP lost saved browser page");
            var preview = await BrowserInputs.Ensure(session.Id, url, true); preview.PreviewOpen = true;
            BrowserInputs.SetPreviewAudio(preview, true);
            if (preview.View.CoreWebView2.IsMuted) throw new InvalidOperationException("Preview listening failed");
            await Send(new EngineCommand { Action = "Rundown.Remove", Channel = 1, ItemId = item.Id });
            await Task.Delay(500);
            if (BrowserInputs.Find(session.Id) is not null) throw new InvalidOperationException("Last playlist removal kept browser running");
            await Send(new EngineCommand { Action = "Live.Delete", MediaId = session.Id, Confirm = true });
            if ((await client.GetState()).Media.Any(m => m.Id == session.Id)) throw new InvalidOperationException("Live profile was not deleted");            await File.WriteAllTextAsync(smokeFile + ".browser.result.txt", "PASS: URL render, keyboard/mouse, Save adds item, native frames, editor close preserves on-air source, STOP retains saved page in standby, last rundown removal disposes preview, live profile delete removes saved entry; browser audio follows TAKE, mute/unmute and preview listen. Physical sound and A/V sync require operator verification.");
        }
        finally { editor.Close(); cancel.Cancel(); server.Stop(); await serve; }
    }
}


