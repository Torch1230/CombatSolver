using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    // A member whose search surface the time-bound primary cannot cover keeps this floor even after the
    // primary has spent the shared request budget; otherwise it would never run on exactly those roots.
    internal static long DedicatedMemberMilliseconds(SolverSearchProfile profile)
        => Math.Clamp(profile.SoftTimeBudgetMilliseconds / 5L, 1_000L, 30_000L);

    internal static PrimarySearchIncumbent? BuildRefinementPrimarySearchIncumbent(
        CombatRootSnapshot root, SearchPolicySnapshot policy,
        SolverPotionPolicy? memberPotionPolicyOverride, SolverResult incumbent)
        => !policy.DisableRefinementIncumbentForTesting
            && (root.CanCertifyRemainingHealing || root.UsesKnownNativeHealingPolicy)
            && IsReusablePotionFreeVictory(policy, memberPotionPolicyOverride, incumbent)
                ? BuildPrimarySearchIncumbent(root, policy, incumbent)
                : null;

    private static bool IsReusablePotionFreeVictory(
        SearchPolicySnapshot policy, SolverPotionPolicy? memberPotionPolicyOverride, SolverResult result)
        => (memberPotionPolicyOverride ?? policy.PotionPolicy)
                is SolverPotionPolicy.Disabled or SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && result.ResultScope == SolverResultScope.SearchCompletion
            && result.BoundaryReason == SearchBoundaryReason.None
            && result.ExplicitPotionCount == 0
            && !result.Snapshot.HasRisk
            && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && result.CombatEndedTurn.HasValue
            && IsCompleteVictory(result);

    /// <summary>
    /// 主搜索的宽度组合接线，开关开关两种情况都走这里，所以逐成员诊断和
    /// <see cref="BeamWidthPortfolioTelemetry" /> 在关闭时同样存在（单成员一行）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 没有提前计划胜利时，首个成员沿用原 profile。提前计划已消费的节点计入请求账本，
    /// 首个成员获得剩余节点与时间，并使用该完整胜利的既有 incumbent 界；宽度和排名不变。
    /// 其余成员仍使用组合器的宽度、排名策略及共享余量。
    /// </para>
    /// <para>
    /// 界面的中途路线走 <c>SolverProgress</c> 回调：搜索发布进度，运行时把进度里的
    /// <c>SpeculativeRoutePreview</c> / <c>CurrentTurnPreview</c> 渲染出来。因此基线成员一完成就用
    /// <paramref name="publishBaseline" />（协调器已有的 interim 回调）把完整结果推出去，
    /// 玩家看到第一条路线的时刻不受后面的精炼影响。
    /// </para>
    /// <para>
    /// 精炼成员的准入全部交给 <see cref="BeamWidthPortfolioGate" />：基线必须已经把这一宽度搜干净、
    /// 自己没吃掉超过四分之一的时间预算，剩余节点和剩余时间满足原有门槛，才会启动。
    /// 内存由每个成员执行期间的逐批预约和 Runtime 回收检查点处理。成员顺序执行。
    /// </para>
    /// </remarks>
    private static SolverResult RunBeamWidthPortfolioPass(
        SearchPassContext context,
        Func<SolverSearchProfile, bool, PrimarySearchIncumbent?, SolverResult> solveMember,
        Action<SolverResult>? publishBaseline,
        SolverPotionPolicy? memberPotionPolicyOverride,
        SolverResult? initialPlanIncumbent,
        Func<SolverSearchProfile, bool, SolverSearchProfile>? prepareMemberProfile = null)
    {
        CombatRootSnapshot root = context.Root;
        SearchPolicySnapshot policy = context.Policy;
        SolverSearchProfile profile = context.Profile;
        Stopwatch passClock = context.Clock;
        CancellationToken cancellationToken = context.CancellationToken;
        SearchRequestWorkTotals totals = context.Budget.WorkTotals;
        BeamWidthPortfolioTelemetry telemetry = policy.PortfolioTelemetry
            ?? throw new InvalidOperationException("Beam 宽度组合需要请求级诊断记录。");
        List<BeamWidthPortfolioMemberCost> costs = [];
        BeamWidthPortfolioBaseline baseline = default;
        bool baselineObserved = false;
        long expandedByMembers = 0;
        BeamPortfolioExperiment? experiment = policy.PortfolioExperiment;
        SolverResult? baselineResult = null;
        SolverResult? incumbent = initialPlanIncumbent;
        double[]? pendingFeatures = null;
        string pendingDecision = "Observe";
        string solverAssemblyId = typeof(CombatSearchCoordinator).Module.ModuleVersionId.ToString();
        string gameAssemblyId = typeof(CombatState).Module.ModuleVersionId.ToString();
        bool hasReachablePower = root.PlayerCardIds.Any(
            PowerCardValuationModels.Registry.ContainsCardId);
        // Respect explicit portfolio layouts/experiments and the separately enabled novelty pass.
        // This is exactly the measured no-plain-baseline + bounded-refinement combination.
        bool useReallocation = profile.ReallocatedRefinementPortfolio
            && policy.UseBeamWidthPortfolio && !policy.UseNoveltyPortfolio
            && !profile.AdaptiveNoveltyRefinement
            && profile.ContextualRanking == null && profile.BeamWeightPerturbation == null
            && !profile.ContinuousThreatRanking && !profile.BaseScoreTacticalTies
            && !profile.BaseScoreOnly && !profile.SecondRankBand
            && policy.BeamWidthPortfolioWidths is not { Count: > 0 }
            && policy.BeamWidthPortfolioPlainBaselineMember
            && !profile.OffensiveRefinementPortfolio && !profile.BoundedOffensiveRefinementPortfolio;
        if (useReallocation && policy.MeasurePhasePerformance)
            policy.Diagnostics.Info("[CombatSolver/Test] PORTFOLIO_REALLOCATION plain_baseline=False bounded_refinement=True");

        long RemainingMilliseconds()
            => context.RemainingMilliseconds;

        BeamWidthPortfolioRun<SolverResult> RunMember(SolverSearchProfile memberProfile)
        {
            // 精炼成员沿用现有的软时间预算取消：把它收紧到本轮预算的剩余部分，成员自己就会在
            // 预算耗尽时停下，不必另造一套超时。基线成员原样不动。
            long remainingMilliseconds = RemainingMilliseconds();
            long dedicatedPowerMilliseconds = DedicatedMemberMilliseconds(profile);
            SolverSearchProfile effectiveProfile = baselineObserved
                ? memberProfile with
                {
                    SoftTimeBudgetMilliseconds = (int)Math.Clamp(
                        memberProfile.AggressivePowerCommitment
                            ? Math.Max(remainingMilliseconds, dedicatedPowerMilliseconds)
                            : remainingMilliseconds,
                        1,
                        memberProfile.SoftTimeBudgetMilliseconds),
                }
                : memberProfile;
            effectiveProfile = prepareMemberProfile?.Invoke(effectiveProfile, baselineObserved)
                ?? effectiveProfile;
            if (memberProfile.AggressivePowerCommitment
                && policy.MemoryPressureSignal.IsEnabled
                && !policy.MemoryPressureSignal.CanReachCommit(256L * 1024 * 1024))
            {
                policy.MemoryPressureSignal.ReclaimAndContinue(
                    cancellationToken,
                    "power_commitment_portfolio_member");
            }
            SearchRequestWorkSnapshot before = totals.Snapshot();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            long startedMilliseconds = passClock.ElapsedMilliseconds;
            if (policy.MeasurePhasePerformance)
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] BEAM_WIDTH_PORTFOLIO_MEMBER_START run_index={costs.Count} " +
                    $"beam={effectiveProfile.BeamWidth} second_rank_band={effectiveProfile.SecondRankBand} " +
                    $"base_score_only={effectiveProfile.BaseScoreOnly} " +
                    $"power_commitment={effectiveProfile.AggressivePowerCommitment} " +
                    $"nodes={effectiveProfile.MaxExpandedNodes} " +
                    $"time_ms={effectiveProfile.SoftTimeBudgetMilliseconds}");
            }
            PrimarySearchIncumbent? primaryIncumbent = incumbent == null ? null
                : BuildRefinementPrimarySearchIncumbent(root, policy, memberPotionPolicyOverride, incumbent);
            if (primaryIncumbent is { } bound)
                policy.Diagnostics.Info($"[CombatSolver/Test] BEAM_REFINEMENT_INCUMBENT "
                    + $"member={costs.Count} deficit={bound.StrategicHpDeficit} turn={bound.CombatEndedTurn}");
            SolverResult memberResult = solveMember(effectiveProfile, baselineObserved, primaryIncumbent);
            long memberElapsed = Math.Max(0, passClock.ElapsedMilliseconds - startedMilliseconds);
            long memberAllocated = Math.Max(
                0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore);
            long managedHeapAfter = GC.GetTotalMemory(forceFullCollection: false);
            SearchRequestWorkSnapshot after = totals.Snapshot();
            long expanded = after.ExpandedNodes - before.ExpandedNodes;
            expandedByMembers += expanded;
            costs.Add(new BeamWidthPortfolioMemberCost(memberElapsed, memberAllocated, managedHeapAfter));
            bool won = IsCompleteVictory(memberResult);
            bool terminal = won || memberResult.Snapshot.PlayerDead;
            // 与组合器同一条可比性规则。撞节点上限又没到终局的结果不参与质量比较，
            // 否则它既会污染后续成员的 improved 标签，也会让 incumbent 不再代表当前最佳；
            // 被内存回收提前截断的结果同理。
            bool comparable = terminal
                || memberResult.BoundaryReason is not (SearchBoundaryReason.NodeLimit
                    or SearchBoundaryReason.MemoryNoProgress);
            if (experiment != null && baselineObserved && pendingFeatures != null)
            {
                bool improved = comparable && incumbent != null
                    && IsBetterPotionPolicyResult(root, policy, memberResult, incumbent);
                bool usable = won && memberResult.BoundaryReason == SearchBoundaryReason.None
                    && !memberResult.Snapshot.HasRisk && pendingDecision != "UnsupportedSemantics";
                experiment.Observe?.Invoke(new BeamPortfolioObservation(
                    pendingFeatures, pendingDecision, true, improved, usable,
                    memberElapsed, memberResult.BoundaryReason.ToString()));
                pendingFeatures = null;
            }
            if (comparable && (incumbent == null
                || IsBetterPotionPolicyResult(root, policy, memberResult, incumbent)))
                incumbent = memberResult;
            if (!baselineObserved)
            {
                if (experiment != null)
                    baselineResult = memberResult;
                baseline = new BeamWidthPortfolioBaseline(
                    memberResult.BoundaryReason == SearchBoundaryReason.None,
                    IsProvenZeroDamageRoute(root, policy, memberResult),
                    memberElapsed,
                    expanded,
                    effectiveProfile.BeamWidth);
                baselineObserved = true;
                telemetry.RecordFirstRoutePublished(passClock.Elapsed.TotalMilliseconds);
                publishBaseline?.Invoke(memberResult);
            }
            return new BeamWidthPortfolioRun<SolverResult>(
                memberResult,
                expanded,
                after.TransitionCount - before.TransitionCount,
                memberResult.BoundaryReason.ToString(),
                terminal,
                won,
                terminal ? memberResult.ProjectedBattleHpLost : null,
                memberResult.PotionCount)
            {
                StopPortfolio = memberResult.ResultScope != SolverResultScope.SearchCompletion,
                MemoryTruncated = memberResult.BoundaryReason == SearchBoundaryReason.MemoryNoProgress,
            };
        }

        string? RejectMember(BeamWidthPortfolioMemberSpec member)
        {
            if (!baselineObserved)
                return null;
            if (incumbent != null && CanFinishTargetPortfolio(root, policy, profile, incumbent))
                return "AcceptableBattleHpLoss";
            // 能力牌成员走自己的门控（它只要求确实存在可达的能力牌），其余成员走宽度余量门控。
            string? rejection = member.AggressivePowerCommitment
                ? PowerCommitmentPortfolioGate.Reject(hasReachablePower)
                : BeamWidthPortfolioGate.RejectRefinement(
                    baseline, member.BeamWidth, profile.MaxExpandedNodes - expandedByMembers,
                    RemainingMilliseconds(), profile.SoftTimeBudgetMilliseconds);
            // 学习型跳过器未见过能力承诺或进攻精炼成员，不由它裁决这些新策略。
            if (rejection != null || experiment == null || member.AggressivePowerCommitment
                || member.OffensiveRefinement)
                return rejection;
            // 门控已经放行，说明基线没被任何上限截断，因此两边都已经有可比结果。
            SolverResult first = baselineResult
                ?? throw new InvalidOperationException("成员准入已放行，但基线结果未记录。");
            SolverResult best = incumbent
                ?? throw new InvalidOperationException("成员准入已放行，但当前最佳结果未记录。");
            pendingFeatures =
            [
                root.InitialPlayerHp, root.InitialPlayerMaxHp, root.CapturedCardCount,
                root.CapturedPowerCount, root.Enemies.Count, root.StartTurnNumber,
                root.SearchablePotionCount, root.IsActEndingBoss ? 1 : 0,
                profile.BeamWidth, profile.MaxExpandedNodes, profile.MaxCardBranchesPerNode,
                profile.MaxPileChoiceBranchesPerAction, profile.MaxHandChoiceBranchesPerAction,
                IsCompleteVictory(first) ? 1 : 0, first.ProjectedBattleHpLost,
                first.BestNode.ActionCount, first.CombatEndedTurn ?? 0,
                baseline.ExpandedNodes, first.TransitionCount, baseline.ElapsedMilliseconds,
                best.ProjectedBattleHpLost, best.PotionCount,
                (double)(profile.MaxExpandedNodes - expandedByMembers) / profile.MaxExpandedNodes,
                Math.Max(0d, (double)RemainingMilliseconds() / profile.SoftTimeBudgetMilliseconds),
                (double)member.BeamWidth / profile.BeamWidth,
                member.SecondRankBand ? 1 : 0, member.BaseScoreOnly ? 1 : 0,
                policy.MaxDegreeOfParallelism,
                policy.EffectiveHasGrowthTargets ? 1 : 0, policy.RelicTargets.Count,
                policy.TheftPolicy == null ? 0 : 1,
                policy.PotionStrategy.HasForcedDirectives ? 1 : 0,
            ];
            // The label is the production comparator, so policy shape does not disqualify an
            // observation; it becomes input. Only captures that change simulation fidelity do.
            bool eligible = IsCompleteVictory(first) && !first.Snapshot.HasRisk
                && !policy.UseNoveltyPortfolio && policy.BeamWidthPortfolioWidths == null
                && policy.BeamWidthPortfolioPlainBaselineMember && !useReallocation
                && root.CapturedRunModSubscriberCount == 0 && root.CapturedCombatModSubscriberCount == 0
                && !root.CapturedBaseLibCardModifiers;
            pendingDecision = !eligible ? "UnsupportedSemantics"
                : experiment.Model?.Decide(pendingFeatures, solverAssemblyId, gameAssemblyId) ?? "Observe";
            if (experiment.Model != null)
                policy.Diagnostics.Info($"[CombatSolver/Test] PORTFOLIO_SELECTOR_DECISION model={experiment.Model.ModelId} member={member} decision={pendingDecision}");
            if (pendingDecision != "LearnedNoImprovement")
                return null;
            experiment.Observe?.Invoke(new BeamPortfolioObservation(
                pendingFeatures, pendingDecision, false, null, null, 0, null));
            pendingFeatures = null;
            return pendingDecision;
        }

        IReadOnlyList<BeamWidthPortfolioMemberSpec> builtInMembers = BeamWidthPortfolio.ProductionMembers(
            profile.BeamWidth,
            policy.UseBeamWidthPortfolio ? policy.BeamWidthPortfolioWidths : [profile.BeamWidth],
            policy.BeamWidthPortfolioPlainBaselineMember && !useReallocation,
            includePowerCommitmentMember: hasReachablePower,
            useOffensiveRefinement: profile.OffensiveRefinementPortfolio,
            appendBoundedOffensiveRefinement: profile.BoundedOffensiveRefinementPortfolio || useReallocation);
        BeamWidthPortfolioOutcome<SolverResult> outcome = policy.UseBeamWidthPortfolio || hasReachablePower
            ? BeamWidthPortfolio.Run(
                policy.DevelopmentStrategy?.OrganizeMembers(builtInMembers) ?? builtInMembers,
                profile.MaxExpandedNodes,
                profile,
                RunMember,
                (candidate, current) => IsBetterPotionPolicyResult(root, policy, candidate, current),
                RejectMember,
                policy.Diagnostics.Info)
            : SingleMemberOutcome(profile, RunMember);
        RecordPortfolioMembers(policy, telemetry, outcome, costs);
        // 组合路径也要出阶段表：novelty 分支那条打印覆盖不到这里，于是开了 MeasurePhasePerformance
        // 的组合跑批拿不到逐阶段耗时/分配归属。
        if (policy.MeasurePhasePerformance)
            policy.Diagnostics.Info(SolverDiagnostics.DescribeSearchPhasePerformance(outcome.Selected));
        return initialPlanIncumbent != null
            && IsBetterPotionPolicyResult(root, policy, initialPlanIncumbent, outcome.Selected)
                ? initialPlanIncumbent : outcome.Selected;
    }

    /// <summary>
    /// 组合关闭时的一条成员明细。求解走请求自己的 Profile 实例，可比性与选中理由按组合器同一条
    /// 规矩判定，A/B 才能并排读同一张表。
    /// </summary>
    private static BeamWidthPortfolioOutcome<SolverResult> SingleMemberOutcome(
        SolverSearchProfile profile,
        Func<SolverSearchProfile, BeamWidthPortfolioRun<SolverResult>> runMember)
    {
        BeamWidthPortfolioRun<SolverResult> run = runMember(profile);
        bool comparable = run.Terminal
            || (!run.MemoryTruncated
                && !string.Equals(
                    run.Termination, BeamWidthPortfolio.NodeLimitTermination, StringComparison.Ordinal));
        string selectionReason = run.StopPortfolio
            ? BeamWidthPortfolio.SelectionStopped
            : comparable
                ? BeamWidthPortfolio.SelectionBest
                : BeamWidthPortfolio.SelectionBaselineFallback;
        BeamWidthPortfolioMember member = new(
            profile.BeamWidth,
            profile.SecondRankBand,
            profile.BaseScoreOnly,
            profile.AggressivePowerCommitment,
            profile.MaxExpandedNodes,
            Ran: true,
            run.ExpandedNodes,
            run.TransitionCount,
            run.Termination,
            run.Terminal,
            run.Won,
            run.BattleHpLost,
            run.PotionCount,
            Compared: run.StopPortfolio || comparable,
            SkippedReason: run.StopPortfolio || comparable
                ? null
                : run.MemoryTruncated
                    ? BeamWidthPortfolio.SkippedMemoryTruncated
                    : BeamWidthPortfolio.SkippedNodeLimitNotTerminal);
        return new BeamWidthPortfolioOutcome<SolverResult>(
            run.Result, 0, selectionReason, [member], run.ExpandedNodes, run.TransitionCount);
    }

    /// <summary>逐成员一行诊断，同时把明细与托管堆峰值写进请求级记录。</summary>
    private static void RecordPortfolioMembers(
        SearchPolicySnapshot policy,
        BeamWidthPortfolioTelemetry telemetry,
        BeamWidthPortfolioOutcome<SolverResult> outcome,
        IReadOnlyList<BeamWidthPortfolioMemberCost> costs)
    {
        int costIndex = 0;
        for (int index = 0; index < outcome.Members.Count; index++)
        {
            BeamWidthPortfolioMember member = outcome.Members[index];
            BeamWidthPortfolioMemberCost cost = member.Ran
                ? costs[costIndex++]
                : default;
            BeamWidthPortfolioMemberReport report = new(
                member.BeamWidth,
                member.SecondRankBand,
                member.BaseScoreOnly,
                member.AggressivePowerCommitment,
                member.NodeBudget,
                member.Ran,
                Selected: index == outcome.SelectedIndex,
                member.Compared,
                member.SkippedReason,
                member.ExpandedNodes,
                member.TransitionCount,
                member.Termination,
                member.Terminal,
                member.Won,
                member.BattleHpLost,
                member.PotionCount,
                cost.ElapsedMilliseconds,
                cost.AllocatedBytes,
                cost.ManagedHeapBytesAfter)
            { OffensiveRefinement = member.OffensiveRefinement, BoundedRefinement = member.BoundedRefinement };
            telemetry.RecordMember(report);
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] BEAM_WIDTH_PORTFOLIO_MEMBER index={index} " +
                $"beam={report.BeamWidth} second_rank_band={report.SecondRankBand} " +
                $"base_score_only={report.BaseScoreOnly} " +
                $"power_commitment={report.AggressivePowerCommitment} " +
                $"offensive_refinement={report.OffensiveRefinement} " +
                $"bounded_refinement={report.BoundedRefinement} " +
                $"nodes={report.NodeBudget} ran={report.Ran} " +
                $"selected={report.Selected} compared={report.Compared} " +
                $"skipped={report.SkippedReason ?? "-"} " +
                $"elapsed_ms={report.ElapsedMilliseconds} " +
                $"allocated_delta={report.AllocatedBytes} " +
                $"managed_heap_after={report.ManagedHeapBytesAfter} " +
                $"expanded={report.ExpandedNodes} transitions={report.TransitionCount} " +
                $"termination={report.Termination ?? "-"} won={report.Won?.ToString() ?? "-"} " +
                $"battle_hp_lost={report.BattleHpLost?.ToString() ?? "-"} " +
                $"potions={report.PotionCount?.ToString() ?? "-"}");
        }
        if (costIndex != costs.Count)
        {
            throw new InvalidOperationException(
                $"组合成员明细与实测开销条数不一致：明细 {costIndex} 条，实测 {costs.Count} 条。");
        }
    }

    /// <summary>
    /// 基线是否已经拿到「证明最优」的那一类结果：零战损、零主动用药、没卖血、满血且最大生命没掉。
    /// 与搜索里 <c>ProvenZeroDamage</c> 提前收手的条件同一套，只是从返回结果上复算，不在搜索里加观察点。
    /// </summary>
}
