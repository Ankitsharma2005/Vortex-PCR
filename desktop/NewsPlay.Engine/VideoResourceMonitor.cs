using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NewsPlay.Engine;

internal static class VideoResourceMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public static long AvailableMemory
    {
        get { var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() }; return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
    }
    private static readonly object gate = new();
    private static long lastWall = Stopwatch.GetTimestamp();
    private static TimeSpan lastCpu;
    private static bool sampled;
    public static object Snapshot()
    {
        using var process = Process.GetCurrentProcess();
        lock (gate)
        {
            var wall = Stopwatch.GetTimestamp(); var cpu = process.TotalProcessorTime;
            var seconds = (wall - lastWall) / (double)Stopwatch.Frequency;
            var percent = !sampled || seconds <= 0 ? 0 : Math.Clamp((cpu - lastCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100);
            lastWall = wall; lastCpu = cpu; sampled = true;
            return new { cpuPercent = percent, workingMemoryBytes = process.WorkingSet64, availableMemoryBytes = AvailableMemory, gpuUtilizationPercent = (double?)null };
        }
    }
}
