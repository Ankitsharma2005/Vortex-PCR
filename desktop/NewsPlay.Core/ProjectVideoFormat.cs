namespace NewsPlay.Core;

public sealed record ProjectVideoFormat(string Id, int Width, int Height, int Fps, bool Interlaced = false)
{
    public int FieldRate => Interlaced ? Fps * 2 : Fps;
    public int FrameBytes => checked(Width * Height * 4);
    public string Description => $"{Width} × {Height} · {Fps} fps" + (Interlaced ? $" / {FieldRate} fields · interlaced" : " · progressive");
    public static readonly ProjectVideoFormat[] Supported =
    [
        new("576i50",720,576,25,true), new("720p25",1280,720,25), new("720p50",1280,720,50),
        new("1080i50",1920,1080,25,true), new("1080p25",1920,1080,25), new("1080p30",1920,1080,30),
        new("1080p50",1920,1080,50), new("1080p60",1920,1080,60), new("2160p25",3840,2160,25),
        new("2160p30",3840,2160,30), new("2160p50",3840,2160,50), new("2160p60",3840,2160,60)
    ];
    public static ProjectVideoFormat Find(string id) => Supported.SingleOrDefault(f => f.Id == id)
        ?? throw new InvalidOperationException("Unsupported project video format. Choose SD through UHD 2160p60.");
    public override string ToString() => Id + " · " + Description;
}

// Field-aware letterbox conversion. Interlaced rasters never blend opposite fields.
public static class VideoRaster
{
    public static void Scale(byte[] source, int sw, int sh, byte[] target, int width, int height, bool interlaced = false)
    {
        if (sw < 1 || sh < 1) { Array.Clear(target, 0, checked(width * height * 4)); return; }
        if (source.Length < checked(sw * sh * 4) || target.Length < checked(width * height * 4)) throw new ArgumentException("Raster buffer is too small");
        if (sw == width && sh == height) { Buffer.BlockCopy(source, 0, target, 0, width * height * 4); return; }
        Array.Clear(target, 0, width * height * 4);
        var ratio = Math.Min((double)width / sw, (double)height / sh);
        var w = Math.Max(2, (int)(sw * ratio) / 2 * 2); var h = Math.Max(2, (int)(sh * ratio) / 2 * 2);
        var left = (width - w) / 2; var top = (height - h) / 2; if (interlaced) top = top / 2 * 2;
        var x0 = new int[w]; var fx = new int[w];
        for (var x = 0; x < w; x++) { var at = Math.Clamp((x + .5) * sw / w - .5, 0, sw - 1); x0[x] = (int)at; fx[x] = (int)((at - x0[x]) * 256); }
        void Row(int y)
        {
            var field = interlaced ? y % 2 : 0; var step = interlaced ? 2 : 1;
            var sy = Math.Clamp(((y / step) + .5) * (sh / step) / (h / step) - .5, 0, sh / step - 1);
            var first = (int)sy * step + field; var second = Math.Min(sh - step + field, first + step); var fy = (int)((sy - (int)sy) * 256);
            for (var x = 0; x < w; x++)
            {
                var a = (first * sw + x0[x]) * 4; var b = (first * sw + Math.Min(sw - 1, x0[x] + 1)) * 4;
                var c = (second * sw + x0[x]) * 4; var d = (second * sw + Math.Min(sw - 1, x0[x] + 1)) * 4; var to = ((y + top) * width + x + left) * 4;
                for (var ch = 0; ch < 3; ch++) { var upper = source[a + ch] * (256 - fx[x]) + source[b + ch] * fx[x]; var lower = source[c + ch] * (256 - fx[x]) + source[d + ch] * fx[x]; target[to + ch] = (byte)((upper * (256 - fy) + lower * fy + 32768) >> 16); }
                target[to + 3] = 255;
            }
        }
        if (w * h >= 1280 * 720) Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, Row);
        else for (int y = 0; y < h; y++) Row(y);
    }
    public static void CopyField(byte[] source, byte[] target, int width, int height, int parity)
    {
        var stride = checked(width * 4);
        for (int y = parity; y < height; y += 2) Buffer.BlockCopy(source, y * stride, target, y * stride, stride);
    }
    public static void Bob(byte[] source, byte[] target, int width, int height, int parity)
    {
        var stride = width * 4;
        for (int y = 0; y < height; y++) Buffer.BlockCopy(source, Math.Min(height - 1, y / 2 * 2 + parity) * stride, target, y * stride, stride);
    }
}
