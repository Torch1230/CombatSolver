using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal static class CombatSearchCoordinator
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
        SolverSearchProfile shortProfile = policy.ShortProfile;
        if (policy.ShortBudgetOverrideMilliseconds is { } shortBudget)
            shortProfile = shortProfile with { SoftTimeBudgetMilliseconds = shortBudget };
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
        if (policy.ForceShortOnly)
        {
            SolverResult shortResult = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                shortProfile,
                potionPolicyOverride: initialPotionPolicyOverride).Solve();
            PopulateSingleSessionTotals(shortResult, shortProfile.SoftTimeBudgetMilliseconds, deepTriggered: false);
            interimResultCallback?.Invoke(shortResult);
            if (ResolveTakeoverResult(shortResult, policy.Interaction) is { } shortTakeoverResult)
                return shortTakeoverResult;
            if (!policy.PotionStrategy.HasForcedDirectives)
            {
                shortResult = RunSupplementalAudits(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    shortProfile,
                    shortCheckpointMilliseconds: null,
                    requestClock,
                    shortResult,
                    interimResultCallback);
            }
            if (policy.MeasurePhasePerformance)
                policy.Diagnostics.Info(SolverDiagnostics.DescribeSearchPhasePerformance(shortResult));
            return shortResult;
        }

        // 普通搜索只建立一次根状态。深化宽度从一开始就是候选超集；短预算仅作为
        // UI/统计检查点。搜索空间在检查点前耗尽时会自然提前返回，否则原地继续，
        // 不再从根重复分叉、回放并保留两套模拟图。
        SolverSearchProfile deepProfile = policy.DeepProfile;
        if (policy.DeepBudgetOverrideMilliseconds is { } deepBudget)
            deepProfile = deepProfile with { SoftTimeBudgetMilliseconds = deepBudget };
        if (root.IsActEndingBoss && deepProfile.BeamWidth < 45)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] ACT_ENDING_BOSS_SEARCH_OVERRIDE " +
                $"beam={deepProfile.BeamWidth}->45 reason=preserve_survival_routes");
            deepProfile = deepProfile with { BeamWidth = 45 };
        }
        SolverResult result = new CombatBeamSolver(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback,
            deepProfile,
            shortCheckpointMilliseconds: shortProfile.SoftTimeBudgetMilliseconds,
            potionPolicyOverride: initialPotionPolicyOverride).Solve();
        if (policy.MeasurePhasePerformance)
            policy.Diagnostics.Info(SolverDiagnostics.DescribeSearchPhasePerformance(result));
        bool deepTriggered = result.Elapsed.TotalMilliseconds > shortProfile.SoftTimeBudgetMilliseconds;
        result.SearchPhase = deepTriggered ? SolverSearchPhase.Deep : SolverSearchPhase.Short;
        result.DeepSearchTriggered = deepTriggered;
        result.DeepSearchImprovedResult = false;
        result.SingleSessionSearch = true;
        PopulateSingleSessionTotals(result, shortProfile.SoftTimeBudgetMilliseconds, deepTriggered);
        interimResultCallback?.Invoke(result);
        if (ResolveTakeoverResult(result, policy.Interaction) is { } takeoverResult)
            return takeoverResult;
        if (!policy.PotionStrategy.HasForcedDirectives)
        {
            result = RunSupplementalAudits(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                deepProfile,
                shortProfile.SoftTimeBudgetMilliseconds,
                requestClock,
                result,
                interimResultCallback);
        }
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SEARCH_SESSION mode=single_anytime " +
            $"short_checkpoint_ms={shortProfile.SoftTimeBudgetMilliseconds} " +
            $"total_budget_ms={deepProfile.SoftTimeBudgetMilliseconds}");
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
        int? shortCheckpointMilliseconds,
        Stopwatch requestClock,
        SolverResult primary,
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
                shortCheckpointMilliseconds,
                selected);
            if (ResolveTakeoverResult(selected, policy.Interaction) is { } requiredTakeoverResult)
                return requiredTakeoverResult;
            selected = AuditSmartPotionUse(
                root,
                displayNames,
                battleDamage,
                policy,
                deadline.Token,
                cancellationToken,
                progressCallback,
                profile,
                shortCheckpointMilliseconds,
                selected,
                interimResultCallback);
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
                    shortCheckpointMilliseconds,
                    selected);
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
        int? shortCheckpointMilliseconds,
        SolverResult primary)
    {
        int primaryDeficit = StrategicHpDeficit(root, primary);
        int maximumSmartPotionUses = policy.PotionPolicy == SolverPotionPolicy.Smart
            ? MaximumSmartPotionUses(root, policy, potionFreeWon: true, primaryDeficit)
            : Math.Max(1, primary.PotionCount);
        if (HasReachedProvablePrimaryQualityLowerBound(root, primary)
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
                profile,
                shortCheckpointMilliseconds)
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
                    shortCheckpointMilliseconds,
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
                    shortCheckpointMilliseconds,
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
                profile,
                shortCheckpointMilliseconds)
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
                    shortCheckpointMilliseconds,
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
                shortCheckpointMilliseconds,
                fixedPrefixActions: [openingPower]).Solve();
            if (posterior.ResultScope != SolverResultScope.SearchCompletion)
                return posterior;
            bool posteriorDeepTriggered = shortCheckpointMilliseconds is { } checkpoint
                && posterior.Elapsed.TotalMilliseconds > checkpoint;
            posterior.SearchPhase = posteriorDeepTriggered
                ? SolverSearchPhase.Deep
                : SolverSearchPhase.Short;
            posterior.DeepSearchTriggered = posteriorDeepTriggered;
            posterior.DeepSearchImprovedResult = false;
            posterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(
                posterior,
                shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
                posteriorDeepTriggered);
            searches.Add(posterior);

            bool posteriorWon = posterior.Snapshot.AllEnemiesDead
                && !posterior.Snapshot.PlayerDead
                && posterior.Snapshot.ProjectedPlayerHp > 0;
            int posteriorDeficit = StrategicHpDeficit(root, posterior);
            if (IsBetterCompletedResult(root, posterior, selected))
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
                    profile,
                    shortCheckpointMilliseconds)
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
                shortCheckpointMilliseconds,
                fixedPrefixActions: [openingPower, offensiveFollowUp]).Solve();
            if (linkedPosterior.ResultScope != SolverResultScope.SearchCompletion)
                return linkedPosterior;
            bool linkedDeepTriggered = shortCheckpointMilliseconds is { } linkedCheckpoint
                && linkedPosterior.Elapsed.TotalMilliseconds > linkedCheckpoint;
            linkedPosterior.SearchPhase = linkedDeepTriggered
                ? SolverSearchPhase.Deep
                : SolverSearchPhase.Short;
            linkedPosterior.DeepSearchTriggered = linkedDeepTriggered;
            linkedPosterior.DeepSearchImprovedResult = false;
            linkedPosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(
                linkedPosterior,
                shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
                linkedDeepTriggered);
            searches.Add(linkedPosterior);

            bool linkedWon = linkedPosterior.Snapshot.AllEnemiesDead
                && !linkedPosterior.Snapshot.PlayerDead
                && linkedPosterior.Snapshot.ProjectedPlayerHp > 0;
            int linkedDeficit = StrategicHpDeficit(root, linkedPosterior);
            if (IsBetterCompletedResult(root, linkedPosterior, selected))
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
                    profile,
                    shortCheckpointMilliseconds)
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
                shortCheckpointMilliseconds,
                fixedPrefixActions: [openingResource, defensiveFollowUp]).Solve();
            if (resourceDefensePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return resourceDefensePosterior;
            bool posteriorDeepTriggered = shortCheckpointMilliseconds is { } posteriorCheckpoint
                && resourceDefensePosterior.Elapsed.TotalMilliseconds > posteriorCheckpoint;
            resourceDefensePosterior.SearchPhase = posteriorDeepTriggered
                ? SolverSearchPhase.Deep
                : SolverSearchPhase.Short;
            resourceDefensePosterior.DeepSearchTriggered = posteriorDeepTriggered;
            resourceDefensePosterior.DeepSearchImprovedResult = false;
            resourceDefensePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(
                resourceDefensePosterior,
                shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
                posteriorDeepTriggered);
            searches.Add(resourceDefensePosterior);

            if (IsBetterCompletedResult(root, resourceDefensePosterior, selected))
                selected = resourceDefensePosterior;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_RESOURCE_DEFENSE_POSTERIOR " +
                $"cards={openingResource.CardId}+{defensiveFollowUp.CardId} " +
                $"won={resourceDefensePosterior.Snapshot.AllEnemiesDead && !resourceDefensePosterior.Snapshot.PlayerDead} " +
                $"hp_deficit={StrategicHpDeficit(root, resourceDefensePosterior)} " +
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
                    shortCheckpointMilliseconds,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: 1,
                    fixedPrefixActions: [openingPotion]),
                policy,
                $"POTION_RESOURCE_POSTERIOR potion={openingPotion.PotionId}");
            if (resourcePosterior == null)
                continue;
            if (resourcePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return resourcePosterior;
            bool resourceDeepTriggered = shortCheckpointMilliseconds is { } resourceCheckpoint
                && resourcePosterior.Elapsed.TotalMilliseconds > resourceCheckpoint;
            resourcePosterior.SearchPhase = resourceDeepTriggered
                ? SolverSearchPhase.Deep
                : SolverSearchPhase.Short;
            resourcePosterior.DeepSearchTriggered = resourceDeepTriggered;
            resourcePosterior.DeepSearchImprovedResult = false;
            resourcePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(
                resourcePosterior,
                shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
                resourceDeepTriggered);
            searches.Add(resourcePosterior);

            bool resourceWon = resourcePosterior.Snapshot.AllEnemiesDead
                && !resourcePosterior.Snapshot.PlayerDead
                && resourcePosterior.Snapshot.ProjectedPlayerHp > 0;
            int resourceDeficit = StrategicHpDeficit(root, resourcePosterior);
            if (IsBetterCompletedResult(root, resourcePosterior, selected))
            {
                selected = resourcePosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_RESOURCE_POSTERIOR " +
                $"potion={openingPotion.PotionId} card={openingPotion.Choice!.Cards[0].CardId} " +
                $"won={resourceWon} hp_deficit={resourceDeficit} " +
                $"selected={ReferenceEquals(selected, resourcePosterior)}");
        }

        if (HasReachedProvablePrimaryQualityLowerBound(root, selected)
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
                    shortCheckpointMilliseconds,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses,
                    fixedPrefixActions: jointPrefix),
                policy,
                $"POTION_POWER_POSTERIOR potion={openingPotion.PotionId} power={postPotionPower.CardId}");
            if (jointPosterior == null)
                continue;
            if (jointPosterior.ResultScope != SolverResultScope.SearchCompletion)
                return jointPosterior;
            bool jointDeepTriggered = shortCheckpointMilliseconds is { } jointCheckpoint
                && jointPosterior.Elapsed.TotalMilliseconds > jointCheckpoint;
            jointPosterior.SearchPhase = jointDeepTriggered
                ? SolverSearchPhase.Deep
                : SolverSearchPhase.Short;
            jointPosterior.DeepSearchTriggered = jointDeepTriggered;
            jointPosterior.DeepSearchImprovedResult = false;
            jointPosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(
                jointPosterior,
                shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
                jointDeepTriggered);
            searches.Add(jointPosterior);

            bool jointWon = jointPosterior.Snapshot.AllEnemiesDead
                && !jointPosterior.Snapshot.PlayerDead
                && jointPosterior.Snapshot.ProjectedPlayerHp > 0;
            int jointDeficit = StrategicHpDeficit(root, jointPosterior);
            int comparisonDeficit = StrategicHpDeficit(root, selected);
            if (IsBetterCompletedResult(root, jointPosterior, selected))
            {
                selected = jointPosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POWER_POSTERIOR " +
                $"potion={openingPotion.PotionId} power={postPotionPower.CardId} " +
                $"won={jointWon} hp_deficit={jointDeficit} " +
                $"selected={ReferenceEquals(selected, jointPosterior)}");

            if (!jointWon
                || HasReachedProvablePrimaryQualityLowerBound(root, jointPosterior)
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
                    shortCheckpointMilliseconds,
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
                    shortCheckpointMilliseconds,
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
            bool defensiveDeepTriggered = shortCheckpointMilliseconds is { } defensiveCheckpoint
                && defensivePosterior.Elapsed.TotalMilliseconds > defensiveCheckpoint;
            defensivePosterior.SearchPhase = defensiveDeepTriggered
                ? SolverSearchPhase.Deep
                : SolverSearchPhase.Short;
            defensivePosterior.DeepSearchTriggered = defensiveDeepTriggered;
            defensivePosterior.DeepSearchImprovedResult = false;
            defensivePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(
                defensivePosterior,
                shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
                defensiveDeepTriggered);
            searches.Add(defensivePosterior);

            bool defensiveWon = defensivePosterior.Snapshot.AllEnemiesDead
                && !defensivePosterior.Snapshot.PlayerDead
                && defensivePosterior.Snapshot.ProjectedPlayerHp > 0;
            int defensiveDeficit = StrategicHpDeficit(root, defensivePosterior);
            if (IsBetterCompletedResult(root, defensivePosterior, selected))
            {
                selected = defensivePosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POWER_DEFENSIVE_POSTERIOR " +
                $"potion={openingPotion.PotionId} power={postPotionPower.CardId} " +
                $"follow_up={defensiveFollowUp.CardId} won={defensiveWon} " +
                $"hp_deficit={defensiveDeficit} selected={ReferenceEquals(selected, defensivePosterior)}");

            if (HasReachedProvablePrimaryQualityLowerBound(root, defensivePosterior))
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
        int? shortCheckpointMilliseconds,
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
            shortCheckpointMilliseconds,
            SolverPotionPolicy.Disabled).Solve();
        if (potionFree.ResultScope != SolverResultScope.SearchCompletion)
            return potionFree;
        bool auditDeepTriggered = shortCheckpointMilliseconds is { } checkpoint
            && potionFree.Elapsed.TotalMilliseconds > checkpoint;
        potionFree.SearchPhase = auditDeepTriggered ? SolverSearchPhase.Deep : SolverSearchPhase.Short;
        potionFree.DeepSearchTriggered = auditDeepTriggered;
        potionFree.DeepSearchImprovedResult = false;
        potionFree.SingleSessionSearch = true;
        PopulateSingleSessionTotals(
            potionFree,
            shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
            auditDeepTriggered);

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
                    shortCheckpointMilliseconds,
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
                    shortCheckpointMilliseconds,
                    SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: primary.PotionCount,
                    fixedPrefixActions: [openingPotion]).Solve();
                if (posterior.ResultScope != SolverResultScope.SearchCompletion)
                    return posterior;
                bool posteriorDeepTriggered = shortCheckpointMilliseconds is { } posteriorCheckpoint
                    && posterior.Elapsed.TotalMilliseconds > posteriorCheckpoint;
                posterior.SearchPhase = posteriorDeepTriggered
                    ? SolverSearchPhase.Deep
                    : SolverSearchPhase.Short;
                posterior.DeepSearchTriggered = posteriorDeepTriggered;
                posterior.DeepSearchImprovedResult = false;
                posterior.SingleSessionSearch = true;
                PopulateSingleSessionTotals(
                    posterior,
                    shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
                    posteriorDeepTriggered);
                searches.Add(posterior);

                bool posteriorWon = posterior.Snapshot.AllEnemiesDead
                    && !posterior.Snapshot.PlayerDead
                    && posterior.Snapshot.ProjectedPlayerHp > 0;
                int posteriorDeficit = StrategicHpDeficit(root, posterior);
                if (IsBetterCompletedResult(root, posterior, selected))
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
                        shortCheckpointMilliseconds,
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
                        shortCheckpointMilliseconds,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount,
                        fixedPrefixActions: [openingPotion, secondPotion]).Solve();
                    if (pairPosterior.ResultScope != SolverResultScope.SearchCompletion)
                        return pairPosterior;
                    bool pairDeepTriggered = shortCheckpointMilliseconds is { } pairCheckpoint
                        && pairPosterior.Elapsed.TotalMilliseconds > pairCheckpoint;
                    pairPosterior.SearchPhase = pairDeepTriggered
                        ? SolverSearchPhase.Deep
                        : SolverSearchPhase.Short;
                    pairPosterior.DeepSearchTriggered = pairDeepTriggered;
                    pairPosterior.DeepSearchImprovedResult = false;
                    pairPosterior.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(
                        pairPosterior,
                        shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
                        pairDeepTriggered);
                    searches.Add(pairPosterior);

                    bool pairWon = pairPosterior.Snapshot.AllEnemiesDead
                        && !pairPosterior.Snapshot.PlayerDead
                        && pairPosterior.Snapshot.ProjectedPlayerHp > 0;
                    int pairDeficit = StrategicHpDeficit(root, pairPosterior);
                    if (IsBetterCompletedResult(root, pairPosterior, selected))
                    {
                        selected = pairPosterior;
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] REQUIRED_POTION_PAIR_POSTERIOR " +
                        $"first={openingPotion.PotionId}:{openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"second={secondPotion.PotionId}:{secondPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"won={pairWon} hp_deficit={pairDeficit} " +
                        $"selected={ReferenceEquals(selected, pairPosterior)}");

                    int selectedDeficit = StrategicHpDeficit(root, selected);
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
                            shortCheckpointMilliseconds,
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
                        shortCheckpointMilliseconds,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount,
                        fixedPrefixActions: [openingPotion, secondPotion, defensiveFollowUp]).Solve();
                    if (defensivePosterior.ResultScope != SolverResultScope.SearchCompletion)
                        return defensivePosterior;
                    bool defensiveDeepTriggered = shortCheckpointMilliseconds is { } defensiveCheckpoint
                        && defensivePosterior.Elapsed.TotalMilliseconds > defensiveCheckpoint;
                    defensivePosterior.SearchPhase = defensiveDeepTriggered
                        ? SolverSearchPhase.Deep
                        : SolverSearchPhase.Short;
                    defensivePosterior.DeepSearchTriggered = defensiveDeepTriggered;
                    defensivePosterior.DeepSearchImprovedResult = false;
                    defensivePosterior.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(
                        defensivePosterior,
                        shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
                        defensiveDeepTriggered);
                    searches.Add(defensivePosterior);

                    bool defensiveWon = defensivePosterior.Snapshot.AllEnemiesDead
                        && !defensivePosterior.Snapshot.PlayerDead
                        && defensivePosterior.Snapshot.ProjectedPlayerHp > 0;
                    int defensiveDeficit = StrategicHpDeficit(root, defensivePosterior);
                    if (IsBetterCompletedResult(root, defensivePosterior, selected))
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

        PotionFreePolicyBaseline baseline = new(
            Won: true,
            HpDeficit: potionFree.Snapshot.CumulativePlayerHpLost
                + Math.Max(0, root.InitialPlayerMaxHp - potionFree.Snapshot.PlayerMaxHp),
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
            shortCheckpointMilliseconds,
            SolverPotionPolicy.RequireAtLeastOne,
            baseline,
            maximumPotionUses: 1).Solve();
        if (audited.ResultScope != SolverResultScope.SearchCompletion)
            return audited;
        bool auditedDeepTriggered = shortCheckpointMilliseconds is { } auditedCheckpoint
            && audited.Elapsed.TotalMilliseconds > auditedCheckpoint;
        audited.SearchPhase = auditedDeepTriggered ? SolverSearchPhase.Deep : SolverSearchPhase.Short;
        audited.DeepSearchTriggered = auditedDeepTriggered;
        audited.DeepSearchImprovedResult = false;
        audited.SingleSessionSearch = true;
        PopulateSingleSessionTotals(
            audited,
            shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
            auditedDeepTriggered);
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
        int? shortCheckpointMilliseconds,
        SolverResult primary,
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
                shortCheckpointMilliseconds,
                primary,
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
        int? shortCheckpointMilliseconds,
        SolverResult potionFree,
        Action<SolverResult>? interimResultCallback)
    {
        if (potionFree.ExplicitPotionCount != 0)
            throw new InvalidOperationException("Smart 梯度搜索必须从无主动用药结果开始。");

        bool potionFreeWon = potionFree.Snapshot.AllEnemiesDead
            && !potionFree.Snapshot.PlayerDead
            && potionFree.Snapshot.ProjectedPlayerHp > 0;
        int potionFreeDeficit = StrategicHpDeficit(root, potionFree);
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
                selected);
            SolverResult candidate;
            try
            {
                candidate = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    searchCancellationToken,
                    progressCallback,
                    profile,
                    shortCheckpointMilliseconds,
                    SolverPotionPolicy.RequireAtLeastOne,
                    baseline,
                    maximumPotionUses: potionCount,
                    minimumPotionUses: potionCount,
                    primaryIncumbent: primaryIncumbent).Solve();
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
            if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                return candidate;

            bool candidateDeepTriggered = shortCheckpointMilliseconds is { } checkpoint
                && candidate.Elapsed.TotalMilliseconds > checkpoint;
            candidate.SearchPhase = candidateDeepTriggered
                ? SolverSearchPhase.Deep
                : SolverSearchPhase.Short;
            candidate.DeepSearchTriggered = candidateDeepTriggered;
            candidate.DeepSearchImprovedResult = false;
            candidate.SingleSessionSearch = true;
            PopulateSingleSessionTotals(
                candidate,
                shortCheckpointMilliseconds ?? profile.SoftTimeBudgetMilliseconds,
                candidateDeepTriggered);
            searches.Add(candidate);
            interimResultCallback?.Invoke(candidate);

            bool candidateWon = IsCompleteVictory(candidate);
            int candidateDeficit = StrategicHpDeficit(root, candidate);
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
            if (acceptable)
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
                $"selected={acceptable} " +
                $"expanded={candidate.ExpandedNodes} transitions={candidate.TransitionCount} " +
                $"choice_branches={candidate.ChoiceBranchesEvaluated} " +
                $"elapsed_ms={candidate.Elapsed.TotalMilliseconds:F1} " +
                $"allocated_bytes={candidate.WorkerAllocatedBytes} " +
                $"incumbent_deficit={primaryIncumbent?.StrategicHpDeficit.ToString() ?? "-"} " +
                $"incumbent_turn={primaryIncumbent?.CombatEndedTurn.ToString() ?? "-"} " +
                $"incumbent_pruned={candidate.PrimaryIncumbentBranchesPruned} " +
                $"incumbent_updates={candidate.PrimaryIncumbentUpdates}");
            if (acceptable)
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

    private static void ReclaimAtPotionGradientBoundary(
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult totalsCarrier,
        int completedPotionCount,
        int nextPotionCount)
    {
        SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
        if (!signal.IsEnabled || signal.AllocatedBytes == 0)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
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
            signal.ReclaimAndContinue(cancellationToken);
        }
        finally
        {
            // ReclaimWithinSearch can observe a deadline after completing its blocking Gen2.
            // Retain that completed work in request totals even when cancellation then unwinds.
            stopwatch.Stop();
            TimeSpan gcPause = GC.GetTotalPauseDuration() - pauseBefore;
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
            if (gcPause > totalsCarrier.TotalMaxObservedGcPause)
                totalsCarrier.TotalMaxObservedGcPause = gcPause;
            totalsCarrier.TotalSearchElapsed += stopwatch.Elapsed;
            if (totalsCarrier.SearchPhase == SolverSearchPhase.Deep)
                totalsCarrier.DeepSearchElapsed += stopwatch.Elapsed;
            else
                totalsCarrier.ShortSearchElapsed += stopwatch.Elapsed;
            policy.RequestWorkTotals?.RecordCoordinatorOverhead(
                stopwatch.Elapsed,
                totalsCarrier.SearchPhase == SolverSearchPhase.Deep,
                allocatedBytes,
                gen0Collections,
                gen1Collections,
                gen2Collections,
                gcPause,
                gcPause);

            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_GRADIENT_MEMORY_RESET " +
                $"completed_layer={completedPotionCount} next_layer={nextPotionCount} " +
                $"allocated_before={pressureBefore} limit_before={limitBefore} " +
                $"allocated_after={signal.AllocatedBytes} limit_after={signal.AllocationLimitBytes} " +
                $"gc_pause_ms={gcPause.TotalMilliseconds:F1} " +
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
            StrategicHpDeficit: StrategicHpDeficit(root, result),
            PotionStrategicCost: SmartPotionHpRequired(root, policy, result),
            ProjectedBattlePotionCount: result.ProjectedBattlePotionCount,
            CombatEndedTurn: result.CombatEndedTurn,
            EnemyHp: result.Snapshot.EnemyHp,
            Score: result.BestNode.Score);


    private static bool IsBetterCompletedResult(
        CombatRootSnapshot root,
        SolverResult candidate,
        SolverResult current)
    {
        int primaryQuality = CompareCompletedResultPrimaryQuality(root, candidate, current);
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
        int primaryQuality = SolverInterimResultOrdering.ComparePrimaryQuality(
            candidate.Won,
            candidate.StrategicHpDeficit,
            candidate.CombatEndedTurn,
            current.Won,
            current.StrategicHpDeficit,
            current.CombatEndedTurn);
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
        SolverResult candidate,
        SolverResult current)
        => SolverInterimResultOrdering.ComparePrimaryQuality(
            IsCompleteVictory(candidate),
            StrategicHpDeficit(root, candidate),
            candidate.CombatEndedTurn,
            IsCompleteVictory(current),
            StrategicHpDeficit(root, current),
            current.CombatEndedTurn);

    private static bool IsCompleteVictory(SolverResult result)
        => SolverInterimResultOrdering.IsCompleteVictory(
            result.BestNode.ActionCount,
            result.Snapshot.AllEnemiesDead,
            result.Snapshot.PlayerDead,
            result.Snapshot.ProjectedPlayerHp);

    private static bool HasReachedProvablePrimaryQualityLowerBound(
        CombatRootSnapshot root,
        SolverResult result)
        => HasReachedProvablePrimaryQualityLowerBound(
            IsCompleteVictory(result),
            StrategicHpDeficit(root, result),
            result.CombatEndedTurn,
            root.StartTurnNumber);

    internal static bool HasReachedProvablePrimaryQualityLowerBound(
        bool completeVictory,
        int strategicHpDeficit,
        int? combatEndedTurn,
        int? earliestPossibleCombatEndedTurn)
    {
        if (earliestPossibleCombatEndedTurn is not { } earliestTurn)
            return false;
        return SolverInterimResultOrdering.ComparePrimaryQuality(
            completeVictory,
            strategicHpDeficit,
            combatEndedTurn,
            currentCompleteVictory: true,
            currentStrategicHpDeficit: 0,
            currentCombatEndedTurn: earliestTurn) <= 0;
    }

    private static PrimarySearchIncumbent? BuildPrimarySearchIncumbent(
        CombatRootSnapshot root,
        SolverResult result)
    {
        if (!IsCompleteVictory(result) || result.CombatEndedTurn is not { } combatEndedTurn)
            return null;
        return new PrimarySearchIncumbent(
            StrategicHpDeficit(root, result),
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

    private static int StrategicHpDeficit(CombatRootSnapshot root, SolverResult result)
        => result.Snapshot.CumulativePlayerHpLost
            + Math.Max(0, root.InitialPlayerMaxHp - result.Snapshot.PlayerMaxHp);

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
            result.ShortSearchElapsed,
            result.DeepSearchElapsed,
            result.TotalWorkerAllocatedBytes,
            result.ShortExpandedNodes,
            result.DeepExpandedNodes,
            result.ShortTransitionCount,
            result.DeepTransitionCount,
            result.TotalGen0Collections,
            result.TotalGen1Collections,
            result.TotalGen2Collections,
            result.TotalGcPauseDuration,
            result.TotalMaxObservedGcPause,
            result.DeepSearchTriggered);

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
        result.ShortSearchElapsed = totals.ShortElapsed;
        result.DeepSearchElapsed = totals.DeepElapsed;
        result.TotalSearchElapsed = totals.ShortElapsed + totals.DeepElapsed;
        result.TotalWorkerAllocatedBytes = totals.WorkerAllocatedBytes;
        result.ShortExpandedNodes = SaturatingInt(totals.ShortExpandedNodes);
        result.DeepExpandedNodes = SaturatingInt(totals.DeepExpandedNodes);
        result.ShortTransitionCount = SaturatingInt(totals.ShortTransitionCount);
        result.DeepTransitionCount = SaturatingInt(totals.DeepTransitionCount);
        result.TotalGen0Collections = SaturatingInt(totals.Gen0Collections);
        result.TotalGen1Collections = SaturatingInt(totals.Gen1Collections);
        result.TotalGen2Collections = SaturatingInt(totals.Gen2Collections);
        result.TotalGcPauseDuration = totals.GcPauseDuration;
        result.TotalMaxObservedGcPause = totals.MaxObservedGcPause;
        result.DeepSearchTriggered = totals.DeepSearchTriggered;
        result.SearchPhase = totals.DeepSearchTriggered
            ? SolverSearchPhase.Deep
            : SolverSearchPhase.Short;
        result.TotalExpandedNodes = totals.ExpandedNodes;
        result.TotalTransitionCount = totals.TransitionCount;
        result.TotalChoiceBranchesEvaluated = totals.ChoiceBranchesEvaluated;
    }

    private static int SaturatingInt(long value)
        => value >= int.MaxValue ? int.MaxValue : (int)value;

    private static void PopulateSingleSessionTotals(
        SolverResult result,
        int shortCheckpointMilliseconds,
        bool deepTriggered)
    {
        double shortMilliseconds = deepTriggered
            ? Math.Min(result.Elapsed.TotalMilliseconds, shortCheckpointMilliseconds)
            : result.Elapsed.TotalMilliseconds;
        result.ShortSearchElapsed = TimeSpan.FromMilliseconds(shortMilliseconds);
        result.DeepSearchElapsed = result.Elapsed - result.ShortSearchElapsed;
        result.TotalSearchElapsed = result.Elapsed;
        result.TotalWorkerAllocatedBytes = result.WorkerAllocatedBytes;
        result.TotalGen0Collections = result.Gen0Collections;
        result.TotalGen1Collections = result.Gen1Collections;
        result.TotalGen2Collections = result.Gen2Collections;
        result.TotalGcPauseDuration = result.GcPauseDuration;
        result.TotalMaxObservedGcPause = result.MaxObservedGcPause;
        result.ShortExpandedNodes = deepTriggered ? 0 : result.ExpandedNodes;
        result.DeepExpandedNodes = deepTriggered ? result.ExpandedNodes : 0;
        result.ShortTransitionCount = deepTriggered ? 0 : result.TransitionCount;
        result.DeepTransitionCount = deepTriggered ? result.TransitionCount : 0;
        result.TotalExpandedNodes = result.ExpandedNodes;
        result.TotalTransitionCount = result.TransitionCount;
        result.TotalChoiceBranchesEvaluated = result.ChoiceBranchesEvaluated;
    }
}
