using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;
using System.Globalization;

namespace CombatSolver;

internal sealed partial class SolverMemoryUsageBar : PanelContainer
{
    private enum MemoryPressureTone
    {
        Idle,
        Normal,
        Warning,
        Danger,
    }

    private enum MemoryDisplayState
    {
        Search,
        SearchNearLimit,
        ForegroundReclaim,
        BackgroundCleanup,
        Idle,
        AutomaticManagement,
    }

    private const double RefreshIntervalSeconds = 0.25d;
    private const long BytesPerGigabyte = 1_000_000_000L;

    private readonly Label _label;
    private readonly PanelContainer _progress;
    private readonly ColorRect _systemSegment;
    private readonly ColorRect _processSegment;
    private readonly ColorRect _remainingSegment;
    private double _elapsedSinceRefresh = RefreshIntervalSeconds;
    private MemoryDisplayState? _lastLoggedState;
    private int _lastLoggedSearchLoadDecile = -1;

    public SolverMemoryUsageBar()
    {
        Name = "MemoryUsage";
        CustomMinimumSize = new Vector2(0f, SolverUiTokens.Size.ButtonHeight);
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText =
            "求解器内存与性能监视\n" +
            "- 灰色：系统和其他程序当前占用的内存。\n" +
            "- 彩色：游戏进程当前占用的内存，包含求解器与其他已加载 Mod。\n" +
            "- 当前占用 / 上限：游戏进程占用 / 安全总量扣除系统占用后的动态上限。\n" +
            "- 系统内存变化时，上限和进度条会自动调整；正在整理或后台清理属于正常释放阶段。";
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.Border,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));

        VBoxContainer content = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xxs);
        AddChild(content);

        _label = SolverUiTokens.CreateLabel(
            "当前内存 --",
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Bold);
        _label.HorizontalAlignment = HorizontalAlignment.Right;
        _label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        content.AddChild(_label);

        _progress = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0f, 8f),
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true,
        };
        _progress.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.ProgressBackground,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            0,
            0));
        HBoxContainer segments = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
        };
        segments.AddThemeConstantOverride("separation", 0);
        _systemSegment = CreateSegment(SolverUiTokens.Palette.TextMuted.Darkened(0.3f));
        _processSegment = CreateSegment(SolverUiTokens.Palette.Accent);
        _remainingSegment = CreateSegment(Colors.Transparent);
        segments.AddChild(_systemSegment);
        segments.AddChild(_processSegment);
        segments.AddChild(_remainingSegment);
        _progress.AddChild(segments);
        content.AddChild(_progress);
        RefreshDisplay();
    }

    public override void _Process(double delta)
    {
        _elapsedSinceRefresh += delta;
        if (_elapsedSinceRefresh < RefreshIntervalSeconds || !IsVisibleInTree())
            return;
        _elapsedSinceRefresh = 0d;
        RefreshDisplay();
    }

    internal bool LayoutConfiguredForTesting
        => Math.Abs(CustomMinimumSize.X) < 0.01f
            && SizeFlagsHorizontal == SizeFlags.ExpandFill
            && Math.Abs(_progress.CustomMinimumSize.Y - 8f) < 0.01f;

    internal static bool ExerciseFormattingForTesting()
    {
        MemoryBarDisplay active = BuildDisplay(new SearchMemoryUsageSnapshot(
            6_400_000_000L,
            21_400_000_000L,
            16_000_000_000L,
            SearchActive: true,
            SearchAllocatedBytes: 9_000_000_000L,
            SearchAllocationLimitBytes: 10_000_000_000L,
            ProjectedSystemMemoryLoadBytes: 8_000_000_000L,
            SystemMemoryLimitBytes: 22_000_000_000L,
            Reclaiming: false,
            BackgroundReclaiming: false));
        MemoryBarDisplay reclaiming = BuildDisplay(new SearchMemoryUsageSnapshot(
            6_100_000_000L,
            20_000_000_000L,
            16_000_000_000L,
            SearchActive: true,
            SearchAllocatedBytes: 10_000_000_000L,
            SearchAllocationLimitBytes: 10_000_000_000L,
            ProjectedSystemMemoryLoadBytes: 10_000_000_000L,
            SystemMemoryLimitBytes: 22_000_000_000L,
            Reclaiming: true,
            BackgroundReclaiming: false));
        MemoryBarDisplay idle = BuildDisplay(new SearchMemoryUsageSnapshot(
            2_000_000_000L,
            8_000_000_000L,
            16_000_000_000L,
            SearchActive: false,
            SearchAllocatedBytes: 0L,
            SearchAllocationLimitBytes: long.MaxValue,
            ProjectedSystemMemoryLoadBytes: 0L,
            SystemMemoryLimitBytes: 12_000_000_000L,
            Reclaiming: false,
            BackgroundReclaiming: false));
        MemoryBarDisplay background = BuildDisplay(new SearchMemoryUsageSnapshot(
            8_000_000_000L,
            18_000_000_000L,
            16_000_000_000L,
            SearchActive: false,
            SearchAllocatedBytes: 0L,
            SearchAllocationLimitBytes: long.MaxValue,
            ProjectedSystemMemoryLoadBytes: 0L,
            SystemMemoryLimitBytes: 22_000_000_000L,
            Reclaiming: false,
            BackgroundReclaiming: true));
        MemoryBarDisplay systemLimited = BuildDisplay(new SearchMemoryUsageSnapshot(
            7_200_000_000L,
            21_200_000_000L,
            16_000_000_000L,
            SearchActive: true,
            SearchAllocatedBytes: 2_000_000_000L,
            SearchAllocationLimitBytes: 10_000_000_000L,
            ProjectedSystemMemoryLoadBytes: 9_600_000_000L,
            SystemMemoryLimitBytes: 22_000_000_000L,
            Reclaiming: false,
            BackgroundReclaiming: false));
        return active.Text == "当前占用 6.4 / 7.0 GB  ·  系统占用 15.0 GB  ·  即将整理"
            && Math.Abs(active.PressureRatio - 6.4d / 7d) < 0.001d
            && active.Tone == MemoryPressureTone.Danger
            && reclaiming.Text == "当前占用 6.1 / 8.1 GB  ·  系统占用 13.9 GB  ·  正在整理…"
            && Math.Abs(reclaiming.PressureRatio - 6.1d / 8.1d) < 0.001d
            && idle.Text == "当前占用 2.0 / 6.0 GB  ·  系统占用 6.0 GB"
            && Math.Abs(idle.PressureRatio - 1d / 3d) < 0.001d
            && background.Text == "当前占用 8.0 / 12.0 GB  ·  系统占用 10.0 GB  ·  后台清理中"
            && Math.Abs(background.PressureRatio - 2d / 3d) < 0.001d
            && background.Tone == MemoryPressureTone.Warning
            && systemLimited.Text == "当前占用 7.2 / 8.0 GB  ·  系统占用 14.0 GB  ·  即将整理"
            && Math.Abs(systemLimited.PressureRatio - 0.9d) < 0.001d;
    }

    private void RefreshDisplay()
    {
        SearchMemoryUsageSnapshot snapshot = SolverController.CaptureSearchMemoryUsage();
        MemoryBarDisplay display = BuildDisplay(snapshot);
        Color color = ToneColor(display.Tone);
        _label.Text = display.Text;
        _label.AddThemeColorOverride("font_color", color);
        _processSegment.Color = color;
        SetSegmentRatio(_systemSegment, display.SystemRatio);
        SetSegmentRatio(_processSegment, display.ProcessRatio);
        SetSegmentRatio(
            _remainingSegment,
            Math.Max(0d, 1d - display.SystemRatio - display.ProcessRatio));
        LogDisplayTransition(snapshot, display);
    }

    private void LogDisplayTransition(
        SearchMemoryUsageSnapshot snapshot,
        MemoryBarDisplay display)
    {
        int searchLoadDecile = display.State is MemoryDisplayState.Search
            or MemoryDisplayState.SearchNearLimit
                ? Math.Min(10, (int)Math.Floor(display.PressureRatio * 10d))
                : -1;
        if (_lastLoggedState == display.State
            && _lastLoggedSearchLoadDecile == searchLoadDecile)
        {
            return;
        }
        _lastLoggedState = display.State;
        _lastLoggedSearchLoadDecile = searchLoadDecile;
        SolverController.LogSearchMemoryDisplayState(
            snapshot,
            DisplayStateToken(display.State),
            display.PressureRatio);
    }

    private static MemoryBarDisplay BuildDisplay(SearchMemoryUsageSnapshot snapshot)
    {
        string summary =
            "当前占用 " + FormatGigabytes(snapshot.ProcessWorkingSetBytes) +
            " / " + FormatGigabytes(snapshot.ProcessMemoryLimitBytes) + " GB" +
            "  ·  系统占用 " + FormatGigabytes(snapshot.SystemOccupiedBytes) + " GB";
        double pressureRatio = snapshot.ProcessMemoryPressureRatio;
        double systemRatio = snapshot.SystemSegmentRatio;
        double processRatio = snapshot.ProcessSegmentRatio;
        if (snapshot.Reclaiming)
        {
            return new MemoryBarDisplay(
                summary + "  ·  正在整理…",
                systemRatio,
                processRatio,
                pressureRatio,
                MemoryPressureTone.Warning,
                MemoryDisplayState.ForegroundReclaim);
        }
        if (snapshot.BackgroundReclaiming)
        {
            return new MemoryBarDisplay(
                summary + "  ·  后台清理中",
                systemRatio,
                processRatio,
                pressureRatio,
                MemoryPressureTone.Warning,
                MemoryDisplayState.BackgroundCleanup);
        }
        if (!snapshot.SearchActive)
        {
            return new MemoryBarDisplay(
                summary,
                systemRatio,
                processRatio,
                pressureRatio,
                ToneForRatio(pressureRatio),
                MemoryDisplayState.Idle);
        }
        if (!snapshot.HasGcWall)
        {
            return new MemoryBarDisplay(
                summary + "  ·  自动管理",
                systemRatio,
                processRatio,
                pressureRatio,
                ToneForRatio(pressureRatio),
                MemoryDisplayState.AutomaticManagement);
        }

        return new MemoryBarDisplay(
            summary + (pressureRatio >= 0.9d ? "  ·  即将整理" : string.Empty),
            systemRatio,
            processRatio,
            pressureRatio,
            ToneForRatio(pressureRatio),
            pressureRatio >= 0.9d ? MemoryDisplayState.SearchNearLimit : MemoryDisplayState.Search);
    }

    private static string FormatGigabytes(long bytes)
        => (bytes / (double)BytesPerGigabyte).ToString("F1", CultureInfo.InvariantCulture);

    private static ColorRect CreateSegment(Color color)
        => new()
        {
            Color = color,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };

    private static void SetSegmentRatio(Control segment, double ratio)
    {
        float stretchRatio = (float)Math.Clamp(ratio, 0d, 1d);
        segment.Visible = stretchRatio > 0f;
        segment.SizeFlagsStretchRatio = stretchRatio;
    }

    private static MemoryPressureTone ToneForRatio(double ratio)
        => ratio >= 0.9d
            ? MemoryPressureTone.Danger
            : ratio >= 0.7d
                ? MemoryPressureTone.Warning
                : MemoryPressureTone.Normal;

    private static Color ToneColor(MemoryPressureTone tone)
        => tone switch
        {
            MemoryPressureTone.Danger => SolverUiTokens.Palette.Danger,
            MemoryPressureTone.Warning => SolverUiTokens.Palette.Warning,
            MemoryPressureTone.Normal => SolverUiTokens.Palette.Accent,
            _ => SolverUiTokens.Palette.TextMuted,
        };

    private static string DisplayStateToken(MemoryDisplayState state) => state switch
    {
        MemoryDisplayState.Search => "search_load",
        MemoryDisplayState.SearchNearLimit => "search_near_cleanup",
        MemoryDisplayState.ForegroundReclaim => "foreground_cleanup",
        MemoryDisplayState.BackgroundCleanup => "idle_background_cleanup",
        MemoryDisplayState.Idle => "idle",
        MemoryDisplayState.AutomaticManagement => "automatic_management",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private readonly record struct MemoryBarDisplay(
        string Text,
        double SystemRatio,
        double ProcessRatio,
        double PressureRatio,
        MemoryPressureTone Tone,
        MemoryDisplayState State);
}
