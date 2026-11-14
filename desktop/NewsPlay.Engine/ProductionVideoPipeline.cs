using System.Diagnostics;
using NewsPlay.Core;

namespace NewsPlay.Engine;

// A paced production raster, separate from the unchanged 960x540 operator preview.
public sealed class ProductionVideoPipeline : IDisposable
{
    private readonly object sourceGate = new(), outputGate = new();
    private readonly byte[] source, snapshot, converted, progressive, upper, lower, workingUpper, workingLower;
    private readonly CancellationTokenSource cancel = new();
    private readonly Task worker;
    private readonly Func<long> clock;
    private int sourceWidth, sourceHeight;
    private long sourceClock, outputClock, wovenClock, firstFieldClock, submitted, produced, missed, repeated, lastSource;
    public ProjectVideoFormat Format { get; }
    public long Produced => Interlocked.Read(ref produced);
    public long MissedDeadlines => Interlocked.Read(ref missed);
    public long RepeatedFrames => Interlocked.Read(ref repeated);
    public double ProcessingMs { get; private set; }
    public ProductionVideoPipeline(ProjectVideoFormat format, Func<long> clock)
    {
        Format = format; this.clock = clock;
        source = new byte[format.FrameBytes]; snapshot = new byte[format.FrameBytes]; converted = new byte[format.FrameBytes]; progressive = new byte[format.FrameBytes];
        var weave = format.Interlaced || format.Fps >= 50;
        upper = weave ? new byte[format.FrameBytes] : progressive;
        lower = weave ? new byte[format.FrameBytes] : progressive;
        workingUpper = weave ? new byte[format.FrameBytes] : progressive;
        workingLower = weave ? new byte[format.FrameBytes] : progressive;
        worker = Task.Run(Run);
    }
    public void Submit(byte[] pixels, int width, int height, long presentationClock)
    {
        if (width * height * 4 > source.Length) throw new ArgumentException("Source exceeds configured production raster");
        lock (sourceGate) { Buffer.BlockCopy(pixels, 0, source, 0, width * height * 4); sourceWidth = width; sourceHeight = height; sourceClock = presentationClock > 0 ? presentationClock : clock(); submitted++; }
    }
    public void Clear()
    {
        lock (sourceGate) { sourceWidth = sourceHeight = 0; sourceClock = 0; submitted++; }
    }
    public bool Copy(byte[] target, out int width, out int height, out long presentationClock, bool interlaced = false, bool lowerFieldFirst = false)
    {
        width = Format.Width; height = Format.Height;
        lock (outputGate)
        {
            presentationClock = interlaced && (Format.Interlaced || Format.Fps >= 50) ? wovenClock : outputClock;
            if (target.Length < Format.FrameBytes) throw new ArgumentException("Production destination buffer is too small");
            Buffer.BlockCopy(interlaced ? (lowerFieldFirst ? lower : upper) : progressive, 0, target, 0, Format.FrameBytes); return true;
        }
    }
    private async Task Run()
    {
        var timer = Stopwatch.StartNew(); long tick = 0; int field = 0;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var due = tick * 1000d / Format.FieldRate;
                var wait = due - timer.Elapsed.TotalMilliseconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), cancel.Token);
                var work = Stopwatch.StartNew(); int sw, sh; long pts, version;
                lock (sourceGate) { sw = sourceWidth; sh = sourceHeight; pts = sourceClock; version = submitted; if (sw > 0 && sh > 0) Buffer.BlockCopy(source, 0, snapshot, 0, sw * sh * 4); }
                // Scale once per tick; retain two time-separated fields for interlaced output.
                var scaled = snapshot;
                if (sw != Format.Width || sh != Format.Height)
                {
                    // The source storage is no longer used until the next tick.
                    VideoRaster.Scale(snapshot, sw, sh, converted, Format.Width, Format.Height);
                    scaled = converted;
                }
                lock (outputGate)
                {
                    if (version == lastSource && pts > 0) Interlocked.Increment(ref repeated);
                    if (!ReferenceEquals(scaled, progressive)) Buffer.BlockCopy(scaled, 0, progressive, 0, Format.FrameBytes);
                    if (Format.Interlaced || Format.Fps >= 50)
                    {
                        if (field == 0) firstFieldClock = pts;
                        VideoRaster.CopyField(progressive, workingUpper, Format.Width, Format.Height, field);
                        VideoRaster.CopyField(progressive, workingLower, Format.Width, Format.Height, 1 - field);
                        if (field == 1) { Buffer.BlockCopy(workingUpper, 0, upper, 0, Format.FrameBytes); Buffer.BlockCopy(workingLower, 0, lower, 0, Format.FrameBytes); wovenClock = firstFieldClock; if (Format.Interlaced) Interlocked.Increment(ref produced); }
                        field = 1 - field;
                    }
                    if (!Format.Interlaced) Interlocked.Increment(ref produced);
                    outputClock = pts; lastSource = version;
                }
                ProcessingMs = work.Elapsed.TotalMilliseconds;
                tick++;
                var behind = (long)(timer.Elapsed.TotalSeconds * Format.FieldRate) - tick;
                if (behind > 0) { Interlocked.Add(ref missed, behind); tick += behind; }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { cancel.Cancel(); worker.GetAwaiter().GetResult(); cancel.Dispose(); }
}
