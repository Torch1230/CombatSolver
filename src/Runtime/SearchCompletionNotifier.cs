using System.Runtime.InteropServices;
using Godot;

namespace CombatSolver;

internal enum SearchCompletionNotificationKind
{
    Succeeded,
    Failed,
    Canceled,
    Stale,
}

internal static class SearchCompletionNotifier
{
    private const uint NotifyIconAdd = 0x00000000;
    private const uint NotifyIconModify = 0x00000001;
    private const uint NotifyIconDelete = 0x00000002;
    private const uint NotifyIconFlagIcon = 0x00000002;
    private const uint NotifyIconFlagTip = 0x00000004;
    private const uint NotifyIconFlagInfo = 0x00000010;
    private const uint NotifyIconInfo = 0x00000001;
    private const uint NotifyIconRespectQuietTime = 0x00000080;
    private const int InformationIconResource = 32516;
    private const uint NotificationIconId = 0x43534F4C;
    private const int IconLifetimeMilliseconds = 15_000;

    private static readonly object Sync = new();
    private static System.Threading.Timer? _cleanupTimer;
    private static IntPtr _ownerWindow;
    private static bool _iconVisible;
    private static int _requestCountForTesting;
    private static int _nativeNotificationCountForTesting;

    internal static int RequestCountForTesting => Volatile.Read(ref _requestCountForTesting);
    internal static int NativeNotificationCountForTesting
        => Volatile.Read(ref _nativeNotificationCountForTesting);

    public static void Notify(SearchCompletionNotificationKind kind)
    {
        if (Entry.IsLiveAutoPilotMode)
            return;
        Interlocked.Increment(ref _requestCountForTesting);
        SolverSettingsData settings = SolverSettings.Current;
        if (!ShouldNotify(
                settings.SearchCompletionNotificationsEnabled,
                settings.SearchCompletionNotificationMode,
                IsGameForeground()))
        {
            return;
        }
        if (!OperatingSystem.IsWindows())
            return;
        if (string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase))
            return;

        if (!TryShowWindowsNotification(Message(kind)))
        {
            Entry.Logger.Warn(
                $"[CombatSolver/Test] SEARCH_COMPLETION_NOTIFICATION kind={kind} native=failed");
            return;
        }

        Interlocked.Increment(ref _nativeNotificationCountForTesting);
        Entry.Logger.Info(
            $"[CombatSolver/Test] SEARCH_COMPLETION_NOTIFICATION kind={kind} native=shown");
    }

    internal static bool ShouldNotifyForTesting(
        bool enabled,
        SolverSearchCompletionNotificationMode mode,
        bool gameForeground)
        => ShouldNotify(enabled, mode, gameForeground);

    private static bool ShouldNotify(
        bool enabled,
        SolverSearchCompletionNotificationMode mode,
        bool gameForeground)
        => enabled
           && (mode == SolverSearchCompletionNotificationMode.Always || !gameForeground);

    private static bool IsGameForeground()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;
        _ = GetWindowThreadProcessId(foreground, out uint processId);
        return processId == (uint)System.Environment.ProcessId;
    }

    private static string Message(SearchCompletionNotificationKind kind)
        => kind switch
        {
            SearchCompletionNotificationKind.Succeeded => "计算完成，推荐路线已经显示。",
            SearchCompletionNotificationKind.Failed => "计算失败，请查看游戏内提示。",
            SearchCompletionNotificationKind.Canceled => "计算已停止。",
            SearchCompletionNotificationKind.Stale => "计算结束，但战斗状态已经变化，结果未采用。",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static bool TryShowWindowsNotification(string message)
    {
        IntPtr window = (IntPtr)DisplayServer.WindowGetNativeHandle(
            DisplayServer.HandleType.WindowHandle);
        if (window == IntPtr.Zero)
            return false;

        IntPtr icon = LoadIcon(IntPtr.Zero, (IntPtr)InformationIconResource);
        NotifyIconData data = new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = window,
            Id = NotificationIconId,
            Flags = NotifyIconFlagInfo | NotifyIconFlagTip |
                    (icon == IntPtr.Zero ? 0u : NotifyIconFlagIcon),
            Icon = icon,
            Tip = "战斗路线求解器",
            Info = message,
            InfoTitle = "战斗路线求解器",
            InfoFlags = NotifyIconInfo | NotifyIconRespectQuietTime,
        };

        lock (Sync)
        {
            if (_iconVisible && _ownerWindow != window)
                RemoveIconLocked();
            uint operation = _iconVisible ? NotifyIconModify : NotifyIconAdd;
            if (!ShellNotifyIcon(operation, ref data))
                return false;
            _iconVisible = true;
            _ownerWindow = window;
            _cleanupTimer ??= new System.Threading.Timer(
                static _ => RemoveIcon(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
            _cleanupTimer.Change(IconLifetimeMilliseconds, Timeout.Infinite);
            return true;
        }
    }

    private static void RemoveIcon()
    {
        if (!OperatingSystem.IsWindows())
            return;
        lock (Sync)
            RemoveIconLocked();
    }

    private static void RemoveIconLocked()
    {
        if (!_iconVisible)
            return;
        NotifyIconData data = new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _ownerWindow,
            Id = NotificationIconId,
            Tip = string.Empty,
            Info = string.Empty,
            InfoTitle = string.Empty,
        };
        _ = ShellNotifyIcon(NotifyIconDelete, ref data);
        _iconVisible = false;
        _ownerWindow = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport(
        "user32.dll",
        EntryPoint = "LoadIconW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
