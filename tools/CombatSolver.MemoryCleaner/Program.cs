using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CombatSolver.MemoryCleaner;

internal static class Program
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorSuccess = 0;
    private const int SystemMemoryListInformation = 80;
    private const int ExitPrivilegeUnavailable = 10;
    private const int ExitEmptyWorkingSetsFailed = 20;
    private const int ExitPurgeStandbyListFailed = 21;

    private enum SystemMemoryListCommand
    {
        MemoryCaptureAccessedBits = 0,
        MemoryCaptureAndResetAccessedBits = 1,
        MemoryEmptyWorkingSets = 2,
        MemoryFlushModifiedList = 3,
        MemoryPurgeStandbyList = 4,
    }

    [STAThread]
    private static int Main()
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
            return ExitPrivilegeUnavailable;

        if (!ExecuteMemoryListCommand(SystemMemoryListCommand.MemoryEmptyWorkingSets))
            return ExitEmptyWorkingSetsFailed;
        if (!ExecuteMemoryListCommand(SystemMemoryListCommand.MemoryPurgeStandbyList))
            return ExitPurgeStandbyListFailed;
        return ErrorSuccess;
    }

    private static bool EnablePrivilege(string privilegeName)
    {
        using Process process = Process.GetCurrentProcess();
        if (!OpenProcessToken(
                process.Handle,
                TokenAdjustPrivileges | TokenQuery,
                out nint token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out Luid luid))
                return false;
            TokenPrivileges privileges = new()
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };
            if (!AdjustTokenPrivileges(
                    token,
                    disableAllPrivileges: false,
                    ref privileges,
                    bufferLength: 0,
                    previousState: IntPtr.Zero,
                    returnLength: IntPtr.Zero))
            {
                return false;
            }
            return Marshal.GetLastWin32Error() == ErrorSuccess;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool ExecuteMemoryListCommand(SystemMemoryListCommand command)
    {
        int value = (int)command;
        int status = NtSetSystemInformation(
            SystemMemoryListInformation,
            ref value,
            sizeof(int));
        return status >= 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        internal uint PrivilegeCount;
        internal Luid Luid;
        internal uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out nint tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(
        string? systemName,
        string name,
        out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(
        int systemInformationClass,
        ref int systemInformation,
        int systemInformationLength);
}
