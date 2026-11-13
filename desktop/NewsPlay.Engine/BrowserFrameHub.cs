using System.Collections.Concurrent;
using System.Text;

namespace NewsPlay.Engine;

public sealed class BrowserFrameHub
{
    private readonly ConcurrentDictionary<Guid, (byte[] Jpeg, DateTimeOffset At)> frames = new();
    private readonly object signalGate = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Remove(Guid id) => frames.TryRemove(id, out _);
    public async Task Upload(Guid id, HttpContext context)
    {
        if (context.Request.ContentLength is null or > 4000000 or < 4) { context.Response.StatusCode = 413; return; }
        var jpeg = new byte[checked((int)context.Request.ContentLength.Value)];
        try { await context.Request.Body.ReadExactlyAsync(jpeg, context.RequestAborted); }
        catch (EndOfStreamException) { context.Response.StatusCode = 400; return; }
        if (jpeg.Length < 4 || jpeg.Length > 4000000 || jpeg[0] != 0xff || jpeg[1] != 0xd8) { context.Response.StatusCode = 400; return; }
        foreach (var old in frames.Where(p => (DateTimeOffset.UtcNow - p.Value.At).TotalSeconds > 60).Select(p => p.Key)) frames.TryRemove(old, out _);
        if (!frames.ContainsKey(id) && frames.Count >= 8) { context.Response.StatusCode = 429; return; }
        lock(signalGate) { frames[id] = (jpeg, DateTimeOffset.UtcNow); var previous = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); previous.TrySetResult(); } context.Response.StatusCode = 204;
    }
    public async Task Stream(Guid id, HttpContext context)
    {
        context.Response.ContentType = "multipart/x-mixed-replace; boundary=newsplayframe";
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            DateTimeOffset lastSent = DateTimeOffset.MinValue;
            while (!context.RequestAborted.IsCancellationRequested)
            {
                Task next; lock(signalGate) next = changed.Task;
                if (frames.TryGetValue(id, out var frame) && frame.At != lastSent && (DateTimeOffset.UtcNow - frame.At).TotalSeconds < 3)
                {
                    var header = Encoding.ASCII.GetBytes($"--newsplayframe\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Jpeg.Length}\r\n\r\n");
                    await context.Response.Body.WriteAsync(header, context.RequestAborted);
                    await context.Response.Body.WriteAsync(frame.Jpeg, context.RequestAborted);
                    await context.Response.Body.WriteAsync(new byte[] { 13, 10 }, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    lastSent = frame.At;
                }
                try { await next.WaitAsync(TimeSpan.FromMilliseconds(250), context.RequestAborted); } catch(TimeoutException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
