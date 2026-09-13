namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed record EndTurnChoiceLayer(
        PendingChoiceReplayLayer Layer,
        ChoiceSearchBudget Budget,
        ChoiceOccurrenceCollector<DeferredOccurrenceChoiceBranch> Occurrences);

    private readonly record struct PreparedEndTurnEvaluation(
        ExpansionBatch? Batch,
        IReadOnlyList<CrossTurnStandPatBaseline>? Baselines,
        PrimaryChoiceReplayFrontier? Frontier);

    private PreparedEndTurnEvaluation EvaluatePreparedEndTurn(
        SearchNode parent, object forkGate, PrimaryChoiceReplayFrontier? frontier = null)
    {
        if (_parallelActionReplayForkGate != null)
            throw new InvalidOperationException("不能嵌套回合尾部作业的 Fork 上下文。");
        ExpansionBatch batch = RentExpansionBatch();
        bool completed = false;
        _parallelActionReplayForkGate = forkGate;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> branches;
            if (frontier != null)
            {
                branches = ResolvePreparedEndTurnChoices(parent, frontier.EndTurn!, frontier);
            }
            else if (parent.Snapshot.PlayerDead || parent.Snapshot.AllEnemiesDead)
            {
                completed = true;
                return new PreparedEndTurnEvaluation(batch, null, null);
            }
            else
            {
                PlanAction action = new(PlanActionKind.EndTurn, parent.Turn);
                SimulationSnapshot snapshot = ReplayAction(parent, action);
                EndTurnChoiceLayer? layer = PrepareEndTurnChoiceLayer(parent, action, snapshot);
                if (layer == null)
                {
                    branches = ResolveRoundChoiceBranches(parent, action, snapshot);
                }
                else if (CanReservePrimaryReplayPrefix(layer.Layer.Branches.Count,
                    layer.Budget.ActiveFinalQuota, layer.Budget.ReplayAttemptQuota))
                {
                    var prepared = new PrimaryChoiceReplayFrontier(layer);
                    batch.Dispose();
                    completed = true;
                    return new PreparedEndTurnEvaluation(null, null, prepared);
                }
                else
                {
                    branches = ResolvePreparedEndTurnChoices(parent, layer);
                }
            }
            // The private batch and baselines remain unpublished until all sibling jobs drain.
            var baselines = GenerateRawEndTurnCandidates(parent, batch,
                publishBaselines: false, resolvedBranches: branches);
            frontier?.AssertConsumed();
            completed = true;
            return new PreparedEndTurnEvaluation(batch, baselines, null);
        }
        finally
        {
            _parallelActionReplayForkGate = null;
            if (!completed) batch.Dispose();
        }
    }

    private EndTurnChoiceLayer? PrepareEndTurnChoiceLayer(
        SearchNode parent, PlanAction action, SimulationSnapshot snapshot)
    {
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice
            || !GrowthCostPolicy.AllowsBrightestFlame(policy.BrightestFlameMaxHpLossLimit,
                root.InitialBrightestFlameMaxHpSpent, snapshot.BrightestFlameMaxHpSpent))
            return null;
        try
        {
            WholeActionChoiceBudget budget = CreateWholeActionChoiceBudget(null,
                BuildChoiceSpecSemanticBranchCount(null), BuildCurrentPendingChoiceBudgetSeed(snapshot));
            var occurrences = new ChoiceOccurrenceCollector<DeferredOccurrenceChoiceBranch>(
                budget.OccurrenceFinalReserve, budget.OccurrenceReplayAttemptQuota);
            if (!budget.SemanticSearchBudget.HasWork)
            {
                // Preserve the serial exhaustion path, including its accounting.
                return null;
            }
            PendingChoiceReplayLayer layer = BuildPendingChoiceReplayLayer(parent, action,
                snapshot, null, budget.SemanticSearchBudget, occurrences);
            snapshot.ReleaseSimulator();
            return new EndTurnChoiceLayer(layer, budget.SemanticSearchBudget, occurrences);
        }
        catch
        {
            snapshot.ReleaseSimulator();
            throw;
        }
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolvePreparedEndTurnChoices(
        SearchNode parent, EndTurnChoiceLayer layer, PrimaryChoiceReplayFrontier? frontier = null)
    {
        foreach (var branch in ResolvePendingChoiceReplayLayer(parent, layer.Layer, null,
            layer.Budget, layer.Occurrences, frontier))
            yield return branch;
        foreach (var branch in ResolveCollectedOccurrenceChoiceBranches(parent, layer.Occurrences))
            yield return branch;
    }
}
