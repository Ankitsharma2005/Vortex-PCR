using System.Globalization;
using System.Numerics;

namespace NewsPlay.Core;

public sealed class CubeLut
{
    public string Name { get; set; } = "LUT";
    public int Size { get; set; }
    public float[] Values { get; set; } = [];
    public float[] Minimum { get; set; } = [0, 0, 0];
    public float[] Maximum { get; set; } = [1, 1, 1];
    public float[] Shaper { get; set; } = [];
    public void Validate()
    {
        if (Size < 2 || Size > 65 || Values is null || Values.Length != Size * Size * Size * 3) throw new InvalidOperationException("LUT must contain a complete 2–65 point 3D cube");
        if (Minimum is null || Maximum is null || Minimum.Length != 3 || Maximum.Length != 3) throw new InvalidOperationException("Invalid LUT domain");
        for (var i = 0; i < 3; i++) { PlayoutController.Number(Minimum[i], -16, 16); PlayoutController.Number(Maximum[i], Minimum[i] + .00001, 16); }
        foreach (var v in Values) PlayoutController.Number(v, -16, 16);
        if (Shaper is null || (Shaper.Length != 0 && (Shaper.Length != Size || Shaper.Any(v => !float.IsFinite(v) || v < 0 || v > 1) || Shaper[0] != 0 || Shaper[^1] != 1 || Shaper.Zip(Shaper.Skip(1)).Any(p => p.First >= p.Second)))) throw new InvalidOperationException("Invalid 3DL input mesh");
    }
    public static CubeLut Parse(string text, string name, int? outputBits = null)
    {
        if (text.Length > 32 * 1024 * 1024) throw new InvalidOperationException("LUT exceeds 32 MB");
        var lut = new CubeLut { Name = name }; var rows = new List<float[]>();
        var is3dl = name.EndsWith(".3dl", StringComparison.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim(); if (line.Length == 0 || line.StartsWith("TITLE ")) continue;
            var cells = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cells[0] == "LUT_3D_SIZE" && cells.Length == 2) { lut.Size = int.Parse(cells[1], CultureInfo.InvariantCulture); continue; }
            if (cells[0] is "DOMAIN_MIN" or "DOMAIN_MAX" && cells.Length == 4) { var v = cells.Skip(1).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); if (cells[0] == "DOMAIN_MIN") lut.Minimum = v; else lut.Maximum = v; continue; }
            if (cells[0].StartsWith("LUT_1D") || cells[0] == "LUT_3D_INPUT_RANGE") throw new InvalidOperationException("Export a 3D-only .cube LUT with DOMAIN_MIN/MAX");
            if (!float.TryParse(cells[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) throw new InvalidOperationException("Unsupported LUT header: " + cells[0] + ". Export a plain 3D .cube or mesh-based .3dl file.");
            rows.Add(cells.Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray());
        }
        if (is3dl)
        {
            if (rows.Count == 0 || rows[0].Length < 2) throw new InvalidOperationException("3DL input mesh missing");
            var mesh = rows[0]; rows.RemoveAt(0); lut.Size = mesh.Length;
            if (mesh[^1] <= 0) throw new InvalidOperationException("Invalid 3DL mesh");
            lut.Shaper = mesh.Select(x => x / mesh[^1]).ToArray();
            if (lut.Size > 65 || rows.Count != lut.Size * lut.Size * lut.Size || rows.Any(r => r.Length != 3)) throw new InvalidOperationException("Incomplete 3DL cube");
            var max = rows.SelectMany(r => r).Max(); if (max < 0 || max > 65535 || rows.SelectMany(r => r).Any(v => v < 0 || v != MathF.Floor(v))) throw new InvalidOperationException("3DL requires unsigned integer output samples");
            if(outputBits is not null && outputBits is not (8 or 10 or 12 or 16))throw new InvalidOperationException("3DL output precision must be 8, 10, 12 or 16 bits");
            var scale = outputBits is { } bits ? (1 << bits) - 1 : Math.Max(1,MathF.Pow(2, MathF.Ceiling(MathF.Log2(max + 1))) - 1);
            if(max>scale)throw new InvalidOperationException("3DL data exceeds the selected output bit depth");
            lut.Values = new float[rows.Count * 3];
            for (var r = 0; r < lut.Size; r++) for (var g = 0; g < lut.Size; g++) for (var b = 0; b < lut.Size; b++)
                for (var c = 0; c < 3; c++) lut.Values[((b * lut.Size + g) * lut.Size + r) * 3 + c] = rows[(r * lut.Size + g) * lut.Size + b][c] / scale;
        }
        else { if (rows.Any(r => r.Length != 3)) throw new InvalidOperationException("Each cube row requires three values"); lut.Values = rows.SelectMany(r => r).ToArray(); }
        lut.Validate(); return lut;
    }
    private float Coordinate(float v, int channel)
    {
        v = Math.Clamp((v - Minimum[channel]) / (Maximum[channel] - Minimum[channel]), 0, 1);
        if (Shaper.Length == 0) return v * (Size - 1);
        var i = 0; while (i < Size - 2 && v > Shaper[i + 1]) i++;
        return i + (v - Shaper[i]) / (Shaper[i + 1] - Shaper[i]);
    }
    public Vector3 Sample(Vector3 v)
    {
        var x = Coordinate(v.X, 0); var y = Coordinate(v.Y, 1); var z = Coordinate(v.Z, 2);
        var r = Math.Min(Size - 2, (int)x); var g = Math.Min(Size - 2, (int)y); var b = Math.Min(Size - 2, (int)z);
        Vector3 At(int rr, int gg, int bb) { var i = ((bb * Size + gg) * Size + rr) * 3; return new(Values[i], Values[i + 1], Values[i + 2]); }
        return Vector3.Lerp(Vector3.Lerp(Vector3.Lerp(At(r,g,b), At(r+1,g,b), x-r), Vector3.Lerp(At(r,g+1,b), At(r+1,g+1,b), x-r), y-g), Vector3.Lerp(Vector3.Lerp(At(r,g,b+1), At(r+1,g,b+1), x-r), Vector3.Lerp(At(r,g+1,b+1), At(r+1,g+1,b+1), x-r), y-g), z-b);
    }
}
