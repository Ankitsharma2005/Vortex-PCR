using System.Numerics;

namespace NewsPlay.Core;

// Bake expensive controls once. Frame processing uses four-point tetrahedral interpolation.
public sealed class AdvancedColourTransform
{
    private const int N = 33;
    private readonly Vector3[] cube = new Vector3[N * N * N];
    private readonly ColourCorrection c;
    public static bool Required(ColourCorrection c) => c.Lift != 0 || c.Gain != 1 || c.Offset != 0 || c.Hue != 0 || c.Black != 0 || c.White != 1 || c.ChromaLimit != 1 || c.Lut is not null || c.Qualifier || c.Mask != "None" || new[] { c.Shadows, c.Midtones, c.Highlights, c.Master }.Any(w => w.Any(v => v != 0)) || c.Curves.Any(w => w.Where((v, i) => v != i / 4d).Any()) || c.HueCurves.Any(w => w.Any(v => v != 0));
    public AdvancedColourTransform(ColourCorrection settings)
    {
        c = Json.Copy(settings);
        for (var b = 0; b < N; b++) for (var g = 0; g < N; g++) for (var r = 0; r < N; r++) cube[(b * N + g) * N + r] = Evaluate(new Vector3(r, g, b) / (N - 1));
    }
    public static Vector3 Hsl(Vector3 v)
    {
        var max = Math.Max(v.X, Math.Max(v.Y, v.Z)); var min = Math.Min(v.X, Math.Min(v.Y, v.Z)); var d = max - min; var l = (max + min) / 2;
        if (d < .000001) return new(0, 0, l);
        var h = max == v.X ? ((v.Y - v.Z) / d + (v.Y < v.Z ? 6 : 0)) : max == v.Y ? (v.Z - v.X) / d + 2 : (v.X - v.Y) / d + 4;
        return new(h * 60, d / Math.Max(.000001f, 1 - Math.Abs(2 * l - 1)), l);
    }
    public static Vector3 FromHsl(Vector3 hsl)
    {
        var h = ((hsl.X % 360) + 360) % 360 / 60; var s = Math.Clamp(hsl.Y, 0, 1); var l = Math.Clamp(hsl.Z, 0, 1);
        var chroma = (1 - Math.Abs(2 * l - 1)) * s; var x = chroma * (1 - Math.Abs(h % 2 - 1));
        var v = h < 1 ? new Vector3(chroma,x,0) : h < 2 ? new(x,chroma,0) : h < 3 ? new(0,chroma,x) : h < 4 ? new(0,x,chroma) : h < 5 ? new(x,0,chroma) : new(chroma,0,x);
        return v + new Vector3(l - chroma / 2);
    }
    private static float Curve(double[] points, float x) { x = Math.Clamp(x, 0, 1) * 4; var i = Math.Min(3, (int)x); return (float)(points[i] + (points[i + 1] - points[i]) * (x - i)); }
    private static float HueCurve(double[] points, float hue) { var x = ((hue % 360 + 360) % 360) / 60; var i = (int)x; return (float)(points[i] + (points[(i + 1) % 6] - points[i]) * (x - i)); }
    private Vector3 Evaluate(Vector3 input)
    {
        var hsl = Hsl(input); var amount = 1f;
        if (c.Qualifier)
        {
            var distance = Math.Abs(hsl.X - c.QualifierHue); distance = Math.Min(distance, 360 - distance);
            amount = (float)Math.Clamp(1 - (distance - c.QualifierWidth) / c.QualifierSoftness, 0, 1);
            if (hsl.Y < c.QualifierSatMin || hsl.Y > c.QualifierSatMax || hsl.Z < c.QualifierLumaMin || hsl.Z > c.QualifierLumaMax) amount = 0;
        }
        var values = new[] { input.X, input.Y, input.Z }; var gains = new[] { c.Red, c.Green, c.Blue };
        for (var i = 0; i < 3; i++)
        {
            var v = (values[i] - c.Black) / (c.White - c.Black);
            v += c.Lift * (1 - v) + c.Shadows[i] * (1 - v) * (1 - v) + c.Midtones[i] * 4 * v * (1 - v) + c.Highlights[i] * v * v;
            v = (v + c.Offset + c.Master[i]) * c.Gain;
            v = ((v - .5) * c.Contrast + .5 + c.Brightness) * gains[i];
            values[i] = Curve(c.Curves[i + 1], Curve(c.Curves[0], (float)Math.Pow(Math.Clamp(v, 0, 1), 1 / c.Gamma)));
        }
        var colour = new Vector3(values[0], values[1], values[2]);
        var luma = Vector3.Dot(colour, new(.2126f,.7152f,.0722f)); var newLuma = Curve(c.Curves[4], luma); colour += new Vector3(newLuma - luma);
        var result = Hsl(Vector3.Clamp(colour, Vector3.Zero, Vector3.One));
        result.X += (float)c.Hue + HueCurve(c.HueCurves[0], hsl.X) * 180;
        result.Y *= (float)c.Saturation * (1 + HueCurve(c.HueCurves[1], hsl.X)); result.Y = Math.Min(result.Y, (float)c.ChromaLimit);
        colour = FromHsl(result);
        // Equal RGB offsets change Rec.709 luma while preserving channel differences
        // until the final gamut clamp, unlike changing HSL lightness.
        colour = Vector3.Clamp(colour + new Vector3(HueCurve(c.HueCurves[2], hsl.X) * .5f), Vector3.Zero, Vector3.One);
        if (c.Lut is not null) colour = c.Lut.Sample(colour);
        return Vector3.Lerp(input, colour, amount) * 255;
    }
    public void Apply(byte[] bytes, int length, int width, int height)
    {
        for (var i = 0; i < length; i += 4)
        {
            var x = bytes[i + 2] * (32f / 255); var y = bytes[i + 1] * (32f / 255); var z = bytes[i] * (32f / 255);
            var r = Math.Min(31, (int)x); var g = Math.Min(31, (int)y); var b = Math.Min(31, (int)z); x -= r; y -= g; z -= b;
            var p = (b * N + g) * N + r; var origin = cube[p]; Vector3 colour;
            if (x >= y) colour = y >= z ? origin + x*(cube[p+1]-origin) + y*(cube[p+1+N]-cube[p+1]) + z*(cube[p+1+N+N*N]-cube[p+1+N]) : x >= z ? origin + x*(cube[p+1]-origin) + z*(cube[p+1+N*N]-cube[p+1]) + y*(cube[p+1+N+N*N]-cube[p+1+N*N]) : origin + z*(cube[p+N*N]-origin) + x*(cube[p+1+N*N]-cube[p+N*N]) + y*(cube[p+1+N+N*N]-cube[p+1+N*N]);
            else colour = x >= z ? origin + y*(cube[p+N]-origin) + x*(cube[p+1+N]-cube[p+N]) + z*(cube[p+1+N+N*N]-cube[p+1+N]) : y >= z ? origin + y*(cube[p+N]-origin) + z*(cube[p+N+N*N]-cube[p+N]) + x*(cube[p+1+N+N*N]-cube[p+N+N*N]) : origin + z*(cube[p+N*N]-origin) + y*(cube[p+N+N*N]-cube[p+N*N]) + x*(cube[p+1+N+N*N]-cube[p+N+N*N]);
            if (c.Mask != "None" && width > 0 && height > 0)
            {
                var px = ((i / 4 % width + .5) / width - c.MaskX) / (c.MaskWidth / 2); var py = ((i / 4 / width + .5) / height - c.MaskY) / (c.MaskHeight / 2);
                var distance = c.Mask == "Ellipse" ? Math.Sqrt(px * px + py * py) : Math.Max(Math.Abs(px), Math.Abs(py));
                var weight = Math.Clamp((1 - distance) / Math.Max(.001, c.MaskFeather), 0, 1); if (c.MaskInvert) weight = 1 - weight;
                colour = Vector3.Lerp(new(bytes[i+2],bytes[i+1],bytes[i]), colour, (float)weight);
            }
            bytes[i] = (byte)Math.Clamp((int)MathF.Round(colour.Z),0,255); bytes[i+1] = (byte)Math.Clamp((int)MathF.Round(colour.Y),0,255); bytes[i+2] = (byte)Math.Clamp((int)MathF.Round(colour.X),0,255);
        }
    }
}
