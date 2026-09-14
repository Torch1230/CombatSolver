using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private PotionChoiceReplayCheckpoint? _potionChoiceReplayCheckpoint;
    private bool _disablePotionChoiceContinuationsForTesting;
    internal bool DisablePotionChoiceContinuationsForTesting
    { init => _disablePotionChoiceContinuationsForTesting = value; }

    internal int VerifyPotionChoiceContinuationForTesting()
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        SearchNode parent = new(null, 0, rootSnapshot.PotionUseCount,
            rootSnapshot.PotionStrategicCost, rootSnapshot.Turn, SearchRouteTraits.None,
            0, rootSnapshot.Score, rootSnapshot.StateKey, rootSnapshot.HasRisk,
            rootSnapshot.BoundaryReason, false, null, rootSnapshot,
            CombatProgressState.Capture(rootSnapshot));
        ContinuationStamp before = CaptureDiagnosticContinuation(rootSnapshot);
        int checkedBranches = 0;
        try
        {
            foreach (PreparedPotionAction prepared in PreparePotionActions(parent))
            {
                using var checkpoint = PreparePotionChoiceOptions(parent, prepared.Action, prepared.Potion,
                    out SimulationSnapshot? probe, out IReadOnlyList<PlanCardChoice?> choices, out _);
                probe?.ReleaseSimulator();
                if (checkpoint is null) throw new InvalidOperationException("Potion search did not capture a checkpoint.");
                foreach (PlanCardChoice? choice in choices.Concat(choices.Take(1)))
                {
                    PlanAction resolved = prepared.Action with { Choice = choice };
                    SimulationSnapshot full = ReplayAction(parent, resolved);
                    SimulationSnapshot? resumed = null;
                    int transitions = _run.TransitionCount;
                    try
                    {
                        _parallelActionReplayForkGate = new object();
                        _potionChoiceReplayCheckpoint = checkpoint;
                        resumed = ReplayAction(parent, resolved);
                        AssertIncrementalEquivalent(resolved, [resolved], resumed, full);
                        if (resumed.ShufflesCrossed != full.ShufflesCrossed
                            || resumed.Simulator.History.Entries.Count != full.Simulator.History.Entries.Count
                            || _run.TransitionCount != transitions + 1)
                            throw new InvalidOperationException("Potion continuation history/shuffle/budget differs.");
                        checkedBranches++;
                    }
                    finally
                    {
                        _parallelActionReplayForkGate = null; _potionChoiceReplayCheckpoint = null;
                        resumed?.ReleaseSimulator(); full.ReleaseSimulator();
                    }
                }
            }
            if (checkedBranches < 3 || _run.PotionChoicePrefixReuses < checkedBranches
                || CaptureDiagnosticContinuation(rootSnapshot) != before)
                throw new InvalidOperationException("Potion search continuation siblings/isolation not exercised.");
            return checkedBranches;
        }
        finally { rootSnapshot.ReleaseSimulator(); }
    }

    private PotionChoiceReplayCheckpoint? PreparePotionChoiceOptions(SearchNode parent, PlanAction action,
        PotionModel potion, out SimulationSnapshot? probe, out IReadOnlyList<PlanCardChoice?> choices,
        out CardChoiceSpec? spec)
    {
        probe = null; spec = null;
        PotionChoiceReplayCheckpoint? checkpoint = null;
        PotionChoiceReplayCheckpoint? previous = _potionChoiceReplayCheckpoint;
        try
        {
            if (!PotionChoiceSupport.RequiresChoice(potion))
            {
                probe = ReplayAction(parent, action); choices = [null];
                return null;
            }
            var source = (CombatPredictionSimulator)parent.Snapshot.Simulator;
            bool generates = PotionChoiceSupport.GeneratesCardChoice(potion);
            if (generates)
            {
                checkpoint = PreparePotionChoiceCheckpoint(parent, action, potion);
                _potionChoiceReplayCheckpoint = checkpoint;
                probe = ReplayAction(parent, action);
                _potionChoiceReplayCheckpoint = previous;
                source = (CombatPredictionSimulator)probe.Simulator;
            }
            spec = PotionChoiceSupport.GetSpec(source, potion);
            choices = CardChoiceSupport.BuildChoices(spec, displayNames,
                    _profile.MaxPileChoiceBranchesPerAction, _profile.MaxHandChoiceBranchesPerAction)
                .Select(choice => choice with { SourceId = potion.Id.Entry }).Cast<PlanCardChoice?>().ToList();
            probe?.ReleaseSimulator(); probe = null;
            if (!generates && choices.Count >= 2)
                checkpoint = PreparePotionChoiceCheckpoint(parent, action, potion);
            return checkpoint;
        }
        catch
        {
            checkpoint?.Dispose(); probe?.ReleaseSimulator(); probe = null;
            throw;
        }
        finally { _potionChoiceReplayCheckpoint = previous; }
    }

    private PotionChoiceReplayCheckpoint? PreparePotionChoiceCheckpoint(SearchNode parent, PlanAction action, PotionModel potion)
    {
        if (_disablePotionChoiceContinuationsForTesting || !PotionChoiceContinuation.Supports(potion)
            || action.Choice != null || action.NestedChoices is { Count: > 0 }
            || !((CombatPredictionSimulator)parent.Snapshot.Simulator).StateStore.SupportsManualCardChoiceContinuation)
            return null;
        return SearchTransitionGuard.Execute(action, parent.StateKey, parent.ActionCount, () =>
        {
            CombatPredictionSimulator seed;
            ForkableSet<uint> deaths;
            if (_parallelActionReplayForkGate != null)
            {
                using var fork = PrepareReplayForkSeed(parent.Snapshot, _parallelActionReplayForkGate);
                (seed, deaths) = fork.Take();
            }
            else
            {
                _run.ForkCount++;
                using var measure = _run.Performance.Measure(SearchMetricPhase.Fork);
                seed = ((CombatPredictionSimulator)parent.Snapshot.Simulator).Fork();
                deaths = ((ForkableSet<uint>)parent.Snapshot.ProcessedEnemyDeaths).Fork();
            }
            // Includes a prepared prefix that later cannot be retained, so physical work is
            // accounted for even when the original complete-action path must be used.
            _run.PotionChoicePrefixForks++;
            var combat = (SimulatedCombatState)seed.State.CombatState;
            PotionModel source = combat.GetPotionAtSlot(_player, action.PotionSlot)
                ?? throw new InvalidOperationException("Preparing a potion continuation from an empty slot.");
            if (source.Id.Entry != action.PotionId)
                throw new InvalidOperationException("Potion continuation source identity changed.");
            PotionChoiceFrame frame = new(source, seed.History.Entries.Count, seed.ShuffleEventCount);
            combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
            bool completed;
            try
            {
                using var measure = _run.Performance.Measure(SearchMetricPhase.PotionExecution);
                completed = PotionExecutionSupport.Prepare(seed, combat, source, action.PotionSlot,
                    combat.GetCreature(action.TargetCombatId));
            }
            finally { combat.EndActionChoices(); }
            if (!completed || !combat.CanCaptureStableChoicePrefix
                || !seed.StateStore.SupportsManualCardChoiceContinuation)
                return null;
            var continuation = new PotionChoiceContinuation(seed, deaths, frame);
            _run.PotionChoicePrefixCaptures++;
            return new PotionChoiceReplayCheckpoint(parent, action, continuation);
        });
    }

    private sealed class PotionChoiceReplayCheckpoint(SearchNode parent, PlanAction action,
        PotionChoiceContinuation continuation) : IDisposable
    {
        private SearchNode? _parent = parent;
        private PlanAction? _action = action;
        private PotionChoiceContinuation? _continuation = continuation;

        // The original no-selection probe is also resumed, so its existing after-use hooks
        // still run before generated options are read by the unchanged choice builder.
        public bool Matches(SearchNode candidate, PlanAction resolved)
            => ReferenceEquals(_parent, candidate) && _action != null
                && resolved == (_action with { Choice = resolved.Choice });

        public ReplayForkSeed Fork(CombatBeamSolver owner, out PotionChoiceFrame frame)
        {
            owner._run.ForkCount++; owner._run.PotionChoicePrefixReuses++;
            using var measure = owner._run.Performance.Measure(SearchMetricPhase.Fork);
            var child = (_continuation ?? throw new ObjectDisposedException(nameof(PotionChoiceReplayCheckpoint)))
                .Fork(owner.SearchCancellationToken);
            frame = child.Frame;
            return new ReplayForkSeed(child.Simulator, child.Deaths);
        }

        public void Dispose()
        {
            _continuation?.Dispose(); _continuation = null; _parent = null; _action = null;
        }
    }
    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> WithPotionChoiceCheckpoint(
        PotionChoiceReplayCheckpoint? checkpoint,
        IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> branches)
    {
        if (checkpoint is null) return branches;
        return Enumerate(this, checkpoint, branches);
        static IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> Enumerate(
            CombatBeamSolver owner, PotionChoiceReplayCheckpoint checkpoint,
            IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> branches)
        {
            using (checkpoint)
            {
                PotionChoiceReplayCheckpoint? previous = owner._potionChoiceReplayCheckpoint;
                owner._potionChoiceReplayCheckpoint = checkpoint;
                try { foreach (var branch in branches) yield return branch; }
                finally { owner._potionChoiceReplayCheckpoint = previous; }
            }
        }
    }

}
