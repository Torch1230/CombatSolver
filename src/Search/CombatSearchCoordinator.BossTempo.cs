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
            string observeSeq = string.Join(">", prefix.Actions.Select(a =>
                a.Kind == PlanActionKind.UsePotion ? a.PotionId
                : a.Kind == PlanActionKind.PlayCard ? a.CardId : a.Kind.ToString()));
            policy.Diagnostics.Info($"[CombatSolver/Test] BOSS_TEMPO_OBSERVE "
                + $"actions={prefix.Actions.Length} potions={prefix.PotionCount} "
                + $"eliminated={prefix.EliminatedEnemies} rank={prefix.Rank} "
                + $"seq={(observeSeq.Length > 240 ? observeSeq[..240] + "…" : observeSeq)}");
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
        // 复用主搜免药完胜作为 Smart 审计基线；不满足免药完胜条件时返回 null（不伪造基线）。
        // 组件证书门（CanUseComponentSmartPotionEligibility）是剪枝捷径的条件，与审计基线语义无关。
        PotionFreePolicyBaseline? FreeAuditBaseline(SolverResult primary)
            => IsCompleteVictory(primary) && !primary.Snapshot.HasRisk
                && primary.ExplicitPotionCount == 0
                && primary.Snapshot.ProjectedDeathSaveUseCount == 0
                    ? new(true, StrategicHpDeficit(context.Root, policy, primary),
                        primary.Snapshot.PlayerHp, primary.CombatEndedTurn)
                    : null;
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
            // E5：主搜唯一47胜成员=OffensiveRefinement(EnemyHp×1.5)；清空扰动则续搜只到普通beam水平。
            BeamWeightPerturbation = new(BeamWeightTerm.EnemyHp, 1.5d),
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
            memberPolicy with { BossTempoSearch = new(10) { ScoutTurns = 1, PrefixObserver = ObservePrefix } },
            context.CancellationToken, context.ProgressCallback,
            Member(Math.Max(1, allowance.MaxExpandedNodes / 3), Math.Max(1, allowance.SoftTimeBudgetMilliseconds / 3)),
            directSearchPurpose: DirectSearchPurpose.BossTempo,
            potionFreePolicyBaseline: FreeAuditBaseline(selected)), policy, "boss_tempo_scout");
        searches++;
        if (scout is { ResultScope: not SolverResultScope.SearchCompletion }) return scout;
        Accept(scout);
        policy.Diagnostics.Info($"[CombatSolver/Test] BOSS_TEMPO_PREFIXES observed={observed} retained={prefixes.Count}");
        List<BossTempoPrefix> attemptPrefixes = prefixes.GroupBy(p => p.PotionCount)
            .Select(bucket => bucket.OrderByDescending(ForcedCommitmentRank)
                .ThenByDescending(p => p.EliminatedEnemies)
                .ThenByDescending(p => p.Rank).First())
            .OrderByDescending(ForcedCommitmentRank)
            .ThenByDescending(p => p.EliminatedEnemies).ThenBy(p => p.PotionCount)
            .ThenByDescending(p => p.Rank).Take(2).ToList();
        // 侦察观测流不保证覆盖主搜 T1 形态（池保留的同质短前缀与主搜路线可分叉），
        // 故将主搜 T1 段作为确定性候选注入 attempt 序列，续搜以 Accept 的比较门槛裁决。
        PlanAction[] primaryActions = selected.BestNode.Actions.ToArray();
        if (primaryActions.Length > 0)
        {
            int firstTurn = primaryActions[0].Turn;
            PlanAction[] t1Segment = primaryActions.TakeWhile(a => a.Turn <= firstTurn).ToArray();
            // State=null：主搜轨迹合成条目，无观测指纹，不参与去重；
            // EliminatedEnemies=0 仅作诊断占位（T1 末敌况不可得），该条不参与池选择。
            BossTempoPrefix primaryPrefix = new(t1Segment, null, 0,
                t1Segment.Count(a => a.Kind == PlanActionKind.UsePotion), selected.BestNode.Score);
            prefixes.Add(primaryPrefix);
            // 主搜段是已验证优于池同质族的候选；附加预算紧张时排尾候选可能永远轮不到，
            // 故置于 attempt 序列首位。
            attemptPrefixes.Insert(0, primaryPrefix);
        }
        HashSet<BossTempoPrefix> attempted = new(attemptPrefixes);
        foreach (BossTempoPrefix retained in prefixes)
        {
            string retainedSeq = string.Join(">", retained.Actions.Select(a =>
                a.Kind == PlanActionKind.UsePotion ? a.PotionId
                : a.Kind == PlanActionKind.PlayCard ? a.CardId : a.Kind.ToString()));
            policy.Diagnostics.Info($"[CombatSolver/Test] BOSS_TEMPO_POOL "
                + $"potions={retained.PotionCount} rank={retained.Rank} "
                + $"eliminated={retained.EliminatedEnemies} attempt={(attempted.Contains(retained) ? 1 : 0)} "
                + $"seq={(retainedSeq.Length > 240 ? retainedSeq[..240] + "…" : retainedSeq)}");
        }
        foreach (BossTempoPrefix prefix in attemptPrefixes)
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
                directSearchPurpose: DirectSearchPurpose.BossTempo,
                potionFreePolicyBaseline: FreeAuditBaseline(selected)), policy, "boss_tempo_continuation");
            searches++;
            if (candidate is { ResultScope: not SolverResultScope.SearchCompletion }) return candidate;
            bool accepted = Accept(candidate);
            string prefixSeq = string.Join(">", prefix.Actions.Select(a => a.Kind));
            policy.Diagnostics.Info($"[CombatSolver/Test] BOSS_TEMPO_CONTINUATION "
                + $"eliminated={prefix.EliminatedEnemies} prefix_potions={prefix.PotionCount} "
                + $"prefix_actions={prefix.Actions.Length} prefix_seq={(prefixSeq.Length > 240 ? prefixSeq[..240] + "…" : prefixSeq)} "
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
