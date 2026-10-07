using System.Diagnostics;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static SolverResult RunBossTempoSearch(SearchPassContext context, SolverResult selected)
    {
        SearchPolicySnapshot policy = context.Policy;
        if (!policy.UseBossTempoSearch || context.Root.EncounterRoomType != RoomType.Boss
            || policy.IncludeTurnSetup || selected.ResultScope != SolverResultScope.SearchCompletion
            || policy.Interaction?.CurrentTakeoverRequest != null
            || CanFinishTargetPortfolio(context.Root, policy, context.Profile, selected))
            return selected;

        SolverSearchProfile allowance = BossTempoSearchOptions.AdditionalBudget(context.Profile,
            policy.BudgetOverrideMilliseconds ?? context.Profile.SoftTimeBudgetMilliseconds);
        Stopwatch clock = Stopwatch.StartNew();
        SearchRequestWorkSnapshot before = context.Budget.WorkTotals.Snapshot();
        int searches = 0;
        bool improved = false;
        string stop = "time_limit";
        List<BossTempoPrefix> prefixes = [];
        int observed = 0;
        int ForcedCommitmentRank(BossTempoPrefix prefix)
            => policy.PotionStrategy.HasForcedDirectives
                && policy.PotionStrategy.EvaluateForcedUses(prefix.Actions,
                    context.Root.HasRenewablePotionShapedRock).AllForcedUsesSatisfied ? 1 : 0;
        void ObservePrefix(BossTempoPrefix prefix)
        {
            observed++;
            int prior = prefixes.FindIndex(p => p.State == prefix.State && p.PotionCount == prefix.PotionCount);
            if (prior >= 0)
            {
                if (prefix.Rank <= prefixes[prior].Rank) return;
                prefixes.RemoveAt(prior);
            }
            prefixes.Add(prefix);
            prefixes = prefixes.GroupBy(p => Math.Min(3, p.PotionCount))
                .SelectMany(bucket => bucket.OrderByDescending(ForcedCommitmentRank)
                    .ThenByDescending(p => p.EliminatedEnemies)
                    .ThenByDescending(p => p.Rank).Take(6)).ToList();
        }
        long RemainingNodes() => allowance.MaxExpandedNodes
            - (context.Budget.WorkTotals.Snapshot().ExpandedNodes - before.ExpandedNodes);
        int RemainingTime() => allowance.SoftTimeBudgetMilliseconds - (int)clock.ElapsedMilliseconds;
        bool Accept(SolverResult? candidate)
        {
            if (candidate == null || candidate.Snapshot.HasRisk || !IsCompleteVictory(candidate)
                || !IsBetterPotionPolicyResult(policy.TheftPolicy,
                    CapturePortfolioQuality(context.Root, policy, candidate) with { Score = 0 },
                    CapturePortfolioQuality(context.Root, policy, selected) with { Score = 0 }))
                return false;
            selected = candidate;
            improved = true;
            context.InterimResultCallback?.Invoke(selected);
            return true;
        }
        SolverSearchProfile Member(int nodes, int milliseconds) => allowance with
        {
            MaxExpandedNodes = nodes,
            SoftTimeBudgetMilliseconds = milliseconds,
            BaseScoreOnly = false,
            SecondRankBand = false,
            ContextualRanking = null,
            BeamWeightPerturbation = null,
            ContinuousThreatRanking = false,
            BaseScoreTacticalTies = false,
            BossTempoHpPricing = policy.BossTempoNormalizeHpPricing,
        };
        SearchPolicySnapshot memberPolicy = policy with
        {
            NoveltySearch = null,
            PreserveBossTempoHpTies = true,
            MemoryNoProgressRecoveryLimit = policy.MemoryNoProgressRecoveryLimit > 0
                ? policy.MemoryNoProgressRecoveryLimit : 2,
        };
        policy.Diagnostics.Info($"[CombatSolver/Test] BOSS_TEMPO_START "
            + $"nodes={allowance.MaxExpandedNodes} time_ms={allowance.SoftTimeBudgetMilliseconds}");
        SolverResult? scout = SolveOptionalPotionPosterior(new CombatBeamSolver(
            context.Root, context.DisplayNames, context.BattleDamage,
            memberPolicy with { BossTempoSearch = new(3) { ScoutTurns = 1, PrefixObserver = ObservePrefix } },
            context.CancellationToken, context.ProgressCallback,
            Member(Math.Max(1, allowance.MaxExpandedNodes / 3), Math.Max(1, allowance.SoftTimeBudgetMilliseconds / 3)),
            directSearchPurpose: DirectSearchPurpose.BossTempo), policy, "boss_tempo_scout");
        searches++;
        if (scout is { ResultScope: not SolverResultScope.SearchCompletion }) return scout;
        Accept(scout);
        policy.Diagnostics.Info($"[CombatSolver/Test] BOSS_TEMPO_PREFIXES observed={observed} retained={prefixes.Count}");
        foreach (BossTempoPrefix prefix in prefixes.GroupBy(p => p.PotionCount)
            .Select(bucket => bucket.OrderByDescending(ForcedCommitmentRank)
                .ThenByDescending(p => p.EliminatedEnemies)
                .ThenByDescending(p => p.Rank).First())
            .OrderByDescending(ForcedCommitmentRank)
            .ThenByDescending(p => p.EliminatedEnemies).ThenBy(p => p.PotionCount)
            .ThenByDescending(p => p.Rank).Take(2))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (CanFinishTargetPortfolio(context.Root, policy, context.Profile, selected))
            { stop = "hp_target"; break; }
            if (RemainingNodes() <= 0) { stop = "node_limit"; break; }
            if (RemainingTime() <= 0) break;
            if (policy.Interaction?.CurrentTakeoverRequest != null) { stop = "adoption"; break; }
            PrimarySearchIncumbent? bound = IsReusablePotionFreeVictory(policy, null, selected)
                ? BuildPrimarySearchIncumbent(context.Root, policy, selected) : null;
            int availableNodes = (int)RemainingNodes();
            int availableTime = RemainingTime();
            if (availableTime <= 0) break;
            SolverResult? candidate = SolveOptionalPotionPosterior(new CombatBeamSolver(
                context.Root, context.DisplayNames, context.BattleDamage,
                memberPolicy with { BossTempoSearch = null },
                context.CancellationToken, context.ProgressCallback,
                Member(availableNodes, availableTime),
                fixedPrefixActions: prefix.Actions, primaryIncumbent: bound,
                directSearchPurpose: DirectSearchPurpose.BossTempo), policy, "boss_tempo_continuation");
            searches++;
            if (candidate is { ResultScope: not SolverResultScope.SearchCompletion }) return candidate;
            bool accepted = Accept(candidate);
            policy.Diagnostics.Info($"[CombatSolver/Test] BOSS_TEMPO_CONTINUATION "
                + $"eliminated={prefix.EliminatedEnemies} prefix_potions={prefix.PotionCount} "
                + $"won={candidate != null && IsCompleteVictory(candidate)} "
                + $"hp_lost={candidate?.ProjectedBattleHpLost} potions={candidate?.ProjectedBattlePotionCount} improved={accepted} "
                + $"searched_turns={candidate?.SearchedTurns} boundary={candidate?.BoundaryReason} expanded={candidate?.ExpandedNodes}");
            if (candidate?.BoundaryReason == SearchBoundaryReason.MemoryNoProgress)
            { stop = "memory_no_progress"; break; }
        }
        if (stop == "time_limit")
        {
            if (RemainingNodes() <= 0) stop = "node_limit";
            else if (RemainingTime() > 0) stop = "prefix_limit";
        }
        if (prefixes.Count == 0) stop = "admitted_frontier_exhausted";
        if (CanFinishTargetPortfolio(context.Root, policy, context.Profile, selected)) stop = "hp_target";
        SearchRequestWorkSnapshot after = context.Budget.WorkTotals.Snapshot();
        selected.BossTempoSearch = new(stop, searches,
            after.ExpandedNodes - before.ExpandedNodes, after.TransitionCount - before.TransitionCount,
            after.ChoiceBranchesEvaluated - before.ChoiceBranchesEvaluated, clock.ElapsedMilliseconds, improved);
        policy.Diagnostics.Info("[CombatSolver/Test] BOSS_TEMPO_END "
            + System.Text.Json.JsonSerializer.Serialize(selected.BossTempoSearch));
        return selected;
    }
}
