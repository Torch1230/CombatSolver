namespace CombatSolver.Beam;

/// <summary>Whole-route outcome ordering: victory, HP loss, potions, duration. Method: CombatBeamSolver.Terminal.cs and FinalPlanOrdering.cs @ d231e9e5.</summary>
internal static class PlanSelector
{
    public static SearchNode Select(IEnumerable<SearchNode> candidates, int initialHp, StateEvaluator evaluator) =>
        SelectWithPool(candidates, initialHp, evaluator, out _);

    public static SearchNode? TrySelect(
        IEnumerable<SearchNode> candidates,
        int initialHp,
        StateEvaluator evaluator,
        CancellationToken ct = default) =>
        TrySelectCore(candidates, initialHp, evaluator, out _, out _, ct);

    public static SearchNode SelectWithPool(
        IEnumerable<SearchNode> candidates,
        int initialHp,
        StateEvaluator evaluator,
        out SearchNode[] filteredPool)
    {
        SearchNode? selected = TrySelectCore(candidates, initialHp, evaluator, out filteredPool, out string? failureReason, CancellationToken.None);
        return selected ?? throw new InvalidOperationException(failureReason!);
    }

    private static SearchNode? TrySelectCore(
        IEnumerable<SearchNode> candidates,
        int initialHp,
        StateEvaluator evaluator,
        out SearchNode[] filteredPool,
        out string? failureReason,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SearchNode[] allCandidates = candidates.ToArray();
        if (allCandidates.Length == 0)
        {
            filteredPool = [];
            failureReason = "No executable routes are available for selection.";
            return null;
        }
        SearchNode[] pool = allCandidates;
        if (evaluator.RequiresPotionUse)
        {
            pool = pool.Where(node =>
            {
                ct.ThrowIfCancellationRequested();
                return evaluator.SatisfiesPotionPolicy(node);
            }).ToArray();
            if (pool.Length == 0)
            {
                filteredPool = [];
                failureReason = "Combat policy requires potion use, but no executable route satisfied it.";
                return null;
            }
        }
        if (evaluator.UsesSmartPotionPolicy)
        {
            SearchNode? potionFreeBaseline = SelectPotionFreeBaseline(allCandidates, initialHp, evaluator, ct);
            pool = pool.Where(node =>
            {
                ct.ThrowIfCancellationRequested();
                return evaluator.IsSmartPotionEligible(node, potionFreeBaseline);
            }).ToArray();
            if (pool.Length == 0)
            {
                filteredPool = [];
                failureReason = "No executable route meets the Smart potion saving threshold.";
                return null;
            }
        }
        if (evaluator.RequiresAtLeastOnePotion)
        {
            SearchNode? potionFreeBaseline = SelectPotionFreeBaseline(allCandidates, initialHp, evaluator, ct);
            ct.ThrowIfCancellationRequested();
            bool anyRouteWon = allCandidates.Any(node => node.Won);
            pool = pool.Where(node =>
            {
                ct.ThrowIfCancellationRequested();
                return evaluator.IsRequiredPotionEligible(node, potionFreeBaseline, anyRouteWon);
            }).ToArray();
            if (pool.Length == 0)
            {
                filteredPool = [];
                failureReason = "No executable route meets the required potion-use policy.";
                return null;
            }
        }
        filteredPool = pool;
        failureReason = null;
        // SolverDiff and strict replay consume a whole battle route. A complete
        // loss is therefore a reportable outcome when no winning route exists;
        // retain an incomplete boundary only if no complete policy route survived.
        return pool
            .OrderByDescending(node =>
            {
                ct.ThrowIfCancellationRequested();
                return node.Won;
            })
            .ThenByDescending(node => node.Ended)
            .ThenBy(node => node.DeathSaveUseCount)
            .ThenBy(node =>
            {
                ct.ThrowIfCancellationRequested();
                return evaluator.OutstandingTheft(node);
            })
            .ThenBy(node => !node.Won && !node.State.Players[0].Creature.IsAlive)
            .ThenBy(node =>
            {
                ct.ThrowIfCancellationRequested();
                return evaluator.PolicyHpCost(node, initialHp);
            })
            .ThenByDescending(evaluator.SatisfiedRelicTargets)
            .ThenByDescending(node => node.State.Players[0].PotionSlots.Count(potion => potion is not null))
            .ThenBy(node => node.Turn)
            .ThenByDescending(node =>
            {
                ct.ThrowIfCancellationRequested();
                return evaluator.EvaluateBaselineScore(node);
            })
            .ThenBy(node => node.Depth)
            .First();
    }

    private static SearchNode? SelectPotionFreeBaseline(
        SearchNode[] allCandidates,
        int initialHp,
        StateEvaluator evaluator,
        CancellationToken ct)
    {
        SearchNode? best = null;
        int bestCost = 0;
        for (int i = 0; i < allCandidates.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            SearchNode node = allCandidates[i];
            if (node.PotionUses != 0)
                continue;
            int cost = evaluator.PolicyHpCost(node, initialHp);
            if (best is null
                || (node.Won != best.Won ? node.Won :
                    node.Ended != best.Ended ? node.Ended :
                    cost < bestCost))
            {
                best = node;
                bestCost = cost;
            }
        }
        return best;
    }
}
