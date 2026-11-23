using Sts2Sim.Core.Combat;
using CombatSolver;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal readonly record struct BeamPrimarySearchIncumbent(
    int StrategicHpDeficit,
    int CombatEndedTurn,
    int? ExplicitPotionStrategicCost = null);

internal readonly record struct BeamIncumbentBucket(
    int Stolen,
    int Potions,
    GrowthCounts Growth = default,
    ulong RelicMask = 0);

internal sealed class BeamPrimaryIncumbentTable
{
    private readonly Dictionary<BeamIncumbentBucket, BeamPrimarySearchIncumbent> _bounds = [];

    public ForecastPassOutcome? PotionFreeWitness { get; set; }

    public bool TryGet(int stolen, int potions, out BeamPrimarySearchIncumbent incumbent) =>
        TryGet(new BeamIncumbentBucket(stolen, potions), out incumbent);

    public bool TryGet(BeamIncumbentBucket bucket, out BeamPrimarySearchIncumbent incumbent)
    {
        lock (_bounds)
            return _bounds.TryGetValue(bucket, out incumbent);
    }

    public bool TryGetStrictHpWitness(
        int stolen,
        int potions,
        int paidPotionCost,
        out BeamPrimarySearchIncumbent incumbent)
    {
        incumbent = default;
        bool found = false;
        lock (_bounds)
        {
            foreach ((BeamIncumbentBucket key, BeamPrimarySearchIncumbent value) in _bounds)
            {
                if (key.Stolen != stolen || key.Potions != potions || key.RelicMask != 0
                    || value.ExplicitPotionStrategicCost is not { } cost || cost > paidPotionCost)
                {
                    continue;
                }
                if (!found || value.StrategicHpDeficit < incumbent.StrategicHpDeficit)
                {
                    incumbent = value;
                    found = true;
                }
            }
        }
        return found;
    }

    public bool Tighten(int stolen, int potions, BeamPrimarySearchIncumbent candidate) =>
        Tighten(new BeamIncumbentBucket(stolen, potions), candidate);

    public bool Tighten(BeamIncumbentBucket bucket, BeamPrimarySearchIncumbent candidate)
    {
        lock (_bounds)
        {
            if (_bounds.TryGetValue(bucket, out BeamPrimarySearchIncumbent current)
                && (candidate.StrategicHpDeficit > current.StrategicHpDeficit
                    || (candidate.StrategicHpDeficit == current.StrategicHpDeficit
                        && candidate.CombatEndedTurn >= current.CombatEndedTurn)))
            {
                return false;
            }
            _bounds[bucket] = candidate;
            return true;
        }
    }
}

internal static class BeamIncumbentPolicy
{
    public static long DedicatedMemberMilliseconds(TimeSpan softTimeLimit) =>
        Math.Clamp((long)softTimeLimit.TotalMilliseconds / 5L, 1_000L, 30_000L);

    public static bool IsReusablePotionFreeVictory(
        CombatPolicy policy,
        StateEvaluator evaluator,
        ForecastPassOutcome outcome)
    {
        if (policy.PotionPolicy is not (CombatSolver.Beam.Policy.SolverPotionPolicy.Disabled
            or CombatSolver.Beam.Policy.SolverPotionPolicy.Smart))
        {
            return false;
        }
        if (evaluator.HasForcedDirectives)
            return false;
        SearchNode selected = outcome.Selected;
        return selected.Won
            && outcome.PassBoundary == ForecastSearchBoundary.None
            && selected.NodeBoundary == ForecastSearchBoundary.None
            && selected.PotionUses == 0
            && selected.DeathSaveUseCount == 0
            && selected.Turn > 0
            && evaluator.SatisfiesRelicTargets(selected)
            && evaluator.SatisfiesGrowthTargets(selected)
            && evaluator.SatisfiesTheftPolicy(selected);
    }

    public static BeamPrimarySearchIncumbent? BuildRefinementIncumbent(
        CombatPolicy policy,
        StateEvaluator evaluator,
        ForecastPassOutcome? outcome)
    {
        if (outcome is null || !evaluator.CanCertifyRemainingHealing)
            return null;
        if (!IsReusablePotionFreeVictory(policy, evaluator, outcome))
            return null;

        SearchNode node = outcome.Selected;
        return new BeamPrimarySearchIncumbent(
            evaluator.StrategicHpDeficit(node),
            Math.Max(1, node.Turn),
            node.PotionUses == 0 ? 0 : node.OptionalPotionCost);
    }

    public static BeamPrimarySearchIncumbent? BuildGeneralIncumbent(
        CombatPolicy policy,
        StateEvaluator evaluator,
        SearchNode? node,
        ForecastSearchBoundary passBoundary)
    {
        if (node is null || !node.Won || !evaluator.CanCertifyRemainingHealing)
            return null;
        if (passBoundary != ForecastSearchBoundary.None || node.NodeBoundary != ForecastSearchBoundary.None)
            return null;
        if (node.DeathSaveUseCount > 0
            || !evaluator.SatisfiesPotionPolicy(node)
            || !evaluator.SatisfiesTheftPolicy(node)
            || !evaluator.SatisfiesRelicTargets(node)
            || !evaluator.SatisfiesGrowthTargets(node))
        {
            return null;
        }

        return new BeamPrimarySearchIncumbent(
            evaluator.StrategicHpDeficit(node),
            Math.Max(1, node.Turn),
            node.PotionUses == 0 ? 0 : node.OptionalPotionCost);
    }

