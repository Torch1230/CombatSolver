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
    public SolverRouteActionFlow ActionFlow { get; }
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

        ActionFlow = new SolverRouteActionFlow
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
            // Populate starts a fresh presentation even when its controls survive: colors snap
            // back instead of fading out a previous deployment.
            ResetDeploymentMotion();
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
            Container destination = ActionFlow;
            if (run.Repetitions > 1)
            {
                ActionFlow.AddChild(SolverActionPill.CreateCycle(run, out HFlowContainer loopActions));
                destination = loopActions;
            }
            for (int offset = 0; offset < run.Period; offset++)
            {
                Control pill = SolverActionPill.Create(turn.Actions[run.Start + offset]);
                destination.AddChild(pill);
                _deploymentActions.Add((pill, run, offset));
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

        // Pace is measured between newly current actions only; completion-only updates (the final
        // step, end of turn) arrive back to back and would otherwise read as an instant deployment.
        if (activeActionIndex is { } current)
            NoteDeploymentStep((completedActions, current));
        foreach (var (pill, run, offset) in _deploymentActions)
        {
            bool isActive = run.IsActive(offset, activeActionIndex);
            SetPillTarget(
                pill,
                isActive
                    ? SolverUiTokens.Palette.ActiveActionModulate
                    : run.IsCompleted(offset, completedActions)
                        ? SolverUiTokens.Palette.CompletedActionModulate
                        : Colors.White,
                isActive);
        }
    }

    public void SetEndTurnDeploymentState(bool active, bool completed)
    {
        if (_endTurnAction == null)
            return;
        if (active && !completed)
            NoteDeploymentStep((-1, -1));
        SetPillTarget(
            _endTurnAction,
            completed
                ? SolverUiTokens.Palette.CompletedActionModulate
                : active
                    ? SolverUiTokens.Palette.ActiveActionModulate
                    : Colors.White,
            active && !completed);
    }

    // Deployment highlight motion. Steps can arrive faster than any fixed animation (Instant speed
    // with zero extra delay plays several cards per second), so every duration is derived from the
    // measured gap between steps: fast deployments collapse to near-instant color changes and never
    // flash or breathe, slow ones get a visible fade, an activation flash and a breathing highlight.
    // Motion only touches Modulate after the target is decided; the target itself stays synchronous.
    private const double MaxHighlightTimeConstantSeconds = 0.08d;
    private const double HighlightTimeConstantShareOfStep = 0.2d;
    private const double MinStepForFlashSeconds = 0.3d;
    private const float ActivationFlashGain = 1.45f;
    private const double BreathDelaySeconds = 0.45d;
    private const double BreathRampSeconds = 0.35d;
    private const double BreathPeriodSeconds = 1.2d;
    private const float BreathGain = 0.22f;

    private readonly Dictionary<CanvasItem, PillMotion> _pillMotion = [];
    private (int, int)? _lastDeploymentStep;
    private ulong _lastDeploymentStepUsec;
    private double? _deploymentStepSeconds;

    private sealed class PillMotion
    {
        public Color Current;
        public Color Target;
        public bool Active;
        public double ActiveSeconds;
    }

    private void NoteDeploymentStep((int, int) step)
    {
        if (_lastDeploymentStep == step)
            return;
        ulong now = Time.GetTicksUsec();
        if (_lastDeploymentStep != null)
            _deploymentStepSeconds = (now - _lastDeploymentStepUsec) / 1_000_000d;
        _lastDeploymentStep = step;
        _lastDeploymentStepUsec = now;
    }

    // Unknown pace (first step of a deployment) counts as slow so the route start is marked.
    private double HighlightTimeConstant()
        => Math.Min(
            MaxHighlightTimeConstantSeconds,
            (_deploymentStepSeconds ?? double.PositiveInfinity) * HighlightTimeConstantShareOfStep);

    private void SetPillTarget(CanvasItem pill, Color target, bool active)
    {
        if (!_pillMotion.TryGetValue(pill, out PillMotion? motion))
        {
            motion = new PillMotion { Current = pill.Modulate };
            _pillMotion.Add(pill, motion);
        }
        if (active && !motion.Active)
        {
            motion.ActiveSeconds = 0d;
            if ((_deploymentStepSeconds ?? double.PositiveInfinity) >= MinStepForFlashSeconds)
                motion.Current = Brighten(target, ActivationFlashGain);
        }
        motion.Target = target;
        motion.Active = active;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (_pillMotion.Count == 0)
        {
            SetProcess(false);
            return;
        }
        double timeConstant = HighlightTimeConstant();
        // Below about half a frame an eased step is indistinguishable from a snap.
        double blend = timeConstant < 0.008d ? 1d : SolverUiMotion.Blend(delta, timeConstant);
        bool moving = false;
        foreach (var (pill, motion) in _pillMotion)
        {
            if (!GodotObject.IsInstanceValid(pill))
                continue;
            motion.Current = SolverUiMotion.Approach(motion.Current, motion.Target, blend);
            Color shown = motion.Current;
            if (motion.Active)
            {
                motion.ActiveSeconds += delta;
                double breath = BreathLevel(motion.ActiveSeconds);
                shown = Brighten(shown, 1f + BreathGain * (float)breath);
                moving = true;
            }
            moving |= motion.Current != motion.Target;
            if (pill.Modulate != shown)
                pill.Modulate = shown;
        }
        if (!moving)
            SetProcess(false);
    }

    // A pill that stays current for a while (slow speed, a choice screen, a long card animation)
    // starts breathing; at fast paces the next step arrives before the delay and nothing pulses.
    private static double BreathLevel(double activeSeconds)
    {
        if (activeSeconds <= BreathDelaySeconds)
            return 0d;
        double sinceDelay = activeSeconds - BreathDelaySeconds;
        double ramp = Math.Min(1d, sinceDelay / BreathRampSeconds);
        return ramp * (0.5d - 0.5d * Math.Cos(sinceDelay / BreathPeriodSeconds * Math.Tau));
    }

    private static Color Brighten(Color color, float gain)
        => new(color.R * gain, color.G * gain, color.B * gain, color.A);

    private void ResetDeploymentMotion()
    {
        foreach (var (pill, _) in _pillMotion)
            if (GodotObject.IsInstanceValid(pill))
                pill.Modulate = Colors.White;
        _pillMotion.Clear();
        _lastDeploymentStep = null;
        _deploymentStepSeconds = null;
    }

    // Instant-speed steps must snap and never breathe; unknown or slow paces keep the full fade.
    internal static bool ExerciseDeploymentPacingForTesting()
    {
        SolverRouteRow row = new(0);
        try
        {
            double unknown = row.HighlightTimeConstant();
            row._deploymentStepSeconds = 0.02d;
            double instant = row.HighlightTimeConstant();
            row._deploymentStepSeconds = 0.2d;
            double quick = row.HighlightTimeConstant();
            row._deploymentStepSeconds = 1.5d;
            double slow = row.HighlightTimeConstant();
            return unknown == MaxHighlightTimeConstantSeconds
                && instant < 0.008d
                && Math.Abs(quick - 0.04d) < 1e-9
                && slow == MaxHighlightTimeConstantSeconds
                && BreathLevel(BreathDelaySeconds) == 0d
                && BreathLevel(0.2d) == 0d
                && BreathLevel(BreathDelaySeconds + BreathPeriodSeconds / 2d) > 0.99d;
        }
        finally
        {
            row.Free();
        }
    }

    internal void SettleDeploymentMotionForTesting()
    {
        foreach (var (pill, motion) in _pillMotion)
        {
            motion.Current = motion.Target;
            if (GodotObject.IsInstanceValid(pill))
                pill.Modulate = motion.Target;
        }
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
        ResetDeploymentMotion();
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
