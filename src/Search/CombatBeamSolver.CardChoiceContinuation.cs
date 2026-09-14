using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private CardChoiceReplayCheckpoint? _cardChoiceReplayCheckpoint;
    private bool _disableCardChoiceContinuationsForTesting;
    internal bool DisableCardChoiceContinuationsForTesting
    { init => _disableCardChoiceContinuationsForTesting = value; }

    internal int VerifyCardChoiceContinuationForTesting()
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
            foreach (PreparedCardAction prepared in PrepareCardActions(parent)
                .Where(c => CombatPredictionSimulator.SupportsManualCardChoiceContinuation(c.Action.CardId)))
            {
                PlanAction action = prepared.Action;
                using CardChoiceReplayCapture capture = PrepareCardChoiceCapture(parent, action)!;
                SimulationSnapshot probe = ReplayAction(parent, action, cardChoiceCapture: capture);
                using CardChoiceReplayCheckpoint checkpoint = capture.Take()
                    ?? throw new InvalidOperationException("Search discovery did not capture card continuation.");
                PlanCardChoice[] choices;
                try
                {
                    CardChoiceSpec spec = BuildPrimaryCardChoiceSpec(probe)!;
                    choices = CardChoiceSupport.BuildChoices(spec, displayNames, 32, 32).ToArray();
                }
                finally { probe.ReleaseSimulator(); }
                foreach (PlanCardChoice choice in choices.Concat(choices.Take(1)))
                {
                    PlanAction resolved = action with { Choice = choice };
                    SimulationSnapshot full = ReplayAction(parent, resolved);
                    SimulationSnapshot? resumed = null;
                    int transitions = _run.TransitionCount;
                    try
                    {
                        _parallelActionReplayForkGate = new object();
                        _cardChoiceReplayCheckpoint = checkpoint;
                        resumed = ReplayAction(parent, resolved);
                        AssertIncrementalEquivalent(resolved, [resolved], resumed, full);
                        if (resumed.ShufflesCrossed != full.ShufflesCrossed
                            || resumed.Simulator.History.Entries.Count != full.Simulator.History.Entries.Count
                            || _run.TransitionCount != transitions + 1)
                            throw new InvalidOperationException("Card continuation history/shuffle/budget differs.");
                        checkedBranches++;
                    }
                    finally
                    {
                        _parallelActionReplayForkGate = null;
                        _cardChoiceReplayCheckpoint = null;
                        resumed?.ReleaseSimulator(); full.ReleaseSimulator();
                    }
                }
            }
            if (checkedBranches < 6 || _run.CardChoicePrefixReuses != checkedBranches
                || _run.CardChoicePrefixFallbacks <= 0 || CaptureDiagnosticContinuation(rootSnapshot) != before)
                throw new InvalidOperationException("Search continuation siblings/fallback/isolation not exercised.");
            return checkedBranches;
        }
        finally { rootSnapshot.ReleaseSimulator(); }
    }

    private CardChoiceReplayCapture? PrepareCardChoiceCapture(SearchNode parent, PlanAction action)
    {
        if (_disableCardChoiceContinuationsForTesting || action.Kind != PlanActionKind.PlayCard
            || !CombatPredictionSimulator.SupportsManualCardChoiceContinuation(action.CardId)
            || action.ReplayCount != 0 || action.Choice != null
            || action.NestedChoices is { Count: > 0 } || action.TurnStartChoices is { Count: > 0 })
            return null;
        _run.CardChoicePrefixAttempts++;
        return new CardChoiceReplayCapture(parent, action);
    }

    private sealed class CardChoiceReplayCapture(SearchNode parent, PlanAction action) : IDisposable
    {
        private CardChoiceReplayCheckpoint? _checkpoint;
        public void Receive(CombatBeamSolver owner, CombatPredictionSimulator simulator, ForkableSet<uint> deaths)
        {
            if (_checkpoint != null) throw new InvalidOperationException("Card choice capture received twice.");
            CardChoiceContinuation? continuation = CardChoiceContinuation.Take(simulator, deaths);
            if (continuation is null) return;
            try { _checkpoint = new CardChoiceReplayCheckpoint(parent, action, continuation); }
            catch { continuation.Dispose(); throw; }
            owner._run.CardChoicePrefixCaptures++;
        }
        public CardChoiceReplayCheckpoint? Take()
        {
            CardChoiceReplayCheckpoint? result = _checkpoint;
            _checkpoint = null;
            return result;
        }
        public void Dispose() { _checkpoint?.Dispose(); _checkpoint = null; }
    }

    // A checkpoint is scoped to exactly one parent/action. The original primary-choice frontier
    // owns it across all producers and its single ordered consumer; serial traversal owns it locally.
    private sealed class CardChoiceReplayCheckpoint(SearchNode parent, PlanAction action,
        CardChoiceContinuation continuation) : IDisposable
    {
        private SearchNode? _parent = parent;
        private PlanAction? _action = action;
        private CardChoiceContinuation? _continuation = continuation;
        public bool Matches(SearchNode candidate, PlanAction resolved)
            => ReferenceEquals(_parent, candidate) && _action != null && resolved.Choice != null
                && resolved == (_action with { Choice = resolved.Choice });

        public ReplayForkSeed Fork(CombatBeamSolver owner, out ManualCardChoiceFrame frame)
        {
            owner._run.ForkCount++;
            owner._run.CardChoicePrefixReuses++;
            using var measure = owner._run.Performance.Measure(SearchMetricPhase.Fork);
            var child = (_continuation ?? throw new ObjectDisposedException(nameof(CardChoiceReplayCheckpoint)))
                .Fork(owner.SearchCancellationToken);
            frame = child.Frame;
            return new ReplayForkSeed(child.Simulator, child.Deaths);
        }
        public void Dispose()
        {
            _continuation?.Dispose();
            _continuation = null; _parent = null; _action = null;
        }
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> WithCardChoiceCheckpoint(
        CardChoiceReplayCheckpoint? checkpoint,
        IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> branches)
    {
        if (checkpoint is null) return branches;
        return Enumerate(this, checkpoint, branches);
        static IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> Enumerate(
            CombatBeamSolver owner, CardChoiceReplayCheckpoint checkpoint,
            IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> branches)
        {
            using (checkpoint)
            {
                CardChoiceReplayCheckpoint? previous = owner._cardChoiceReplayCheckpoint;
                owner._cardChoiceReplayCheckpoint = checkpoint;
                try
                {
                    foreach (var branch in branches) yield return branch;
                }
                finally { owner._cardChoiceReplayCheckpoint = previous; }
            }
        }
    }
}
