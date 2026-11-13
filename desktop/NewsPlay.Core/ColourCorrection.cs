namespace NewsPlay.Core;

public sealed class ColourCorrection
{
    public double Lift { get; set; }
    public double Gain { get; set; } = 1;
    public double Offset { get; set; }
    public double Hue { get; set; }
    public double Black { get; set; }
    public double White { get; set; } = 1;
    public double[] Shadows { get; set; } = [0, 0, 0];
    public double[] Midtones { get; set; } = [0, 0, 0];
    public double[] Highlights { get; set; } = [0, 0, 0];
    public double[] Master { get; set; } = [0, 0, 0];
    public double[][] Curves { get; set; } = Enumerable.Range(0, 5).Select(_ => new double[] { 0, .25, .5, .75, 1 }).ToArray();
    public double[][] HueCurves { get; set; } = Enumerable.Range(0, 3).Select(_ => new double[6]).ToArray();
    public bool Qualifier { get; set; }
    public double QualifierHue { get; set; } = 25;
    public double QualifierWidth { get; set; } = 30;
    public double QualifierSoftness { get; set; } = 15;
    public double QualifierSatMin { get; set; }
    public double QualifierSatMax { get; set; } = 1;
    public double QualifierLumaMin { get; set; }
    public double QualifierLumaMax { get; set; } = 1;
    public string Mask { get; set; } = "None";
    public double MaskX { get; set; } = .5;
    public double MaskY { get; set; } = .5;
    public double MaskWidth { get; set; } = 1;
    public double MaskHeight { get; set; } = 1;
    public double MaskFeather { get; set; } = .1;
    public bool MaskInvert { get; set; }
    public double ChromaLimit { get; set; } = 1;
    public CubeLut? Lut { get; set; }
    public bool Enabled { get; set; } = true;
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public double Gamma { get; set; } = 1;
    public double Red { get; set; } = 1;
    public double Green { get; set; } = 1;
    public double Blue { get; set; } = 1;
    public void Validate()
    {
        foreach (var v in new[] { Lift, Offset }) PlayoutController.Number(v, -1, 1);
        PlayoutController.Number(Gain, 0, 2); PlayoutController.Number(Hue, -180, 180);
        PlayoutController.Number(Black, 0, .99); PlayoutController.Number(White, Black + .001, 1);
        foreach (var wheel in new[] { Shadows, Midtones, Highlights, Master }) { if (wheel is null || wheel.Length != 3) throw new InvalidOperationException("Wheel requires RGB values"); foreach (var v in wheel) PlayoutController.Number(v, -1, 1); }
        if (Curves is null || Curves.Length != 5 || Curves.Any(c => c is null || c.Length != 5)) throw new InvalidOperationException("Five RGB/luma curves required");
        foreach (var v in Curves.SelectMany(c => c)) PlayoutController.Number(v, 0, 1);
        if (HueCurves is null || HueCurves.Length != 3 || HueCurves.Any(c => c is null || c.Length != 6)) throw new InvalidOperationException("Three hue curves required");
        foreach (var v in HueCurves.SelectMany(c => c)) PlayoutController.Number(v, -1, 1);
        PlayoutController.Number(QualifierHue, 0, 360); PlayoutController.Number(QualifierWidth, 0, 180); PlayoutController.Number(QualifierSoftness, .1, 180);
        foreach (var v in new[] { QualifierSatMin, QualifierSatMax, QualifierLumaMin, QualifierLumaMax, MaskX, MaskY, MaskFeather, ChromaLimit }) PlayoutController.Number(v, 0, 1);
        if (QualifierSatMin > QualifierSatMax || QualifierLumaMin > QualifierLumaMax) throw new InvalidOperationException("Qualifier minimum must not exceed maximum");
        if (Mask is not ("None" or "Ellipse" or "Rectangle")) throw new InvalidOperationException("Unknown power window");
        PlayoutController.Number(MaskWidth, .01, 2); PlayoutController.Number(MaskHeight, .01, 2);
        Lut?.Validate();
        PlayoutController.Number(Brightness, -1, 1);
        foreach (var value in new[] { Contrast, Saturation, Red, Green, Blue }) PlayoutController.Number(value, 0, 2);
        PlayoutController.Number(Gamma, .2, 3);
    }
}

// Immutable transform: computed once per adjustment, shared safely with frame callbacks.
public sealed class ColourTransform
{
    private readonly AdvancedColourTransform? advanced;
    private readonly byte[] red = new byte[256], green = new byte[256], blue = new byte[256];
    private readonly int saturation;
    public bool IsIdentity { get; }
    public ColourTransform(ColourCorrection correction)
    {
        correction.Validate();
        if (correction.Enabled && AdvancedColourTransform.Required(correction)) { advanced = new(correction); return; }
        IsIdentity = !correction.Enabled || (correction.Brightness == 0 && correction.Contrast == 1 && correction.Saturation == 1 && correction.Gamma == 1 && correction.Red == 1 && correction.Green == 1 && correction.Blue == 1);
        saturation = (int)Math.Round(correction.Saturation * 256);
        byte Map(int v, double gain) => (byte)Math.Clamp((int)Math.Round(Math.Pow(Math.Clamp(((v / 255d - .5) * correction.Contrast + .5 + correction.Brightness) * gain, 0, 1), 1 / correction.Gamma) * 255), 0, 255);
        for (var i = 0; i < 256; i++) { red[i] = Map(i, correction.Red); green[i] = Map(i, correction.Green); blue[i] = Map(i, correction.Blue); }
    }
    public void Apply(byte[] bgra, int length, int width = 0, int height = 0)
    {
        if (advanced is not null) { advanced.Apply(bgra, length, width, height); return; }
        if (IsIdentity) return;
        for (var i = 0; i < length; i += 4)
        {
            int b = blue[bgra[i]], g = green[bgra[i + 1]], r = red[bgra[i + 2]];
            if (saturation != 256)
            {
                var luma = (54 * r + 183 * g + 19 * b + 128) >> 8;
                b = Math.Clamp(luma + ((b - luma) * saturation >> 8), 0, 255);
                g = Math.Clamp(luma + ((g - luma) * saturation >> 8), 0, 255);
                r = Math.Clamp(luma + ((r - luma) * saturation >> 8), 0, 255);
            }
            bgra[i] = (byte)b; bgra[i + 1] = (byte)g; bgra[i + 2] = (byte)r;
        }
    }
}
