namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private const int MaximumOpeningPowerPrefixes = 12;
    private const int MinimumPowerRouteNodes = 25_000;
    private const int MinimumPowerRouteMilliseconds = 10_000;

    /// <summary>
    /// 从同一根为每张当前可打能力建立固定开牌前缀，并继续搜索到完整战斗结果。单能力路线全部运行；
    /// 其后再补有限的双能力前缀。这里直接比较最终真实战损，不把能力估值带进终局排序。
    /// </summary>
    private static SolverResult RunOpeningPowerRoutePortfolio(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverPotionPolicy? potionPolicyOverride,
        SolverResult baseline)
    {
        if (!root.PlayerCardIds.Any(PowerCardValuationModels.Registry.ContainsCardId))
            return baseline;
        CombatBeamSolver prefixBuilder = new(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback,
            profile,
            potionPolicyOverride: potionPolicyOverride);
        PlanAction[] openingPowers = prefixBuilder.BuildOpeningPowerActions()
            .Where(action => PowerCardValuationModels.Registry.ContainsCardId(action.CardId!))
            .ToArray();
        if (openingPowers.Length == 0)
            return baseline;

        List<PlanAction[]> prefixes = openingPowers
            .Select(action => new[] { action })
            .ToList();
        HashSet<string> seen = prefixes
            .Select(PowerPrefixKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (PlanAction openingPower in openingPowers)
        {
            if (prefixes.Count >= Math.Max(openingPowers.Length, MaximumOpeningPowerPrefixes))
                break;
            IReadOnlyList<PlanAction> followUps = prefixBuilder.BuildPowerActionsAfterPrefix([openingPower]);
            foreach (PlanAction followUp in followUps)
            {
                if (!PowerCardValuationModels.Registry.ContainsCardId(followUp.CardId!))
                    continue;
                PlanAction[] prefix = [openingPower, followUp];
                if (seen.Add(PowerPrefixKey(prefix)))
                    prefixes.Add(prefix);
                if (prefixes.Count >= Math.Max(openingPowers.Length, MaximumOpeningPowerPrefixes))
                    break;
            }
        }

        BeamWidthPortfolioMemberSpec[] variants = prefixes.Count <= 3
            ?
            [
                new(profile.BeamWidth),
                new(BeamWidthPortfolio.ScaledWidth(
                    profile.BeamWidth,
                    BeamWidthPortfolio.WideRefinementRatio)),
                new(profile.BeamWidth, SecondRankBand: true),
                new(profile.BeamWidth, BaseScoreOnly: true),
            ]
            :
            [
                new(profile.BeamWidth),
                new(BeamWidthPortfolio.ScaledWidth(
                    profile.BeamWidth,
                    BeamWidthPortfolio.WideRefinementRatio)),
            ];
        int totalMembers = checked(prefixes.Count * variants.Length);
        int perRouteNodes = Math.Min(
            profile.MaxExpandedNodes,
            Math.Max(MinimumPowerRouteNodes, profile.MaxExpandedNodes / totalMembers));
        int perRouteMilliseconds = Math.Min(
            profile.SoftTimeBudgetMilliseconds,
            Math.Max(
                MinimumPowerRouteMilliseconds,
                profile.SoftTimeBudgetMilliseconds / totalMembers));
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] POWER_ROUTE_PORTFOLIO start singles={openingPowers.Length} " +
            $"prefixes={prefixes.Count} variants={variants.Length} members={totalMembers} " +
            $"nodes_each={perRouteNodes} time_ms_each={perRouteMilliseconds}");

        SolverResult selected = baseline;
        int memberIndex = 0;
        foreach (PlanAction[] prefix in prefixes)
        {
            foreach (BeamWidthPortfolioMemberSpec variant in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (policy.MemoryPressureSignal.IsEnabled
                    && !policy.MemoryPressureSignal.CanReachCommit(256L * 1024 * 1024))
                {
                    policy.MemoryPressureSignal.ReclaimAndContinue(
                        cancellationToken,
                        "opening_power_route_member");
                }

                SolverSearchProfile routeProfile = profile with
                {
                    // 前缀已经保证能力真实在场；后续不再用激进承诺干扰普通剪枝。
                    AggressivePowerCommitment = false,
                    BeamWidth = variant.BeamWidth,
                    SecondRankBand = variant.SecondRankBand,
                    BaseScoreOnly = variant.BaseScoreOnly,
                    MaxExpandedNodes = perRouteNodes,
                    SoftTimeBudgetMilliseconds = perRouteMilliseconds,
                };
                SearchRequestWorkSnapshot before = policy.RequestWorkTotals?.Snapshot() ?? default;
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
                long startedAt = Environment.TickCount64;
                SolverResult candidate = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback == null
                        ? null
                        : progress => progressCallback(progress with { Phase = "正在深搜能力路线" }),
                    routeProfile,
                    potionPolicyOverride: potionPolicyOverride,
                    fixedPrefixActions: prefix,
                    resetFixedPrefixSchedulingBaseline: true).Solve();
                if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                    return candidate;

                PopulateSingleSessionTotals(candidate);
                bool won = IsCompleteVictory(candidate);
                bool improved = IsBetterPotionPolicyResult(root, policy, candidate, selected);
                if (improved)
                    selected = candidate;
                SearchRequestWorkSnapshot after = policy.RequestWorkTotals?.Snapshot() ?? default;
                long elapsed = Math.Max(0, Environment.TickCount64 - startedAt);
                long allocated = Math.Max(
                    0,
                    GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore);
                string prefixText = string.Join('+', prefix.Select(action => action.CardId));
                policy.PortfolioTelemetry?.RecordPowerRouteMember(new PowerRoutePortfolioMemberReport(
                    prefixText,
                    variant.BeamWidth,
                    variant.SecondRankBand,
                    variant.BaseScoreOnly,
                    perRouteNodes,
                    perRouteMilliseconds,
                    after.ExpandedNodes - before.ExpandedNodes,
                    after.TransitionCount - before.TransitionCount,
                    candidate.BoundaryReason.ToString(),
                    won,
                    won ? candidate.ProjectedBattleHpLost : null,
                    improved,
                    elapsed,
                    allocated,
                    GC.GetTotalMemory(forceFullCollection: false)));
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] POWER_ROUTE_PORTFOLIO_MEMBER index={memberIndex++} " +
                    $"prefix={prefixText} beam={variant.BeamWidth} " +
                    $"second_rank_band={variant.SecondRankBand} base_score_only={variant.BaseScoreOnly} " +
                    $"won={won} boundary={candidate.BoundaryReason} " +
                    $"expanded={candidate.ExpandedNodes} " +
                    $"battle_hp_lost={(won ? candidate.ProjectedBattleHpLost.ToString() : "-")} " +
                    $"selected={improved}");
            }
        }

        policy.Diagnostics.Info(
            $"[CombatSolver/Test] POWER_ROUTE_PORTFOLIO result " +
            $"selected_hp_lost={selected.ProjectedBattleHpLost} " +
            $"changed={!ReferenceEquals(selected, baseline)}");
        return selected;
    }

    private static string PowerPrefixKey(IEnumerable<PlanAction> prefix)        => string.Join(
            '>',
            prefix.Select(action =>
                $"{action.CardId}:{action.CardStateKey}:{action.CardStateOccurrence}:" +
                $"{action.TargetCombatId?.ToString() ?? "-"}"));
}
