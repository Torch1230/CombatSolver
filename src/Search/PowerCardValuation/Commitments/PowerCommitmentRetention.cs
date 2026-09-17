namespace CombatSolver;

internal static class PowerCommitmentRetention
{
    internal static IReadOnlyList<SearchNode> RankRepresentatives(
        IReadOnlyList<SearchNode> pool,
        int quota)
    {
        if (quota <= 0)
            return [];
        return pool
            .Where(node => node.PowerCommitment != null
                && !node.Snapshot.PlayerDead
                && !node.IsTerminal)
            .GroupBy(node => (
                node.PowerCommitment!.Family,
                node.PotionCount,
                node.Turn))
            .Select(group => group
                .OrderByDescending(node => node.PowerCommitment!.RealizedEvidence)
                .ThenByDescending(node => node.PowerCommitment!.Priority)
                .ThenByDescending(node => node.PowerCommitment!.ProgressEvidence)
                .ThenByDescending(node => node.PowerCommitment!.NetUnrealizedValue)
                .ThenBy(node => node.PowerCommitment!.OpenedActionCount)
                .ThenBy(node => node.PowerCommitment!.RoundTransitions)
                .ThenByDescending(node => node.Snapshot.ProjectedPlayerHp)
                .ThenByDescending(node => node.Snapshot.OffensiveProgressValue)
                .ThenByDescending(node => node.Score)
                .ThenBy(node => node.ActionCount)
                .First())
            .OrderByDescending(node => node.PowerCommitment!.RealizedEvidence)
            .ThenByDescending(node => node.PowerCommitment!.Priority)
            .ThenByDescending(node => node.PowerCommitment!.ProgressEvidence)
            .ThenByDescending(node => node.PowerCommitment!.NetUnrealizedValue)
            .ThenBy(node => node.PowerCommitment!.OpenedActionCount)
            .ThenBy(node => node.PowerCommitment!.RoundTransitions)
            .ThenByDescending(node => node.Snapshot.ProjectedPlayerHp)
            .ThenByDescending(node => node.Score)
            .ThenBy(node => node.PowerCommitment!.Family)
            .Take(quota)
            .ToArray();
    }
}
