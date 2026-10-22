using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CombatSolver;

internal readonly record struct ProcessMemoryUsage(
    long WorkingSetBytes,
    long PrivateBytes)
{
    public static ProcessMemoryUsage? CaptureCurrent()
    {
        if (OperatingSystem.IsWindows())
            return CaptureWindows(GetCurrentProcess());

        // Preserve the existing counters on other platforms.
        using Process process = Process.GetCurrentProcess();
        return new ProcessMemoryUsage(process.WorkingSet64, process.PrivateMemorySize64);
    }

    internal static ProcessMemoryUsage? CaptureWindows(nint processHandle)
    {
        uint size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>();
        ProcessMemoryCountersEx counters = new() { Size = size };
        if (!K32GetProcessMemoryInfo(processHandle, ref counters, size))
            return null;

        return FromWindowsCounters(in counters);
    }

    internal static ProcessMemoryUsage FromWindowsCounters(in ProcessMemoryCountersEx counters)
        => new(
            checked((long)counters.WorkingSetSize),
            checked((long)counters.PrivateUsage));

    // Windows' Process memory getters populate ProcessInfo, including process/thread
    // enumeration. This API reads only the current process's memory counters instead.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    // GetCurrentProcess returns a borrowed pseudo-handle; it must not be closed.
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(
        nint processHandle,
        ref ProcessMemoryCountersEx counters,
        uint size);
}
