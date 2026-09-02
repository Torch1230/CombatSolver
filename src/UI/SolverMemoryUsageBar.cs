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

    private const double RefreshIntervalSeconds = 0.25d;
    private const long BytesPerGigabyte = 1_000_000_000L;

    private readonly Label _label;
    private readonly ProgressBar _progress;
    private double _elapsedSinceRefresh = RefreshIntervalSeconds;

    public SolverMemoryUsageBar()
    {
        Name = "MemoryUsage";
        CustomMinimumSize = new Vector2(0f, SolverUiTokens.Size.ButtonHeight);
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText =
            "求解器内存与性能监视\n" +
            "- 左侧数值：游戏进程当前占用的物理内存（RAM）。\n" +
            "- 搜索计算时：进度条显示本次搜索的内存缓冲负荷。达到 100% 时，求解器会短暂整理内存，然后自动继续计算。\n" +
            "- 待机或自动管理时：进度条显示当前内存占设置中内存预算的比例。\n" +
            "- 正在整理或后台清理属于正常的内存释放阶段。";
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

        _progress = new ProgressBar
        {
            MinValue = 0d,
            MaxValue = 1d,
            Value = 0d,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0f, 8f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _progress.AddThemeStyleboxOverride("background", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.ProgressBackground,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            0,
            0));
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
            16_000_000_000L,
            SearchActive: true,
            SearchAllocatedBytes: 9_000_000_000L,
            SearchAllocationLimitBytes: 10_000_000_000L,
            Reclaiming: false,
            BackgroundReclaiming: false));
        MemoryBarDisplay reclaiming = BuildDisplay(new SearchMemoryUsageSnapshot(
            6_100_000_000L,
            16_000_000_000L,
            SearchActive: true,
            SearchAllocatedBytes: 10_000_000_000L,
            SearchAllocationLimitBytes: 10_000_000_000L,
            Reclaiming: true,
            BackgroundReclaiming: false));
        MemoryBarDisplay idle = BuildDisplay(new SearchMemoryUsageSnapshot(
            2_000_000_000L,
            16_000_000_000L,
            SearchActive: false,
            SearchAllocatedBytes: 0L,
            SearchAllocationLimitBytes: long.MaxValue,
            Reclaiming: false,
            BackgroundReclaiming: false));
        MemoryBarDisplay background = BuildDisplay(new SearchMemoryUsageSnapshot(
            8_000_000_000L,
            16_000_000_000L,
            SearchActive: false,
            SearchAllocatedBytes: 0L,
            SearchAllocationLimitBytes: long.MaxValue,
            Reclaiming: false,
            BackgroundReclaiming: true));
        return active.Text == "目前内存占用 6.4 GB  ·  即将整理 90%"
            && Math.Abs(active.Ratio - 0.9d) < 0.001d
            && active.Tone == MemoryPressureTone.Danger
            && reclaiming.Text == "目前内存占用 6.1 GB  ·  正在整理…"
            && reclaiming.Ratio == 1d
            && idle.Text == "目前内存占用 2.0 GB  ·  占用 13%"
            && Math.Abs(idle.Ratio - 0.125d) < 0.001d
            && background.Text == "目前内存占用 8.0 GB  ·  待机 · 后台清理中"
            && Math.Abs(background.Ratio - 0.5d) < 0.001d
            && background.Tone == MemoryPressureTone.Warning;
    }

    private void RefreshDisplay()
    {
        MemoryBarDisplay display = BuildDisplay(SolverController.CaptureSearchMemoryUsage());
        Color color = ToneColor(display.Tone);
        _label.Text = display.Text;
        _label.AddThemeColorOverride("font_color", color);
        _progress.Value = display.Ratio;
        _progress.AddThemeStyleboxOverride("fill", SolverUiTokens.CreateBox(
            color,
            color,
            SolverUiTokens.Radius.Small,
            0,
            0,
            borderWidth: 0));
    }

    private static MemoryBarDisplay BuildDisplay(SearchMemoryUsageSnapshot snapshot)
    {
        string memory = (snapshot.ProcessWorkingSetBytes / (double)BytesPerGigabyte)
            .ToString("F1", CultureInfo.InvariantCulture) + " GB";
        if (snapshot.Reclaiming)
        {
            return new MemoryBarDisplay(
                "目前内存占用 " + memory + "  ·  正在整理…",
                1d,
                MemoryPressureTone.Warning);
        }
        double configuredBudgetRatio = snapshot.ConfiguredBudgetRatio;
        if (snapshot.BackgroundReclaiming)
        {
            return new MemoryBarDisplay(
                "目前内存占用 " + memory + "  ·  待机 · 后台清理中",
                configuredBudgetRatio,
                MemoryPressureTone.Warning);
        }
        if (!snapshot.SearchActive)
        {
            return new MemoryBarDisplay(
                "目前内存占用 " + memory + "  ·  占用 " + FormatPercentage(configuredBudgetRatio),
                configuredBudgetRatio,
                ToneForRatio(configuredBudgetRatio));
        }
        if (!snapshot.HasGcWall)
        {
            return new MemoryBarDisplay(
                "目前内存占用 " + memory + "  ·  自动管理",
                configuredBudgetRatio,
                ToneForRatio(configuredBudgetRatio));
        }

        double ratio = snapshot.GcWallRatio;
        string pressure = ratio >= 0.9d ? "即将整理 " : "负荷 ";
        return new MemoryBarDisplay(
            "目前内存占用 " + memory + "  ·  " + pressure + FormatPercentage(ratio),
            ratio,
            ToneForRatio(ratio));
    }

    private static string FormatPercentage(double ratio)
        => Math.Round(ratio * 100d, MidpointRounding.AwayFromZero)
            .ToString("F0", CultureInfo.InvariantCulture) + "%";

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

    private readonly record struct MemoryBarDisplay(
        string Text,
        double Ratio,
        MemoryPressureTone Tone);
}
