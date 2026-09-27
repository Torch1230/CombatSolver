namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // Experimental pruning uses witnessed-outcome predictions. Final route policy
    // still compares complete simulated outcomes, never the regressor's output.
    private List<SearchNode> RetainObjectives(IEnumerable<SearchNode> nodes, int limit)
    {
        var learned = policy.ObjectiveValueModel
            ?? throw new InvalidOperationException("Missing outcome value model.");
        var ranked = nodes.Select(n => (Node: n, Priority: learned.PredictPriority(n, _player)))
            .OrderBy(n => n.Node.Snapshot.PlayerDead).ThenByDescending(n => n.Priority)
            .ThenBy(n => n.Node.Snapshot.EnemyHp).Take(limit).Select(n => n.Node).ToList();
        for (int i = 0; i < ranked.Count; i++) ranked[i].RetentionRank = i;
        return ranked;
    }
}
