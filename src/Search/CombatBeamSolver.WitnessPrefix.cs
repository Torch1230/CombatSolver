namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // The ordinary fixed-prefix entry keeps its existing restrictions. This
    // offline seed replays the complete observed chain through the existing
    // action/round implementation and checks every physical and policy identity.
    private SearchNode ApplyWitnessPrefix(SearchNode seed, SearchWitnessPrefix prefix)
    {
        SearchNode node = seed;
        try
        {
            if (prefix.Steps.Length is < 1 or > SearchWitnessPrefix.MaximumActions)
                throw new InvalidOperationException("Invalid continuation prefix length.");
            foreach (SearchWitnessPrefix.Step step in prefix.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node.IsTerminal || step.Action.Turn != node.Turn
                    || !CanApplyFixedPrefixAction(node, step.Action))
                    throw new InvalidOperationException("Observed continuation action cannot be replayed.");
                SimulationSnapshot snapshot = Replay([step.Action], node.Snapshot, node.Turn, node.ActionCount,
                    allowExecutionCapture: false);
                SearchNode child;
                try
                {
                    if (snapshot.StateKey != step.State || snapshot.Turn != step.Turn
                        || SearchWitnessPrefix.HpCost(snapshot) != step.HpCost
                        || snapshot.PotionStrategicCost != step.PotionCost
                        || snapshot.HasRisk || snapshot.BoundaryReason != SearchBoundaryReason.None
                        || snapshot.PlayerDead || snapshot.AllEnemiesDead)
                        throw new InvalidOperationException($"Continuation identity mismatch at action {node.ActionCount + 1}.");
                    child = new(step.Action, node.ActionCount + 1, snapshot.PotionUseCount,
                        snapshot.PotionStrategicCost, snapshot.Turn, step.Traits, step.FutureSoldHp,
                        ApplySoldHpPenalty(snapshot.Score, step.FutureSoldHp), snapshot.StateKey,
                        snapshot.HasRisk, snapshot.BoundaryReason, false, node, snapshot,
                        node.CombatProgress.Advance(snapshot))
                    {
                        CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, snapshot),
                        Outcome = step.Outcome,
                    };
                }
                catch { snapshot.ReleaseSimulator(); throw; }
                node.Snapshot.ReleaseSimulator();
                node = child;
            }
            // A fresh bounded continuation search, not restoration of the old
            // frontier's scheduling leases. The complete prefix remains attached
            // for cumulative policy, replay verification and witness backpropagation.
            return node with { CombatProgress = CombatProgressState.Capture(node.Snapshot) };
        }
        catch { node.Snapshot.ReleaseSimulator(); throw; }
    }

    internal SimulationSnapshot ReplayWitnessPrefixForTesting(SearchWitnessPrefix prefix)
        => ApplyWitnessPrefix(CreateOpeningSearchSeed(Replay([])), prefix).Snapshot;
}
