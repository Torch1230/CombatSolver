using System.Runtime.InteropServices;

namespace CombatSolver;

/// <summary>Actual current-thread CPU time, distinct from Stopwatch wall time and process background work.</summary>
internal static class CompactThreadCpu
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { public long Seconds; public long Nanoseconds; }

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ClockGetTime(int clock, out Timespec value);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    internal static double Milliseconds()
    {
        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            if (ClockGetTime(3, out Timespec value) != 0)
                throw new InvalidOperationException($"clock_gettime failed: {Marshal.GetLastPInvokeError()}.");
            return value.Seconds * 1000d + value.Nanoseconds / 1_000_000d;
        }
        if (OperatingSystem.IsWindows())
        {
            if (!GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user))
                throw new InvalidOperationException($"GetThreadTimes failed: {Marshal.GetLastPInvokeError()}.");
            return (kernel + user) / 10_000d;
        }
        throw new PlatformNotSupportedException("Compact CPU benchmark supports Linux x64/arm64 and Windows.");
    }
}
