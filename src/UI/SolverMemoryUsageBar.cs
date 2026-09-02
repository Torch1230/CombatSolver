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
        CustomMinimumSize = new Vector2(280f, SolverUiTokens.Size.ButtonHeight);
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText =
            "左侧为游戏进程当前内存；进度表示本次搜索距离 GC 回收检查点。" +
            "接近满格时搜索会暂停回收，可能短暂变慢或卡顿。";
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
            CustomMinimumSize = new Vector2(0f, 5f),
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

    internal static bool ExerciseFormattingForTesting()
    {
        MemoryBarDisplay active = BuildDisplay(new SearchMemoryUsageSnapshot(
            6_400_000_000L,
            SearchActive: true,
            SearchAllocatedBytes: 9_000_000_000L,
            SearchAllocationLimitBytes: 10_000_000_000L,
            Reclaiming: false));
        MemoryBarDisplay reclaiming = BuildDisplay(new SearchMemoryUsageSnapshot(
            6_100_000_000L,
            SearchActive: true,
            SearchAllocatedBytes: 10_000_000_000L,
            SearchAllocationLimitBytes: 10_000_000_000L,
            Reclaiming: true));
        MemoryBarDisplay idle = BuildDisplay(new SearchMemoryUsageSnapshot(
            2_000_000_000L,
            SearchActive: false,
            SearchAllocatedBytes: 0L,
            SearchAllocationLimitBytes: long.MaxValue,
            Reclaiming: false));
        return active.Text == "当前内存 6.4 GB  ·  距 GC 回收 90%"
            && Math.Abs(active.Ratio - 0.9d) < 0.001d
            && active.Tone == MemoryPressureTone.Danger
            && reclaiming.Text == "当前内存 6.1 GB  ·  内存回收中"
            && reclaiming.Ratio == 1d
            && idle.Text == "当前内存 2.0 GB  ·  待机"
            && idle.Ratio == 0d;
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
                "当前内存 " + memory + "  ·  内存回收中",
                1d,
                MemoryPressureTone.Warning);
        }
        if (!snapshot.SearchActive)
        {
            return new MemoryBarDisplay(
                "当前内存 " + memory + "  ·  待机",
                0d,
                MemoryPressureTone.Idle);
        }
        if (!snapshot.HasGcWall)
        {
            return new MemoryBarDisplay(
                "当前内存 " + memory + "  ·  常规 GC",
                0d,
                MemoryPressureTone.Idle);
        }

        double ratio = snapshot.GcWallRatio;
        MemoryPressureTone tone = ratio >= 0.9d
            ? MemoryPressureTone.Danger
            : ratio >= 0.7d
                ? MemoryPressureTone.Warning
                : MemoryPressureTone.Normal;
        return new MemoryBarDisplay(
            "当前内存 " + memory + "  ·  距 GC 回收 "
                + Math.Round(ratio * 100d).ToString("F0", CultureInfo.InvariantCulture) + "%",
            ratio,
            tone);
    }

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
