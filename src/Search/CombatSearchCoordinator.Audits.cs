using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static SolverResult RunSupplementalAudits(
        SearchPassContext context,
        SolverResult primary,
        SmartLayerMemoryForecast memoryForecast)
    {
        CombatRootSnapshot root = context.Root;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        SolverSearchProfile profile = context.Profile;
        Stopwatch requestClock = context.Clock;
        long remainingMilliseconds = context.RemainingMilliseconds;
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
        SearchPassContext auditContext = context with { CancellationToken = deadline.Token };
        SolverResult selected = primary;
        try
        {
            if (!policy.PotionStrategy.HasForcedDirectives)
                selected = AuditRequiredPotionUse(auditContext, selected);
            if (ResolveTakeoverResult(selected, policy.Interaction) is { } requiredTakeoverResult)
                return requiredTakeoverResult;
            if (!policy.PotionStrategy.HasForcedDirectives
                && HasReachedAcceptableBattleHpLoss(policy, selected))
                return selected;
            selected = AuditSmartPotionUse(
                auditContext, cancellationToken, selected, memoryForecast);
            if (selected.ResultScope == SolverResultScope.SearchCompletion)
                selected = RunPlanSearchPass(auditContext, selected);
            if (selected.ResultScope != SolverResultScope.SearchCompletion)
                return selected;
            if (root.PlayerCardIds.Contains("NIGHTMARE")
                && !IsProvenZeroDamageRoute(root, policy, selected))
            {
                selected = RunOpeningNightmarePortfolio(
                    auditContext, selected);
            }
            if (selected.BestNode.Actions.FirstOrDefault() is
                    { Kind: PlanActionKind.UsePotion }
                && root.PlayerCardIds.Contains("WHITE_NOISE"))
            {
                selected = RunOpeningPowerRoutePortfolio(
                    auditContext,
                    potionPolicyOverride: null,
                    selected,
                    generatedAfterOpeningPotionsOnly: true);
            }
            if (HasReachedAcceptableBattleHpLoss(policy, selected))
                return selected;
            if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            {
                selected = AuditOpeningPowerUse(auditContext, selected);
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
        SearchPassContext context,
        SolverResult primary)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SolverSearchProfile profile = context.Profile;
        int primaryDeficit = StrategicHpDeficit(root, policy, primary);
        if (policy.IncludeTurnSetup)
            return primary;
        int maximumSmartPotionUses = policy.PotionPolicy == SolverPotionPolicy.Smart
            ? MaximumSmartPotionUses(root, policy, potionFreeWon: true, primaryDeficit)
            : Math.Max(1, primary.PotionCount);
        if (HasReachedProvablePrimaryQualityLowerBound(root, policy, primary)
            || policy.PotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                && battleDamage.PotionsUsedSoFar == 0)
            return primary;

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
        IReadOnlyList<PlanAction> generatedCardPotions = openingPotions.Count == 0
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
                .SelectGeneratedCardPotionActions(openingPotions);
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
        if (potionPowerPairs.Count == 0
            && generatedCardPotions.Count == 0
            && openingResources.Count == 0)
            return primary;

        List<SolverResult> searches = [primary];
        SolverResult selected = primary;
        FrontierContinuationScheduler continuationScheduler = new(context);
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

            SolverResult resourceDefensePosterior = continuationScheduler.Dispatch(
                new ContinuationSearchRequest(context, ContinuationPurpose.OpeningResourceDefense,
                    [openingResource, defensiveFollowUp], profile, null, null, null)
                { ResetFixedPrefixSchedulingBaseline = false });
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

        foreach (PlanAction openingPotion in generatedCardPotions)
        {
            SolverResult? resourcePosterior = continuationScheduler.DispatchOptional(
                new ContinuationSearchRequest(context, ContinuationPurpose.PotionResourcePosterior,
                    [openingPotion], profile, SolverPotionPolicy.RequireAtLeastOne, 1, null)
                { ResetFixedPrefixSchedulingBaseline = false },
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
            SolverResult? jointPosterior = continuationScheduler.DispatchOptional(
                new ContinuationSearchRequest(context, ContinuationPurpose.PotionPowerPosterior,
                    jointPrefix, profile, SolverPotionPolicy.RequireAtLeastOne,
                    maximumSmartPotionUses, null)
                { ResetFixedPrefixSchedulingBaseline = false },
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

            SolverResult? defensivePosterior = continuationScheduler.DispatchOptional(
                new ContinuationSearchRequest(context,
                    ContinuationPurpose.PotionPowerDefensivePosterior,
                    [openingPotion, postPotionPower, defensiveFollowUp], profile,
                    SolverPotionPolicy.RequireAtLeastOne, maximumSmartPotionUses, null)
                { ResetFixedPrefixSchedulingBaseline = false },
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
        SearchPassContext context,
        SolverResult primary)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SolverSearchProfile profile = context.Profile;
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
            SolverPotionPolicy.Disabled,
            directSearchPurpose: DirectSearchPurpose.PotionFreeAudit).Solve();
        if (potionFree.ResultScope != SolverResultScope.SearchCompletion)
            return potionFree;

        potionFree.SingleSessionSearch = true;
        PopulateSingleSessionTotals(potionFree);

        bool potionFreeWon = IsCompleteVictory(potionFree);
        if (!potionFreeWon)
        {
            if (policy.IncludeTurnSetup)
            {
                MergeAuditTotals(primary, primary, potionFree);
                return primary;
            }
            List<SolverResult> searches = [primary, potionFree];
            SolverResult selected = primary;
            FrontierContinuationScheduler continuationScheduler = new(context);
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
                SolverResult posterior = continuationScheduler.Dispatch(
                    new ContinuationSearchRequest(context,
                        ContinuationPurpose.RequiredOpeningPotion,
                        [openingPotion], profile, SolverPotionPolicy.RequireAtLeastOne,
                        primary.PotionCount, null)
                    { ResetFixedPrefixSchedulingBaseline = false });
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
                    SolverResult pairPosterior = continuationScheduler.Dispatch(
                        new ContinuationSearchRequest(context,
                            ContinuationPurpose.RequiredPotionPair,
                            [openingPotion, secondPotion], profile,
                            SolverPotionPolicy.RequireAtLeastOne, primary.PotionCount, null)
                        { ResetFixedPrefixSchedulingBaseline = false });
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

                    SolverResult defensivePosterior = continuationScheduler.Dispatch(
                        new ContinuationSearchRequest(context,
                            ContinuationPurpose.RequiredPotionPairDefensive,
                            [openingPotion, secondPotion, defensiveFollowUp], profile,
                            SolverPotionPolicy.RequireAtLeastOne, primary.PotionCount, null)
                        { ResetFixedPrefixSchedulingBaseline = false });
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
            CombatEndedTurn: potionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = potionFree.Snapshot.ProjectedDeathSaveUseCount,
        };
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
            maximumPotionUses: 1,
            directSearchPurpose: DirectSearchPurpose.RequiredPotionAudit).Solve();
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
        SearchPassContext context,
        CancellationToken callerCancellationToken,
        SolverResult primary,
        SmartLayerMemoryForecast memoryForecast)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken searchCancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SolverSearchProfile profile = context.Profile;
        Action<SolverResult>? interimResultCallback = context.InterimResultCallback;
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return primary;
        try
        {
            SolverResult gradient = SearchSmartPotionGradient(
                context, callerCancellationToken, primary, memoryForecast);
            if (policy.IncludeTurnSetup
                || gradient.ResultScope != SolverResultScope.SearchCompletion
                || policy.PotionStrategy.HasForcedDirectives
                || battleDamage.PotionsUsedSoFar != 0
                || CanFinishNativeLouseZeroDamageRoute(root, policy, gradient)
                || (gradient.ExplicitPotionCount <= 1
                    && HasReachedProvablePrimaryQualityLowerBound(root, policy, gradient)))
                return gradient;

            CombatBeamSolver builder = new(root, displayNames, battleDamage, policy,
                searchCancellationToken, progressCallback, profile,
                potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 1);
            IReadOnlyList<PlanAction> openingPotions = builder.BuildOpeningPotionActions();
            IReadOnlyList<PlanAction> generatedPotions = builder.SelectGeneratedCardPotionActions(openingPotions);
            bool openingBlockPotionCanPayForItself = root.Forecast.Rounds.FirstOrDefault()?
                .SelectMany(move => move.AttackHits)
                .Sum(hit => hit.Damage) >= SolverWeights.PotionMinimumHpSaved;
            List<PlanAction[]> prefixes = openingPotions
                .Where(potion => potion.Choice == null
                    && (PotionUsePolicy.RequiresOpeningUse(potion.PotionId)
                        || potion.PotionId == "BLOCK_POTION" && openingBlockPotionCanPayForItself))
                .GroupBy(potion => potion.PotionSlot)
                .Select(group => new[] { group.First() })
                .Concat(generatedPotions.Select(potion => new[] { potion }))
                .Concat(openingPotions
                    .Where(potion => potion.Choice?.Effect == PlanChoiceEffect.SetFreeThisCombat)
                    .Take(8)
                    .Select(potion => new[] { potion }))
                .ToList();
            foreach (PlanAction[] openingPotion in prefixes.ToArray())
            {
                if (openingPotion[0].PotionId == "BLOCK_POTION")
                {
                    foreach (PlanAction attack in builder
                                 .BuildOpeningOffensiveCardVariantsAfterPrefix(openingPotion)
                                 .Take(5))
                    {
                        PlanAction[] attackPrefix = [.. openingPotion, attack];
                        prefixes.Add(attackPrefix);
                        foreach (PlanAction followUpAttack in builder
                                     .BuildOpeningOffensiveCardVariantsAfterPrefix(attackPrefix))
                        {
                            PlanAction[] offensivePrefix = [.. attackPrefix, followUpAttack];
                            PlanAction? defense = builder.BuildOpeningDefensiveFollowUp(offensivePrefix);
                            if (defense == null)
                                continue;
                            prefixes.Add([.. offensivePrefix, defense]);
                            break;
                        }
                    }
                }
                foreach (PlanAction power in builder.BuildPowerActionsAfterPrefix(openingPotion)
                             .Where(action => PowerCardValuationModels.Registry.ContainsCardId(action.CardId!))
                             .Take(2))
                {
                    PlanAction[] powerPrefix = [.. openingPotion, power];
                    prefixes.Add(powerPrefix);
                    PlanAction? defense = builder.BuildOpeningDefensiveFollowUp(powerPrefix);
                    if (defense == null)
                        continue;
                    PlanAction[] defendedPrefix = [.. powerPrefix, defense];
                    foreach (PlanAction setup in builder.BuildOpeningHandSetupActions(defendedPrefix).Take(2))
                        prefixes.Add([.. defendedPrefix, setup]);
                }
                if (openingPotion[0].Choice == null
                    && PotionUsePolicy.RequiresOpeningUse(openingPotion[0].PotionId))
                {
                    foreach (PlanAction draw in builder.BuildOpeningHandSetupActions(openingPotion).Take(1))
                    {
                        PlanAction[] drawnPrefix = [.. openingPotion, draw];
                        foreach (PlanAction attack in builder.BuildOpeningOffensiveCardVariantsAfterPrefix(drawnPrefix))
                            prefixes.Add([.. drawnPrefix, attack]);
                    }
                }
                if (openingPotion[0].Choice?.Effect == PlanChoiceEffect.SetFreeThisCombat)
                {
                    foreach (PlanAction setup in builder.BuildOpeningHandSetupActions(openingPotion).Take(1))
                    {
                        PlanAction[] setupPrefix = [.. openingPotion, setup];
                        foreach (PlanAction attack in builder.BuildOpeningOffensiveFollowUps(setupPrefix))
                            prefixes.Add([.. setupPrefix, attack]);
                    }
                }
            }
            foreach (PlanAction[] synergy in builder.BuildOpeningPowerPotionSynergyPrefixes())
            {
                PlanAction? setup = builder.BuildOpeningSetupFollowUp(synergy);
                if (setup == null)
                {
                    prefixes.Add(synergy);
                    continue;
                }
                prefixes.Add([.. synergy, setup]);
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_OPENING_POTION_PREFIXES " +
                $"generated={generatedPotions.Count} total={prefixes.Count}");
            SolverResult selected = gradient;
            int prefixLimit = prefixes.Any(prefix => prefix[0].PotionId == "BLOCK_POTION"
                || prefix[0].Choice?.Effect == PlanChoiceEffect.SetFreeThisCombat) ? 12 : 8;
            FrontierContinuationScheduler continuationScheduler = new(context);
            foreach (PlanAction[] prefix in prefixes
                         .DistinctBy(PowerPrefixKey)
                         .Take(prefixLimit))
            {
                string prefixText = string.Join('+', prefix.Select(action =>
                    action.Kind == PlanActionKind.UsePotion
                        ? $"POTION:{action.PotionId}@{action.PotionSlot}" +
                          (action.Choice?.Cards.FirstOrDefault() is { } chosen
                              ? $":{chosen.CardId}" : "")
                        : action.CardId));
                SolverSearchProfile routeProfile = prefix.Length > 1
                    ? profile with
                    {
                        MaxExpandedNodes = Math.Min(profile.MaxExpandedNodes, 120_000),
                        SoftTimeBudgetMilliseconds = Math.Min(profile.SoftTimeBudgetMilliseconds, 60_000),
                        BaseScoreOnly = prefix[0].Choice?.Effect != PlanChoiceEffect.SetFreeThisCombat,
                        BeamWidth = prefix[0].Choice?.Effect == PlanChoiceEffect.SetFreeThisCombat
                            ? Math.Min(512, profile.BeamWidth * 3)
                            : BeamWidthPortfolio.ScaledWidth(
                                profile.BeamWidth, BeamWidthPortfolio.WideRefinementRatio),
                    }
                    : profile;
                SolverResult? candidate = continuationScheduler.DispatchOptional(
                    new ContinuationSearchRequest(context,
                        ContinuationPurpose.SmartOpeningPotionPosterior,
                        prefix, routeProfile, SolverPotionPolicy.RequireAtLeastOne,
                        prefix[0].PotionId == "BLOCK_POTION"
                            ? Math.Min(2, MaximumSmartPotionUses(root, policy,
                                potionFreeWon: false, potionFreeHpDeficit: 0))
                            : 1, null)
                    { ResetFixedPrefixSchedulingBaseline = false },
                    $"SMART_OPENING_POTION_POSTERIOR prefix={prefixText}");
                if (candidate == null)
                    continue;
                if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                    return candidate;
                PopulateSingleSessionTotals(candidate);
                bool won = IsCompleteVictory(candidate);
                int saved = IsCompleteVictory(primary)
                    ? Math.Max(0, StrategicHpDeficit(root, policy, primary)
                        - StrategicHpDeficit(root, policy, candidate))
                    : won ? Math.Max(0, candidate.Snapshot.PlayerHp - primary.Snapshot.PlayerHp) : 0;
                int required = SmartPotionHpRequired(root, policy, candidate);
                bool acceptable = IsSmartPotionGradientCandidateAcceptable(
                    IsCompleteVictory(primary), won, saved, required,
                    policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                        && candidate.OutstandingStolenResource < primary.OutstandingStolenResource);
                bool improved = acceptable && IsBetterSmartPotionAuditResult(
                    root, policy, candidate, selected);
                if (improved)
                {
                    candidate.PotionHpSaved = saved;
                    candidate.PotionHpRequired = required;
                    selected = candidate;
                }
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SMART_OPENING_POTION_POSTERIOR " +
                    $"prefix={prefixText} " +
                    $"won={won} hp_deficit={StrategicHpDeficit(root, policy, candidate)} " +
                    $"saved={saved} required={required} selected={improved} " +
                    $"first_turn={string.Join(',', candidate.BestNode.Actions
                        .TakeWhile(action => action.Turn == root.StartTurnNumber)
                        .Select(action => action.Kind == PlanActionKind.PlayCard
                            ? action.CardId : action.Kind.ToString()))}");
                if (selected.ExplicitPotionCount <= 1
                    && HasReachedProvablePrimaryQualityLowerBound(root, policy, selected))
                    break;
            }
            return selected;
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

    private static bool IsBetterSmartPotionAuditResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        if (!IsCompleteVictory(candidate) || !IsCompleteVictory(current)
            || candidate.Snapshot.StrategyGoalHpCredit != current.Snapshot.StrategyGoalHpCredit
            || policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            return IsBetterCompletedResult(root, policy, candidate, current);

        int candidateCost = StrategicHpDeficit(root, policy, candidate)
            + SmartPotionHpRequired(root, policy, candidate);
        int currentCost = StrategicHpDeficit(root, policy, current)
            + SmartPotionHpRequired(root, policy, current);
        return candidateCost != currentCost
            ? candidateCost < currentCost
            : IsBetterCompletedResult(root, policy, candidate, current);
    }

    private static SolverResult SearchSmartPotionGradient(
        SearchPassContext context,
        CancellationToken callerCancellationToken,
        SolverResult potionFree,
        SmartLayerMemoryForecast memoryForecast)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken searchCancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SolverSearchProfile profile = context.Profile;
        Action<SolverResult>? interimResultCallback = context.InterimResultCallback;
        int forcedPotionCount = policy.PotionStrategy.ForcedDirectiveCount;
        if (potionFree.ExplicitPotionCount != forcedPotionCount)
            throw new InvalidOperationException("Smart 梯度搜索必须从仅满足强制用药的结果开始。");

        bool potionFreeWon = potionFree.Snapshot.AllEnemiesDead
            && !potionFree.Snapshot.PlayerDead
            && potionFree.Snapshot.ProjectedPlayerHp > 0;
        int potionFreeDeficit = StrategicHpDeficit(root, policy, potionFree);
        int maximumOptionalPotionUses = MaximumSmartPotionUses(
            root,
            policy,
            potionFreeWon,
            potionFreeDeficit);
        if (maximumOptionalPotionUses == 0)
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
            potionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = potionFree.Snapshot.ProjectedDeathSaveUseCount,
        };
        List<SolverResult> searches = [potionFree];
        SolverResult selected = potionFree;
        bool deadlineExpired = false;
        bool acceptablePotionLayerFound = false;
        for (int optionalPotionCount = 1; optionalPotionCount <= maximumOptionalPotionUses; optionalPotionCount++)
        {
            int potionCount = forcedPotionCount + optionalPotionCount;
            if (searchCancellationToken.IsCancellationRequested)
            {
                callerCancellationToken.ThrowIfCancellationRequested();
                deadlineExpired = true;
                break;
            }
            try
            {
                ReclaimAtPotionGradientBoundary(
                    context,
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
            long layerTransitionsAtStart = context.Budget.WorkTotals.Snapshot().TransitionCount;
            SolverResult? observedLayerResult = null;
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
                    SolverPotionPolicy.RequireAtLeastOne,
                    baseline,
                    maximumPotionUses: potionCount,
                    minimumPotionUses: potionCount,
                    primaryIncumbent: primaryIncumbent,
                    directSearchPurpose: DirectSearchPurpose.SmartPotionGradient).Solve();
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
                    context, memoryForecast, layerAllocatedAtStart, layerTransitionsAtStart,
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
            bool improvesSelection = acceptable
                && IsBetterPotionPolicyResult(root, policy, candidate, selected);
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
                $"incumbent_certified_healing_bound_pruned={candidate.PrimaryIncumbentCertifiedHealingBoundBranchesPruned} " +
                $"incumbent_updates={candidate.PrimaryIncumbentUpdates}");
            if (HasReachedAcceptableBattleHpLoss(policy, selected)
                && TheftEncounterStrategy.RecoverySatisfied(
                    policy.TheftPolicy, selected.OutstandingStolenResource))
                break;
        }

        callerCancellationToken.ThrowIfCancellationRequested();
        MergeAuditTotals(selected, [.. searches]);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
            $"stop={(deadlineExpired ? "deadline" : acceptablePotionLayerFound ? "threshold_met" : "complete")} " +
            $"maximum={forcedPotionCount + maximumOptionalPotionUses} " +
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
        SearchPassContext context,
        SmartLayerMemoryForecast forecast,
        long processAllocatedAtStart,
        long transitionsAtStart,
        SolverResult? result,
        SolverSearchProfile profile,
        int completedPotionCount)
    {
        SearchPolicySnapshot policy = context.Policy;
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return;
        long processAllocated = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: false) - processAllocatedAtStart);
        long transitions = Math.Max(
            0,
            context.Budget.WorkTotals.Snapshot().TransitionCount - transitionsAtStart);
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
        SearchPassContext context,
        SolverResult totalsCarrier,
        SmartLayerMemoryForecast forecast,
        int completedPotionCount,
        int nextPotionCount)
    {
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SolverSearchProfile profile = context.Profile;
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
            TimeSpan? maxObservedGcPause = signal.LastReclaimMaxObservedGcPause;
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
            if (maxObservedGcPause is { } observedPause && observedPause > totalsCarrier.TotalMaxObservedGcPause)
                totalsCarrier.TotalMaxObservedGcPause = observedPause;
            totalsCarrier.TotalSearchElapsed += stopwatch.Elapsed;
            context.Budget.WorkTotals.RecordCoordinatorOverhead(
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
                $"max_observed_gc_pause_ms={maxObservedGcPause?.TotalMilliseconds.ToString("F1") ?? "unavailable"} " +
                signal.CaptureGcLifecycle().DeltaFrom(lifecycleBefore).ToDiagnosticString() + " " +
                $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                $"canceled={cancellationToken.IsCancellationRequested.ToString().ToLowerInvariant()}");
        }
    }

}
