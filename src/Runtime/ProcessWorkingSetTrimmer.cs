using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CombatSolver;

internal readonly record struct WorkingSetTrimResult(
    bool Supported,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes);

internal static partial class ProcessWorkingSetTrimmer
{
    internal static WorkingSetTrimResult TrimCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            return new WorkingSetTrimResult(false, 0, 0);

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        if (!K32EmptyWorkingSet(process.Handle))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        process.Refresh();
        return new WorkingSetTrimResult(
            true,
            workingSetBefore,
            process.WorkingSet64);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32EmptyWorkingSet(nint processHandle);
}