    public static bool ShouldPruneByIncumbent(
        SearchNode node,
        BeamPrimarySearchIncumbent incumbent,
        StateEvaluator evaluator,
        CombatPolicy policy)
    {
        if (node.Ended || !evaluator.CanCertifyRemainingHealing)
            return false;
        if (evaluator.HasGrowthTargets || evaluator.HasRelicTargets || evaluator.HasForcedDirectives)
            return false;
        if (node.DeathSaveUseCount > 0 || !node.State.Players[0].Creature.IsAlive)
            return false;

        // 对未使用药水的节点，若策略仍允许使用可选药水，则仅在仅剩余无药水机会或已有同费用药水见证时剪枝。
        if (node.PotionUses == 0
            && policy.PotionPolicy is not CombatSolver.Beam.Policy.SolverPotionPolicy.Disabled
            && incumbent.ExplicitPotionStrategicCost is not 0)
        {
            return false;
        }
        if (incumbent.ExplicitPotionStrategicCost is { } cost && node.OptionalPotionCost < cost)
            return false;

        int hpLowerBound = evaluator.StrategicHpLowerBound(node);
        if (hpLowerBound == int.MaxValue)
            return false;

        if (hpLowerBound > incumbent.StrategicHpDeficit)
            return true;
        if (hpLowerBound == incumbent.StrategicHpDeficit
            && incumbent.CombatEndedTurn < int.MaxValue
            && node.Turn > incumbent.CombatEndedTurn)
        {
            return true;
        }
        return false;
    }

    public static List<SearchNode> ApplyIncumbentBound(
        List<SearchNode> frontier,
        BeamPrimarySearchIncumbent? incumbent,
        StateEvaluator evaluator,
        CombatPolicy policy)
    {
        if (incumbent is null || frontier.Count == 0 || !evaluator.CanCertifyRemainingHealing)
            return frontier;

        List<SearchNode>? filtered = null;
        for (int i = 0; i < frontier.Count; i++)
        {
            SearchNode node = frontier[i];
            if (ShouldPruneByIncumbent(node, incumbent.Value, evaluator, policy))
            {
                if (filtered is null)
                {
                    filtered = new List<SearchNode>(frontier.Count);
                    for (int k = 0; k < i; k++)
                        filtered.Add(frontier[k]);
                }
                continue;
            }
            filtered?.Add(node);
        }
        return filtered ?? frontier;
    }

    public static (ForecastPassOutcome Chosen, int WinnerIndex) ChooseBetterPassOutcomeWithOwner(
        ForecastPassOutcome current,
        ForecastPassOutcome candidate,
        int initialHp,
        StateEvaluator baselineEvaluator)
    {
        if (current.Selected.Won && (!candidate.Selected.Ended || !candidate.Selected.Won))
            return (current, 0);
        return PreCombatBeamPortfolio.ChooseWinningPassWithOwner(
            [current, candidate],
            initialHp,
            baselineEvaluator);
    }

    public static ForecastPassOutcome ChooseBetterPassOutcome(
        ForecastPassOutcome current,
        ForecastPassOutcome candidate,
        int initialHp,
        StateEvaluator baselineEvaluator) =>
        ChooseBetterPassOutcomeWithOwner(current, candidate, initialHp, baselineEvaluator).Chosen;

    public static bool IsBetterPassOutcome(
        ForecastPassOutcome candidate,
        ForecastPassOutcome current,
        int initialHp,
        StateEvaluator baselineEvaluator)
    {
        if (current.Selected.Won && (!candidate.Selected.Ended || !candidate.Selected.Won))
            return false;
        (_, int winnerIndex) = PreCombatBeamPortfolio.ChooseWinningPassWithOwner(
            [current, candidate],
            initialHp,
            baselineEvaluator);
        return winnerIndex == 1;
    }

    public static bool HasReachedAcceptableBattleHpLoss(
        CombatPolicy policy,
        StateEvaluator evaluator,
        ForecastPassOutcome outcome,
        int openingPreRootBattleHpLost)
    {
        if (!policy.StopAtAcceptableBattleHpLoss
            || evaluator.HasVisibleHealingSource
            || outcome.PassBoundary != ForecastSearchBoundary.None)
        {
            return false;
        }

        SearchNode selected = outcome.Selected;
        if (selected.NodeBoundary != ForecastSearchBoundary.None)
            return false;

        var creature = selected.State.Players[0].Creature;
        int recoveredPlayerHp = Math.Max(
            0,
            creature.CurrentHp - evaluator.InitialHp + evaluator.BattleHpLost(selected) - selected.DeathSaveHpRestored);
        if (recoveredPlayerHp != 0)
            return false;

        return BeamSearch.MeetsAcceptableVictory(selected, policy, evaluator, openingPreRootBattleHpLost);
    }

    public static bool CanFinishTargetPortfolio(
        CombatPolicy policy,
        StateEvaluator evaluator,
        ForecastPassOutcome outcome,
        int openingPreRootBattleHpLost,
        bool stopPortfolioAtHpTarget) =>
        stopPortfolioAtHpTarget
        && HasReachedAcceptableBattleHpLoss(policy, evaluator, outcome, openingPreRootBattleHpLost);

    public static bool CanStopEarlyTurnExploration(
        CombatPolicy policy,
        StateEvaluator evaluator,
        ForecastPassOutcome outcome,
        int openingPreRootBattleHpLost)
    {
        if (outcome.PassBoundary != ForecastSearchBoundary.None
            || outcome.Selected.NodeBoundary != ForecastSearchBoundary.None)
        {
            return false;
        }
        return evaluator.IsProvenZeroDamageRoute(outcome.Selected)
            || HasReachedAcceptableBattleHpLoss(policy, evaluator, outcome, openingPreRootBattleHpLost);
    }
}
