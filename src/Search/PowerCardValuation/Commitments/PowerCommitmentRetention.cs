namespace CombatSolver;

internal static class PowerCommitmentRetention
{
    internal static IReadOnlyList<SearchNode> RankRepresentatives(
        IReadOnlyList<SearchNode> pool,
        int quota,
        bool preferUnrealizedPotential = false)
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
                node.Turn,
                node.PowerCommitment.Cards), CapabilityComparer.Instance)
            .Select(group => group
                .OrderByDescending(node => preferUnrealizedPotential
                    ? node.PowerCommitment!.NetUnrealizedValue
                    : node.PowerCommitment!.RealizedEvidence)
                .ThenByDescending(node => node.PowerCommitment!.Priority)
                .ThenByDescending(node => node.PowerCommitment!.ProgressEvidence)
                .ThenByDescending(node => preferUnrealizedPotential
                    ? node.PowerCommitment!.RealizedEvidence
                    : node.PowerCommitment!.NetUnrealizedValue)
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

    // Activation order is history, while the registered capability set is a retention
    // feature. Compare sets without sorting or allocating a string for every pool node.
    private sealed class CapabilityComparer : IEqualityComparer<(
        PowerCommitmentFamily Family, int PotionCount, int Turn, IReadOnlyList<string> Cards)>
    {
        internal static readonly CapabilityComparer Instance = new();

        public bool Equals(
            (PowerCommitmentFamily Family, int PotionCount, int Turn, IReadOnlyList<string> Cards) left,
            (PowerCommitmentFamily Family, int PotionCount, int Turn, IReadOnlyList<string> Cards) right)
            => left.Family == right.Family && left.PotionCount == right.PotionCount
                && left.Turn == right.Turn && left.Cards.Count == right.Cards.Count
                && left.Cards.All(card => right.Cards.Contains(card, StringComparer.Ordinal));

        public int GetHashCode((PowerCommitmentFamily Family, int PotionCount, int Turn,
            IReadOnlyList<string> Cards) value)
        {
            int cards = 0;
            foreach (string card in value.Cards)
                cards ^= StringComparer.Ordinal.GetHashCode(card);
            return HashCode.Combine(value.Family, value.PotionCount, value.Turn, value.Cards.Count, cards);
        }
    }
}
