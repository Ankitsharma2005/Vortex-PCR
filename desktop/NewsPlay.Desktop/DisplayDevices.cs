using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NewsPlay.Desktop;

public sealed record DisplayDevice(string Id, string Name, int Left, int Top, int Width, int Height, bool Primary);
public static class DisplayDevices
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    public static List<DisplayDevice> All()
    {
        var result = new List<DisplayDevice>();
        MonitorCallback callback = (IntPtr m, IntPtr h, ref Rect r, IntPtr p) => { var i = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" }; if (GetMonitorInfo(m, ref i)) { var b = i.Monitor; result.Add(new(i.Device, $"{i.Device} · {b.Right - b.Left} × {b.Bottom - b.Top}" + ((i.Flags & 1) != 0 ? " · Primary" : ""), b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, (i.Flags & 1) != 0)); } return true; };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new InvalidOperationException("Unable to enumerate Windows displays");
        return result;
    }
    public static void Place(Window window, DisplayDevice device)
    {
        var current = All().FirstOrDefault(d => d.Id == device.Id) ?? throw new InvalidOperationException("Display disconnected; refresh output settings");
        window.WindowState = WindowState.Normal; window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!SetWindowPos(handle, IntPtr.Zero, current.Left, current.Top, current.Width, current.Height, 0x0014)) throw new InvalidOperationException("Unable to place output on selected display");
        if (!GetWindowRect(handle, out var placed) || placed.Left != current.Left || placed.Top != current.Top || placed.Right - placed.Left != current.Width || placed.Bottom - placed.Top != current.Height) throw new InvalidOperationException("Windows did not accept the requested display bounds");
    }
}
