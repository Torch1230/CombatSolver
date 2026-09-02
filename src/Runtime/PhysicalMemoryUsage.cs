using System.Runtime.InteropServices;

namespace CombatSolver;

internal readonly record struct PhysicalMemoryUsage(
    long UsedBytes,
    long TotalBytes)
{
    public static PhysicalMemoryUsage Capture(GCMemoryInfo fallback)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PhysicalMemoryUsage(
                Math.Max(0, fallback.MemoryLoadBytes),
                Math.Max(0, fallback.TotalAvailableMemoryBytes));
        }

        MemoryStatusEx status = new()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
        };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return new PhysicalMemoryUsage(
                Math.Max(0, fallback.MemoryLoadBytes),
                Math.Max(0, fallback.TotalAvailableMemoryBytes));
        }

        long total = status.TotalPhysicalMemory > (ulong)long.MaxValue
            ? long.MaxValue
            : (long)status.TotalPhysicalMemory;
        long available = status.AvailablePhysicalMemory > (ulong)long.MaxValue
            ? long.MaxValue
            : (long)status.AvailablePhysicalMemory;
        return new PhysicalMemoryUsage(
            Math.Max(0, total - Math.Min(total, available)),
            total);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoadPercent;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtualMemory;
        public ulong AvailableVirtualMemory;
        public ulong AvailableExtendedVirtualMemory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
