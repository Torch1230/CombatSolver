using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CombatSolver;

internal sealed partial class SolverRouteRow : PanelContainer
{
    private readonly List<CanvasItem> _deploymentActions = [];
    private CanvasItem? _endTurnAction;

    public Label TurnLabel { get; }
    public HFlowContainer ActionFlow { get; }
    public Label EnemyDamageLabel { get; }
    public Label OutcomeLabel { get; }
    public Label EnergyLabel { get; }
    public int DeploymentActionCount => _deploymentActions.Count;

    public SolverRouteRow(int index)
    {
        Name = $"Route{index + 1}";
        CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.RouteRowHeight);
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            index == 0 ? SolverUiTokens.Palette.SurfaceRaised : SolverUiTokens.Palette.Surface,
            index == 0
                ? SolverUiTokens.IsLightTheme
                    ? SolverUiTokens.Palette.Border
                    : SolverUiTokens.Palette.Accent
                : SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Sm));

        HBoxContainer layout = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        layout.AddChild(new ColorRect
        {
            Color = index == 0 ? SolverUiTokens.Palette.Accent : Godot.Colors.Transparent,
            CustomMinimumSize = new Vector2(3, 24),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        });

        TurnLabel = SolverUiTokens.CreateLabel(
            $"第 {index + 1} 回合",
            SolverUiTokens.Type.Body,
            index == 0 ? SolverUiTokens.Palette.Accent : SolverUiTokens.Palette.TextPrimary,
            FontType.Bold);
        TurnLabel.CustomMinimumSize = new Vector2(SolverUiTokens.Size.TurnColumnWidth, SolverUiTokens.Size.ActionPillHeight);
        TurnLabel.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        TurnLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        layout.AddChild(TurnLabel);

        ActionFlow = new HFlowContainer
        {
            Name = "ActionFlow",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ActionFlow.AddThemeConstantOverride("h_separation", 6);
        ActionFlow.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        layout.AddChild(ActionFlow);

        HBoxContainer outcomeLayout = new()
        {
            Alignment = BoxContainer.AlignmentMode.End,
            CustomMinimumSize = new Vector2(
                SolverUiTokens.Size.OutcomeColumnWidth,
                SolverUiTokens.Size.ActionPillHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        outcomeLayout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        EnemyDamageLabel = SolverUiTokens.CreateLabel(
            string.Empty,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.Warning,
            FontType.Bold);
        EnemyDamageLabel.HorizontalAlignment = HorizontalAlignment.Right;
        EnemyDamageLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        EnemyDamageLabel.CustomMinimumSize = new Vector2(92, SolverUiTokens.Size.ActionPillHeight);
        EnemyDamageLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        outcomeLayout.AddChild(EnemyDamageLabel);
        OutcomeLabel = SolverUiTokens.CreateLabel(
            string.Empty,
            SolverUiTokens.Type.Metric,
            SolverUiTokens.Palette.TextMuted,
            FontType.Bold);
        OutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        OutcomeLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        OutcomeLabel.CustomMinimumSize = new Vector2(76, SolverUiTokens.Size.ActionPillHeight);
        OutcomeLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        outcomeLayout.AddChild(OutcomeLabel);
        EnergyLabel = SolverUiTokens.CreateLabel(
            string.Empty,
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Bold,
            outlineSize: SolverUiTokens.IsLightTheme ? 0 : 1);
        EnergyLabel.HorizontalAlignment = HorizontalAlignment.Right;
        EnergyLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        EnergyLabel.CustomMinimumSize = new Vector2(54, SolverUiTokens.Size.ActionPillHeight);
        EnergyLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        outcomeLayout.AddChild(EnergyLabel);
        layout.AddChild(outcomeLayout);
        AddChild(layout);
    }

    public void Populate(SolverOverlayTurnSnapshot turn)
    {
        ClearActions();
        foreach (string choice in turn.TurnStartChoices)
        {
            ActionFlow.AddChild(SolverActionPill.CreateStatus(
                choice,
                SolverUiTokens.Palette.TextSecondary));
        }
        if (turn.Actions.Count == 0)
        {
            Control endTurn = turn.EndTurnAction == null
                ? SolverActionPill.CreateStatus("直接结束", SolverUiTokens.Palette.TextMuted)
                : SolverActionPill.Create(turn.EndTurnAction);
            ActionFlow.AddChild(endTurn);
            _endTurnAction = endTurn;
            return;
        }

        foreach (SolverOverlayActionSnapshot action in turn.Actions)
        {
            Control pill = SolverActionPill.Create(action);
            ActionFlow.AddChild(pill);
            _deploymentActions.Add(pill);
        }

        if (turn.EndTurnAction is { Kills.Count: > 0 } endTurnAction)
        {
            Control endTurn = SolverActionPill.Create(endTurnAction);
            ActionFlow.AddChild(endTurn);
            _endTurnAction = endTurn;
        }
    }

    public void SetDeploymentProgress(int completedActions, int? activeActionIndex)
    {
        if (completedActions < 0 || completedActions > _deploymentActions.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedActions),
                completedActions,
                $"路线只有 {_deploymentActions.Count} 个可执行动作胶囊。");
        }
        if (activeActionIndex is { } active
            && (active < completedActions || active >= _deploymentActions.Count))
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeActionIndex),
                active,
                "当前动作必须是尚未完成的路线动作。");
        }

        for (int index = 0; index < _deploymentActions.Count; index++)
        {
            _deploymentActions[index].Modulate = index < completedActions
                ? SolverUiTokens.Palette.CompletedActionModulate
                : index == activeActionIndex
                    ? SolverUiTokens.Palette.ActiveActionModulate
                    : Colors.White;
        }
    }

    public void SetEndTurnDeploymentState(bool active, bool completed)
    {
        if (_endTurnAction == null)
            return;
        _endTurnAction.Modulate = completed
            ? SolverUiTokens.Palette.CompletedActionModulate
            : active
                ? SolverUiTokens.Palette.ActiveActionModulate
                : Colors.White;
    }

    public void ShowStatus(string text)
    {
        ClearActions();
        ActionFlow.AddChild(SolverActionPill.CreateStatus(text, SolverUiTokens.Palette.TextMuted));
    }

    public void SetOutcome(
        string text,
        Color color,
        string energyText = "",
        string enemyDamageText = "")
    {
        EnemyDamageLabel.Text = enemyDamageText;
        OutcomeLabel.Text = text;
        OutcomeLabel.AddThemeColorOverride("font_color", color);
        EnergyLabel.Text = energyText;
    }

    private void ClearActions()
    {
        _deploymentActions.Clear();
        _endTurnAction = null;
        foreach (Node child in ActionFlow.GetChildren())
        {
            ActionFlow.RemoveChild(child);
            child.QueueFree();
        }
    }
}
