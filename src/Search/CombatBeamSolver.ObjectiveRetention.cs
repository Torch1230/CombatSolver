namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // Intermediate proposal and novelty ordering use the same selected scalar.
    // The classic branch preserves its original score and comparison order.
    private double CandidateRankScore(SearchNode node)
        => policy.UseObjectiveSearch ? ObjectiveRankScore(node) : node.Score;

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
