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
        Idle,
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
    private MemoryBarDisplay? _target;
    private bool _hasShownDisplay;
    private double _shownSystemRatio;
    private double _shownProcessRatio;
    private Color _shownColor;
    private string? _renderedText;
    private Color? _renderedLabelColor;

    public SolverMemoryUsageBar()
    {
        Name = "MemoryUsage";
        CustomMinimumSize = new Vector2(0f, SolverUiTokens.Size.ButtonHeight);
        MouseFilter = MouseFilterEnum.Pass;
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
        SearchMemoryUsageSnapshot activeSnapshot = new(
            6_400_000_000L,
            29_440_000_000L,
            32_000_000_000L,
            SearchActive: true);
        MemoryBarDisplay active = BuildDisplay(activeSnapshot);
        MemoryBarDisplay warning = BuildDisplay(new SearchMemoryUsageSnapshot(
            6_000_000_000L,
            24_000_000_000L,
            32_000_000_000L,
            SearchActive: true));
        MemoryBarDisplay idle = BuildDisplay(new SearchMemoryUsageSnapshot(
            2_000_000_000L,
            8_000_000_000L,
            16_000_000_000L,
            SearchActive: false));
        MemoryBarDisplay unavailable = BuildDisplay(new SearchMemoryUsageSnapshot(
            6_000_000_000L,
            0L,
            0L,
            SearchActive: true));
        return active.Text == FormatSummary(6_400_000_000L, 2_560_000_000L)
            && active.Tone == MemoryPressureTone.Danger
            && active.State == MemoryDisplayState.Search
            && warning.Text == FormatSummary(6_000_000_000L, 8_000_000_000L)
            && warning.Tone == MemoryPressureTone.Warning
            && warning.SystemRatio == 18d / 32d && warning.ProcessRatio == 6d / 32d
            && warning.SystemRatio + warning.ProcessRatio == 24d / 32d
            && idle.Text == FormatSummary(2_000_000_000L, 8_000_000_000L)
            && idle.Tone == MemoryPressureTone.Normal
            && idle.State == MemoryDisplayState.Idle
            && unavailable.Text == FormatSummary(6_000_000_000L, null)
            && unavailable.SystemRatio == 0d && unavailable.ProcessRatio == 0d
            && BuildTooltip(activeSnapshot).Contains("32.0 GB", StringComparison.Ordinal);
    }

    private void RefreshDisplay()
    {
        SearchMemoryUsageSnapshot snapshot = SolverController.CaptureSearchMemoryUsage();
        MemoryBarDisplay display = BuildDisplay(snapshot);
        TooltipText = BuildTooltip(snapshot);
        _target = display;
        if (!_hasShownDisplay)
        {
            _hasShownDisplay = true;
            _shownSystemRatio = display.SystemRatio;
            _shownProcessRatio = display.ProcessRatio;
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
        _shownColor = SolverUiMotion.Approach(_shownColor, ToneColor(target.Tone), blend);
        ApplyShownDisplay(target);
    }

    private void ApplyShownDisplay(MemoryBarDisplay target)
    {
        // Both numbers describe the same sampling round; only the visual segments are eased.
        string text = target.Text;
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
        _processSegment.Color = _shownColor;
        SetSegmentRatio(_systemSegment, _shownSystemRatio);
        SetSegmentRatio(_processSegment, _shownProcessRatio);
        SetSegmentRatio(
            _remainingSegment,
            Math.Max(0d, 1d - _shownSystemRatio - _shownProcessRatio));
    }

    private void LogDisplayTransition(
        SearchMemoryUsageSnapshot snapshot,
        MemoryBarDisplay display)
    {
        int searchLoadDecile = display.State == MemoryDisplayState.Search
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
        double pressureRatio = snapshot.MemoryPressureRatio;
        return new MemoryBarDisplay(
            snapshot.SystemSegmentRatio,
            snapshot.ProcessSegmentRatio,
            pressureRatio,
            ToneForRatio(pressureRatio),
            snapshot.SearchActive ? MemoryDisplayState.Search : MemoryDisplayState.Idle)
        {
            ProcessBytes = snapshot.ProcessWorkingSetBytes,
            AvailableBytes = snapshot.PhysicalMemoryAvailableBytes,
        };
    }

    private static string FormatSummary(long processBytes, long? availableBytes)
        => availableBytes is long available
            ? SolverText.Format($"游戏占用 {FormatGigabytes(processBytes)} GB · 系统可用 {FormatGigabytes(available)} GB")
            : SolverText.Format($"游戏占用 {FormatGigabytes(processBytes)} GB · 系统可用未知");

    private static string BuildTooltip(SearchMemoryUsageSnapshot snapshot)
    {
        string total = snapshot.HasPhysicalMemorySample
            ? FormatGigabytes(snapshot.PhysicalMemoryTotalBytes)
            : SolverText.Get("未知");
        return SolverText.Get("求解器内存与性能监视\n")
            + SolverText.Get("- 游戏占用是进程工作集，包含求解器和其他 Mod；系统可用是操作系统当前可用的物理内存，两者不是用量与上限。\n")
            + SolverText.Get("- 整条表示物理内存总量。灰色为其他占用估计，彩色为游戏工作集，空白为系统可用；进度条平滑过渡，数字取自同一轮采样。\n")
            + SolverText.Format($"物理内存总量：{total} GB。GB = 1,000,000,000 字节。");
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
        MemoryDisplayState.Idle => "idle",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private readonly record struct MemoryBarDisplay(
        double SystemRatio,
        double ProcessRatio,
        double PressureRatio,
        MemoryPressureTone Tone,
        MemoryDisplayState State)
    {
        public long ProcessBytes { get; init; }
        public long? AvailableBytes { get; init; }
        public string Text => FormatSummary(ProcessBytes, AvailableBytes);
    }
}
