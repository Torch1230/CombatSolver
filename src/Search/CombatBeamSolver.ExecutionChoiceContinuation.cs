using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private ExecutionChoiceReplayCheckpoint? _executionChoiceReplayCheckpoint;
    private bool _disableExecutionChoiceContinuationsForTesting;
    internal bool DisableExecutionChoiceContinuationsForTesting
    { init => _disableExecutionChoiceContinuationsForTesting = value; }

    private bool ShouldCaptureExecution(CombatPredictionSimulator simulator, PlanAction action)
    {
        if (_disableExecutionChoiceContinuationsForTesting) return false;
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        if (action.Kind == PlanActionKind.EndTurn)
        {
            foreach (var power in combat.EffectivePowers())
                if (power.Amount > 0 && ReferenceEquals(power.Owner, _player.Creature)
                    && power is ForegoneConclusionPower or EntropyPower or ToolsOfTheTradePower or TyrannyPower or StratagemPower or MayhemPower)
                    return true;
            foreach (var relic in combat.RelicsOf(_player))
                if (!relic.IsMelted && relic.Id.Entry is "TOOLBOX" or "CHOICES_PARADOX" or "GAMBLING_CHIP" or "TOASTY_MITTENS" or "HISTORY_COURSE")
                    return true;
            return false;
        }
        if (action.Kind != PlanActionKind.PlayCard) return false;
        if (action.NestedChoices is { Count: > 0 } || action.CardId is "HAVOC" or "CASCADE" or "DECISIONS_DECISIONS"
            || action.ReplayCount != 0 || combat.GetAmount<StratagemPower>(_player.Creature) > 0) return true;
        PredictedCard? card = FindCardForReplay(simulator.State.GetPlayerCombatState(_player).Hand.Cards, action);
        return card?.Preview.BaseReplayCount > 0;
    }

    // This final frame carries only branch-owned replay accounting. Search performs the
    // action boundary after all engine frames have returned, as in ordinary replay.
    private sealed record ExecutionReplayTailFrame(ForkableSet<uint> Deaths, int Turn,
        int ActionCount, int ShufflesCrossed, int ShuffleEventsBefore, int ConsumedChoices,
        PlayerStartProgress? PlayerStart, bool RootSetup = false) : ICombatPredictionExecutionFrame
    {
        public int ConsumedChoices { get; set; } = ConsumedChoices;
        public void PrepareFork(PredictionForkContext context)
        {
            SimulatedCombatState.ForkExecutionDeaths(Deaths, context);
            PlayerStart?.Fork(context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Deaths = context.RequireRemap(Deaths),
                PlayerStart = PlayerStart is null ? null : context.RequireRemap(PlayerStart) };
        public bool Resume(CombatPredictionSimulator simulator) => true;
    }

    private ExecutionChoiceReplayCheckpoint? TakeExecutionChoiceCheckpoint(SearchNode? parent,
        PlanAction? action, SimulationSnapshot snapshot, IReadOnlyList<PlanCardChoice>? setupChoices = null)
    {
        var simulator = snapshot.Simulator;
        var tail = simulator.CapturedExecutionFrame<ExecutionReplayTailFrame>();
        if (tail is null) return null;
        IReadOnlyList<PlanCardChoice> prefix = tail.RootSetup ? setupChoices ?? []
            : tail.PlayerStart != null ? action!.TurnStartChoices ?? [] : ActionChoicesForReplay(action!) ?? [];
        // A continuation owns only a fully consumed explicit prefix. Plans whose next
        // preselected choice precedes this request keep the complete replay path.
        if (tail.ConsumedChoices != prefix.Count) return null;
        PredictionExecutionContinuation? plan = simulator.TakeExecutionContinuation();
        if (plan is null) return null;
        _run.ExecutionChoiceCaptures++;
        return new(parent, action, prefix.ToArray(), simulator, plan, tail);
    }

    private sealed class ExecutionChoiceReplayCheckpoint(SearchNode? parent, PlanAction? action,
        IReadOnlyList<PlanCardChoice> prefix, CombatPredictionSimulator simulator,
        PredictionExecutionContinuation continuation, ExecutionReplayTailFrame tail) : IDisposable
    {
        private readonly object _gate = new();
        private CombatPredictionSimulator? _simulator = simulator;
        private SearchNode? _parent = parent;
        private PlanAction? _action = action;
        private IReadOnlyList<PlanCardChoice>? _prefix = prefix;
        private PredictionExecutionContinuation? _continuation = continuation;
        private readonly bool _rootSetup = tail.RootSetup;
        private readonly bool _roundStart = tail.PlayerStart != null;
        public bool RootSetup => _rootSetup;
        public int PrefixCount { get; } = prefix.Count;
        public bool Matches(SearchNode candidate, PlanAction resolved)
        {
            if (!ReferenceEquals(_parent, candidate) || _action is null || _rootSetup) return false;
            if (resolved with { Choice = _action.Choice, NestedChoices = _action.NestedChoices,
                NestedChoicesBeforePrimary = _action.NestedChoicesBeforePrimary, TurnStartChoices = _action.TurnStartChoices } != _action)
                return false;
            IReadOnlyList<PlanCardChoice> choices = _roundStart
                ? resolved.TurnStartChoices ?? [] : ActionChoicesForReplay(resolved) ?? [];
            return MatchesPrefix(choices);
        }
        public bool MatchesPrefix(IReadOnlyList<PlanCardChoice> choices)
            => _prefix != null && choices.Count == PrefixCount + 1 && choices.Take(PrefixCount).SequenceEqual(_prefix);
        public IReadOnlyList<PlanCardChoice> RemainingChoices(PlanAction resolved)
            => (_roundStart ? resolved.TurnStartChoices ?? [] : ActionChoicesForReplay(resolved) ?? [])
                .Skip(PrefixCount).ToArray();
        public (CombatPredictionSimulator Simulator, PredictionExecutionContinuation Plan, ExecutionReplayTailFrame Tail)
            Fork(CombatBeamSolver owner)
        {
            lock (_gate)
            {
                owner.SearchCancellationToken.ThrowIfCancellationRequested();
                owner._run.ExecutionChoiceReuses++;
                if (_rootSetup) owner._run.ReplayCount++;
                else { owner._run.ForkCount++; owner._run.TransitionCount++; }
                using var measure = owner._run.Performance.Measure(SearchMetricPhase.Fork);
                using PredictionForkContext context = new();
                var child = (_simulator ?? throw new ObjectDisposedException(nameof(ExecutionChoiceReplayCheckpoint)))
                    .ForkExecutionContinuation(_continuation!, context, out var copied);
                return (child, copied, copied.Steps.Select(step => step.Frame).OfType<ExecutionReplayTailFrame>().Single());
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                _simulator = null; _parent = null; _action = null; _prefix = null; _continuation = null;
            }
        }
    }

    private SimulationSnapshot ResumeExecutionChoice(ExecutionChoiceReplayCheckpoint checkpoint,
        SearchNode? parent, PlanAction? action, IReadOnlyList<PlanCardChoice>? setupChoices = null)
    {
        _run.WorkPacer.YieldIfNeeded();
        var (simulator, plan, tail) = checkpoint.Fork(this);
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        IReadOnlyList<PlanCardChoice> remaining = tail.RootSetup
            ? setupChoices!.Skip(checkpoint.PrefixCount).ToArray() : checkpoint.RemainingChoices(action!);
        int turn = tail.Turn, shuffles = tail.ShufflesCrossed;
        combat.BeginActionChoices(remaining);
        SearchBoundaryReason boundary;
        try
        {
            using var measure = _run.Performance.Measure(SearchMetricPhase.ExecutionChoiceResume);
            boundary = simulator.ResumeExecutionContinuation(plan) ? SearchBoundaryReason.None : SearchBoundaryReason.PendingChoice;
            if (boundary == SearchBoundaryReason.None)
            {
                using var dispatch = simulator.BeginExecutionDispatch();
                if (tail.PlayerStart is null && !tail.RootSetup
                    && !CorePowerSupport.ApplyEnemyDeathPowers(simulator, combat, combat.KnownEnemies, tail.Deaths))
                    boundary = SearchBoundaryReason.PendingChoice;
                if (boundary == SearchBoundaryReason.None && !SettleReplayActionBoundary(simulator, combat))
                    boundary = SearchBoundaryReason.PendingChoice;
            }
        }
        finally { combat.EndActionChoices(); }
        if (tail.PlayerStart != null)
        {
            shuffles = tail.PlayerStart.ShufflesCrossed;
            turn = combat.GetPlayerTurnNumber(_player);
        }
        if (boundary == SearchBoundaryReason.None && tail.PlayerStart is null && !tail.RootSetup)
        {
            if (simulator.ShuffleEventCount != tail.ShuffleEventsBefore) shuffles++;
            simulator.CheckWinCondition(combat.GetPlayerTurnNumber(_player));
            boundary = ResolveRequestedPlayerTurnEnd(simulator, combat, action!, tail.Deaths, ref turn, ref shuffles);
        }
        if (tail.PlayerStart != null && !tail.RootSetup) _ = combat.ConsumePlayerTurnEndRequest();
        // The cursor on a resumed child contains only the newly appended choices. Store
        // their cumulative count before another branch takes ownership of this seed.
        tail.ConsumedChoices = checkpoint.PrefixCount + combat.LastActionChoicesConsumed;
        return Snapshot(simulator, turn, tail.ActionCount, tail.RootSetup ? simulator.ShuffleEventCount : shuffles, boundary, tail.Deaths);
    }
}
