using System.IO.MemoryMappedFiles;

namespace NewsPlay.Engine;

internal sealed class RawPreview : IDisposable
{
    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private readonly Mutex mutex;
    private long sequence;
    public volatile bool Enabled;
    public RawPreview(string instance, int slot)
    {
        map = MemoryMappedFile.CreateOrOpen($"Local\\NewsPlay.{instance}.CH{slot}", VlcDecoder.Capacity);
        view = map.CreateViewAccessor(); mutex = new Mutex(false, $"Local\\NewsPlay.{instance}.CH{slot}.Lock");
    }
    public void Publish(byte[] pixels, int width, int height, Guid media)
    {
        if (!Enabled) return; var held = false;
        try
        {
            try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) return;
            view.WriteArray(64, pixels, 0, width * height * 4); view.WriteArray(24, media.ToByteArray(), 0, 16);
            view.Write(8, width); view.Write(12, height); view.Write(16, width * 4); view.Write(0, ++sequence);
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }
    public void Dispose() { mutex.Dispose(); view.Dispose(); map.Dispose(); }
}
