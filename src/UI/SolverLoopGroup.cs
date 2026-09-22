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
    private readonly Label _count;
    private readonly HBoxContainer _content;

    public float NaturalWidth => GetThemeStylebox("panel").GetMinimumSize().X
        + Actions.GetChildren().OfType<Control>().Sum(child => child.GetCombinedMinimumSize().X)
        + Mathf.Max(0, Actions.GetChildCount() - 1) * Actions.GetThemeConstant("h_separation")
        + _content.GetThemeConstant("separation") + _count.GetCombinedMinimumSize().X;

    public SolverLoopGroup(SolverActionRun run)
    {
        Name = "LoopGroup";
        MouseFilter = MouseFilterEnum.Pass;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface, SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Large, 2, 2));
        _content = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        Actions.AddThemeConstantOverride("h_separation", 6);
        Actions.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        _content.AddChild(Actions);
        _count = SolverUiTokens.CreateLabel($"×{run.Repetitions}", SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary, FontType.Bold);
        _count.Name = "LoopCount";
        _count.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _content.AddChild(_count);
        AddChild(_content);
        Actions.SortChildren += QueueRedraw;
        SolverLocaleRefresh.Bind(this, () =>
            TooltipText = SolverText.Format($"框内 {run.Period} 个动作按顺序重复 {run.Repetitions} 次，共 {run.Count} 个动作"));
    }

    public override void _Draw()
    {
        // Use the existing bottom padding (and inter-row gap) for the underline.
        // Wrapped rows each get a horizontal segment; the repeat count stays outside it.
        Vector2 origin = _content.Position + Actions.Position;
        float left = 0, right = 0, top = 0, bottom = 0;
        bool hasRow = false;
        foreach (Control action in Actions.GetChildren().OfType<Control>().Where(child => child.Visible))
        {
            Rect2 rect = action.GetRect();
            if (hasRow && !Mathf.IsEqualApprox(top, rect.Position.Y))
            {
                Underline();
                hasRow = false;
            }
            if (!hasRow)
            {
                left = rect.Position.X;
                top = rect.Position.Y;
                right = rect.End.X;
                bottom = rect.End.Y;
                hasRow = true;
            }
            else
            {
                right = Mathf.Max(right, rect.End.X);
                bottom = Mathf.Max(bottom, rect.End.Y);
            }
        }
        if (hasRow) Underline();

        void Underline()
        {
            float y = Mathf.Floor(origin.Y + bottom) + 0.5f;
            DrawDashedLine(new Vector2(origin.X + left, y), new Vector2(origin.X + right, y),
                SolverUiTokens.Palette.Accent, 1f, 4f);
        }
    }
}
