using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NewsPlay.Desktop;

public sealed class FrameReader : IDisposable
{
    private MemoryMappedFile? map;
    private MemoryMappedViewAccessor? view;
    private Mutex? mutex;
    private byte[] pixels = new byte[960 * 540 * 4];
    private long sequence = -1;
    public WriteableBitmap? Image { get; private set; }
    public Guid MediaId { get; private set; }
    public byte[] Snapshot() => Image is null ? [] : pixels.AsSpan(0, Image.PixelWidth * Image.PixelHeight * 4).ToArray();
    public bool Read(string instance, int channel)
    {
        if (view is null)
        {
#if MCR_VARIANT
            try { map = MemoryMappedFile.OpenExisting($"Local\\VortexMCR.{instance}.CH{channel}"); view = map.CreateViewAccessor(); mutex = Mutex.OpenExisting($"Local\\VortexMCR.{instance}.CH{channel}.Lock"); } catch { Dispose(); return false; }
#else
            try { map = MemoryMappedFile.OpenExisting($"Local\\NewsPlay.{instance}.CH{channel}"); view = map.CreateViewAccessor(); mutex = Mutex.OpenExisting($"Local\\NewsPlay.{instance}.CH{channel}.Lock"); } catch { Dispose(); return false; }
#endif
        }
        var held = false;
        try
        {
            try { held = mutex!.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) return false;
            var seq = view.ReadInt64(0); if (seq == sequence) return false;
            var width = view.ReadInt32(8); var height = view.ReadInt32(12); var stride = view.ReadInt32(16);
            if (width < 1 || height < 1 || width > 960 || height > 540 || stride != width * 4) return false;
            System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(view.SafeMemoryMappedViewHandle.DangerousGetHandle(), checked((int)view.PointerOffset + 64)), pixels, 0, stride * height);
            var identity = new byte[16]; view.ReadArray(24, identity, 0, 16); MediaId = new Guid(identity);
            mutex!.ReleaseMutex(); held = false;
            if (Image is null || Image.PixelWidth != width || Image.PixelHeight != height) Image = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            Image.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0); sequence = seq; return true;
        }
        finally { if (held) mutex!.ReleaseMutex(); }
    }
    public void Dispose() { mutex?.Dispose(); view?.Dispose(); map?.Dispose(); mutex = null; view = null; map = null; sequence = -1; }
}
