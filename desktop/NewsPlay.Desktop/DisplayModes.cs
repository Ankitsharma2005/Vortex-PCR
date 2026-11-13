using System.Runtime.InteropServices;

namespace NewsPlay.Desktop;

public sealed record DisplayMode(int Width, int Height, int Hertz, bool Interlaced)
{
    public override string ToString() => $"{Width} × {Height} · {Hertz} Hz · {(Interlaced ? "interlaced" : "progressive")}";
}
public static class DisplayModes
{
    // DEVMODEW, display union. Use the complete 220-byte Windows structure.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsEx(string device, int mode, [In, Out] byte[] data, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, byte[] data, IntPtr window, uint flags, IntPtr parameter);
    private static byte[] Data() { var data = new byte[220]; BitConverter.GetBytes((ushort)220).CopyTo(data, 68); return data; }
    private static DisplayMode Mode(byte[] data) => new(BitConverter.ToInt32(data, 172), BitConverter.ToInt32(data, 176), BitConverter.ToInt32(data, 184), (BitConverter.ToInt32(data, 180) & 2) != 0);
    public static List<DisplayMode> All(string device)
    {
        var result = new List<DisplayMode>();
        for (var index = 0; index < 1000; index++) { var data = Data(); if (!EnumDisplaySettingsEx(device, index, data, 0)) break; var mode = Mode(data); if (mode.Width > 0 && !result.Contains(mode)) result.Add(mode); }
        return result.OrderBy(m => m.Width).ThenBy(m => m.Height).ThenBy(m => m.Hertz).ToList();
    }
    public static DisplayMode Current(string device) { var data = Data(); if (!EnumDisplaySettingsEx(device, -1, data, 0)) throw new InvalidOperationException("Display mode unavailable"); return Mode(data); }
    public static void Apply(string device, DisplayMode selected)
    {
        if (!All(device).Contains(selected)) throw new InvalidOperationException("This display does not advertise that output mode");
        var data = Data(); if (!EnumDisplaySettingsEx(device, -1, data, 0)) throw new InvalidOperationException("Display disconnected");
        BitConverter.GetBytes(0x007C0000).CopyTo(data, 72); // bits/pels width/pels height/display flags/frequency
        BitConverter.GetBytes(32).CopyTo(data, 168); BitConverter.GetBytes(selected.Width).CopyTo(data, 172); BitConverter.GetBytes(selected.Height).CopyTo(data, 176);
        BitConverter.GetBytes(selected.Interlaced ? 2 : 0).CopyTo(data, 180); BitConverter.GetBytes(selected.Hertz).CopyTo(data, 184);
        if (ChangeDisplaySettingsEx(device, data, IntPtr.Zero, 2, IntPtr.Zero) != 0) throw new InvalidOperationException("Graphics driver rejected this mode");
        if (ChangeDisplaySettingsEx(device, data, IntPtr.Zero, 0, IntPtr.Zero) != 0) throw new InvalidOperationException("Could not apply display mode");
    }
}
