namespace CombatSolver.Beam;

/// <summary>Whole-route outcome ordering: victory, HP loss, potions, duration. Method: CombatBeamSolver.Terminal.cs and FinalPlanOrdering.cs @ d231e9e5.</summary>
internal static class PlanSelector
{
    public static SearchNode Select(IEnumerable<SearchNode> candidates, int initialHp, StateEvaluator evaluator) =>
        SelectWithPool(candidates, initialHp, evaluator, out _);

    public static SearchNode SelectWithPool(
        IEnumerable<SearchNode> candidates,
        int initialHp,
        StateEvaluator evaluator,
        out SearchNode[] filteredPool)
    {
        SearchNode[] allCandidates = candidates.ToArray();
        if (allCandidates.Length == 0)
            throw new InvalidOperationException("No executable routes are available for selection.");
        SearchNode[] pool = allCandidates;
        if (evaluator.RequiresPotionUse)
        {
            pool = pool.Where(evaluator.SatisfiesPotionPolicy).ToArray();
            if (pool.Length == 0)
                throw new InvalidOperationException("Combat policy requires potion use, but no executable route satisfied it.");
        }
        if (evaluator.UsesSmartPotionPolicy)
        {
            SearchNode? potionFreeBaseline = allCandidates.Where(node => node.PotionUses == 0)
                .OrderByDescending(node => node.Won)
                .ThenByDescending(node => node.Ended)
                .ThenBy(node => evaluator.PolicyHpCost(node, initialHp))
                .FirstOrDefault();
            pool = pool.Where(node => evaluator.IsSmartPotionEligible(node, potionFreeBaseline))
                .ToArray();
            if (pool.Length == 0)
                throw new InvalidOperationException(
                    "No executable route meets the Smart potion saving threshold.");
        }
        if (evaluator.RequiresAtLeastOnePotion)
        {
            SearchNode? potionFreeBaseline = allCandidates.Where(node => node.PotionUses == 0)
                .OrderByDescending(node => node.Won)
                .ThenByDescending(node => node.Ended)
                .ThenBy(node => evaluator.PolicyHpCost(node, initialHp))
                .FirstOrDefault();
            bool anyRouteWon = allCandidates.Any(node => node.Won);
            pool = pool.Where(node => evaluator.IsRequiredPotionEligible(
                node, potionFreeBaseline, anyRouteWon)).ToArray();
            if (pool.Length == 0)
                throw new InvalidOperationException(
                    "No executable route meets the required potion-use policy.");
        }
        filteredPool = pool;
        // SolverDiff and strict replay consume a whole battle route. A complete
        // loss is therefore a reportable outcome when no winning route exists;
        // retain an incomplete boundary only if no complete policy route survived.
        return pool
            .OrderByDescending(node => node.Won)
            .ThenByDescending(node => node.Ended)
            .ThenBy(node => node.DeathSaveUseCount)
            .ThenBy(evaluator.OutstandingTheft)
            .ThenBy(node => !node.Won && !node.State.Players[0].Creature.IsAlive)
            .ThenBy(node => evaluator.PolicyHpCost(node, initialHp))
            .ThenByDescending(evaluator.SatisfiedRelicTargets)
            .ThenByDescending(node => node.State.Players[0].PotionSlots.Count(potion => potion is not null))
            .ThenBy(node => node.Turn)
            .ThenByDescending(node => evaluator.EvaluateBaselineScore(node))
            .ThenBy(node => node.Depth)
            .First();
    }
}
