namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private bool _verifyChoiceContinuationStepsForTesting;
    internal bool VerifyChoiceContinuationStepsForTesting
    { init => _verifyChoiceContinuationStepsForTesting = value; }

    private SimulationSnapshot ReplayChoiceReferenceForTesting(SearchNode parent, PlanAction action)
    {
        int forks = _run.ForkCount, transitions = _run.TransitionCount, replays = _run.ReplayCount;
        try { return Replay([action], parent.Snapshot, parent.Turn, parent.ActionCount, allowExecutionCapture: false); }
        finally { _run.ForkCount = forks; _run.TransitionCount = transitions; _run.ReplayCount = replays; }
    }

    private void VerifyRejectedChoiceContinuationStepForTesting(SearchNode parent, PlanAction action, Exception rejection)
    {
        SimulationSnapshot full;
        try { full = ReplayChoiceReferenceForTesting(parent, action); }
        catch (InvalidPlannedChoiceBranchException) { return; }
        full.ReleaseSimulator();
        throw new InvalidOperationException($"Choice continuation rejected a valid complete replay: {PolicyActionIdentityToken(action)}", rejection);
    }

    private void VerifyChoiceContinuationStepForTesting(SearchNode parent, PlanAction action, SimulationSnapshot resumed)
    {
        SimulationSnapshot full;
        try { full = ReplayChoiceReferenceForTesting(parent, action); }
        catch (InvalidPlannedChoiceBranchException error)
        { throw new InvalidOperationException($"Choice continuation accepted an invalid complete replay: {PolicyActionIdentityToken(action)}", error); }
        try
        {
            AssertIncrementalEquivalent(action, [action], resumed, full);
            string Pending(SimulationSnapshot snapshot)
            {
                var request = ((SimulatedCombatState)snapshot.Simulator.State.CombatState).PendingTurnStartChoice;
                if (request is null) return "none";
                var spec = request.Spec!;
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    request.SourceId, request.ContextId, request.Timing, request.Count, request.Effect, request.SourcePile,
                    spec.MinCount, spec.MaxCount, spec.ReplacementValue, spec.MaxBranches, spec.IsImplicitAllSelection,
                    options = spec.Options.Select(CardChoiceSupport.ChoiceCardKey).ToArray(),
                    sources = spec.SourceCards.Select(CardChoiceSupport.ChoiceCardKey).ToArray(),
                    choices = CardChoiceSupport.BuildChoices(spec, displayNames,
                        _profile.MaxPileChoiceBranchesPerAction, _profile.MaxHandChoiceBranchesPerAction),
                });
            }
            string expected = Pending(full), actual = Pending(resumed);
            if (expected != actual || full.ShufflesCrossed != resumed.ShufflesCrossed
                || full.Simulator.History.Entries.Count != resumed.Simulator.History.Entries.Count)
                throw new InvalidOperationException($"Choice continuation pending spec/history differs for {PolicyActionIdentityToken(action)}:\nEXPECTED\n{expected}\nACTUAL\n{actual}");
        }
        finally { full.ReleaseSimulator(); }
    }

    internal void VerifyExecutionSetupBudgetBoundaryForTesting()
    {
        _disableExecutionChoiceContinuationsForTesting = true;
        var reference = BuildTurnSetupRoots();
        int attempts = _run.ChoiceReplayAttempts, replays = _run.ReplayCount;
        _disableExecutionChoiceContinuationsForTesting = false;
        var continued = BuildTurnSetupRoots();
        try
        {
            if (reference.Count != 0 || continued.Count != 0 || _run.ChoiceReplayAttempts != 2 * attempts
                || _run.ReplayCount != 2 * replays || _run.ExecutionChoiceReuses <= 0)
                throw new InvalidOperationException("Deep setup source chain changed the original finite choice budget boundary.");
        }
        finally
        {
            foreach (var entry in reference.Concat(continued)) entry.Snapshot.ReleaseSimulator();
        }
    }

    internal readonly record struct ExecutionContinuationTestResult(int Branches, PlanAction Action, string StateText);

    internal ExecutionContinuationTestResult VerifyExecutionChoiceContinuationForTesting(bool setup, string? cardId = null)
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        SearchNode parent = new(null, 0, rootSnapshot.PotionUseCount, rootSnapshot.PotionStrategicCost,
            rootSnapshot.Turn, SearchRouteTraits.None, 0, rootSnapshot.Score, rootSnapshot.StateKey,
            rootSnapshot.HasRisk, rootSnapshot.BoundaryReason, false, null, rootSnapshot, CombatProgressState.Capture(rootSnapshot));
        var before = CaptureDiagnosticContinuation(rootSnapshot);
        PlanAction action = cardId == null ? new(PlanActionKind.EndTurn, parent.Turn)
            : PrepareCardActions(parent).First(prepared => prepared.Action.CardId == cardId).Action;
        IReadOnlyList<PlanCardChoice> setupChoices = [];
        SimulationSnapshot? seed = null;
        int layers = 0, branchesChecked = 0;
        try
        {
            seed = setup ? ReplayTurnSetup(setupChoices) : ReplayAction(parent, action);
            while (seed.BoundaryReason == SearchBoundaryReason.PendingChoice)
            {
                if (++layers > 20) throw new InvalidOperationException("Search execution continuation failed to advance.");
                var request = ((SimulatedCombatState)seed.Simulator.State.CombatState).PendingTurnStartChoice
                    ?? throw new InvalidOperationException("Search execution fixture lost its pending request.");
                using var checkpoint = TakeExecutionChoiceCheckpoint(setup ? null : parent, setup ? null : action, seed, setupChoices)
                    ?? throw new InvalidOperationException($"Search execution fixture did not capture {request.SourceId} at layer {layers}.");
                var choices = CardChoiceSupport.BuildChoices(request.Spec!, displayNames, 12, 12)
                    .Select(choice => choice with { SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing }).ToArray();
                if (choices.Length == 0) throw new InvalidOperationException("Search execution fixture has no branches.");
                if (request.SourceId == "GAMBLING_CHIP") choices = choices.OrderByDescending(choice => choice.Cards.Count).ToArray();
                SimulationSnapshot? next = null;
                IReadOnlyList<PlanCardChoice>? nextSetup = null;
                PlanAction? nextAction = null;
                try
                {
                    foreach (PlanCardChoice choice in choices.Concat(choices.Take(1)))
                    {
                        IReadOnlyList<PlanCardChoice> proposedSetup = [.. setupChoices, choice];
                        PlanAction proposed = setup ? action : action.Kind == PlanActionKind.EndTurn
                            ? action with { TurnStartChoices = [.. action.TurnStartChoices ?? [], choice] }
                            : action.Choice == null && request.SourceId.Length == 0
                                ? action with { Choice = choice }
                                : action with { NestedChoices = [.. action.NestedChoices ?? [], choice],
                                    NestedChoicesBeforePrimary = action.NestedChoicesBeforePrimary + (action.Choice is null ? 1 : 0) };
                        SimulationSnapshot full = setup ? ReplayTurnSetup(proposedSetup, allowExecutionCapture: false)
                            : Replay([proposed], parent.Snapshot, parent.Turn, parent.ActionCount, allowExecutionCapture: false);
                        SimulationSnapshot? resumed = null;
                        try
                        {
                            int transitions = _run.TransitionCount;
                            _executionChoiceReplayCheckpoint = checkpoint;
                            resumed = setup ? ResumeExecutionChoice(checkpoint, null, null, proposedSetup) : ReplayAction(parent, proposed);
                            AssertIncrementalEquivalent(proposed, [proposed], resumed, full);
                            if (resumed.ShufflesCrossed != full.ShufflesCrossed
                                || resumed.Simulator.History.Entries.Count != full.Simulator.History.Entries.Count
                                || _run.TransitionCount != transitions + (setup ? 0 : 1))
                                throw new InvalidOperationException($"Search execution history/shuffle/budget differs at {request.SourceId}.");
                            branchesChecked++;
                            if (next is null)
                            {
                                next = resumed; resumed = null; nextSetup = proposedSetup; nextAction = proposed;
                            }
                        }
                        finally { _executionChoiceReplayCheckpoint = null; resumed?.ReleaseSimulator(); full.ReleaseSimulator(); }
                    }
                    seed.ReleaseSimulator(); seed = next ?? throw new InvalidOperationException("Search execution lost the first sibling."); next = null;
                    setupChoices = nextSetup!; action = nextAction!;
                }
                finally { next?.ReleaseSimulator(); }
            }
            if (layers < 2 || branchesChecked < 4 || _run.ExecutionChoiceReuses != branchesChecked
                || CaptureDiagnosticContinuation(rootSnapshot) != before)
                throw new InvalidOperationException("Search execution did not prove repeated capture, reuse and parent isolation.");
            return new(branchesChecked, action, CaptureDiagnosticContinuation(seed).StateText);
        }
        finally { seed?.ReleaseSimulator(); rootSnapshot.ReleaseSimulator(); }
    }
}
