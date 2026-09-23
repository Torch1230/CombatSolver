using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CombatSolver;

internal sealed partial class SolverLoopGroup : PanelContainer
{
    public HFlowContainer Actions { get; } = new()
    {
        Name = "LoopActions",
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        MouseFilter = MouseFilterEnum.Ignore,
    };
    private readonly PanelContainer _badge;
    private readonly Label _badgeLabel;
    private readonly HBoxContainer _content;

    public float NaturalWidth => GetThemeStylebox("panel").GetMinimumSize().X
        + Actions.GetChildren().OfType<Control>().Sum(child => child.GetCombinedMinimumSize().X)
        + Mathf.Max(0, Actions.GetChildCount() - 1) * Actions.GetThemeConstant("h_separation")
        + _content.GetThemeConstant("separation") + _badge.GetCombinedMinimumSize().X;

    public SolverLoopGroup(SolverActionRun run)
    {
        Name = "LoopGroup";
        MouseFilter = MouseFilterEnum.Pass;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.Border,
            SolverUiTokens.Radius.Medium,
            horizontalPadding: 6,
            verticalPadding: 3,
            borderWidth: 1));

        _content = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);

        Actions.AddThemeConstantOverride("h_separation", 6);
        Actions.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        _content.AddChild(Actions);

        _badge = new PanelContainer
        {
            Name = "LoopBadge",
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        Color badgeBg = SolverUiTokens.IsLightTheme
            ? SolverUiTokens.Palette.Accent.Lightened(0.85f)
            : SolverUiTokens.Palette.Accent.Darkened(0.70f);
        Color badgeBorder = SolverUiTokens.IsLightTheme
            ? SolverUiTokens.Palette.Accent.Lightened(0.40f)
            : SolverUiTokens.Palette.Accent.Darkened(0.35f);
        _badge.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            badgeBg,
            badgeBorder,
            SolverUiTokens.Radius.Small,
            horizontalPadding: 8,
            verticalPadding: 3,
            borderWidth: 1));

        _badgeLabel = SolverUiTokens.CreateLabel(
            SolverText.Format($"循环 ×{run.Repetitions}"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.Accent,
            FontType.Bold);
        _badgeLabel.Name = "LoopCount";
        _badgeLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _badge.AddChild(_badgeLabel);
        _content.AddChild(_badge);

        AddChild(_content);

        SolverLocaleRefresh.Bind(this, () =>
        {
            _badgeLabel.Text = SolverText.Format($"循环 ×{run.Repetitions}");
            TooltipText = SolverText.Format($"框内 {run.Period} 个动作按顺序重复 {run.Repetitions} 次，共 {run.Count} 个动作");
        });
    }
}
