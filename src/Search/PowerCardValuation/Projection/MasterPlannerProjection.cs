namespace CombatSolver;

internal readonly record struct MasterPlannerSkillFact(
    int Value,
    int EnergyCost,
    int TurnsUntilSeed,
    int TurnsUntilPayoff);

internal readonly record struct MasterPlannerProjectionResult(
    int SeededSkillCount,
    int EarliestPayoffTurns,
    int CardAccessValue)
{
    public bool HasPayoff => SeededSkillCount > 0 && CardAccessValue > 0;
}

internal static class MasterPlannerProjection
{
    internal static MasterPlannerProjectionResult Evaluate(
        int currentEnergy,
        int futureEnergyPerTurn,
        int remainingTurns,
        int discardWindows,
        ReadOnlySpan<MasterPlannerSkillFact> currentHandSkills)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentEnergy);
        ArgumentOutOfRangeException.ThrowIfNegative(futureEnergyPerTurn);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingTurns);
        ArgumentOutOfRangeException.ThrowIfNegative(discardWindows);
        if (remainingTurns <= 1
            || discardWindows == 0
            || currentHandSkills.IsEmpty)
        {
            return default;
        }

        int maximumSeeds = Math.Min(discardWindows, currentHandSkills.Length);
        int futureEnergyBudget = futureEnergyPerTurn
            * Math.Min(2, Math.Max(0, remainingTurns - 1));
        int[,,] best = new int[
            maximumSeeds + 1,
            currentEnergy + 1,
            futureEnergyBudget + 1];
        for (int count = 0; count <= maximumSeeds; count++)
            for (int energy = 0; energy <= currentEnergy; energy++)
                for (int futureEnergy = 0; futureEnergy <= futureEnergyBudget; futureEnergy++)
                    best[count, energy, futureEnergy] = -1;
        best[0, 0, 0] = 0;

        foreach (MasterPlannerSkillFact skill in currentHandSkills)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(skill.Value);
            ArgumentOutOfRangeException.ThrowIfNegative(skill.EnergyCost);
            ArgumentOutOfRangeException.ThrowIfNegative(skill.TurnsUntilSeed);
            ArgumentOutOfRangeException.ThrowIfNegative(skill.TurnsUntilPayoff);
            if (skill.Value == 0
                || skill.TurnsUntilSeed > 2
                || skill.TurnsUntilPayoff >= remainingTurns)
            {
                continue;
            }
            bool currentTurnSeed = skill.TurnsUntilSeed == 0;
            int currentCost = currentTurnSeed ? skill.EnergyCost : 0;
            int futureCost = currentTurnSeed ? 0 : skill.EnergyCost;
            if (currentCost > currentEnergy || futureCost > futureEnergyBudget)
                continue;
            int discountedValue = Math.Max(
                1,
                skill.Value / Math.Max(1, skill.TurnsUntilPayoff));
            for (int count = maximumSeeds; count >= 1; count--)
            {
                for (int energy = currentEnergy; energy >= currentCost; energy--)
                {
                    for (int futureEnergy = futureEnergyBudget;
                         futureEnergy >= futureCost;
                         futureEnergy--)
                    {
                        int previous = best[
                            count - 1,
                            energy - currentCost,
                            futureEnergy - futureCost];
                        if (previous < 0)
                            continue;
                        best[count, energy, futureEnergy] = Math.Max(
                            best[count, energy, futureEnergy],
                            SaturatingAdd(previous, discountedValue));
                    }
                }
            }
        }

        int bestCount = 0;
        int bestValue = 0;
        int earliestPayoffTurns = int.MaxValue;
        for (int count = 1; count <= maximumSeeds; count++)
        {
            for (int energy = 0; energy <= currentEnergy; energy++)
            {
                for (int futureEnergy = 0; futureEnergy <= futureEnergyBudget; futureEnergy++)
                {
                    if (best[count, energy, futureEnergy] <= bestValue)
                        continue;
                    bestValue = best[count, energy, futureEnergy];
                    bestCount = count;
                }
            }
        }
        if (bestCount == 0)
            return default;
        foreach (MasterPlannerSkillFact skill in currentHandSkills)
        {
            if (skill.Value > 0 && skill.TurnsUntilPayoff < remainingTurns)
                earliestPayoffTurns = Math.Min(earliestPayoffTurns, skill.TurnsUntilPayoff);
        }
        return new(bestCount, earliestPayoffTurns, bestValue);
    }

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);
}
