using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;

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
        SearchRequestWorkTotals requestWorkTotals = new();
        policy = policy with { RequestWorkTotals = requestWorkTotals };
        SearchInteractionState? interaction = policy.Interaction;
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
        try
        {
            SolverResult result = SolveCore(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                enrichedProgressCallback,
                interaction == null ? null : PublishAdoptableResult);
            SolverResult selected = ResolveTakeoverResult(result, interaction) ?? result;
            if (interaction?.CurrentTakeoverRequest?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                && selected.ResultScope == SolverResultScope.SearchCompletion
                && currentCompleteAdoptableResult != null)
            {
                selected = currentCompleteAdoptableResult;
            }
            PopulateRequestWorkTotals(selected, requestWorkTotals);
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
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        Action<SolverResult>? interimResultCallback)
    {
        Stopwatch requestClock = Stopwatch.StartNew();
        SolverPotionPolicy? initialPotionPolicyOverride = policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
                ? SolverPotionPolicy.Disabled
                : null;
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
                });
            };
        }
        SmartLayerMemoryForecast memoryForecast = new();
        // One search profile drives primary search and all supplemental audits.
        // Beam 宽度增大
        // 不保证跨层候选仍是超集；未找到胜利时可用本层剩余预算进行一次窄 Beam 恢复。
        SolverSearchProfile profile = policy.Profile;
        if (policy.BudgetOverrideMilliseconds is { } deepBudget)
            profile = profile with { SoftTimeBudgetMilliseconds = deepBudget };
        if (root.IsActEndingBoss && profile.BeamWidth < 45)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] ACT_ENDING_BOSS_SEARCH_OVERRIDE " +
                $"beam={profile.BeamWidth}->45 reason=preserve_survival_routes");
            profile = profile with { BeamWidth = 45 };
        }
        // 一轮完整的深化搜索：主搜索（Smart 时先按无主动用药跑）＋补充审计。抬节点上限重搜时
        // 原样再走一遍，所以抽成一个本地函数；每一轮自带一只秒表，补充审计那边算剩余预算靠它。
        SolverResult? takeoverResult = null;
        bool passSettled = false;
        SolverResult RunSearchPass(SolverSearchProfile passProfile, Stopwatch passClock)
        {
            long passAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            long passTransitionsAtStart = policy.RequestWorkTotals?.Snapshot().TransitionCount ?? 0;
            SolverResult passResult = SolveWithNarrowBeamRecovery(
                root,
                policy,
                passProfile,
                cancellationToken,
                cancellationToken,
                (attemptProfile, attemptCancellationToken) => new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    attemptCancellationToken,
                    progressCallback,
                    attemptProfile,
                    potionPolicyOverride: initialPotionPolicyOverride).Solve());
            ObserveSmartLayerMemory(
                policy, memoryForecast, passAllocatedAtStart, passTransitionsAtStart,
                passResult, passProfile, completedPotionCount: 0);
            if (policy.MeasurePhasePerformance)
                policy.Diagnostics.Info(SolverDiagnostics.DescribeSearchPhasePerformance(passResult));
            passResult.SingleSessionSearch = true;
            PopulateSingleSessionTotals(passResult);
            interimResultCallback?.Invoke(passResult);
            if (ResolveTakeoverResult(passResult, policy.Interaction) is { } passTakeover)
            {
                takeoverResult = passTakeover;
                return passResult;
            }
            if (!policy.PotionStrategy.HasForcedDirectives)
            {
                if (HasReachedAcceptableBattleHpLoss(policy, passResult))
                {
                    passSettled = true;
                    return passResult;
                }
                passResult = RunSupplementalAudits(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    passProfile,
                    passClock,
                    passResult,
                    memoryForecast,
                    interimResultCallback);
            }
            return passResult;
        }

        SolverResult result = RunSearchPass(profile, requestClock);
        if (takeoverResult != null)
            return takeoverResult;
        // 打到可接受战损就收手那一条和改动之前一样直接返回，连 SEARCH_SESSION 都不打。
        if (passSettled || policy.FixedBudget)
            return result;
        result = EscalateSearchWhenNoVictory(
            root,
            policy,
            profile,
            requestClock,
            result,
            RunSearchPass,
            () => takeoverResult != null
                || passSettled
                || cancellationToken.IsCancellationRequested
                || policy.Interaction?.CurrentTakeoverRequest != null);
        if (takeoverResult != null)
            return takeoverResult;
        if (passSettled)
            return result;
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SEARCH_SESSION mode=single_anytime " +
            $"total_budget_ms={profile.SoftTimeBudgetMilliseconds}");
        return result;
    }

    private static SolverResult RunSupplementalAudits(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        Stopwatch requestClock,
        SolverResult primary,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback)
    {
        long remainingMilliseconds = profile.SoftTimeBudgetMilliseconds - requestClock.ElapsedMilliseconds;
        if (remainingMilliseconds <= 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SUPPLEMENTAL_AUDIT_BUDGET exhausted=true " +
                $"elapsed_ms={requestClock.ElapsedMilliseconds} " +
                $"budget_ms={profile.SoftTimeBudgetMilliseconds}");
            return primary;
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(remainingMilliseconds));
        SolverResult selected = primary;
        try
        {
            selected = AuditRequiredPotionUse(
                root,
                displayNames,
                battleDamage,
                policy,
                deadline.Token,
                progressCallback,
                profile,
                selected);
            if (ResolveTakeoverResult(selected, policy.Interaction) is { } requiredTakeoverResult)
                return requiredTakeoverResult;
            if (HasReachedAcceptableBattleHpLoss(policy, selected))
                return selected;
            selected = AuditSmartPotionUse(
                root,
                displayNames,
                battleDamage,
                policy,
                deadline.Token,
                cancellationToken,
                progressCallback,
                profile,
                selected,
                memoryForecast,
                interimResultCallback);
            if (HasReachedAcceptableBattleHpLoss(policy, selected))
                return selected;
            if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            {
                selected = AuditOpeningPowerUse(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    deadline.Token,
                    progressCallback,
                    profile,
                    selected);
                if (HasReachedAcceptableBattleHpLoss(policy, selected))
                    return selected;
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SUPPLEMENTAL_AUDIT_BUDGET exhausted=true " +
                $"elapsed_ms={requestClock.ElapsedMilliseconds} " +
                $"budget_ms={profile.SoftTimeBudgetMilliseconds} " +
                $"selected_potions={selected.PotionCount}");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return selected;
    }

    private static SolverResult AuditOpeningPowerUse(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult primary)
    {
        int primaryDeficit = StrategicHpDeficit(root, policy, primary);
        int maximumSmartPotionUses = policy.PotionPolicy == SolverPotionPolicy.Smart
            ? MaximumSmartPotionUses(root, policy, potionFreeWon: true, primaryDeficit)
            : Math.Max(1, primary.PotionCount);
        if (HasReachedProvablePrimaryQualityLowerBound(root, policy, primary)
            || policy.PotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                && battleDamage.PotionsUsedSoFar == 0)
            return primary;

        IReadOnlyList<PlanAction> openingPowers = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile)
            .BuildOpeningPowerActions();
        IReadOnlyList<PlanAction> openingPotions = policy.PotionPolicy == SolverPotionPolicy.Disabled
            || maximumSmartPotionUses == 0
            ? []
            : new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .BuildOpeningPotionActions();
        IReadOnlyList<PlanAction> generatedResourcePotions = openingPotions.Count == 0
            ? []
            : new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .SelectGeneratedResourcePotionActions(openingPotions);
        IReadOnlyList<PlanAction> openingResources = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile)
            .BuildOpeningResourceActions();
        List<(PlanAction Potion, PlanAction Power)> potionPowerPairs = [];
        foreach (PlanAction openingPotion in openingPotions)
        {
            IReadOnlyList<PlanAction> powers = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .BuildPowerActionsAfterPrefix([openingPotion]);
            foreach (PlanAction power in powers)
            {
                potionPowerPairs.Add((openingPotion, power));
                if (potionPowerPairs.Count == 4)
                    break;
            }
            if (potionPowerPairs.Count == 4)
                break;
        }
        if (openingPowers.Count == 0
            && potionPowerPairs.Count == 0
            && generatedResourcePotions.Count == 0
            && openingResources.Count == 0)
            return primary;

        List<SolverResult> searches = [primary];
        SolverResult selected = primary;
        foreach (PlanAction openingPower in openingPowers)
        {
            SolverResult posterior = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile,
                fixedPrefixActions: [openingPower]).Solve();
            if (posterior.ResultScope != SolverResultScope.SearchCompletion)
                return posterior;

            posterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(posterior);
            searches.Add(posterior);
            if (HasReachedAcceptableBattleHpLoss(policy, posterior))
            {
                MergeAuditTotals(posterior, searches.ToArray());
                return posterior;
            }

            bool posteriorWon = posterior.Snapshot.AllEnemiesDead
                && !posterior.Snapshot.PlayerDead
                && posterior.Snapshot.ProjectedPlayerHp > 0;
            int posteriorDeficit = StrategicHpDeficit(root, policy, posterior);
            if (IsBetterCompletedResult(root, policy, posterior, selected))
            {
                selected = posterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_POWER_POSTERIOR card={openingPower.CardId} " +
                $"won={posteriorWon} hp_deficit={posteriorDeficit} " +
                $"selected={ReferenceEquals(selected, posterior)}");

            PlanAction? offensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile)
                .BuildOpeningPowerOffensiveFollowUp(openingPower);
            if (offensiveFollowUp == null)
                continue;

            SolverResult linkedPosterior = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile,
                fixedPrefixActions: [openingPower, offensiveFollowUp]).Solve();
            if (linkedPosterior.ResultScope != SolverResultScope.SearchCompletion)
                return linkedPosterior;

            linkedPosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(linkedPosterior);
            searches.Add(linkedPosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, linkedPosterior))
            {
                MergeAuditTotals(linkedPosterior, searches.ToArray());
                return linkedPosterior;
            }

            bool linkedWon = linkedPosterior.Snapshot.AllEnemiesDead
                && !linkedPosterior.Snapshot.PlayerDead
                && linkedPosterior.Snapshot.ProjectedPlayerHp > 0;
            int linkedDeficit = StrategicHpDeficit(root, policy, linkedPosterior);
            if (IsBetterCompletedResult(root, policy, linkedPosterior, selected))
            {
                selected = linkedPosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_POWER_LINK_POSTERIOR " +
                $"cards={openingPower.CardId}+{offensiveFollowUp.CardId} " +
                $"won={linkedWon} hp_deficit={linkedDeficit} " +
                $"selected={ReferenceEquals(selected, linkedPosterior)}");
        }

        foreach (PlanAction openingResource in openingResources)
        {
            PlanAction? defensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile)
                .BuildOpeningDefensiveFollowUp([openingResource]);
            if (defensiveFollowUp == null)
                continue;

            SolverResult resourceDefensePosterior = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile,
                fixedPrefixActions: [openingResource, defensiveFollowUp]).Solve();
            if (resourceDefensePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return resourceDefensePosterior;

            resourceDefensePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(resourceDefensePosterior);
            searches.Add(resourceDefensePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, resourceDefensePosterior))
            {
                MergeAuditTotals(resourceDefensePosterior, searches.ToArray());
                return resourceDefensePosterior;
            }

            if (IsBetterCompletedResult(root, policy, resourceDefensePosterior, selected))
                selected = resourceDefensePosterior;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_RESOURCE_DEFENSE_POSTERIOR " +
                $"cards={openingResource.CardId}+{defensiveFollowUp.CardId} " +
                $"won={resourceDefensePosterior.Snapshot.AllEnemiesDead && !resourceDefensePosterior.Snapshot.PlayerDead} " +
                $"hp_deficit={StrategicHpDeficit(root, policy, resourceDefensePosterior)} " +
                $"selected={ReferenceEquals(selected, resourceDefensePosterior)}");
        }

        foreach (PlanAction openingPotion in generatedResourcePotions)
        {
            SolverResult? resourcePosterior = SolveOptionalPotionPosterior(
                new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: 1,
                    fixedPrefixActions: [openingPotion]),
                policy,
                $"POTION_RESOURCE_POSTERIOR potion={openingPotion.PotionId}");
            if (resourcePosterior == null)
                continue;
            if (resourcePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return resourcePosterior;

            resourcePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(resourcePosterior);
            searches.Add(resourcePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, resourcePosterior))
            {
                MergeAuditTotals(resourcePosterior, searches.ToArray());
                return resourcePosterior;
            }

            bool resourceWon = resourcePosterior.Snapshot.AllEnemiesDead
                && !resourcePosterior.Snapshot.PlayerDead
                && resourcePosterior.Snapshot.ProjectedPlayerHp > 0;
            int resourceDeficit = StrategicHpDeficit(root, policy, resourcePosterior);
            if (IsBetterCompletedResult(root, policy, resourcePosterior, selected))
            {
                selected = resourcePosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_RESOURCE_POSTERIOR " +
                $"potion={openingPotion.PotionId} card={openingPotion.Choice!.Cards[0].CardId} " +
                $"won={resourceWon} hp_deficit={resourceDeficit} " +
                $"selected={ReferenceEquals(selected, resourcePosterior)}");
        }

        if (HasReachedProvablePrimaryQualityLowerBound(root, policy, selected)
            && selected.PotionCount <= 1)
        {
            MergeAuditTotals(selected, searches.ToArray());
            return selected;
        }

        foreach ((PlanAction openingPotion, PlanAction postPotionPower) in potionPowerPairs)
        {
            PlanAction[] jointPrefix = [openingPotion, postPotionPower];
            SolverResult? jointPosterior = SolveOptionalPotionPosterior(
                new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses,
                    fixedPrefixActions: jointPrefix),
                policy,
                $"POTION_POWER_POSTERIOR potion={openingPotion.PotionId} power={postPotionPower.CardId}");
            if (jointPosterior == null)
                continue;
            if (jointPosterior.ResultScope != SolverResultScope.SearchCompletion)
                return jointPosterior;

            jointPosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(jointPosterior);
            searches.Add(jointPosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, jointPosterior))
            {
                MergeAuditTotals(jointPosterior, searches.ToArray());
                return jointPosterior;
            }

            bool jointWon = jointPosterior.Snapshot.AllEnemiesDead
                && !jointPosterior.Snapshot.PlayerDead
                && jointPosterior.Snapshot.ProjectedPlayerHp > 0;
            int jointDeficit = StrategicHpDeficit(root, policy, jointPosterior);
            int comparisonDeficit = StrategicHpDeficit(root, policy, selected);
            if (IsBetterCompletedResult(root, policy, jointPosterior, selected))
            {
                selected = jointPosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POWER_POSTERIOR " +
                $"potion={openingPotion.PotionId} power={postPotionPower.CardId} " +
                $"won={jointWon} hp_deficit={jointDeficit} " +
                $"selected={ReferenceEquals(selected, jointPosterior)}");

            if (!jointWon
                || HasReachedProvablePrimaryQualityLowerBound(root, policy, jointPosterior)
                || jointDeficit > comparisonDeficit + 1)
            {
                continue;
            }

            PlanAction? defensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .BuildOpeningDefensiveFollowUp(jointPrefix);
            if (defensiveFollowUp == null)
                continue;

            SolverResult? defensivePosterior = SolveOptionalPotionPosterior(
                new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses,
                    fixedPrefixActions: [openingPotion, postPotionPower, defensiveFollowUp]),
                policy,
                $"POTION_POWER_DEFENSIVE_POSTERIOR potion={openingPotion.PotionId} " +
                $"power={postPotionPower.CardId} follow_up={defensiveFollowUp.CardId}");
            if (defensivePosterior == null)
                continue;
            if (defensivePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return defensivePosterior;

            defensivePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(defensivePosterior);
            searches.Add(defensivePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, defensivePosterior))
            {
                MergeAuditTotals(defensivePosterior, searches.ToArray());
                return defensivePosterior;
            }

            bool defensiveWon = defensivePosterior.Snapshot.AllEnemiesDead
                && !defensivePosterior.Snapshot.PlayerDead
                && defensivePosterior.Snapshot.ProjectedPlayerHp > 0;
            int defensiveDeficit = StrategicHpDeficit(root, policy, defensivePosterior);
            if (IsBetterCompletedResult(root, policy, defensivePosterior, selected))
            {
                selected = defensivePosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POWER_DEFENSIVE_POSTERIOR " +
                $"potion={openingPotion.PotionId} power={postPotionPower.CardId} " +
                $"follow_up={defensiveFollowUp.CardId} won={defensiveWon} " +
                $"hp_deficit={defensiveDeficit} selected={ReferenceEquals(selected, defensivePosterior)}");

            if (HasReachedProvablePrimaryQualityLowerBound(root, policy, defensivePosterior))
                break;
        }

        MergeAuditTotals(selected, searches.ToArray());
        return selected;
    }

    private static SolverResult AuditRequiredPotionUse(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult primary)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.RequireAtLeastOne
            || battleDamage.PotionsUsedSoFar > 0
            || primary.PotionCount <= 1)
        {
            return primary;
        }

        policy.Diagnostics.Info(
            $"[CombatSolver/Test] REQUIRED_POTION_AUDIT start potion_count={primary.PotionCount} " +
            $"reported_saved={primary.PotionHpSaved} required={primary.PotionHpRequired}");
        SolverResult potionFree = new CombatBeamSolver(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback,
            profile,
            SolverPotionPolicy.Disabled).Solve();
        if (potionFree.ResultScope != SolverResultScope.SearchCompletion)
            return potionFree;

        potionFree.SingleSessionSearch = true;
        PopulateSingleSessionTotals(potionFree);

        bool potionFreeWon = IsCompleteVictory(potionFree);
        if (!potionFreeWon)
        {
            List<SolverResult> searches = [primary, potionFree];
            SolverResult selected = primary;
            IReadOnlyList<PlanAction> openingPotions = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: primary.PotionCount)
                .BuildPreferredOpeningPotionActions();
            foreach (PlanAction openingPotion in openingPotions)
            {
                SolverResult posterior = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: primary.PotionCount,
                    fixedPrefixActions: [openingPotion]).Solve();
                if (posterior.ResultScope != SolverResultScope.SearchCompletion)
                    return posterior;

                posterior.SingleSessionSearch = true;
                PopulateSingleSessionTotals(posterior);
                searches.Add(posterior);
                if (HasReachedAcceptableBattleHpLoss(policy, posterior))
                {
                    MergeAuditTotals(posterior, searches.ToArray());
                    return posterior;
                }

                bool posteriorWon = posterior.Snapshot.AllEnemiesDead
                    && !posterior.Snapshot.PlayerDead
                    && posterior.Snapshot.ProjectedPlayerHp > 0;
                int posteriorDeficit = StrategicHpDeficit(root, policy, posterior);
                if (IsBetterCompletedResult(root, policy, posterior, selected))
                {
                    selected = posterior;
                }
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] REQUIRED_MULTI_POTION_POSTERIOR " +
                    $"potion={openingPotion.PotionId} target={openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                    $"won={posteriorWon} hp_deficit={posteriorDeficit} " +
                    $"selected={ReferenceEquals(selected, posterior)}");

                if (primary.PotionCount != 2)
                    continue;

                IReadOnlyList<PlanAction> secondPotions = new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        cancellationToken,
                        progressCallback,
                        profile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount)
                    .BuildPreferredPotionActionsAfterPrefix([openingPotion]);
                foreach (PlanAction secondPotion in secondPotions)
                {
                    SolverResult pairPosterior = new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        cancellationToken,
                        progressCallback,
                        profile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount,
                        fixedPrefixActions: [openingPotion, secondPotion]).Solve();
                    if (pairPosterior.ResultScope != SolverResultScope.SearchCompletion)
                        return pairPosterior;

                    pairPosterior.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(pairPosterior);
                    searches.Add(pairPosterior);
                    if (HasReachedAcceptableBattleHpLoss(policy, pairPosterior))
                    {
                        MergeAuditTotals(pairPosterior, searches.ToArray());
                        return pairPosterior;
                    }

                    bool pairWon = pairPosterior.Snapshot.AllEnemiesDead
                        && !pairPosterior.Snapshot.PlayerDead
                        && pairPosterior.Snapshot.ProjectedPlayerHp > 0;
                    int pairDeficit = StrategicHpDeficit(root, policy, pairPosterior);
                    if (IsBetterCompletedResult(root, policy, pairPosterior, selected))
                    {
                        selected = pairPosterior;
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] REQUIRED_POTION_PAIR_POSTERIOR " +
                        $"first={openingPotion.PotionId}:{openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"second={secondPotion.PotionId}:{secondPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"won={pairWon} hp_deficit={pairDeficit} " +
                        $"selected={ReferenceEquals(selected, pairPosterior)}");

                    int selectedDeficit = StrategicHpDeficit(root, policy, selected);
                    if (!pairWon || pairDeficit > selectedDeficit + 1)
                        continue;

                    PlanAction[] pairPrefix = [openingPotion, secondPotion];
                    PlanAction? defensiveFollowUp = new CombatBeamSolver(
                            root,
                            displayNames,
                            battleDamage,
                            policy,
                            cancellationToken,
                            progressCallback,
                            profile,
                            SolverPotionPolicy.RequireAtLeastOne,
                            maximumPotionUses: primary.PotionCount)
                        .BuildOpeningDefensiveFollowUp(pairPrefix);
                    if (defensiveFollowUp == null)
                        continue;

                    SolverResult defensivePosterior = new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        cancellationToken,
                        progressCallback,
                        profile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount,
                        fixedPrefixActions: [openingPotion, secondPotion, defensiveFollowUp]).Solve();
                    if (defensivePosterior.ResultScope != SolverResultScope.SearchCompletion)
                        return defensivePosterior;

                    defensivePosterior.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(defensivePosterior);
                    searches.Add(defensivePosterior);
                    if (HasReachedAcceptableBattleHpLoss(policy, defensivePosterior))
                    {
                        MergeAuditTotals(defensivePosterior, searches.ToArray());
                        return defensivePosterior;
                    }

                    bool defensiveWon = defensivePosterior.Snapshot.AllEnemiesDead
                        && !defensivePosterior.Snapshot.PlayerDead
                        && defensivePosterior.Snapshot.ProjectedPlayerHp > 0;
                    int defensiveDeficit = StrategicHpDeficit(root, policy, defensivePosterior);
                    if (IsBetterCompletedResult(root, policy, defensivePosterior, selected))
                    {
                        selected = defensivePosterior;
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] REQUIRED_POTION_PAIR_DEFENSIVE_POSTERIOR " +
                        $"first={openingPotion.PotionId}:{openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"second={secondPotion.PotionId}:{secondPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"follow_up={defensiveFollowUp.CardId} won={defensiveWon} " +
                        $"hp_deficit={defensiveDeficit} " +
                        $"selected={ReferenceEquals(selected, defensivePosterior)}");
                }
            }

            MergeAuditTotals(selected, searches.ToArray());
            policy.Diagnostics.Info(
                "[CombatSolver/Test] REQUIRED_POTION_AUDIT result potion_free_won=False " +
                $"selected={(ReferenceEquals(selected, primary) ? "multi_potion_rescue" : "opening_potion_posterior")}");
            return selected;
        }

        // The candidates this baseline is compared against are ranked on the strategic axis, so the
        // baseline has to be measured on it too; the raw sum here predated healing counting at all.
        PotionFreePolicyBaseline baseline = new(
            Won: true,
            HpDeficit: StrategicHpDeficit(root, policy, potionFree),
            PlayerHp: potionFree.Snapshot.PlayerHp,
            CombatEndedTurn: potionFree.CombatEndedTurn);
        SolverResult audited = new CombatBeamSolver(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback,
            profile,
            SolverPotionPolicy.RequireAtLeastOne,
            baseline,
            maximumPotionUses: 1).Solve();
        if (audited.ResultScope != SolverResultScope.SearchCompletion)
            return audited;

        audited.SingleSessionSearch = true;
        PopulateSingleSessionTotals(audited);
        SolverResult auditedSelection = IsBetterPotionPolicyResult(
            root,
            policy,
            audited,
            primary)
                ? audited
                : primary;
        MergeAuditTotals(auditedSelection, primary, potionFree, audited);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] REQUIRED_POTION_AUDIT result potion_free_won=True " +
            $"baseline_hp_deficit={baseline.HpDeficit} " +
            $"selected={(ReferenceEquals(auditedSelection, audited) ? "single_potion_audit" : "primary")} " +
            $"selected_potion_count={auditedSelection.PotionCount} " +
            $"selected_saved={auditedSelection.PotionHpSaved} " +
            $"selected_required={auditedSelection.PotionHpRequired}");
        return auditedSelection;
    }

    private static SolverResult AuditSmartPotionUse(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken searchCancellationToken,
        CancellationToken callerCancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult primary,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return primary;
        try
        {
            return SearchSmartPotionGradient(
                root,
                displayNames,
                battleDamage,
                policy,
                searchCancellationToken,
                callerCancellationToken,
                progressCallback,
                profile,
                primary,
                memoryForecast,
                interimResultCallback);
        }
        catch (PotionPolicyUnsatisfiedException)
            when (policy.PotionPolicy == SolverPotionPolicy.Smart
                && !policy.PotionStrategy.HasForcedDirectives)
        {
            policy.Diagnostics.Info(
                "[CombatSolver/Test] SMART_POTION_AUDIT result optional_route_missing=true selected=primary");
            return primary;
        }
    }

    private static SolverResult SearchSmartPotionGradient(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken searchCancellationToken,
        CancellationToken callerCancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult potionFree,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback)
    {
        if (potionFree.ExplicitPotionCount != 0)
            throw new InvalidOperationException("Smart 梯度搜索必须从无主动用药结果开始。");

        bool potionFreeWon = potionFree.Snapshot.AllEnemiesDead
            && !potionFree.Snapshot.PlayerDead
            && potionFree.Snapshot.ProjectedPlayerHp > 0;
        int potionFreeDeficit = StrategicHpDeficit(root, policy, potionFree);
        int maximumPotionUses = MaximumSmartPotionUses(
            root,
            policy,
            potionFreeWon,
            potionFreeDeficit);
        if (maximumPotionUses == 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
                $"stop=no_potion_acceptable hp_deficit={potionFreeDeficit} maximum=0");
            return potionFree;
        }

        PotionFreePolicyBaseline baseline = new(
            potionFreeWon,
            potionFreeDeficit,
            potionFree.Snapshot.PlayerHp,
            potionFree.CombatEndedTurn);
        List<SolverResult> searches = [potionFree];
        SolverResult selected = potionFree;
        bool deadlineExpired = false;
        bool acceptablePotionLayerFound = false;
        for (int potionCount = 1; potionCount <= maximumPotionUses; potionCount++)
        {
            if (searchCancellationToken.IsCancellationRequested)
            {
                callerCancellationToken.ThrowIfCancellationRequested();
                deadlineExpired = true;
                break;
            }
            try
            {
                ReclaimAtPotionGradientBoundary(
                    policy,
                    searchCancellationToken,
                    progressCallback,
                    profile,
                    potionFree,
                    memoryForecast,
                    potionCount - 1,
                    potionCount);
            }
            catch (OperationCanceledException)
                when (searchCancellationToken.IsCancellationRequested
                    && !callerCancellationToken.IsCancellationRequested)
            {
                deadlineExpired = true;
                break;
            }
            PrimarySearchIncumbent? primaryIncumbent = BuildPrimarySearchIncumbent(
                root,
                policy,
                selected);
            long layerAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            long layerTransitionsAtStart = policy.RequestWorkTotals?.Snapshot().TransitionCount ?? 0;
            SolverResult? observedLayerResult = null;
            SolverResult candidate;
            try
            {
                candidate = SolveWithNarrowBeamRecovery(
                    root,
                    policy,
                    profile,
                    searchCancellationToken,
                    callerCancellationToken,
                    (attemptProfile, attemptCancellationToken) => new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        attemptCancellationToken,
                        progressCallback,
                        attemptProfile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        baseline,
                        maximumPotionUses: potionCount,
                        minimumPotionUses: potionCount,
                        primaryIncumbent: primaryIncumbent).Solve());
                observedLayerResult = candidate;
            }
            catch (PotionPolicyUnsatisfiedException)
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} route_missing=true");
                continue;
            }
            catch (OperationCanceledException)
                when (searchCancellationToken.IsCancellationRequested
                    && !callerCancellationToken.IsCancellationRequested)
            {
                deadlineExpired = true;
                break;
            }
            finally
            {
                // Request totals include a solver that failed or was canceled. Use its actual
                // interval, never the selected route's work paired with another layer's bytes.
                ObserveSmartLayerMemory(
                    policy, memoryForecast, layerAllocatedAtStart, layerTransitionsAtStart,
                    observedLayerResult, profile, potionCount);
            }
            if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                return candidate;

            candidate.SingleSessionSearch = true;
            PopulateSingleSessionTotals(candidate);
            searches.Add(candidate);
            interimResultCallback?.Invoke(candidate);

            bool candidateWon = IsCompleteVictory(candidate);
            int candidateDeficit = StrategicHpDeficit(root, policy, candidate);
            int hpSaved = potionFreeWon
                ? Math.Max(0, potionFreeDeficit - candidateDeficit)
                : candidateWon
                    ? Math.Max(0, candidate.Snapshot.PlayerHp - potionFree.Snapshot.PlayerHp)
                    : 0;
            int hpRequired = SmartPotionHpRequired(root, policy, candidate);
            bool protectsLoot = policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                && candidate.OutstandingStolenResource < potionFree.OutstandingStolenResource;
            bool acceptable = IsSmartPotionGradientCandidateAcceptable(
                potionFreeWon,
                candidateWon,
                hpSaved,
                hpRequired,
                protectsLoot);
            bool improvesSelection = acceptable && (policy.TheftPolicy != SolverTheftPolicy.PreserveResources
                || IsBetterCompletedResult(root, policy, candidate, selected));
            if (improvesSelection)
            {
                candidate.PotionHpSaved = hpSaved;
                candidate.PotionHpRequired = hpRequired;
                selected = candidate;
                acceptablePotionLayerFound = true;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} " +
                $"won={candidateWon} hp_deficit={candidateDeficit} saved={hpSaved} " +
                $"required={hpRequired} protects_loot={protectsLoot} acceptable={acceptable} " +
                $"selected={improvesSelection} " +
                $"expanded={candidate.ExpandedNodes} transitions={candidate.TransitionCount} " +
                $"choice_branches={candidate.ChoiceBranchesEvaluated} " +
                $"elapsed_ms={candidate.Elapsed.TotalMilliseconds:F1} " +
                $"allocated_bytes={candidate.WorkerAllocatedBytes} " +
                $"incumbent_deficit={primaryIncumbent?.StrategicHpDeficit.ToString() ?? "-"} " +
                $"incumbent_turn={primaryIncumbent?.CombatEndedTurn.ToString() ?? "-"} " +
                $"incumbent_pruned={candidate.PrimaryIncumbentBranchesPruned} " +
                $"incumbent_updates={candidate.PrimaryIncumbentUpdates}");
            if (acceptable && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, selected.OutstandingStolenResource))
                break;
        }

        callerCancellationToken.ThrowIfCancellationRequested();
        MergeAuditTotals(selected, [.. searches]);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
            $"stop={(deadlineExpired ? "deadline" : acceptablePotionLayerFound ? "threshold_met" : "complete")} " +
            $"maximum={maximumPotionUses} " +
            $"selected_potions={selected.PotionCount}");
        return selected;
    }

    internal static bool IsSmartPotionGradientCandidateAcceptable(
        bool potionFreeWon,
        bool candidateWon,
        int hpSaved,
        int hpRequired,
        bool protectsLoot)
        => candidateWon
            && (!potionFreeWon || hpSaved >= hpRequired || protectsLoot);

    private static void ObserveSmartLayerMemory(
        SearchPolicySnapshot policy,
        SmartLayerMemoryForecast forecast,
        long processAllocatedAtStart,
        long transitionsAtStart,
        SolverResult? result,
        SolverSearchProfile profile,
        int completedPotionCount)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return;
        long processAllocated = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: false) - processAllocatedAtStart);
        long transitions = Math.Max(
            0,
            (policy.RequestWorkTotals?.Snapshot().TransitionCount ?? 0) - transitionsAtStart);
        // A fixed node budget is a comparable work window for the next layer using this same
        // profile. A timed-out or interrupted layer can understate that window, so keep the
        // optional reset conservative until a complete observation is available again.
        bool usableSample = result is { ResultScope: SolverResultScope.SearchCompletion }
            && result.BoundaryReason != SearchBoundaryReason.TimeLimit
            && result.Elapsed.TotalMilliseconds < profile.SoftTimeBudgetMilliseconds;
        forecast.Observe(processAllocated, transitions, usableSample);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_LAYER_MEMORY_SAMPLE layer={completedPotionCount} " +
            $"process_allocated_bytes={processAllocated} transitions={transitions} " +
            $"sample_usable={usableSample.ToString().ToLowerInvariant()} " +
            $"boundary={result?.BoundaryReason.ToString() ?? "incomplete"} " +
            $"bytes_per_transition_high_water={forecast.BytesPerTransitionHighWater:F1} " +
            $"prediction_error_high_water={forecast.UnderpredictionHighWater:F3}");
    }

    private static void ReclaimAtPotionGradientBoundary(
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult totalsCarrier,
        SmartLayerMemoryForecast forecast,
        int completedPotionCount,
        int nextPotionCount)
    {
        SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
        cancellationToken.ThrowIfCancellationRequested();
        SmartLayerMemoryDecision decision = forecast.Decide(
            signal.IsEnabled,
            signal.HasUnexpectedNoGcLoss(),
            signal.AllocatedBytes,
            signal.RemainingBytes,
            signal.AllocationLimitBytes);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] POTION_GRADIENT_MEMORY_DECISION " +
            $"completed_layer={completedPotionCount} next_layer={nextPotionCount} " +
            $"reclaim={decision.ShouldReclaim.ToString().ToLowerInvariant()} reason={decision.Reason} " +
            $"forecast_bytes={decision.ForecastBytes} remaining_bytes={decision.RemainingBytes} " +
            $"observations={forecast.ObservationCount} minimum_transition_growth=2 allocation_safety_factor=1.5");
        if (!decision.ShouldReclaim)
            return;

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        SearchGcLifecycleSnapshot lifecycleBefore = signal.CaptureGcLifecycle();
        Stopwatch stopwatch = Stopwatch.StartNew();
        progressCallback?.Invoke(new SolverProgress(
            totalsCarrier.StartTurnNumber,
            totalsCarrier.StartTurnNumber + Math.Max(0, totalsCarrier.SearchedTurns - 1),
            totalsCarrier.SearchedTurns,
            PlayDepth: 0,
            // A memory reset is a coordinator-owned interval between solvers. Publish a
            // zero-based interval so the request progress accumulator closes the preceding
            // solver exactly once and does not count potionFree again before every layer.
            ExpandedNodes: 0,
            ReviewedWorldlines: 0,
            MaxNodes: profile.MaxExpandedNodes,
            FrontierNodes: 0,
            EndedNodes: 1,
            ElapsedMilliseconds: 0,
            Phase: "切换用药路线，正在整理内存"));
        long pressureBefore = signal.AllocatedBytes;
        long limitBefore = signal.AllocationLimitBytes;
        try
        {
            signal.ReclaimAndContinue(cancellationToken, "smart_potion_layer");
        }
        finally
        {
            // ReclaimWithinSearch can observe a deadline after completing its blocking Gen2.
            // Retain that completed work in request totals even when cancellation then unwinds.
            stopwatch.Stop();
            TimeSpan gcPause = GC.GetTotalPauseDuration() - pauseBefore;
            TimeSpan maxObservedGcPause = signal.LastReclaimMaxObservedGcPause;
            long allocatedBytes = Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
            int gen0Collections = GC.CollectionCount(0) - gen0Before;
            int gen1Collections = GC.CollectionCount(1) - gen1Before;
            int gen2Collections = GC.CollectionCount(2) - gen2Before;
            totalsCarrier.TotalWorkerAllocatedBytes = checked(
                totalsCarrier.TotalWorkerAllocatedBytes
                + allocatedBytes);
            totalsCarrier.TotalGen0Collections += gen0Collections;
            totalsCarrier.TotalGen1Collections += gen1Collections;
            totalsCarrier.TotalGen2Collections += gen2Collections;
            totalsCarrier.TotalGcPauseDuration += gcPause;
            if (maxObservedGcPause > totalsCarrier.TotalMaxObservedGcPause)
                totalsCarrier.TotalMaxObservedGcPause = maxObservedGcPause;
            totalsCarrier.TotalSearchElapsed += stopwatch.Elapsed;
            policy.RequestWorkTotals?.RecordCoordinatorOverhead(
                stopwatch.Elapsed,
                allocatedBytes,
                gen0Collections,
                gen1Collections,
                gen2Collections,
                gcPause,
                maxObservedGcPause);

            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_GRADIENT_MEMORY_RESET " +
                $"completed_layer={completedPotionCount} next_layer={nextPotionCount} " +
                $"allocated_before={pressureBefore} limit_before={limitBefore} " +
                $"allocated_after={signal.AllocatedBytes} limit_after={signal.AllocationLimitBytes} " +
                $"gc_pause_ms={gcPause.TotalMilliseconds:F1} " +
                $"max_observed_gc_pause_ms={maxObservedGcPause.TotalMilliseconds:F1} " +
                signal.CaptureGcLifecycle().DeltaFrom(lifecycleBefore).ToDiagnosticString() + " " +
                $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                $"canceled={cancellationToken.IsCancellationRequested.ToString().ToLowerInvariant()}");
        }
    }

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
    {
        int recovery = TheftEncounterStrategy.CompareRecovery(theftPolicy,
            candidate.Won, candidate.OutstandingStolenResource, current.Won, current.OutstandingStolenResource);
        if (recovery != 0)
            return recovery < 0;
        int primaryQuality = SolverInterimResultOrdering.ComparePrimaryQuality(
            candidate.Won,
            candidate.StrategicHpDeficit,
            candidate.CombatEndedTurn,
            current.Won,
            current.StrategicHpDeficit,
            current.CombatEndedTurn,
            candidate.GrowthHpCredit,
            current.GrowthHpCredit,
            candidate.GrowthRewardCount,
            current.GrowthRewardCount);
        if (primaryQuality != 0)
            return primaryQuality < 0;
        if (theftPolicy == SolverTheftPolicy.PreserveResources
            && candidate.OutstandingStolenResource != current.OutstandingStolenResource)
        {
            return candidate.OutstandingStolenResource < current.OutstandingStolenResource;
        }
        if (candidate.ProjectedBattlePotionCount != current.ProjectedBattlePotionCount)
        {
            return candidate.ProjectedBattlePotionCount
                < current.ProjectedBattlePotionCount;
        }
        return candidate.Score > current.Score;
    }

    private static int CompareCompletedResultPrimaryQuality(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        int recovery = TheftEncounterStrategy.CompareRecovery(policy.TheftPolicy,
            IsCompleteVictory(candidate), candidate.OutstandingStolenResource,
            IsCompleteVictory(current), current.OutstandingStolenResource);
        if (recovery != 0)
            return recovery;
        return SolverInterimResultOrdering.ComparePrimaryQuality(
            IsCompleteVictory(candidate),
            StrategicHpDeficit(root, policy, candidate),
            candidate.CombatEndedTurn,
            IsCompleteVictory(current),
            StrategicHpDeficit(root, policy, current),
            current.CombatEndedTurn,
            candidate.Snapshot.StrategyGoalHpCredit,
            current.Snapshot.StrategyGoalHpCredit,
            candidate.Snapshot.StrategyGoalCount,
            current.Snapshot.StrategyGoalCount);
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
            && result.PotionCount == policy.MinimumRequiredPotionUses(result.BattlePotionsUsedSoFar)
            && policy.PotionStrategy.EvaluateForcedUses(result.BestNode.Actions, renewablePotionShapedRock: false).AllForcedUsesSatisfied
            && HasReachedAcceptableBattleHpLoss(
            IsCompleteVictory(result),
            result.ProjectedBattleHpLost,
            policy.AcceptableBattleHpLoss);

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
        if (policy.EffectiveHasGrowthTargets || policy.RelicTargets.Count > 0 || !IsCompleteVictory(result) || result.CombatEndedTurn is not { } combatEndedTurn)
            return null;
        return new PrimarySearchIncumbent(
            StrategicHpDeficit(root, policy, result),
            combatEndedTurn);
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
        int ambergrisCount = result.BestNode.Actions.Count(action =>
            action.Kind == PlanActionKind.UsePotion
            && string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal));
        int strategicHpCost = PotionUsePolicy.EffectiveStrategicHpCost(
            result.PotionStrategicCostByTurn.Values.Sum(),
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
            result.Snapshot.DeathSaveRelicHpRestored) - result.Snapshot.StrategicHpCredit;

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
                forceAllDisabled: false))
            .ToArray();
        if (!potionFreeWon || policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            return allowedPotions.Length;
        int paidPotionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
            SolverWeights.PotionMinimumHpSaved,
            StrategicBossHpRelief(root, policy));
        int paidPotionCapacity = paidPotionHpRequired >= int.MaxValue / 4
            ? 0
            : Math.Max(0, potionFreeHpDeficit) / paidPotionHpRequired;
        return Math.Min(
            allowedPotions.Length,
            allowedPotions.Count(potion => potion.StrategicHpCost == 0) + paidPotionCapacity);
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
        => PopulateRequestWorkTotals(result, requestWorkTotals.Snapshot());

    private static void PopulateRequestWorkTotals(
        SolverResult result,
        SearchRequestWorkSnapshot totals)
    {
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
        result.TotalChoiceBranchesEvaluated = result.ChoiceBranchesEvaluated;
    }
}
