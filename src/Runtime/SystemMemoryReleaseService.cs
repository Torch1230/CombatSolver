using System.ComponentModel;
using System.Diagnostics;

namespace CombatSolver;

internal static class SystemMemoryReleaseService
{
    private const int UacCancelledError = 1223;
    private const string HelperFileName = "CombatSolver.MemoryCleaner.exe";
    private static readonly Lock Gate = new();
    private static Task _activeRelease = Task.CompletedTask;

    internal static Task ReleaseAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("深度释放系统内存只支持 Windows。");

        lock (Gate)
        {
            if (!_activeRelease.IsCompleted)
                return _activeRelease;
            _activeRelease = ReleaseCoreAsync();
            return _activeRelease;
        }
    }

    private static async Task ReleaseCoreAsync()
    {
        Entry.Logger.Info("[CombatSolver/Test] SYSTEM_MEMORY_RELEASE stage=requested");
        await SearchGcPolicy.ForceManualProcessMemoryRelease().ConfigureAwait(false);

        string assemblyDirectory = Path.GetDirectoryName(
            typeof(SystemMemoryReleaseService).Assembly.Location)
            ?? throw new InvalidOperationException("无法确定 CombatSolver 程序集目录。");
        string helperPath = Path.Combine(assemblyDirectory, HelperFileName);
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("找不到系统内存释放辅助程序。", helperPath);

        ProcessStartInfo startInfo = new()
        {
            FileName = helperPath,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process helper;
        try
        {
            helper = Process.Start(startInfo)
                ?? throw new InvalidOperationException("系统内存释放辅助程序没有启动。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UacCancelledError)
        {
            throw new OperationCanceledException("玩家取消了管理员权限请求。", ex);
        }

        using (helper)
        {
            await helper.WaitForExitAsync().ConfigureAwait(false);
            if (helper.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"系统内存释放辅助程序失败，退出码 {helper.ExitCode}。");
            }
        }
        Entry.Logger.Info("[CombatSolver/Test] SYSTEM_MEMORY_RELEASE stage=finished success=true");
    }
}
