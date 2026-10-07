using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    public static SolverResult Solve(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback)
    {
        Stopwatch requestClock = Stopwatch.StartNew();
        List<PlanAction[]> firstTurnAnchors = [];
        SearchBudgetLedger ledger = new(requestClock, policy);
        SearchRequestWorkTotals requestWorkTotals = ledger.WorkTotals;
        BeamWidthPortfolioTelemetry portfolioTelemetry = new();
        policy = policy with
        {
            RouteAdoptionCancellationToken = cancellationToken,
            RequestWorkTotals = requestWorkTotals,
            PortfolioTelemetry = portfolioTelemetry,
            PrimaryIncumbents = policy.PrimaryIncumbents ?? new PrimaryIncumbentTable(),
        };
        SearchInteractionState? interaction = policy.Interaction;
        if (policy.IncludeTurnSetup)
            policy.Diagnostics.Info("[CombatSolver/Test] OPENING_PREFIX_REFINEMENT skipped reason=TurnSetupRoot");
        SolverResult? currentCompleteAdoptableResult = null;
        SolverInterimResult? currentDisplayedResult = null;
        SolverProgress? lastProgress = null;
        int currentTurnPreviewVersion = 0;
        int speculativeRouteVersion = 0;
        SolverCurrentTurnPreview? currentTurnPreview = null;
        SolverSpeculativeRoutePreview? speculativeRoutePreview = null;
        SolverRouteAdoptionSeed? currentRouteAdoptionSeed = null;

        bool TryPromoteDisplayedResult(SolverInterimResult candidate)
        {
            if (currentDisplayedResult != null)
            {
                if (candidate == currentDisplayedResult)
                    return true;
                if (!SolverInterimResultOrdering.CanPromoteDisplayedResult(
                        candidate,
                        currentDisplayedResult))
                    return false;
            }
            currentDisplayedResult = candidate;
            return true;
        }

        void PublishAdoptableResult(SolverResult result)
        {
            if (result.OnlyDeathRoutesFound
                || !SolverInterimResultOrdering.IsCompleteVictory(
                    result.BestNode.ActionCount,
                    result.Snapshot.AllEnemiesDead,
                    result.Snapshot.PlayerDead,
                    result.Snapshot.ProjectedPlayerHp))
            {
                return;
            }

            SolverInterimResult summary = BuildInterimResult(root, policy, result);
            bool promoted = TryPromoteDisplayedResult(summary);
            if (!promoted && summary != currentDisplayedResult)
                return;
            currentCompleteAdoptableResult = result;
            currentTurnPreview = SolverCurrentTurnPreview.FromResult(
                result,
                ++currentTurnPreviewVersion);
            speculativeRoutePreview = SolverSpeculativeRoutePreview.FromResult(
                result,
                ++speculativeRouteVersion);
            SolverRouteAdoptionSeed seed = new(
                speculativeRoutePreview.CandidateVersion,
                result.BestNode.Actions,
                () => result);
            currentRouteAdoptionSeed = seed;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_INTERIM_RESULT potions={result.ProjectedBattlePotionCount} " +
                $"projected_battle_hp_lost={result.ProjectedBattleHpLost}");
            if (lastProgress != null && progressCallback != null)
            {
                lastProgress = lastProgress with
                {
                    CurrentBestResult = currentDisplayedResult,
                    CurrentTurnPreview = currentTurnPreview,
                    SpeculativeRoutePreview = speculativeRoutePreview,
                    RouteAdoptionSeed = currentRouteAdoptionSeed,
                };
                progressCallback(lastProgress);
            }
        }

        Action<SolverProgress>? enrichedProgressCallback = progressCallback == null
            ? null
            : progress =>
            {
                lastProgress = progress;
                // Supplemental searches publish their own local previews. Once a global best exists,
                // keep those previews and their adoption seed together unless that local result wins globally.
                bool acceptsRouteUpdate = currentDisplayedResult == null;
                if (progress.CurrentBestResult is { } candidate)
                {
                    acceptsRouteUpdate = TryPromoteDisplayedResult(candidate);
                }
                else if (currentDisplayedResult != null)
                {
                    acceptsRouteUpdate = false;
                }

                if (acceptsRouteUpdate)
                {
                    if (progress.CurrentTurnPreview is { } current)
                    {
                        currentTurnPreview = current;
                        currentTurnPreviewVersion = Math.Max(
                            currentTurnPreviewVersion,
                            current.CandidateVersion);
                    }
                    if (progress.SpeculativeRoutePreview is { } speculative)
                    {
                        speculativeRoutePreview = speculative;
                        currentRouteAdoptionSeed = progress.RouteAdoptionSeed;
                        speculativeRouteVersion = Math.Max(
                            speculativeRouteVersion,
                            speculative.CandidateVersion);
                    }
                }
                progressCallback(progress with
                {
                    CurrentBestResult = currentDisplayedResult,
                    CurrentTurnPreview = currentTurnPreview,
                    SpeculativeRoutePreview = speculativeRoutePreview,
                    RouteAdoptionSeed = currentRouteAdoptionSeed,
                });
            };
        SearchPlanDiscoveryState planDiscovery = new();
        SolverResult RunPostSearch(SolverResult result)
        {
            SolverResult selected = ResolveTakeoverResult(result, interaction) ?? result;
            if (policy.IncludeTurnSetup)
                return selected;
            SearchPassContext postContext = new(root, displayNames, battleDamage,
                policy, policy.Profile, requestClock, ledger, cancellationToken,
                enrichedProgressCallback, interaction == null ? null : PublishAdoptableResult)
            {
                PlanDiscovery = planDiscovery,
            };
            return RunPostSearchPasses(postContext, selected, firstTurnAnchors,
                interaction, () => currentCompleteAdoptableResult);
        }

        try
        {
            SolverResult result = SolveCore(
                root,
                displayNames,
                battleDamage,
                policy,
                ledger,
                planDiscovery,
                cancellationToken,
                enrichedProgressCallback,
                interaction == null ? null : PublishAdoptableResult,
                firstTurnAnchors,
                RunPostSearch);
            SolverResult selected = result;
            selected = RunBossTempoSearch(new(root, displayNames, battleDamage,
                policy, policy.Profile, requestClock, ledger, cancellationToken,
                enrichedProgressCallback, interaction == null ? null : PublishAdoptableResult), selected);
            if (policy.IncludeTurnSetup)
            {
                PopulateRequestWorkTotals(selected, requestWorkTotals);
                selected.PortfolioTelemetry = portfolioTelemetry;
                return selected;
            }
            PopulateRequestWorkTotals(selected, requestWorkTotals);
            selected.PortfolioTelemetry = portfolioTelemetry;
            selected.ComparisonQuality = BuildInterimResult(root, policy, selected);
            selected.ComparisonRootState = root.ContinuationStamp.StateText;
            if (!policy.DisableRefinementIncumbentForTesting && !policy.DisableSharedPrimaryIncumbentsForTesting
                && PrimaryIncumbentTable.CanShareRoot(root)
                && IsReusablePotionFreeVictory(policy, null, selected))
            {
                policy.PrimaryIncumbents!.Tighten(
                    ResourceIncumbentPolicy.CompletedBucket(selected.Snapshot, 0),
                    new(StrategicHpDeficit(root, policy, selected), selected.CombatEndedTurn!.Value, 0));
                policy.PrimaryIncumbents.PotionFreeWitness = selected;
            }
            return selected;
        }
        catch (OperationCanceledException)
            when (interaction?.CurrentTakeoverRequest?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                  && currentCompleteAdoptableResult != null)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_INTERIM_ADOPTED " +
                $"potions={currentCompleteAdoptableResult.ProjectedBattlePotionCount} " +
                $"projected_battle_hp_lost={currentCompleteAdoptableResult.ProjectedBattleHpLost}");
            PopulateRequestWorkTotals(currentCompleteAdoptableResult, requestWorkTotals);
            currentCompleteAdoptableResult.PortfolioTelemetry = portfolioTelemetry;
            return currentCompleteAdoptableResult;
        }
    }

    private static bool IsAdoptionResult(SolverResult result)
        => result.ResultScope is SolverResultScope.CurrentTurnAdoption
            or SolverResultScope.RouteAdoption
            || SolverInterimResultOrdering.IsCompleteVictory(
                result.BestNode.ActionCount,
                result.Snapshot.AllEnemiesDead,
                result.Snapshot.PlayerDead,
                result.Snapshot.ProjectedPlayerHp);

    private static SolverResult? ResolveTakeoverResult(
        SolverResult result,
        SearchInteractionState? interaction)
    {
        SearchTakeoverRequest? request = interaction?.CurrentTakeoverRequest;
        if (request == null)
            return null;
        if (result.ResultScope is SolverResultScope.CurrentTurnAdoption
            or SolverResultScope.RouteAdoption)
        {
            return result;
        }
        if (request.Kind == SearchTakeoverKind.AdoptRoute)
            return request.RouteAdoptionSeed?.Materialize();
        return IsAdoptionResult(result) ? result : null;
    }

    private static SolverResult SolveCore(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        SearchBudgetLedger ledger,
        SearchPlanDiscoveryState planDiscovery,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        Action<SolverResult>? interimResultCallback,
        List<PlanAction[]> firstTurnAnchors,
        Func<SolverResult, SolverResult> postSearch)
    {
        Stopwatch requestClock = Stopwatch.StartNew();
        bool forcedSmartGradient = policy.PotionPolicy == SolverPotionPolicy.Smart
            && policy.PotionStrategy.HasForcedDirectives;
        SearchPolicySnapshot forcedBaselinePolicy = forcedSmartGradient
            ? policy with { PotionStrategy = policy.PotionStrategy.ForForcedBaseline() }
            : policy;
        SolverPotionPolicy? initialPotionPolicyOverride = policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
                ? SolverPotionPolicy.Disabled
                : null;
        // The progress bar represents the whole request. Individual Beam, novelty,
        // refinement and potion-audit searches all consume this same time budget.
        SolverSearchProfile profile = policy.Profile;
        if (policy.BudgetOverrideMilliseconds is { } deepBudget)
            profile = profile with { SoftTimeBudgetMilliseconds = deepBudget };
        if (progressCallback != null)
        {
            long completedSearches = 0;
            long completedElapsed = 0;
            int lastExpanded = 0;
            long lastElapsed = 0;
            Action<SolverProgress> publishProgress = progressCallback;
            progressCallback = progress =>
            {
                if (progress.ExpandedNodes < lastExpanded
                    || progress.ElapsedMilliseconds < lastElapsed)
                {
                    completedSearches += lastExpanded;
                    completedElapsed += lastElapsed;
                }
                lastExpanded = progress.ExpandedNodes;
                lastElapsed = progress.ElapsedMilliseconds;
                publishProgress(progress with
                {
                    ReviewedWorldlines = completedSearches + progress.ExpandedNodes,
                    ElapsedMilliseconds = completedElapsed + progress.ElapsedMilliseconds,
                    RequestBudgetMilliseconds = profile.SoftTimeBudgetMilliseconds,
                });
            };
        }
        SmartLayerMemoryForecast memoryForecast = new();
        // One search profile drives primary search and all supplemental audits.
        if (root.IsActEndingBoss && profile.BeamWidth < 45)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] ACT_ENDING_BOSS_SEARCH_OVERRIDE " +
                $"beam={profile.BeamWidth}->45 reason=preserve_survival_routes");
            profile = profile with { BeamWidth = 45 };
        }
        // 一轮完整的深化搜索：主搜索（Smart 时先按无主动用药跑）＋补充审计。抬节点上限重搜时
        // 原样再走一遍，所以抽成一个本地函数；每一轮自带一只秒表，补充审计那边算剩余预算靠它。
        SearchPassResult RunSearchPass(SearchPassContext passContext)
        {
            SolverSearchProfile passProfile = passContext.Profile;
            Stopwatch passClock = passContext.Clock;
            FrontierContinuationScheduler continuationScheduler = new(passContext);
            SearchPassResult CapturePassResult(
                SolverResult selected,
                SolverResult? takeover,
                bool settled)
                => new(selected, takeover, settled,
                    selected.ResultScope == SolverResultScope.SearchCompletion
                        ? RouteQuality.FromInterim(BuildInterimResult(root, policy, selected))
                        : null,
                    passContext.Budget.WorkTotals.Snapshot(),
                    selected.ResultScope,
                    selected.BoundaryReason);
            long passAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            long passTransitionsAtStart = passContext.Budget.WorkTotals.Snapshot().TransitionCount;
            SearchPolicySnapshot passPolicy = forcedBaselinePolicy;
            SearchPolicySnapshot beamPolicy = passPolicy.NoveltySearch == null
                ? passPolicy : passPolicy with { NoveltySearch = null };
            SolverResult? initialPlanIncumbent = TryRunOpeningPlanIncumbent(
                passContext with { Policy = beamPolicy }, initialPotionPolicyOverride);
            if (!policy.DisableRefinementIncumbentForTesting && !policy.DisableSharedPrimaryIncumbentsForTesting
                && PrimaryIncumbentTable.CanShareRoot(root)
                && beamPolicy.PrimaryIncumbents?.PotionFreeWitness is { } previousVictory
                && IsReusablePotionFreeVictory(beamPolicy, initialPotionPolicyOverride, previousVictory)
                && (initialPlanIncumbent == null || IsBetterPotionPolicyResult(
                    root, beamPolicy, previousVictory, initialPlanIncumbent)))
                initialPlanIncumbent = previousVictory;
            SolverResult SolveMember(SolverSearchProfile memberProfile, bool refinement,
                PrimarySearchIncumbent? primaryIncumbent)
            {
                if ((initialPlanIncumbent != null || passContext.PlanDiscovery.NarrowOpeningIncumbentAttempted)
                    && !refinement)
                    memberProfile = memberProfile with
                    {
                        SoftTimeBudgetMilliseconds = (int)Math.Clamp(passContext.RemainingMilliseconds,
                            1L, memberProfile.SoftTimeBudgetMilliseconds),
                    };
                Action<SolverProgress>? memberProgressCallback = refinement && progressCallback != null
                    ? progress => progressCallback(progress with { Phase = "正在精炼路线" })
                    : progressCallback;
                SolverResult memberResult = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    beamPolicy,
                    cancellationToken,
                    memberProgressCallback,
                    memberProfile,
                    potionPolicyOverride: initialPotionPolicyOverride,
                    primaryIncumbent: primaryIncumbent,
                    directSearchPurpose: refinement
                        ? DirectSearchPurpose.RefinementBeam
                        : DirectSearchPurpose.PrimaryBeam).Solve();
                PlanAction[] firstTurn = memberResult.BestNode.Actions
                    .TakeWhile(action => action.Turn == root.StartTurnNumber)
                    .ToArray();
                if (firstTurn.LastOrDefault()?.Kind == PlanActionKind.EndTurn)
                    firstTurnAnchors.Add(firstTurn);
                return memberResult;
            }
            // 提前计划的完整胜利可立即发布。首个成员随后只能替换为更优结果，
            // 精炼成员仍在本轮末尾发布；同一结果对象不重复发布。
            SolverResult? publishedBaseline = null;
            Action<SolverResult>? publishBaseline =
                (policy.UseBeamWidthPortfolio
                    || root.PlayerCardIds.Any(PowerCardValuationModels.Registry.ContainsCardId))
                && interimResultCallback != null
                    ? baseline =>
                    {
                        SolverResult published = initialPlanIncumbent != null
                            && IsBetterPotionPolicyResult(root, policy, initialPlanIncumbent, baseline)
                                ? initialPlanIncumbent : baseline;
                        if (!ReferenceEquals(published, publishedBaseline))
                        {
                            publishedBaseline = published;
                            interimResultCallback(published);
                        }
                    }
                    : null;
            if (initialPlanIncumbent is { ResultScope: SolverResultScope.SearchCompletion })
            {
                policy.PortfolioTelemetry?.RecordFirstRoutePublished(passClock.Elapsed.TotalMilliseconds);
                publishBaseline?.Invoke(initialPlanIncumbent);
            }
            SolverResult RunBaseline(SolverSearchProfile baselineProfile)
            {
                if (initialPlanIncumbent is { ResultScope: not SolverResultScope.SearchCompletion })
                    return initialPlanIncumbent;
                SearchBudgetWindow baselineWindow = passContext.Budget.ProfileWindow(baselineProfile);
                if (initialPlanIncumbent != null && baselineWindow.RemainingNodes <= 0)
                    return initialPlanIncumbent;
                SolverSearchProfile effectiveBaseline = initialPlanIncumbent == null ? baselineProfile
                    : baselineWindow.Limit(baselineProfile, baselineProfile.MaxExpandedNodes,
                        baselineProfile.SoftTimeBudgetMilliseconds, reserveMilliseconds: 0);
                return RunBeamWidthPortfolioPass(passContext with
                    {
                        Policy = beamPolicy,
                        Profile = effectiveBaseline,
                        Clock = ReferenceEquals(baselineProfile, passProfile)
                            ? passClock : Stopwatch.StartNew(),
                    }, SolveMember, publishBaseline, initialPotionPolicyOverride, initialPlanIncumbent);
            }
            SolverResult RunPrimary()
                => policy.UseNoveltyPortfolio
                    ? RunNoveltyPortfolioPass(passContext with { Policy = passPolicy },
                        initialPotionPolicyOverride, RunBaseline)
                    : RunBaseline(passProfile);
            bool hasForcedBaseline = forcedSmartGradient;
            SolverResult passResult;
            try
            {
                passResult = RunPrimary();
            }
            catch (PotionPolicyUnsatisfiedException) when (forcedSmartGradient)
            {
                // The forced-only layer has no usable route; optional potions can still rescue the fight.
                hasForcedBaseline = false;
                passPolicy = policy;
                beamPolicy = policy.NoveltySearch == null
                    ? policy : policy with { NoveltySearch = null };
                passResult = RunPrimary();
            }
            // Opening posteriors require a resolved hand; turn setup still owns its native choice.
            if (!policy.IncludeTurnSetup)
            {
            if (passResult.ResultScope == SolverResultScope.SearchCompletion)
            {
                passResult = RunOpeningPowerRoutePortfolio(
                    passContext with { Policy = beamPolicy },
                    initialPotionPolicyOverride,
                    passResult);
            }
            // The native closed Regent / Louse environment can establish a zero-loss
            // potion-free route before the optional potion audits. Reuse the existing
            // continuation and quality rules; every member consumes the shared ledger.
            if (root.CanCertifyRemainingHealing
                && root.PlayerIdentity.Character.GetType() == typeof(MegaCrit.Sts2.Core.Models.Characters.Regent)
                && root.Enemies.Count > 0
                && root.Enemies.All(enemy => enemy.Monster?.GetType()
                    == typeof(MegaCrit.Sts2.Core.Models.Monsters.LouseProgenitor))
                && !policy.EffectiveHasGrowthTargets && policy.RelicTargets.Count == 0
                && !policy.PotionStrategy.HasForcedDirectives
                && passResult.ResultScope == SolverResultScope.SearchCompletion
                && IsCompleteVictory(passResult) && !passResult.Snapshot.HasRisk
                && passResult.Snapshot.ProjectedDeathSaveUseCount == 0
                && passResult.ExplicitPotionCount == 0
                && passResult.ProjectedBattleHpLost is > 0 and <= SolverWeights.PotionMinimumHpSaved
                && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy,
                    passResult.OutstandingStolenResource))
            {
                passResult = RunTurnBoundaryRescue(passContext, passResult, firstTurnAnchors);
            }
            if (passResult.ResultScope == SolverResultScope.SearchCompletion
                && IsCompleteVictory(passResult)
                && passResult.ExplicitPotionCount == 0
                && passResult.ProjectedBattleHpLost >= SolverWeights.PotionMinimumHpSaved
                && initialPotionPolicyOverride == SolverPotionPolicy.Disabled)
            {
                PlanAction[] opening = passResult.BestNode.Actions
                    .TakeWhile(action => action.Turn == root.StartTurnNumber).ToArray();
                int generatedIndex = Array.FindIndex(opening, action =>
                    action is { Kind: PlanActionKind.PlayCard, CardId: not null }
                    && !root.PlayerCardIds.Contains(action.CardId));
                int discardIndex = generatedIndex < 1 ? -1 : Array.FindIndex(
                    opening, generatedIndex + 1, action => action.Choice?.Effect is
                        PlanChoiceEffect.Discard or PlanChoiceEffect.DiscardAndDraw);
                if (discardIndex > generatedIndex
                    && opening[generatedIndex - 1].Kind == PlanActionKind.PlayCard)
                {
                    PlanAction[] prefix = [.. opening.Take(generatedIndex - 1), opening[discardIndex]];
                    SearchBudgetWindow reorderedWindow = passContext.SliceWindow;
                    CombatBeamSolver builder = new(root, displayNames, battleDamage,
                        beamPolicy, cancellationToken, progressCallback, passProfile,
                        potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
                    if (reorderedWindow.CanStart(5_000)
                        && builder.CanReplayOpeningPrefix(prefix))
                    {
                        SolverSearchProfile reorderedProfile = reorderedWindow.Limit(passProfile,
                            maximumNodes: 100_000, maximumMilliseconds: 30_000,
                            reserveMilliseconds: 2_000);
                        SolverResult candidate = continuationScheduler.Dispatch(
                            new ContinuationSearchRequest(passContext,
                                ContinuationPurpose.EarlyDiscardBeforeGeneration,
                                prefix, reorderedProfile, SolverPotionPolicy.Disabled, 0, null)
                            { PolicyOverride = beamPolicy });
                        bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                            && IsBetterPotionPolicyResult(root, policy, candidate, passResult);
                        if (improved)
                            passResult = candidate;
                        policy.Diagnostics.Info($"[CombatSolver/Test] EARLY_DISCARD_BEFORE_GENERATION " +
                            $"hp_lost={candidate.ProjectedBattleHpLost} selected={improved}");
                    }
                }
            }
            if (passResult.ResultScope == SolverResultScope.SearchCompletion
                && root.Enemies.Count > 1
                && passResult.ProjectedBattleHpLost >= SolverWeights.PotionMinimumHpSaved
                && initialPotionPolicyOverride == SolverPotionPolicy.Disabled)
            {
                PlanAction[] opening = passResult.BestNode.Actions
                    .TakeWhile(action => action.Turn == root.StartTurnNumber).ToArray();
                CombatBeamSolver targetBuilder = new(root, displayNames, battleDamage,
                    beamPolicy, cancellationToken, progressCallback, passProfile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
                foreach (PlanAction[] prefix in targetBuilder
                             .BuildOpeningLeadingTargetPrefixes(opening)
                             .DistinctBy(PowerPrefixKey).Take(4))
                {
                    SearchBudgetWindow targetWindow = passContext.SliceWindow;
                    if (!targetWindow.CanStart(5_000))
                        break;
                    SolverSearchProfile targetProfile = targetWindow.Limit(passProfile,
                        maximumNodes: 40_000, maximumMilliseconds: 15_000,
                        reserveMilliseconds: 2_000) with
                    {
                        AggressivePowerCommitment = true,
                    };
                    SolverResult candidate = continuationScheduler.Dispatch(
                        new ContinuationSearchRequest(passContext,
                            ContinuationPurpose.OpeningTargetVariant,
                            prefix, targetProfile, SolverPotionPolicy.Disabled, 0, null)
                        { PolicyOverride = beamPolicy });
                    if (candidate.ResultScope == SolverResultScope.SearchCompletion
                        && IsBetterPotionPolicyResult(root, policy, candidate, passResult))
                        passResult = candidate;
                    policy.Diagnostics.Info($"[CombatSolver/Test] OPENING_TARGET_VARIANT " +
                        $"prefix={string.Join('+', prefix.Select(action => $"{action.CardId}@{action.TargetCombatId}"))} " +
                        $"hp_lost={candidate.ProjectedBattleHpLost}");
                    if (HasReachedAcceptableBattleHpLoss(policy, passResult))
                        break;
                    foreach (PlanAction power in targetBuilder.BuildPowerActionsAfterPrefix(prefix)
                                 .Where(action => PowerCardValuationModels.Registry.ContainsCardId(action.CardId!))
                                 .Take(1))
                    {
                        SearchBudgetWindow powerWindow = passContext.SliceWindow;
                        if (!powerWindow.CanStart(5_000))
                            break;
                        SolverSearchProfile powerProfile = powerWindow.Limit(targetProfile,
                            maximumNodes: 40_000, maximumMilliseconds: 15_000,
                            reserveMilliseconds: 2_000) with
                        {
                            BaseScoreOnly = true,
                            AggressivePowerCommitment = false,
                        };
                        SolverResult powered = continuationScheduler.Dispatch(
                            new ContinuationSearchRequest(passContext,
                                ContinuationPurpose.OpeningTargetPowerVariant,
                                [.. prefix, power], powerProfile,
                                SolverPotionPolicy.Disabled, 0, null)
                            { PolicyOverride = beamPolicy });
                        if (powered.ResultScope == SolverResultScope.SearchCompletion
                            && IsBetterPotionPolicyResult(root, policy, powered, passResult))
                            passResult = powered;
                        policy.Diagnostics.Info($"[CombatSolver/Test] OPENING_TARGET_POWER_VARIANT " +
                            $"power={power.CardId} hp_lost={powered.ProjectedBattleHpLost}");
                        foreach (PlanAction defensive in targetBuilder
                                     .BuildOpeningDefensiveFollowUps([.. prefix, power]))
                        {
                            SearchBudgetWindow defensiveWindow = passContext.SliceWindow;
                            if (!defensiveWindow.CanStart(5_000))
                                break;
                            SolverSearchProfile defensiveProfile = defensiveWindow.Limit(
                                powerProfile, maximumNodes: 40_000, maximumMilliseconds: 15_000,
                                reserveMilliseconds: 2_000);
                            SolverResult defended = continuationScheduler.Dispatch(
                                new ContinuationSearchRequest(passContext,
                                    ContinuationPurpose.OpeningTargetPowerDefensiveVariant,
                                    [.. prefix, power, defensive], defensiveProfile,
                                    SolverPotionPolicy.Disabled, 0, null)
                                { PolicyOverride = beamPolicy });
                            if (defended.ResultScope == SolverResultScope.SearchCompletion
                                && IsBetterPotionPolicyResult(root, policy, defended, passResult))
                                passResult = defended;
                            policy.Diagnostics.Info($"[CombatSolver/Test] OPENING_TARGET_POWER_DEFENSIVE_VARIANT " +
                                $"power={power.CardId} defensive={defensive.CardId} " +
                                $"hp_lost={defended.ProjectedBattleHpLost}");
                        }
                    }
                }
            }
            if (passResult.ResultScope == SolverResultScope.SearchCompletion
                && IsCompleteVictory(passResult)
                && passResult.ExplicitPotionCount == 0
                && passResult.ProjectedBattleHpLost >= SolverWeights.PotionMinimumHpSaved
                && root.PlayerCardIds.Any(PowerCardValuationModels.Registry.ContainsCardId)
                && initialPotionPolicyOverride == SolverPotionPolicy.Disabled)
            {
                PlanAction endTurn = new(PlanActionKind.EndTurn, root.StartTurnNumber);
                CombatBeamSolver deferredBuilder = new(root, displayNames, battleDamage,
                    beamPolicy, cancellationToken, progressCallback, passProfile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
                if (deferredBuilder.CanReplayOpeningPrefix([endTurn]))
                {
                    foreach (PlanAction power in deferredBuilder
                                 .BuildPowerActionsAfterPrefix([endTurn])
                                 .Where(action => PowerCardValuationModels.Registry.ContainsCardId(action.CardId!))
                                 .Take(2))
                    {
                        SearchBudgetWindow deferredWindow = passContext.SliceWindow;
                        if (!deferredWindow.CanStart(5_000))
                            break;
                        SolverSearchProfile deferredProfile = deferredWindow.Limit(passProfile,
                            maximumNodes: 50_000, maximumMilliseconds: 20_000,
                            reserveMilliseconds: 2_000);
                        SolverResult deferred = continuationScheduler.Dispatch(
                            new ContinuationSearchRequest(passContext,
                                ContinuationPurpose.DeferredOpeningPower,
                                [endTurn, power], deferredProfile,
                                SolverPotionPolicy.Disabled, 0, null)
                            { PolicyOverride = beamPolicy });
                        if (deferred.ResultScope == SolverResultScope.SearchCompletion
                            && IsBetterPotionPolicyResult(root, policy, deferred, passResult))
                            passResult = deferred;
                        policy.Diagnostics.Info($"[CombatSolver/Test] DEFERRED_OPENING_POWER " +
                            $"power={power.CardId} hp_lost={deferred.ProjectedBattleHpLost}");
                    }
                }
            }
            if (passResult.ResultScope == SolverResultScope.SearchCompletion
                && IsCompleteVictory(passResult)
                && passResult.ExplicitPotionCount == 0
                && passResult.ProjectedBattleHpLost >= SolverWeights.PotionMinimumHpSaved
                && initialPotionPolicyOverride == SolverPotionPolicy.Disabled)
            {
                CombatBeamSolver openingBuilder = new(root, displayNames, battleDamage,
                    beamPolicy, cancellationToken, progressCallback, passProfile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
                IReadOnlyList<PlanAction> freeAttacks = openingBuilder.BuildOpeningFreeOffensiveActions();
                foreach (PlanAction attack in freeAttacks)
                {
                    IReadOnlyList<PlanAction> setups = openingBuilder
                        .BuildOpeningHandCycleActionsAfterPrefix([attack]);
                    foreach (PlanAction setup in setups.Take(2))
                    {
                        PlanAction[] prefix = [attack, setup];
                        SearchBudgetWindow openingWindow = passContext.SliceWindow;
                        if (!openingWindow.CanStart(5_000))
                            break;
                        SolverSearchProfile openingProfile = openingWindow.Limit(passProfile,
                            maximumNodes: 70_000, maximumMilliseconds: 20_000,
                            reserveMilliseconds: 2_000) with
                        {
                            BaseScoreOnly = true,
                        };
                        SolverResult candidate = continuationScheduler.Dispatch(
                            new ContinuationSearchRequest(passContext,
                                ContinuationPurpose.FreeAttackHandSetup,
                                prefix, openingProfile, SolverPotionPolicy.Disabled, 0, null)
                            { PolicyOverride = beamPolicy });
                        if (candidate.ResultScope == SolverResultScope.SearchCompletion
                            && IsBetterPotionPolicyResult(root, policy, candidate, passResult))
                            passResult = candidate;
                        policy.Diagnostics.Info($"[CombatSolver/Test] FREE_ATTACK_HAND_SETUP " +
                            $"attack={attack.CardId} setup={setup.CardId} " +
                            $"hp_lost={candidate.ProjectedBattleHpLost}");
                    }
                }
            }
            }
            NoveltyPortfolioTelemetry? noveltyPass = passResult.NoveltyPortfolio;
            ObserveSmartLayerMemory(
                passContext, memoryForecast, passAllocatedAtStart, passTransitionsAtStart,
                passResult, passProfile,
                completedPotionCount: hasForcedBaseline ? policy.PotionStrategy.ForcedDirectiveCount : 0);
            if (policy.MeasurePhasePerformance)
                policy.Diagnostics.Info(SolverDiagnostics.DescribeSearchPhasePerformance(passResult));
            passResult.SingleSessionSearch = true;
            PopulateSingleSessionTotals(passResult);
            if (!ReferenceEquals(passResult, publishedBaseline))
                interimResultCallback?.Invoke(passResult);
            if (ResolveTakeoverResult(passResult, policy.Interaction) is { } passTakeover)
                return CapturePassResult(passResult, passTakeover, false);
            if (policy.IncludeTurnSetup)
                return CapturePassResult(passResult, null, false);
            SearchPassContext auditContext = passContext;
            // Smart 无强制指令时主路线应无药。插入药由确定性路线引入；开目标变体等预审计
            // continuation 也可能把带插入药的首回合前缀带回主路线。两种情况都先重派生无药
            // 基线再走补充审计，避免 Smart 梯度收到带药起点。
            bool needsPotionFreeAuditBaseline = passResult.DeterministicBlockPotionInserted
                || initialPotionPolicyOverride == SolverPotionPolicy.Disabled
                    && passResult.ExplicitPotionCount > 0;
            if (needsPotionFreeAuditBaseline)
            {
                SearchPolicySnapshot potionFreePolicy = beamPolicy with
                {
                    PotionPolicy = SolverPotionPolicy.Disabled,
                    PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []),
                };
                SolverResult potionFree = new CombatBeamSolver(root, displayNames,
                    battleDamage, potionFreePolicy, cancellationToken, progressCallback,
                    passProfile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                    directSearchPurpose: DirectSearchPurpose.PotionFreeAudit).Solve();
                if (potionFree.ResultScope != SolverResultScope.SearchCompletion)
                    return CapturePassResult(potionFree, null, false);
                SolverResult audited = RunSupplementalAudits(auditContext,
                    potionFree, memoryForecast);
                if (audited.ResultScope != SolverResultScope.SearchCompletion)
                    return CapturePassResult(audited, null, false);
                if (IsBetterSmartPotionAuditResult(root, policy, audited, passResult))
                    passResult = audited;
                return CapturePassResult(passResult, null, false);
            }
            if (!policy.PotionStrategy.HasForcedDirectives || hasForcedBaseline)
            {
                if (!hasForcedBaseline && HasReachedAcceptableBattleHpLoss(policy, passResult))
                    return CapturePassResult(passResult, null, true);
                passResult = RunSupplementalAudits(
                    policy.NoveltySearch == null
                        ? auditContext
                        : auditContext with { Policy = policy with { NoveltySearch = null } },
                    passResult,
                    memoryForecast);
                // The final potion audit may return another result object. Keep the
                // primary-pass observations alongside the request's final outcome.
                passResult.NoveltyPortfolio = noveltyPass;
            }
            return CapturePassResult(passResult, null, false);
        }

        SearchPassContext requestContext = new(root, displayNames, battleDamage,
            policy, profile, requestClock, ledger, cancellationToken,
            progressCallback, interimResultCallback)
        {
            PlanDiscovery = planDiscovery,
        };
        return new SearchRequestPipeline(requestContext, RunSearchPass, postSearch).Run();
    }

    private static bool CanFinishNativeLouseZeroDamageRoute(
        CombatRootSnapshot root, SearchPolicySnapshot policy, SolverResult result)
        => !policy.FixedBudget
            && root.CanCertifyRemainingHealing
            && root.PlayerIdentity.Character.GetType() == typeof(MegaCrit.Sts2.Core.Models.Characters.Regent)
            && root.Enemies.Count > 0
            && root.Enemies.All(enemy => enemy.Monster?.GetType()
                == typeof(MegaCrit.Sts2.Core.Models.Monsters.LouseProgenitor))
            // Unknown cards, powers and potions, or any remaining regeneration,
            // must prevent this health-floor certificate.
            && root.InitialRemainingHealingUpperBound == 0
            && policy.RelicTargets.Count == 0
            && !policy.PotionStrategy.HasForcedDirectives
            && !result.Snapshot.HasRisk
            && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, result.OutstandingStolenResource)
            && IsProvenZeroDamageRoute(root, policy, result);

    private static bool IsProvenZeroDamageRoute(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => !policy.EffectiveHasGrowthTargets
            && IsCompleteVictory(result)
            && result.ExplicitPotionCount == 0
            && result.FutureSoldHp == 0
            && result.ProjectedBattleHpLost - result.BattleHpLostSoFar == 0
            && result.Snapshot.PlayerMaxHp >= root.InitialPlayerMaxHp
            && result.Snapshot.PlayerHp >= result.Snapshot.PlayerMaxHp;

    internal static SolverInterimResult CapturePortfolioQuality(
        CombatRootSnapshot root, SearchPolicySnapshot policy, SolverResult result)
        => BuildInterimResult(root, policy, result);

    private static SolverInterimResult BuildInterimResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => new(
            Won: IsCompleteVictory(result),
            OutstandingStolenResource: result.OutstandingStolenResource,
            ProjectedBattleHpLost: result.ProjectedBattleHpLost,
            StrategicHpDeficit: StrategicHpDeficit(root, policy, result),
            PotionStrategicCost: SmartPotionHpRequired(root, policy, result),
            ProjectedBattlePotionCount: result.ProjectedBattlePotionCount,
            CombatEndedTurn: result.CombatEndedTurn,
            EnemyHp: result.Snapshot.EnemyHp,
            Score: result.BestNode.Score)
        {
            GrowthHpCredit = result.Snapshot.StrategyGoalHpCredit,
            TheftPolicy = policy.TheftPolicy,
            GrowthRewardCount = result.Snapshot.StrategyGoalCount,
            Survives = !result.Snapshot.PlayerDead && result.Snapshot.ProjectedPlayerHp > 0,
            DeathSaveUseCount = result.Snapshot.ProjectedDeathSaveUseCount,
        };


    private static bool IsBetterCompletedResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        int primaryQuality = CompareCompletedResultPrimaryQuality(root, policy, candidate, current);
        if (primaryQuality != 0)
            return primaryQuality < 0;
        return candidate.PotionCount < current.PotionCount
            || candidate.PotionCount == current.PotionCount
                && candidate.BestNode.Score > current.BestNode.Score;
    }

    private static bool IsBetterPotionPolicyResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
        => IsBetterPotionPolicyResult(
            policy.TheftPolicy,
            BuildInterimResult(root, policy, candidate),
            BuildInterimResult(root, policy, current));

    internal static bool IsBetterPotionPolicyResult(
        SolverTheftPolicy? theftPolicy,
        SolverInterimResult candidate,
        SolverInterimResult current)
        => RouteQualityPolicy.Compare(
            RouteQuality.FromInterim(candidate), RouteQuality.FromInterim(current),
            RouteQualityProjection.PotionPolicy, theftPolicy) < 0;

    private static int CompareCompletedResultPrimaryQuality(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        bool candidateWon = IsCompleteVictory(candidate);
        bool currentWon = IsCompleteVictory(current);
        int victoryComparison = currentWon.CompareTo(candidateWon);
        if (victoryComparison != 0)
            return victoryComparison;
        bool candidateSurvives = !candidate.Snapshot.PlayerDead
            && candidate.Snapshot.ProjectedPlayerHp > 0;
        bool currentSurvives = !current.Snapshot.PlayerDead
            && current.Snapshot.ProjectedPlayerHp > 0;
        int survivalComparison = currentSurvives.CompareTo(candidateSurvives);
        if (survivalComparison != 0)
            return survivalComparison;
        int deathSaveComparison = candidate.Snapshot.ProjectedDeathSaveUseCount.CompareTo(
            current.Snapshot.ProjectedDeathSaveUseCount);
        if (deathSaveComparison != 0)
            return deathSaveComparison;
        int recovery = TheftEncounterStrategy.CompareRecovery(policy.TheftPolicy,
            candidateWon, candidate.OutstandingStolenResource,
            currentWon, current.OutstandingStolenResource);
        if (recovery != 0)
            return recovery;
        return RouteQualityPolicy.Compare(
            RouteQuality.Primary(candidateWon, StrategicHpDeficit(root, policy, candidate),
                candidate.CombatEndedTurn, candidate.Snapshot.StrategyGoalHpCredit,
                candidate.Snapshot.StrategyGoalCount, candidate.Snapshot.ProjectedDeathSaveUseCount),
            RouteQuality.Primary(currentWon, StrategicHpDeficit(root, policy, current),
                current.CombatEndedTurn, current.Snapshot.StrategyGoalHpCredit,
                current.Snapshot.StrategyGoalCount, current.Snapshot.ProjectedDeathSaveUseCount),
            RouteQualityProjection.Primary);
    }

    private static bool IsCompleteVictory(SolverResult result)
        => SolverInterimResultOrdering.IsCompleteVictory(
            result.BestNode.ActionCount,
            result.Snapshot.AllEnemiesDead,
            result.Snapshot.PlayerDead,
            result.Snapshot.ProjectedPlayerHp);

    internal static bool HasReachedAcceptableBattleHpLoss(
        SearchPolicySnapshot policy,
        SolverResult result)
        => policy.GrowthTargetSatisfied(result.Snapshot.GrowthRewards)
            && policy.RelicTargetsSatisfied(result.Snapshot.RelicCounters)
            && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, result.OutstandingStolenResource)
            && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && result.PotionCount == policy.MinimumRequiredPotionUses(result.BattlePotionsUsedSoFar)
            && policy.PotionStrategy.EvaluateForcedUses(result.BestNode.Actions, renewablePotionShapedRock: false).AllForcedUsesSatisfied
            && HasReachedAcceptableBattleHpLoss(
            IsCompleteVictory(result),
            result.ProjectedBattleHpLost,
            policy.AcceptableBattleHpLoss);

    // This honors an explicitly enabled satisficing policy, not a proof that no
    // alternate route can heal more or finish sooner. Preserve the selected incumbent.
    private static bool CanFinishTargetPortfolio(
        CombatRootSnapshot root, SearchPolicySnapshot policy, SolverSearchProfile profile, SolverResult result)
        => profile.StopPortfolioAtHpTarget
            && !root.HasVisibleHealingSource
            && result.Snapshot.RecoveredPlayerHp == 0
            && result.ResultScope == SolverResultScope.SearchCompletion
            && !result.Snapshot.HasRisk
            && HasReachedAcceptableBattleHpLoss(policy, result);

    internal static bool HasReachedAcceptableBattleHpLoss(
        bool completeVictory,
        int projectedBattleHpLost,
        int acceptableBattleHpLoss)
        => completeVictory && projectedBattleHpLost <= acceptableBattleHpLoss;

    private static bool HasReachedProvablePrimaryQualityLowerBound(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => !policy.EffectiveHasGrowthTargets
            && policy.RelicTargets.Count == 0
            && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, result.OutstandingStolenResource)
            && HasReachedProvablePrimaryQualityLowerBound(
            IsCompleteVictory(result),
            StrategicHpDeficit(root, policy, result),
            result.CombatEndedTurn,
            root.StartTurnNumber,
            ProvableStrategicHpFloor(root, policy));

    internal static bool HasReachedProvablePrimaryQualityLowerBound(
        bool completeVictory,
        int strategicHpDeficit,
        int? combatEndedTurn,
        int? earliestPossibleCombatEndedTurn,
        int provableStrategicHpFloor)
    {
        if (earliestPossibleCombatEndedTurn is not { } earliestTurn)
            return false;
        return SolverInterimResultOrdering.ComparePrimaryQuality(
            completeVictory,
            strategicHpDeficit,
            combatEndedTurn,
            currentCompleteVictory: true,
            currentStrategicHpDeficit: provableStrategicHpFloor,
            currentCombatEndedTurn: earliestTurn) <= 0;
    }

    private static PrimarySearchIncumbent? BuildPrimarySearchIncumbent(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
    {
        if (policy.EffectiveHasGrowthTargets
            || !ResourceIncumbentPolicy.IsPrimaryHpBucket(
                ResourceIncumbentPolicy.CompletedBucket(result.Snapshot, result.ExplicitPotionCount),
                CombatBeamSolver.CanUseStrictHpRelicBound(root, policy))
            || policy.RelicTargets.Count > 0 && !CombatBeamSolver.CanUseStrictHpRelicBound(root, policy)
            || result.Snapshot.ProjectedDeathSaveUseCount > 0
            || !IsCompleteVictory(result)
            || result.CombatEndedTurn is not { } combatEndedTurn)
            return null;
        return new PrimarySearchIncumbent(
            StrategicHpDeficit(root, policy, result),
            combatEndedTurn,
            result.PotionStrategicCostByTurn.Values.Sum());
    }

    private static SolverResult? SolveOptionalPotionPosterior(
        CombatBeamSolver solver,
        SearchPolicySnapshot policy,
        string diagnostic)
    {
        try
        {
            return solver.Solve();
        }
        catch (PotionPolicyUnsatisfiedException)
        {
            policy.Diagnostics.Info($"[CombatSolver/Test] {diagnostic} qualified=false");
            return null;
        }
    }

    private static int SmartPotionHpRequired(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
    {
        ForcedPotionUseEvaluation forced = policy.PotionStrategy.EvaluateForcedUses(
            result.PotionUses);
        int ambergrisCount = result.BestNode.Actions.Count(action =>
            action.Kind == PlanActionKind.UsePotion
            && string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal))
            - forced.ForcedAmbergrisCount;
        int explicitPotionCount = result.BestNode.Actions.Count(action =>
            action.Kind == PlanActionKind.UsePotion) - forced.ForcedUseCount;
        int strategicHpCost = PotionUsePolicy.EffectiveStrategicHpCost(
            PotionUsePolicy.ApplyReplacementCredit(
                Math.Max(0, result.PotionStrategicCostByTurn.Values.Sum() - forced.ForcedStrategicHpCost),
                explicitPotionCount,
                root.PotionRewardOutlook.ReplacementHpCredit),
            ambergrisCount,
            root.InitialPlayerMaxHp);
        return PotionUsePolicy.SmartRequiredHpSaved(
            strategicHpCost,
            StrategicBossHpRelief(root, policy));
    }

    private static int StrategicHpDeficit(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => ActEndingBossPolicy.StrategicHpDeficit(
            result.Snapshot.CumulativePlayerHpLost,
            Math.Max(0, root.InitialPlayerMaxHp - result.Snapshot.PlayerMaxHp),
            result.Snapshot.RecoveredPlayerHp
                + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                    root.PostCombatRelicHeal,
                    SolverInterimResultOrdering.IsCompleteVictory(
                        result.BestNode.ActionCount,
                        result.Snapshot.AllEnemiesDead,
                        result.Snapshot.PlayerDead,
                        result.Snapshot.ProjectedPlayerHp),
                    result.Snapshot.PlayerHp,
                    result.Snapshot.PlayerMaxHp),
            StrategicBossHpRelief(root, policy),
            result.Snapshot.DeathSaveHpRestored) - result.Snapshot.StrategicHpCredit;

    /// <summary>
    /// Best strategic HP result any route could still reach from this root.
    /// </summary>
    /// <remarks>
    /// Once healing counts, zero is no longer the floor. Current HP is capped by max HP, so a route can at most
    /// heal back to full, which puts the floor at the HP the player was already missing when the fight started.
    /// Treating zero as the floor while a wounded player holds a heal would declare a route provably optimal
    /// when a strictly better one exists, and stop the extra searches that would have found it.
    /// </remarks>
    private static int ProvableStrategicHpFloor(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy)
        => -ActEndingBossPolicy.PersistentValueOfRecoveredHp(
            Math.Max(0, root.InitialPlayerMaxHp - root.InitialPlayerHp),
            StrategicBossHpRelief(root, policy));

    internal static bool CanAnySmartPotionQualify(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        bool potionFreeWon,
        int potionFreeHpDeficit)
        => MaximumSmartPotionUses(root, policy, potionFreeWon, potionFreeHpDeficit) > 0;

    internal static int MaximumSmartPotionUses(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        bool potionFreeWon,
        int potionFreeHpDeficit)
    {
        SearchablePotionSlotSnapshot[] allowedPotions = root.SearchablePotions
            .Where(potion => policy.PotionStrategy.AllowsExplicitUse(
                potion.Slot,
                potion.PotionId,
                SolverPotionPolicy.Smart,
                forceAllDisabled: false)
                && policy.PotionStrategy.Resolve(potion.Slot, potion.PotionId)
                    != SolverPotionDirective.Force)
            .ToArray();
        int generatedPotionCapacity = allowedPotions.Any(potion =>
            potion.PotionId == "ENTROPIC_BREW")
            ? root.PotionSlotCount
            : 0;
        int searchablePotionUses = allowedPotions.Length + generatedPotionCapacity;
        if (!potionFreeWon || policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            return searchablePotionUses;
        BossHpRelief bossHpRelief = StrategicBossHpRelief(root, policy);
        int paidPotionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
            SolverWeights.PotionMinimumHpSaved,
            bossHpRelief);
        // The reward credit is taken off a route once, so only the first paid potion gets the cheaper bar.
        int firstPaidPotionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
            PotionUsePolicy.ApplyReplacementCredit(
                SolverWeights.PotionMinimumHpSaved,
                1,
                root.PotionRewardOutlook.ReplacementHpCredit),
            bossHpRelief);
        int paidPotionCapacity = paidPotionHpRequired >= int.MaxValue / 4
            ? 0
            : Math.Max(0, potionFreeHpDeficit) < firstPaidPotionHpRequired
                ? 0
                : 1 + (Math.Max(0, potionFreeHpDeficit) - firstPaidPotionHpRequired) / paidPotionHpRequired;
        return Math.Min(
            searchablePotionUses,
            allowedPotions.Count(potion => potion.StrategicHpCost == 0)
                + paidPotionCapacity
                + (paidPotionCapacity > 0 ? generatedPotionCapacity : 0));
    }

    private static BossHpRelief StrategicBossHpRelief(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy)
        => ActEndingBossPolicy.ResolveStrategicHpRelief(
            root.BossHpRelief,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy);

    private static void MergeAuditTotals(
        SolverResult selected,
        params SolverResult[] searches)
    {
        if (searches.Length == 0)
            throw new ArgumentException("审计总量至少需要一个搜索结果。", nameof(searches));

        SearchRequestWorkSnapshot totals = AggregateAuditWork(
            searches.Select(AuditWorkContribution).ToArray());
        PopulateRequestWorkTotals(selected, totals);
        // This result spans an audit even when a future caller supplies one layer.
        // Preserve the historical coordinator-session classification.
        selected.SingleSessionSearch = false;
    }

    private static SearchSolverWorkContribution AuditWorkContribution(SolverResult result)
        => new(
            result.ExpandedNodes,
            result.TransitionCount,
            result.ChoiceBranchesEvaluated,
            result.TotalSearchElapsed,
            result.TotalWorkerAllocatedBytes,
            result.TotalGen0Collections,
            result.TotalGen1Collections,
            result.TotalGen2Collections,
            result.TotalGcPauseDuration,
            result.TotalMaxObservedGcPause);

    internal static SearchRequestWorkSnapshot AggregateAuditWork(
        params SearchSolverWorkContribution[] searches)
    {
        SearchRequestWorkTotals totals = new();
        foreach (SearchSolverWorkContribution search in searches)
            totals.Record(search);
        return totals.Snapshot();
    }

    private static void PopulateRequestWorkTotals(
        SolverResult result,
        SearchRequestWorkTotals requestWorkTotals)
    {
        PopulateRequestWorkTotals(result, requestWorkTotals.Snapshot());
        result.SearchWorkAttributions = requestWorkTotals.AttributionSnapshot();
    }

    private static void PopulateRequestWorkTotals(
        SolverResult result,
        SearchRequestWorkSnapshot totals)
    {
        result.TotalCycleReplayActions = totals.CycleReplayActions;
        result.SingleSessionSearch = totals.RecordedSolverCount == 1;
        result.TotalSearchElapsed = totals.Elapsed;
        result.TotalWorkerAllocatedBytes = totals.WorkerAllocatedBytes;
        result.TotalGen0Collections = SaturatingInt(totals.Gen0Collections);
        result.TotalGen1Collections = SaturatingInt(totals.Gen1Collections);
        result.TotalGen2Collections = SaturatingInt(totals.Gen2Collections);
        result.TotalGcPauseDuration = totals.GcPauseDuration;
        result.TotalMaxObservedGcPause = totals.MaxObservedGcPause;
        result.TotalExpandedNodes = totals.ExpandedNodes;
        result.TotalTransitionCount = totals.TransitionCount;
        result.TotalChoiceBranchesEvaluated = totals.ChoiceBranchesEvaluated;
    }

    private static int SaturatingInt(long value)
        => value >= int.MaxValue ? int.MaxValue : (int)value;

    private static void PopulateSingleSessionTotals(
        SolverResult result)
    {
        result.TotalSearchElapsed = result.Elapsed;
        result.TotalWorkerAllocatedBytes = result.WorkerAllocatedBytes;
        result.TotalGen0Collections = result.Gen0Collections;
        result.TotalGen1Collections = result.Gen1Collections;
        result.TotalGen2Collections = result.Gen2Collections;
        result.TotalGcPauseDuration = result.GcPauseDuration;
        result.TotalMaxObservedGcPause = result.MaxObservedGcPause;
        result.TotalExpandedNodes = result.ExpandedNodes;
        result.TotalTransitionCount = result.TransitionCount;
        result.TotalCycleReplayActions = result.CycleReplayActions;
        result.TotalChoiceBranchesEvaluated = result.ChoiceBranchesEvaluated;
    }
}
