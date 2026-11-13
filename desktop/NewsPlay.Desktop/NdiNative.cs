using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NewsPlay.Desktop;

// NDI C ABI: SDK receive-v2 audio is planar float; receive-v2 video is BGRX/BGRA.
internal static class NdiNative
{
    private const string Library = "Processing.NDI.Lib.x64.dll";
    static NdiNative() => NativeLibrary.SetDllImportResolver(typeof(NdiNative).Assembly, Resolve);
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? search)
    {
        if (name != Library) return IntPtr.Zero;
        var paths = new[] { AppContext.BaseDirectory, Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6"), Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V5"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NDI", "NDI 6 Runtime"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NDI", "NDI 5 Runtime") };
        foreach (var path in paths.Concat(new[] { Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6", EnvironmentVariableTarget.Machine), Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6", EnvironmentVariableTarget.User) }).Where(p => !string.IsNullOrEmpty(p))) if (NativeLibrary.TryLoad(Path.Combine(path!, Library), out var handle)) return handle;
        return IntPtr.Zero;
    }
    public static void Check()
    {
        try { if (!NDIlib_initialize()) throw new InvalidOperationException("NDI runtime could not initialize on this CPU."); }
        catch (DllNotFoundException) { throw new InvalidOperationException("Install the official NDI 6 x64 Runtime, then restart NewsPlay. NDI capture is unavailable without it."); }
        catch (BadImageFormatException) { throw new InvalidOperationException("NDI requires the x64 runtime."); }
    }
    public static async Task<string[]> Discover()
    {
        return await Task.Run(() =>
        {
            Check(); var finder = NDIlib_find_create_v2(IntPtr.Zero);
            if (finder == IntPtr.Zero) throw new InvalidOperationException("Cannot start NDI discovery");
            try
            {
                // Keep the finder alive while mDNS sources arrive, rather than returning on the first source.
                var until = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < until) NDIlib_find_wait_for_sources(finder, 250);
                var pointer = NDIlib_find_get_current_sources(finder, out var count);
                var names = new List<string>();
                for (var i = 0; i < Math.Min(count, 1024u); i++)
                {
                    var source = Marshal.PtrToStructure<Source>(pointer + i * Marshal.SizeOf<Source>());
                    if (Marshal.PtrToStringUTF8(source.Name) is { } name) names.Add(name);
                }
                return names.ToArray();
            }
            finally { NDIlib_find_destroy(finder); }
        });
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Source { public IntPtr Name, Address; }
    [StructLayout(LayoutKind.Sequential)] internal struct SenderSettings { public IntPtr Name, Groups; [MarshalAs(UnmanagedType.I1)] public bool ClockVideo; [MarshalAs(UnmanagedType.I1)] public bool ClockAudio; }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr NDIlib_send_create(ref SenderSettings settings);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void NDIlib_send_send_video_v2(IntPtr sender, ref Video video);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void NDIlib_send_send_audio_v2(IntPtr sender, ref Audio audio);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void NDIlib_send_destroy(IntPtr sender);
    [StructLayout(LayoutKind.Sequential)] internal struct ReceiverSettings { public Source Source; public int Color, Bandwidth; [MarshalAs(UnmanagedType.I1)] public bool Fields; public IntPtr Name; }
    [StructLayout(LayoutKind.Sequential)] internal struct Video { public int Width, Height, FourCC, RateN, RateD; public float Aspect; public int Format; public long Timecode; public IntPtr Data; public int Stride; public IntPtr Metadata; public long Timestamp; }
    [StructLayout(LayoutKind.Sequential)] internal struct Audio { public int Rate, Channels, Samples; public long Timecode; public IntPtr Data; public int Stride; public IntPtr Metadata; public long Timestamp; }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool NDIlib_initialize();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr NDIlib_find_create_v2(IntPtr settings);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool NDIlib_find_wait_for_sources(IntPtr finder, uint timeout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr NDIlib_find_get_current_sources(IntPtr finder, out uint count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void NDIlib_find_destroy(IntPtr finder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr NDIlib_recv_create_v3(ref ReceiverSettings settings);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int NDIlib_recv_capture_v2(IntPtr receiver, ref Video video, ref Audio audio, IntPtr metadata, uint timeout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void NDIlib_recv_free_video_v2(IntPtr receiver, ref Video video);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void NDIlib_recv_free_audio_v2(IntPtr receiver, ref Audio audio);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void NDIlib_recv_destroy(IntPtr receiver);
}
