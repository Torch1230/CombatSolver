using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CombatSolver;

internal sealed partial class SolverRouteRow : PanelContainer
{
    private readonly List<(CanvasItem Pill, SolverActionRun Run, int Offset)> _deploymentActions = [];
    private int _deploymentActionCount;
    private CanvasItem? _endTurnAction;
    private SolverOverlayTurnSnapshot? _populatedTurn;
    private string? _populatedLanguage;

    public Label TurnLabel { get; }
    public HFlowContainer ActionFlow { get; }
    public Label EnemyDamageLabel { get; }
    public Label OutcomeLabel { get; }
    public Label EnergyLabel { get; }
    public int DeploymentActionCount => _deploymentActionCount;

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
            SolverText.Format($"第 {index + 1} 回合"),
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
        if (HasSameActions(turn))
        {
            // Populate starts a fresh presentation even when its controls survive.
            // Deployment indexes still refer to this row's executable actions only.
            SetDeploymentProgress(0, null);
            SetEndTurnDeploymentState(active: false, completed: false);
            return;
        }
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
                ? SolverActionPill.CreateStatus(SolverText.Get("直接结束"), SolverUiTokens.Palette.TextMuted)
                : SolverActionPill.Create(turn.EndTurnAction);
            ActionFlow.AddChild(endTurn);
            _endTurnAction = endTurn;
            RememberPopulated(turn);
            return;
        }

        foreach (SolverActionRun run in SolverActionRuns.Capture(turn.Actions,
                     static (left, right) => left.HasSamePresentation(right)))
        {
            for (int offset = 0; offset < run.Period; offset++)
            {
                Control pill = SolverActionPill.Create(turn.Actions[run.Start + offset]);
                ActionFlow.AddChild(pill);
                _deploymentActions.Add((pill, run, offset));
            }
            if (run.Repetitions > 1)
            {
                Control badge = SolverActionPill.CreateCycle(run);
                ActionFlow.AddChild(badge);
            }
        }
        _deploymentActionCount = turn.Actions.Count;

        if (turn.EndTurnAction is { Kills.Count: > 0 } endTurnAction)
        {
            Control endTurn = SolverActionPill.Create(endTurnAction);
            ActionFlow.AddChild(endTurn);
            _endTurnAction = endTurn;
        }
        RememberPopulated(turn);
    }

    private void RememberPopulated(SolverOverlayTurnSnapshot turn)
    {
        _populatedLanguage = LocManager.Instance.Language;
        _populatedTurn = turn;
    }

    private bool HasSameActions(SolverOverlayTurnSnapshot turn)
    {
        if (_populatedTurn is not { } previous
            || _populatedLanguage != LocManager.Instance.Language
            || !previous.TurnStartChoices.SequenceEqual(turn.TurnStartChoices)
            || previous.Actions.Count != turn.Actions.Count)
            return false;
        for (int index = 0; index < turn.Actions.Count; index++)
            if (!previous.Actions[index].HasSamePresentation(turn.Actions[index])) return false;
        return ReferenceEquals(previous.EndTurnAction, turn.EndTurnAction)
            || previous.EndTurnAction is { } endTurn && turn.EndTurnAction is { } nextEndTurn
                && endTurn.HasSamePresentation(nextEndTurn);
    }

    public void SetDeploymentProgress(int completedActions, int? activeActionIndex)
    {
        if (completedActions < 0 || completedActions > _deploymentActionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedActions),
                completedActions,
                $"路线只有 {_deploymentActionCount} 个可执行动作。");
        }
        if (activeActionIndex is { } active
            && (active < completedActions || active >= _deploymentActionCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeActionIndex),
                active,
                "当前动作必须是尚未完成的路线动作。");
        }

        foreach (var (pill, run, offset) in _deploymentActions)
        {
            pill.Modulate = run.IsActive(offset, activeActionIndex)
                ? SolverUiTokens.Palette.ActiveActionModulate
                : run.IsCompleted(offset, completedActions)
                    ? SolverUiTokens.Palette.CompletedActionModulate
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
        _populatedTurn = null;
        _populatedLanguage = null;
        _deploymentActions.Clear();
        _deploymentActionCount = 0;
        _endTurnAction = null;
        foreach (Node child in ActionFlow.GetChildren())
        {
            ActionFlow.RemoveChild(child);
            child.QueueFree();
        }
    }
}
