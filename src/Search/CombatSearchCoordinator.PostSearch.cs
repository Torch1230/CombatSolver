using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static SolverResult RunPostSearchPasses(
        SearchPassContext context,
        SolverResult selected,
        List<PlanAction[]> firstTurnAnchors,
        SearchInteractionState? interaction,
        Func<SolverResult?> currentCompleteAdoptableResult)
    {
        selected = RunEarlyPotionPairRescue(context, selected);
        selected = RunForcedPotionOpeningRescue(context, selected);
        selected = RunTurnBoundaryRescue(context, selected, firstTurnAnchors);
        selected = RunZeroCostOpeningRescue(context, selected);
        selected = RunMidCombatRefinement(context, selected);
        selected = RunTurnEndChoicePosterior(context, selected);
        selected = RunEarlierCopyDelayedDamage(context, selected);
        if (interaction?.CurrentTakeoverRequest?.Kind == SearchTakeoverKind.ApplyCurrentTurn
            && selected.ResultScope == SolverResultScope.SearchCompletion
            && currentCompleteAdoptableResult() is { } adoptable)
        {
            selected = adoptable;
        }
        selected = RunEarlyTurnExploration(context, selected);
        selected = RunDeferredPowerPlanSearchPass(context, selected);
        return RunGeneratedPotionChainSearchPass(context, selected);
    }

    private static SolverResult RunEarlyPotionPairRescue(SearchPassContext context, SolverResult selected)
    {
        CombatRootSnapshot root = context.Root;
        SearchPolicySnapshot policy = context.Policy;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && selected.OnlyDeathRoutesFound
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && root.SearchablePotionCount >= 2)
        {
            foreach (ContinuationSearchOutcome outcome in
                     new FrontierContinuationScheduler(context).Run(
                         new OpeningPotionPairContinuationSource(context),
                         ContinuationPurpose.EarlyPotionPair,
                         minimumRemainingMilliseconds: 5_000,
                         maximumNodes: 100_000,
                         maximumMilliseconds: 30_000,
                         reserveMilliseconds: 2_000,
                         potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                         potionBounds: () => (2, 2),
                         optionalPotionDiagnostic: nameof(ContinuationPurpose.EarlyPotionPair)))
            {
                PlanAction[] prefix = outcome.Request.Prefix;
                SolverResult pair = outcome.Result;
                bool improved = pair.ResultScope == SolverResultScope.SearchCompletion
                    && IsBetterPotionPolicyResult(root, policy, pair, selected);
                if (improved)
                    selected = pair;
                policy.Diagnostics.Info($"[CombatSolver/Test] " +
                    $"EARLY_POTION_PAIR prefix={prefix[0].PotionId}+{prefix[1].PotionId} " +
                    $"won={IsCompleteVictory(pair)} " +
                    $"hp_lost={pair.ProjectedBattleHpLost} selected={improved}");
            }
        }
        return selected;
    }

    private static SolverResult RunForcedPotionOpeningRescue(SearchPassContext context, SolverResult selected)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SearchBudgetLedger ledger = context.Budget;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && selected.OnlyDeathRoutesFound
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && policy.PotionStrategy.HasForcedDirectives
            && policy.PotionStrategy.Directives
                .Where(directive => directive.Directive == SolverPotionDirective.Force)
                .All(directive => !PotionUsePolicy.RequiresOpeningUse(directive.PotionId)))
        {
            PotionFreePolicyBaseline forcedBaseline = new(
                false, StrategicHpDeficit(root, policy, selected),
                selected.Snapshot.PlayerHp, selected.CombatEndedTurn)
            {
                DeathSaveUseCount = selected.Snapshot.ProjectedDeathSaveUseCount,
            };
            HashSet<string> attemptedOpenings = [];
            FrontierContinuationScheduler scheduler = new(context);
            for (int variant = 0; variant < 3; variant++)
            {
                SearchBudgetWindow discoveryWindow = ledger.RequestWindow(policy.Profile);
                if (!discoveryWindow.CanStart(30_000))
                    break;
                SolverSearchProfile discoveryProfile = discoveryWindow.Limit(policy.Profile,
                    maximumNodes: 40_000, maximumMilliseconds: 12_000,
                    reserveMilliseconds: 2_000) with
                {
                    SecondRankBand = variant == 1,
                    BaseScoreOnly = variant == 2,
                    AggressivePowerCommitment = variant == 0,
                };
                SolverResult discovery = new CombatBeamSolver(root, displayNames,
                    battleDamage, policy, cancellationToken, progressCallback,
                    discoveryProfile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                    maximumPotionUses: 0,
                    directSearchPurpose: DirectSearchPurpose.TurnBoundaryDiscovery).Solve();
                PlanAction[] opening = discovery.BestNode.Actions
                    .TakeWhile(action => action.Turn == root.StartTurnNumber)
                    .ToArray();
                if (opening.LastOrDefault()?.Kind != PlanActionKind.EndTurn)
                    continue;
                CombatBeamSolver targetBuilder = new(root, displayNames,
                    battleDamage, policy, cancellationToken, progressCallback,
                    discoveryProfile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                    maximumPotionUses: 0);
                IReadOnlyList<PlanAction[]> targetVariants = targetBuilder
                    .BuildOpeningFocusedTargetPrefixes(opening);
                foreach (PlanAction[] focusedOpening in new[] { opening }
                             .Concat(targetVariants))
                {
                        if (!attemptedOpenings.Add(PowerPrefixKey(focusedOpening)))
                            continue;
                        SearchBudgetWindow continuationWindow = ledger.RequestWindow(policy.Profile);
                        if (!continuationWindow.CanStart(20_000))
                            break;
                        SolverSearchProfile continuationProfile = continuationWindow.Limit(
                            policy.Profile, maximumNodes: 60_000, maximumMilliseconds: 18_000,
                            reserveMilliseconds: 2_000);
                    int forcedPotionCount = policy.PotionStrategy.ForcedDirectiveCount;
                    int potionCount = Math.Min(root.SearchablePotionCount,
                        forcedPotionCount + 1);
                    SolverResult continuation;
                    try
                    {
                        continuation = scheduler.Dispatch(new ContinuationSearchRequest(
                            context, ContinuationPurpose.ForcedPotionOpening,
                            focusedOpening, continuationProfile,
                            potionCount > forcedPotionCount
                                ? SolverPotionPolicy.RequireAtLeastOne : null,
                            potionCount, potionCount)
                        {
                            PotionFreePolicyBaseline = forcedBaseline,
                            EarliestPotionTurn = root.EncounterRoomType == RoomType.Boss
                                ? root.StartTurnNumber
                                    + SolverWeights.BossEnemyStrengthSuppressionHorizon / 2 + 1
                                : null,
                        });
                    }
                    catch (PotionPolicyUnsatisfiedException)
                    {
                        continue;
                    }
                    bool improved = continuation.ResultScope == SolverResultScope.SearchCompletion
                        && policy.PotionStrategy.EvaluateForcedUses(
                            continuation.BestNode.Actions, renewablePotionShapedRock: false)
                            .AllForcedUsesSatisfied
                        && IsBetterPotionPolicyResult(root, policy, continuation, selected);
                    policy.Diagnostics.Info($"[CombatSolver/Test] FORCED_POTION_OPENING_RESCUE " +
                        $"opening={string.Join('+', focusedOpening.Select(action =>
                            $"{action.CardId}:{action.TargetCombatId}"))} " +
                        $"won={IsCompleteVictory(continuation)} " +
                        $"hp_lost={continuation.ProjectedBattleHpLost} " +
                        $"potions={continuation.PotionCount} " +
                        $"selected={improved}");
                    if (improved)
                        selected = continuation;
                    if (IsCompleteVictory(continuation))
                        continue;
                    PlanAction[] nextTurn = continuation.BestNode.Actions
                        .Where(action => action.Turn == root.StartTurnNumber + 1)
                        .Take(2).ToArray();
                    if (nextTurn.Length != 2
                        || nextTurn.Any(action => action.Kind != PlanActionKind.PlayCard))
                        continue;
                        PlanAction[] reordered = [.. focusedOpening, nextTurn[1], nextTurn[0]];
                        if (!targetBuilder.CanReplayOpeningPrefix(reordered))
                            continue;
                        SearchBudgetWindow reorderedWindow = ledger.RequestWindow(policy.Profile);
                        if (!reorderedWindow.CanStart(20_000))
                            break;
                        SolverSearchProfile reorderedProfile = reorderedWindow.Limit(
                            continuationProfile, maximumNodes: 60_000, maximumMilliseconds: 18_000,
                            reserveMilliseconds: 2_000);
                    SolverResult reorderedResult;
                    try
                    {
                        reorderedResult = scheduler.Dispatch(new ContinuationSearchRequest(
                            context, ContinuationPurpose.ForcedPotionTurnOrder,
                            reordered, reorderedProfile,
                            potionCount > forcedPotionCount
                                ? SolverPotionPolicy.RequireAtLeastOne : null,
                            potionCount, potionCount)
                        {
                            PotionFreePolicyBaseline = forcedBaseline,
                            EarliestPotionTurn = root.EncounterRoomType == RoomType.Boss
                                ? root.StartTurnNumber
                                    + SolverWeights.BossEnemyStrengthSuppressionHorizon / 2 + 1
                                : null,
                        });
                    }
                    catch (PotionPolicyUnsatisfiedException)
                    {
                        continue;
                    }
                    bool reorderedImproved = reorderedResult.ResultScope
                            == SolverResultScope.SearchCompletion
                        && policy.PotionStrategy.EvaluateForcedUses(
                            reorderedResult.BestNode.Actions, renewablePotionShapedRock: false)
                            .AllForcedUsesSatisfied
                        && IsBetterPotionPolicyResult(root, policy, reorderedResult, selected);
                    policy.Diagnostics.Info($"[CombatSolver/Test] FORCED_POTION_TURN_ORDER " +
                        $"prefix={nextTurn[1].CardId}+{nextTurn[0].CardId} " +
                        $"won={IsCompleteVictory(reorderedResult)} " +
                        $"hp_lost={reorderedResult.ProjectedBattleHpLost} " +
                        $"potions={reorderedResult.PotionCount} selected={reorderedImproved}");
                    if (reorderedImproved)
                        selected = reorderedResult;
                }
            }
        }
        return selected;
    }

    private static SolverResult RunTurnBoundaryRescue(
        SearchPassContext context,
        SolverResult selected,
        List<PlanAction[]> firstTurnAnchors)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SearchBudgetLedger ledger = context.Budget;
        bool rescueAfterDeath = selected.OnlyDeathRoutesFound
            && root.SearchablePotionCount > 0;
        bool refineNearZeroLoss = IsCompleteVictory(selected)
            && selected.ExplicitPotionCount == 0
            && selected.ProjectedBattleHpLost is > 0 and <= SolverWeights.PotionMinimumHpSaved;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && (rescueAfterDeath || refineNearZeroLoss))
        {
            FrontierContinuationScheduler scheduler = new(context);
            // Revisit distinct first-turn states while the shared request still has time and nodes.
            foreach (ContinuationSearchOutcome outcome in
                     scheduler.Run(
                         new TurnBoundaryContinuationSource(firstTurnAnchors, PowerPrefixKey),
                         ContinuationPurpose.TurnBoundaryRescue,
                         minimumRemainingMilliseconds: 5_000,
                         maximumNodes: 80_000,
                         maximumMilliseconds: 20_000,
                         reserveMilliseconds: 2_000,
                         potionPolicyOverride: rescueAfterDeath
                             ? SolverPotionPolicy.RequireAtLeastOne : SolverPotionPolicy.Disabled,
                         potionBounds: () => (rescueAfterDeath
                             ? MaximumSmartPotionUses(root, policy, potionFreeWon: false,
                                 potionFreeHpDeficit: 0)
                             : 0, null),
                         optionalPotionDiagnostic: "TURN_BOUNDARY_RESCUE"))
            {
                PlanAction[] firstTurn = outcome.Request.Prefix;
                SolverResult rescue = outcome.Result;
                policy.Diagnostics.Info($"[CombatSolver/Test] TURN_BOUNDARY_RESCUE " +
                    $"won={IsCompleteVictory(rescue)} hp_lost={rescue.ProjectedBattleHpLost} " +
                    $"potions={rescue.PotionCount} first_turn={string.Join('+', firstTurn.Select(action => action.CardId))}");
                if (rescue.ResultScope == SolverResultScope.SearchCompletion
                    && IsBetterPotionPolicyResult(root, policy, rescue, selected))
                    selected = rescue;
            }
            if (refineNearZeroLoss && selected.ProjectedBattleHpLost > 0)
            {
                CombatBeamSolver NewPrefixBuilder() => new(root, displayNames, battleDamage,
                    policy, cancellationToken, progressCallback, policy.Profile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled,
                    maximumPotionUses: 0);
                int continuationAttempts = 0;
                foreach (PlanAction freeAction in NewPrefixBuilder().BuildOpeningFreeOffensiveActions())
                {
                    foreach (PlanAction[] firstTurn in firstTurnAnchors
                                 .DistinctBy(PowerPrefixKey).Take(4))
                    {
                        if (firstTurn.LastOrDefault()?.Kind != PlanActionKind.EndTurn)
                            continue;
                        SearchBudgetWindow prefixWindow = ledger.RequestWindow(policy.Profile);
                        if (!prefixWindow.CanStart(5_000))
                            break;
                        PlanAction[] combinedPrefix = [freeAction, .. firstTurn];
                        if (!NewPrefixBuilder().CanReplayOpeningPrefix(combinedPrefix))
                            continue;
                        SolverSearchProfile prefixProfile = prefixWindow.Limit(policy.Profile,
                            maximumNodes: 30_000, maximumMilliseconds: 10_000,
                            reserveMilliseconds: 2_000);
                        SolverResult freeRescue = scheduler.Dispatch(new ContinuationSearchRequest(
                            context, ContinuationPurpose.TurnBoundaryFreeOpening,
                            combinedPrefix, prefixProfile, SolverPotionPolicy.Disabled, 0, null));
                        if (freeRescue.ResultScope == SolverResultScope.SearchCompletion
                            && IsBetterPotionPolicyResult(root, policy, freeRescue, selected))
                            selected = freeRescue;
                        if (selected.ProjectedBattleHpLost == 0)
                            break;
                        PlanAction[] nextTurnPrefix = combinedPrefix;
                        for (int defenseCount = 0; defenseCount < 2; defenseCount++)
                        {
                            PlanAction? nextDefense = NewPrefixBuilder()
                                .BuildOpeningDefensiveFollowUp(nextTurnPrefix);
                            if (nextDefense == null)
                                break;
                            nextTurnPrefix = [.. nextTurnPrefix, nextDefense];
                            foreach (PlanAction nextAttack in NewPrefixBuilder()
                                         .BuildOpeningOffensiveCardVariantsAfterPrefix(nextTurnPrefix).Take(3))
                            {
                                SearchBudgetWindow continuationWindow = ledger.RequestWindow(policy.Profile);
                                if (!continuationWindow.CanStart(5_000))
                                    break;
                                SolverSearchProfile continuationProfile = continuationWindow.Limit(
                                    prefixProfile, maximumNodes: 30_000, maximumMilliseconds: 10_000,
                                    reserveMilliseconds: 2_000);
                                SolverResult continuation = scheduler.Dispatch(new ContinuationSearchRequest(
                                    context, ContinuationPurpose.TurnBoundaryDefensiveFollowUp,
                                    [.. nextTurnPrefix, nextAttack,
                                        new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber + 1)],
                                    continuationProfile, SolverPotionPolicy.Disabled, 0, null));
                                if (continuation.ResultScope == SolverResultScope.SearchCompletion
                                    && IsBetterPotionPolicyResult(root, policy, continuation, selected))
                                    selected = continuation;
                                if (selected.ProjectedBattleHpLost == 0
                                    || ++continuationAttempts >= 8)
                                    break;
                            }
                            if (selected.ProjectedBattleHpLost == 0 || continuationAttempts >= 8)
                                break;
                        }
                        if (selected.ProjectedBattleHpLost == 0 || continuationAttempts >= 8)
                            break;
                    }
                    if (selected.ProjectedBattleHpLost == 0 || continuationAttempts >= 8)
                        break;
                }
            }
        }
        return selected;
    }

    private static SolverResult RunZeroCostOpeningRescue(SearchPassContext context, SolverResult selected)
    {
        SearchPolicySnapshot policy = context.Policy;
        SearchBudgetLedger ledger = context.Budget;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && !policy.IncludeTurnSetup
            && IsCompleteVictory(selected)
            && selected.ExplicitPotionCount == 0
            && selected.ProjectedBattleHpLost > 0
            && selected.BestNode.Actions.FirstOrDefault()?.Kind == PlanActionKind.EndTurn)
        {
            OpeningNoCostContinuationSource source = new(context);
            int attempts = 0;
            if (ledger.RemainingRequestMillisecondsLong > 5_000)
            {
                foreach (ContinuationSearchOutcome outcome in
                         new FrontierContinuationScheduler(context).Run(
                             source, ContinuationPurpose.NoCostOpening,
                             minimumRemainingMilliseconds: 5_000,
                             maximumNodes: 40_000,
                             maximumMilliseconds: 12_000,
                             reserveMilliseconds: 2_000,
                             potionPolicyOverride: SolverPotionPolicy.Disabled,
                             potionBounds: () => (0, null)))
                {
                    PlanAction[] prefix = outcome.Request.Prefix;
                    SolverResult candidate = outcome.Result;
                    bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                        && IsBetterPotionPolicyResult(context.Root, policy, candidate, selected);
                    if (improved)
                        selected = candidate;
                    policy.Diagnostics.Info($"[CombatSolver/Test] NO_COST_OPENING " +
                        $"prefix={string.Join('+', prefix.Select(action => action.CardId))} " +
                        $"hp_lost={candidate.ProjectedBattleHpLost} selected={improved}");
                    if (selected.ProjectedBattleHpLost == 0 || ++attempts >= 8)
                        break;
                }
            }
        }
        return selected;
    }

    private static SolverResult RunMidCombatRefinement(SearchPassContext context, SolverResult selected)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SearchBudgetLedger ledger = context.Budget;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && IsCompleteVictory(selected)
            && selected.ProjectedBattleHpLost > 0
            && selected.ExplicitPotionCount > 0
            && selected.CombatEndedTurn >= root.StartTurnNumber + 6)
        {
            PlanAction[] prefix = selected.BestNode.Actions
                .TakeWhile(action => action.Turn < root.StartTurnNumber + 3)
                .ToArray();
            SearchBudgetWindow refinementWindow = ledger.RequestWindow(policy.Profile);
            if (prefix.LastOrDefault()?.Kind == PlanActionKind.EndTurn
                && prefix.All(action => action.Kind != PlanActionKind.UsePotion)
                && refinementWindow.CanStart(20_000))
            {
                CombatBeamSolver prefixBuilder = new(root, displayNames, battleDamage,
                    policy, cancellationToken, progressCallback, policy.Profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: selected.ExplicitPotionCount);
                FrontierContinuationScheduler scheduler = new(context);
                SolverSearchProfile refinementProfile = refinementWindow.Limit(policy.Profile,
                    maximumNodes: 50_000, maximumMilliseconds: 15_000,
                    reserveMilliseconds: 2_000);
                SolverResult? refined = scheduler.DispatchOptional(
                    new ContinuationSearchRequest(context, ContinuationPurpose.MidCombatRefinement,
                        prefix, refinementProfile, SolverPotionPolicy.RequireAtLeastOne,
                        selected.ExplicitPotionCount, selected.ExplicitPotionCount),
                    "MID_COMBAT_REFINEMENT");
                if (refined?.ResultScope == SolverResultScope.SearchCompletion)
                {
                    bool improved = refined.ProjectedBattleHpLost < selected.ProjectedBattleHpLost
                        && IsBetterPotionPolicyResult(root, policy, refined, selected);
                    policy.Diagnostics.Info($"[CombatSolver/Test] MID_COMBAT_REFINEMENT " +
                        $"hp_lost={refined.ProjectedBattleHpLost} " +
                        $"potions={refined.PotionCount} selected={improved}");
                    if (improved)
                        selected = refined;
                }

                PlanAction[] fourthTurn = selected.BestNode.Actions
                    .TakeWhile(action => action.Turn <= root.StartTurnNumber + 3)
                    .ToArray();
                SearchBudgetWindow followUpWindow = ledger.RequestWindow(policy.Profile);
                if (fourthTurn.Length > prefix.Length + 1
                    && fourthTurn.Last().Kind == PlanActionKind.EndTurn
                    && fourthTurn.All(action => action.Kind != PlanActionKind.UsePotion)
                    && fourthTurn[^2].Kind == PlanActionKind.PlayCard
                    && followUpWindow.CanStart(18_000))
                {
                    PlanAction[] beforeEndTurn = fourthTurn[..^1];
                    PlanAction? followUp = prefixBuilder
                        .BuildFreeOffensiveActionsAfterPrefix(beforeEndTurn)
                        .FirstOrDefault(action => action.CardId == beforeEndTurn[^1].CardId);
                    if (followUp != null)
                    {
                        PlanAction[] freePrefix = [.. beforeEndTurn, followUp, fourthTurn[^1]];
                        SearchBudgetWindow freeWindow = ledger.RequestWindow(policy.Profile);
                        if (freeWindow.CanStart(18_000)
                            && prefixBuilder.CanReplayOpeningPrefix(freePrefix))
                        {
                            SolverSearchProfile freeProfile = freeWindow.Limit(policy.Profile,
                                maximumNodes: 50_000, maximumMilliseconds: 15_000,
                                reserveMilliseconds: 2_000);
                            SolverResult? freeRefined = scheduler.DispatchOptional(
                                new ContinuationSearchRequest(context,
                                    ContinuationPurpose.MidCombatFreeFollowUp,
                                    freePrefix, freeProfile, SolverPotionPolicy.RequireAtLeastOne,
                                    selected.ExplicitPotionCount, selected.ExplicitPotionCount),
                                "MID_COMBAT_FREE_FOLLOW_UP");
                            if (freeRefined?.ResultScope == SolverResultScope.SearchCompletion)
                            {
                                bool improved = freeRefined.ProjectedBattleHpLost < selected.ProjectedBattleHpLost
                                    && IsBetterPotionPolicyResult(root, policy, freeRefined, selected);
                                policy.Diagnostics.Info($"[CombatSolver/Test] MID_COMBAT_FREE_FOLLOW_UP " +
                                    $"hp_lost={freeRefined.ProjectedBattleHpLost} " +
                                    $"potions={freeRefined.PotionCount} selected={improved}");
                                if (improved)
                                    selected = freeRefined;
                            }
                        }
                    }
                }
            }
        }
        return selected;
    }

    private static SolverResult RunTurnEndChoicePosterior(SearchPassContext context, SolverResult selected)
    {
        CombatRootSnapshot root = context.Root;
        SolverDisplayNames displayNames = context.DisplayNames;
        BattleDamageSnapshot battleDamage = context.BattleDamage;
        SearchPolicySnapshot policy = context.Policy;
        CancellationToken cancellationToken = context.CancellationToken;
        Action<SolverProgress>? progressCallback = context.ProgressCallback;
        SearchBudgetLedger ledger = context.Budget;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && IsCompleteVictory(selected)
            && selected.ProjectedBattleHpLost > 0
            && selected.ExplicitPotionCount == 0)
        {
            PlanAction[] firstTurn = selected.BestNode.Actions
                .TakeWhile(action => action.Turn == root.StartTurnNumber)
                .ToArray();
            if (firstTurn.LastOrDefault() is
                { Kind: PlanActionKind.EndTurn, TurnStartChoices: { Count: > 0 } } chosenEndTurn
                && ledger.RemainingRequestMillisecondsLong > 25_000)
            {
                CombatBeamSolver choiceBuilder = new(root, displayNames, battleDamage,
                    policy, cancellationToken, progressCallback, policy.Profile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled,
                    maximumPotionUses: 0);
                PlanAction[] beforeEndTurn = firstTurn[..^1];
                string chosenKey = CombatBeamSolver.TurnEndChoiceKey(chosenEndTurn);
                foreach (ContinuationSearchOutcome outcome in
                         new FrontierContinuationScheduler(context).Run(
                             new TurnEndChoiceContinuationSource(
                                 choiceBuilder, beforeEndTurn, chosenKey),
                             ContinuationPurpose.TurnEndChoice,
                             minimumRemainingMilliseconds: 25_000,
                             maximumNodes: 80_000,
                             maximumMilliseconds: 20_000,
                             reserveMilliseconds: 2_000,
                             potionPolicyOverride: SolverPotionPolicy.Disabled,
                             potionBounds: () => (0, null)))
                {
                    PlanAction alternative = outcome.Request.Prefix[^1];
                    SolverResult candidate = outcome.Result;
                    bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                        && candidate.ProjectedBattleHpLost < selected.ProjectedBattleHpLost
                        && IsBetterPotionPolicyResult(root, policy, candidate, selected);
                    policy.Diagnostics.Info($"[CombatSolver/Test] TURN_END_CHOICE_POSTERIOR " +
                        $"choice={CombatBeamSolver.TurnEndChoiceKey(alternative)} " +
                        $"hp_lost={candidate.ProjectedBattleHpLost} " +
                        $"potions={candidate.PotionCount} selected={improved}");
                    if (improved)
                        selected = candidate;
                }
                PlanAction[] selectedFirstTurn = selected.BestNode.Actions
                    .TakeWhile(action => action.Turn == root.StartTurnNumber)
                    .ToArray();
                if (selectedFirstTurn.LastOrDefault() is
                    { Kind: PlanActionKind.EndTurn, TurnStartChoices: { Count: > 0 } } selectedEndTurn
                    && CombatBeamSolver.TurnEndChoiceKey(selectedEndTurn) != chosenKey)
                {
                    PlanAction[] played = selectedFirstTurn[..^1];
                    for (int index = 0; index + 1 < played.Length; index++)
                    {
                        PlanAction retrieval = played[index];
                        PlanAction followUp = played[index + 1];
                        if (retrieval.Choice is not { Effect: PlanChoiceEffect.MoveToHand } choice
                            || followUp.Kind != PlanActionKind.PlayCard
                            || !choice.Cards.Any(card => card.CardId == followUp.CardId))
                            continue;
                        PlanAction[] shorter = [.. played[..index], .. played[(index + 2)..]];
                        if (ledger.RemainingRequestMillisecondsLong <= 25_000)
                            break;
                        PlanAction? endTurn = choiceBuilder
                            .BuildTurnEndChoiceActionsAfterPrefix(shorter)
                            .FirstOrDefault(action => CombatBeamSolver.TurnEndChoiceKey(action)
                                == CombatBeamSolver.TurnEndChoiceKey(selectedEndTurn));
                        if (endTurn == null)
                            continue;
                        foreach (ContinuationSearchOutcome outcome in
                                 new FrontierContinuationScheduler(context).Run(
                                     new SinglePrefixContinuationSource([.. shorter, endTurn]),
                                     ContinuationPurpose.TurnEndChoiceShorterOpening,
                                     minimumRemainingMilliseconds: 25_000,
                                     maximumNodes: 80_000,
                                     maximumMilliseconds: 20_000,
                                     reserveMilliseconds: 2_000,
                                     potionPolicyOverride: SolverPotionPolicy.Disabled,
                                     potionBounds: () => (0, null)))
                        {
                            SolverResult candidate = outcome.Result;
                            bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                                && candidate.ProjectedBattleHpLost < selected.ProjectedBattleHpLost
                                && IsBetterPotionPolicyResult(root, policy, candidate, selected);
                            policy.Diagnostics.Info($"[CombatSolver/Test] TURN_END_CHOICE_SHORTER_OPENING " +
                                $"hp_lost={candidate.ProjectedBattleHpLost} " +
                                $"potions={candidate.PotionCount} selected={improved}");
                            if (improved)
                                selected = candidate;
                        }
                        break;
                    }
                }
            }
        }
        return selected;
    }

    private static SolverResult RunEarlierCopyDelayedDamage(SearchPassContext context, SolverResult selected)
    {
        CombatRootSnapshot root = context.Root;
        SearchPolicySnapshot policy = context.Policy;
        if (selected.ResultScope == SolverResultScope.SearchCompletion
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && IsCompleteVictory(selected)
            && selected.ProjectedBattleHpLost > 0
            && selected.BestNode.Actions.Any(action => action is
                { Kind: PlanActionKind.UsePotion, PotionId: "DUPLICATOR" }))
        {
            foreach (ContinuationSearchOutcome outcome in
                     new FrontierContinuationScheduler(context).Run(
                         new EarlierCopyDelayedDamageContinuationSource(
                             context, selected.BestNode.Actions),
                         ContinuationPurpose.EarlierCopyDelayedDamage,
                         minimumRemainingMilliseconds: 20_000,
                         maximumNodes: 80_000,
                         maximumMilliseconds: 18_000,
                         reserveMilliseconds: 2_000,
                         potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                         potionBounds: () => (selected.ExplicitPotionCount,
                             selected.ExplicitPotionCount)))
            {
                PlanAction[] prefix = outcome.Request.Prefix;
                SolverResult candidate = outcome.Result;
                bool improved = candidate.ResultScope == SolverResultScope.SearchCompletion
                    && candidate.ProjectedBattleHpLost < selected.ProjectedBattleHpLost
                    && IsBetterPotionPolicyResult(root, policy, candidate, selected);
                policy.Diagnostics.Info($"[CombatSolver/Test] EARLIER_COPY_DELAYED_DAMAGE " +
                    $"target={prefix[^1].CardId} " +
                    $"hp_lost={candidate.ProjectedBattleHpLost} " +
                    $"potions={candidate.PotionCount} selected={improved}");
                if (improved)
                    selected = candidate;
            }
        }
        return selected;
    }
}
