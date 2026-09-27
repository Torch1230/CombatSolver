namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // Only the scalar ranking changes. State deduplication, choice/cycle ledgers,
    // diversity, optimistic incumbent bounds and final policy keep their owners.
    private double ObjectiveRankScore(SearchNode node)
    {
        var learned = policy.ObjectiveValueModel
            ?? throw new InvalidOperationException("Missing outcome value model.");
        return node.Snapshot.PlayerDead ? double.NegativeInfinity
            : learned.PredictPriority(node, _player);
    }
}
