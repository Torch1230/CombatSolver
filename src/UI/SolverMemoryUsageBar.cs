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
    private const double CleanupPulsePeriodSeconds = 1.4d;
    private const float CleanupPulseLightening = 0.28f;

    private readonly Label _label;
    private readonly PanelContainer _progress;
    private readonly ColorRect _systemSegment;
    private readonly ColorRect _processSegment;
    private readonly ColorRect _remainingSegment;
    private double _elapsedSinceRefresh = RefreshIntervalSeconds;
    private MemoryDisplayState? _lastLoggedState;
    private int _lastLoggedSearchLoadDecile = -1;
    private MemoryBarDisplay? _target;
    private bool _hasShownDisplay;
    private double _shownSystemRatio;
    private double _shownProcessRatio;
    private double _shownProcessBytes;
    private double _shownLimitBytes;
    private Color _shownColor;
    private double _cleanupPulseSeconds;
    private string? _renderedText;
    private Color? _renderedLabelColor;

    public SolverMemoryUsageBar()
    {
        Name = "MemoryUsage";
        CustomMinimumSize = new Vector2(0f, SolverUiTokens.Size.ButtonHeight);
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText =
            SolverText.Get("求解器内存与性能监视\n") +
            SolverText.Get("- 灰色：系统和其他程序当前占用的内存。\n") +
            SolverText.Get("- 彩色：游戏进程当前占用的内存，包含求解器与其他已加载 Mod。\n") +
            SolverText.Get("- 当前占用 / 上限：游戏进程占用 / 安全总量扣除系统占用后的动态上限。\n") +
            SolverText.Get("- 系统内存变化时，上限和进度条会自动调整；正在整理或后台清理属于正常释放阶段。");
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle,
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
            SolverText.Get("当前内存 --"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Bold);
        _label.HorizontalAlignment = HorizontalAlignment.Right;
        _label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
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
        if (!IsVisibleInTree())
        {
            // Hidden time is not animated: the next visible frame jumps to a fresh sample instead of
            // replaying a stale transition.
            _hasShownDisplay = false;
            _elapsedSinceRefresh = RefreshIntervalSeconds;
            return;
        }
        _elapsedSinceRefresh += delta;
        if (_elapsedSinceRefresh >= RefreshIntervalSeconds)
        {
            _elapsedSinceRefresh = 0d;
            RefreshDisplay();
        }
        AnimateDisplay(delta);
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
        return active.Text == "当前内存占用 6.4 GB / 搜索总可用 7.0 GB  ·  即将整理"
            && Math.Abs(active.PressureRatio - 6.4d / 7d) < 0.001d
            && active.Tone == MemoryPressureTone.Danger
            && reclaiming.Text == "当前内存占用 6.1 GB / 搜索总可用 8.1 GB  ·  正在整理…"
            && Math.Abs(reclaiming.PressureRatio - 6.1d / 8.1d) < 0.001d
            && idle.Text == "当前内存占用 2.0 GB / 搜索总可用 6.0 GB"
            && Math.Abs(idle.PressureRatio - 1d / 3d) < 0.001d
            && background.Text == "当前内存占用 8.0 GB / 搜索总可用 12.0 GB  ·  后台清理中"
            && Math.Abs(background.PressureRatio - 2d / 3d) < 0.001d
            && background.Tone == MemoryPressureTone.Warning
            && systemLimited.Text == "当前内存占用 7.2 GB / 搜索总可用 8.0 GB  ·  即将整理"
            && Math.Abs(systemLimited.PressureRatio - 0.9d) < 0.001d;
    }

    private void RefreshDisplay()
    {
        SearchMemoryUsageSnapshot snapshot = SolverController.CaptureSearchMemoryUsage();
        MemoryBarDisplay display = BuildDisplay(snapshot);
        _target = display;
        if (!_hasShownDisplay)
        {
            _hasShownDisplay = true;
            _shownSystemRatio = display.SystemRatio;
            _shownProcessRatio = display.ProcessRatio;
            _shownProcessBytes = display.ProcessBytes;
            _shownLimitBytes = display.LimitBytes;
            _shownColor = ToneColor(display.Tone);
            ApplyShownDisplay(display);
        }
        LogDisplayTransition(snapshot, display);
    }

    private void AnimateDisplay(double delta)
    {
        if (_target is not { } target)
            return;
        double blend = SolverUiMotion.Blend(delta, SolverUiMotion.ReadoutTimeConstantSeconds);
        _shownSystemRatio = SolverUiMotion.Approach(_shownSystemRatio, target.SystemRatio, blend, 0.0005d);
        _shownProcessRatio = SolverUiMotion.Approach(_shownProcessRatio, target.ProcessRatio, blend, 0.0005d);
        // Snap once the eased value would round to the same 0.1 GB the label prints.
        _shownProcessBytes = SolverUiMotion.Approach(_shownProcessBytes, target.ProcessBytes, blend, BytesPerGigabyte / 200d);
        _shownLimitBytes = SolverUiMotion.Approach(_shownLimitBytes, target.LimitBytes, blend, BytesPerGigabyte / 200d);
        _shownColor = SolverUiMotion.Approach(_shownColor, ToneColor(target.Tone), blend);
        _cleanupPulseSeconds = IsCleanupState(target.State)
            ? (_cleanupPulseSeconds + delta) % CleanupPulsePeriodSeconds
            : 0d;
        ApplyShownDisplay(target);
    }

    private void ApplyShownDisplay(MemoryBarDisplay target)
    {
        string text = FormatSummary((long)Math.Round(_shownProcessBytes), (long)Math.Round(_shownLimitBytes))
            + target.Suffix;
        if (!string.Equals(_renderedText, text, StringComparison.Ordinal))
        {
            _renderedText = text;
            _label.Text = text;
        }
        if (_renderedLabelColor != _shownColor)
        {
            _renderedLabelColor = _shownColor;
            _label.AddThemeColorOverride("font_color", _shownColor);
        }
        // Cleanup phases breathe the process segment so an in-progress release reads as activity
        // rather than a frozen bar; the label keeps the steady tone color for legibility.
        double pulse = _cleanupPulseSeconds > 0d
            ? 0.5d - 0.5d * Math.Cos(_cleanupPulseSeconds / CleanupPulsePeriodSeconds * Math.Tau)
            : 0d;
        _processSegment.Color = _shownColor.Lightened(CleanupPulseLightening * (float)pulse);
        SetSegmentRatio(_systemSegment, _shownSystemRatio);
        SetSegmentRatio(_processSegment, _shownProcessRatio);
        SetSegmentRatio(
            _remainingSegment,
            Math.Max(0d, 1d - _shownSystemRatio - _shownProcessRatio));
    }

    private static bool IsCleanupState(MemoryDisplayState state)
        => state is MemoryDisplayState.ForegroundReclaim or MemoryDisplayState.BackgroundCleanup;

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
        => BuildDisplayState(snapshot) with
        {
            ProcessBytes = snapshot.ProcessWorkingSetBytes,
            LimitBytes = snapshot.ProcessMemoryLimitBytes,
        };

    private static string FormatSummary(long processBytes, long limitBytes)
        => SolverText.Get("当前内存占用 ") + FormatGigabytes(processBytes) + " GB" +
            SolverText.Get(" / 搜索总可用 ") + FormatGigabytes(limitBytes) + " GB";

    private static MemoryBarDisplay BuildDisplayState(SearchMemoryUsageSnapshot snapshot)
    {
        double pressureRatio = snapshot.ProcessMemoryPressureRatio;
        double systemRatio = snapshot.SystemSegmentRatio;
        double processRatio = snapshot.ProcessSegmentRatio;
        if (snapshot.Reclaiming)
        {
            return new MemoryBarDisplay(
                SolverText.Get("  ·  正在整理…"),
                systemRatio,
                processRatio,
                pressureRatio,
                MemoryPressureTone.Warning,
                MemoryDisplayState.ForegroundReclaim);
        }
        if (snapshot.BackgroundReclaiming)
        {
            return new MemoryBarDisplay(
                SolverText.Get("  ·  后台清理中"),
                systemRatio,
                processRatio,
                pressureRatio,
                MemoryPressureTone.Warning,
                MemoryDisplayState.BackgroundCleanup);
        }
        if (!snapshot.SearchActive)
        {
            return new MemoryBarDisplay(
                string.Empty,
                systemRatio,
                processRatio,
                pressureRatio,
                ToneForRatio(pressureRatio),
                MemoryDisplayState.Idle);
        }
        if (!snapshot.HasGcWall)
        {
            return new MemoryBarDisplay(
                SolverText.Get("  ·  自动管理"),
                systemRatio,
                processRatio,
                pressureRatio,
                ToneForRatio(pressureRatio),
                MemoryDisplayState.AutomaticManagement);
        }

        return new MemoryBarDisplay(
            pressureRatio >= 0.9d ? SolverText.Get("  ·  即将整理") : string.Empty,
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
        string Suffix,
        double SystemRatio,
        double ProcessRatio,
        double PressureRatio,
        MemoryPressureTone Tone,
        MemoryDisplayState State)
    {
        public long ProcessBytes { get; init; }
        public long LimitBytes { get; init; }
        public string Text => FormatSummary(ProcessBytes, LimitBytes) + Suffix;
    }
}
