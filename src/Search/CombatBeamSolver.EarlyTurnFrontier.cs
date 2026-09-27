namespace CombatSolver;

internal sealed record EarlyTurnFrontierCandidate(
    int CompletedTurns,
    PlanAction[] Actions,
    StateFingerprint StateKey,
    double Score);

internal sealed partial class CombatBeamSolver
{
    private const int EarlyTurnCandidatesPerLayer = 24;

    private readonly record struct EarlyTurnOpeningKey(
        PlanActionKind Kind,
        string CardId,
        string PotionId,
        uint? TargetId);

    private readonly record struct EarlyTurnStrategyKey(
        EarlyTurnOpeningKey Opening,
        SearchRouteTraits Traits,
        PersistentSetupTraits Setup,
        int PotionCount,
        int PersistentBuffBand,
        int HandValueBand,
        uint? FocusTargetId);

    private sealed record EarlyTurnEntry(
        SearchNode Node,
        EarlyTurnOpeningKey Opening,
        EarlyTurnStrategyKey Strategy);

    private IReadOnlyList<EarlyTurnFrontierCandidate> SelectEarlyTurnFrontier(
        IReadOnlyList<SearchNode> ended,
        int completedTurns)
    {
        Dictionary<StateFingerprint, SearchNode> states = [];
        foreach (SearchNode node in ended)
        {
            if (node.IsTerminal || node.Snapshot.PlayerDead
                || node.BoundaryReason != SearchBoundaryReason.None
                || node.Turn != _startTurnNumber + completedTurns)
                continue;
            if (!states.TryGetValue(node.StateKey, out SearchNode? prior)
                || node.Score > prior.Score)
                states[node.StateKey] = node;
        }

        EarlyTurnEntry Describe(SearchNode node)
        {
            PlanAction opening = FirstAction(node);
            EarlyTurnOpeningKey openingKey = new(
                opening.Kind, opening.CardId, opening.PotionId, opening.TargetCombatId);
            SimulationSnapshot snapshot = node.Snapshot;
            EarlyTurnStrategyKey strategy = new(
                openingKey,
                node.Traits,
                snapshot.PersistentSetupTraits,
                Math.Min(3, node.PotionCount),
                snapshot.PersistentBuffValue / 8,
                snapshot.ReachableHandValue / 8,
                snapshot.FocusTargetCombatId);
            return new EarlyTurnEntry(node, openingKey, strategy);
        }

        EarlyTurnEntry[][] openings = states.Values
            .Select(Describe)
            .GroupBy(entry => entry.Strategy)
            .SelectMany(group => group.OrderByDescending(entry => entry.Node.Score).Take(2))
            .GroupBy(entry => entry.Opening)
            .Select(group => group.OrderByDescending(entry => entry.Node.Score).ToArray())
            .OrderByDescending(group => group[0].Node.Score)
            .ToArray();
        List<EarlyTurnFrontierCandidate> selected = [];
        for (int rank = 0; selected.Count < EarlyTurnCandidatesPerLayer; rank++)
        {
            bool found = false;
            foreach (EarlyTurnEntry[] opening in openings)
            {
                if (rank >= opening.Length)
                    continue;
                found = true;
                SearchNode node = opening[rank].Node;
                selected.Add(new EarlyTurnFrontierCandidate(
                    completedTurns, node.Actions.ToArray(), node.StateKey, node.Score));
                if (selected.Count == EarlyTurnCandidatesPerLayer)
                    break;
            }
            if (!found)
                break;
        }
        return selected;
    }

    private static PlanAction FirstAction(SearchNode node)
    {
        PlanAction? first = null;
        for (SearchNode? current = node; current != null; current = current.Parent)
            first = current.Action ?? first;
        return first ?? throw new InvalidOperationException("回合边界候选没有动作前缀。");
    }
}
