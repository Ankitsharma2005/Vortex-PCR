namespace NewsPlay.Core;

public sealed record HardwareDecodeSettings(string Mode = "Auto")
{
    public static readonly string[] Modes = ["Auto", "D3D11VA", "DXVA2", "CPU"];
    public void Validate() { if (!Modes.Contains(Mode)) throw new InvalidOperationException("Choose Auto, D3D11VA, DXVA2 or CPU decoding"); }
    public string VlcBackend(bool forceSoftware = false)
    {
        Validate();
        return forceSoftware ? "none" : Mode switch { "D3D11VA" => "d3d11va", "DXVA2" => "dxva2", "CPU" => "none", _ => "any" };
    }
}
public sealed record HardwareDecodeProbeRequest(string Mode = "Auto", string Codec = "h264", int Width = 1280, int Height = 720);
public sealed record HardwareDecodeProbeResult(bool Success, string Mode, string Codec, int Width, int Height, bool HardwareUsed, string? Backend, long DecodedFrames, string Message);
