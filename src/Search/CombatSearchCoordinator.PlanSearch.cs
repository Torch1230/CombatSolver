namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private const int MaximumPlanCommitments = 4;
    private const int MaximumDiscoveredPlanCommitments = 18;

    private readonly record struct PlanMemberExecution(
        int MaximumNodes,
        int MaximumMilliseconds,
        bool AggressivePowerCommitment,
        SolverPotionPolicy? PotionPolicyOverride,
        int? MaximumPotionUses,
        bool AllowUnsatisfiedPotion);

    private static SolverResult RunPlanSearchPass(SearchPassContext context, SolverResult baseline)
    {
        if (ReferenceEquals(context.PlanDiscovery.EarlyOpeningPlanProfile, context.Profile)
            || baseline.ResultScope != SolverResultScope.SearchCompletion
            || context.Policy.PotionStrategy.HasForcedDirectives
            || IsProvenZeroDamageRoute(context.Root, context.Policy, baseline)
            || IsCompleteVictory(baseline)
                && baseline.ProjectedBattleHpLost < SolverWeights.PotionMinimumHpSaved * 3)
            return baseline;

        IReadOnlyList<PlanCommitment> plans = DiscoverOpeningPlanCommitments(context);
        context.PlanDiscovery.OpeningPlanCount = plans.Count;
        if (plans.Count == 0)
            return baseline;

        return RunOpeningPlans(context, baseline, plans)!;
    }

    private static SolverResult? TryRunOpeningPlanIncumbent(
        SearchPassContext context, SolverPotionPolicy? potionPolicyOverride)
    {
        SearchPolicySnapshot policy = context.Policy;
        // Broad known-source eligibility permits pruning after a victory is found;
        // it does not establish that a speculative plan search is cheap enough to
        // run before the primary member. Keep the existing certified-root schedule.
        if (!context.Root.CanCertifyRemainingHealing
            || !policy.UseBeamWidthPortfolio
            || policy.UseNoveltyPortfolio || policy.IncludeTurnSetup
            || policy.PortfolioExperiment != null || policy.DevelopmentStrategy != null
            || policy.DisableRefinementIncumbentForTesting || policy.DisableOpeningPlanIncumbentForTesting
            || policy.EffectiveHasGrowthTargets || policy.RelicTargets.Count != 0
            || policy.PotionStrategy.HasForcedDirectives
            || (potionPolicyOverride ?? policy.PotionPolicy)
                is not (SolverPotionPolicy.Disabled or SolverPotionPolicy.Smart))
            return null;
        IReadOnlyList<PlanCommitment> plans = DiscoverOpeningPlanCommitments(context);
        if (plans.Count == 0 || plans.Any(plan => plan.UsesPotion))
            return null;
        policy.Diagnostics.Info("[CombatSolver/Test] EARLY_OPENING_PLAN_INCUMBENT start");
        SolverResult? result = RunOpeningPlans(context, null, plans);
        if (result != null && (result.ResultScope != SolverResultScope.SearchCompletion
            || BuildRefinementPrimarySearchIncumbent(context.Root, policy, potionPolicyOverride, result) != null))
        {
            context.PlanDiscovery.OpeningPlanCount = plans.Count;
            context.PlanDiscovery.EarlyOpeningPlanProfile = context.Profile;
            return result;
        }
        return null;
    }

    private static SolverResult? RunOpeningPlans(
        SearchPassContext context, SolverResult? baseline, IReadOnlyList<PlanCommitment> plans)
    {
        SearchPolicySnapshot policy = context.Policy;
        SolverResult? selected = baseline;
        FrontierContinuationScheduler scheduler = new(context);
        int attempted = 0;
        List<PlanCommitment> scheduled = [];
        foreach (int priority in plans.Select(plan => plan.Priority).Distinct()
                     .OrderByDescending(priority => priority))
        {
            PlanCommitment[][] payoffGroups = plans.Where(plan => plan.Priority == priority)
                .GroupBy(plan => plan.Payoff.SourceId)
                .Select(group => group.ToArray())
                .ToArray();
            for (int rank = 0; scheduled.Count < MaximumPlanCommitments; rank++)
            {
                bool found = false;
                foreach (PlanCommitment[] payoffGroup in payoffGroups)
                {
                    if (rank >= payoffGroup.Length)
                        continue;
                    found = true;
                    scheduled.Add(payoffGroup[rank]);
                    if (scheduled.Count == MaximumPlanCommitments)
                        break;
                }
                if (!found)
                    break;
            }
            if (scheduled.Count == MaximumPlanCommitments)
                break;
        }
        foreach (PlanCommitment plan in scheduled)
        {
            if (!TryRunPlanMember(context, scheduler, plan,
                    new PlanMemberExecution(60_000, 20_000,
                        plan.Kind == PlanCommitmentKind.CopyPower,
                        plan.UsesPotion ? SolverPotionPolicy.RequireAtLeastOne
                            : SolverPotionPolicy.Disabled,
                        plan.UsesPotion ? 1 : 0,
                        AllowUnsatisfiedPotion: true),
                    out SolverResult? candidate))
                break;
            attempted++;
            if (candidate == null)
                continue;
            if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                return candidate;
            if (baseline == null && BuildRefinementPrimarySearchIncumbent(context.Root, policy,
                    SolverPotionPolicy.Disabled, candidate) == null)
                continue;
            PopulateSingleSessionTotals(candidate);
            bool improved = selected == null
                || IsBetterPotionPolicyResult(context.Root, policy, candidate, selected);
            if (improved)
                selected = candidate;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] PLAN_SEARCH_MEMBER kind={plan.Kind} " +
                $"payoff={plan.Payoff.SourceId} potion={plan.UsesPotion} " +
                $"prefix={string.Join(',', plan.Prefix.Select(action =>
                    action.Kind == PlanActionKind.UsePotion
                        ? $"P:{action.PotionId}" + (action.Choice is { Cards.Count: > 0 } potionChoice
                            ? $"[{string.Join('+', potionChoice.Cards.Select(card => card.CardId))}]" : "")
                        : $"C:{action.CardId}" + (action.Choice is { Cards.Count: > 0 } choice
                            ? $"[{string.Join('+', choice.Cards.Select(card => card.CardId))}]" : "")))} " +
                $"opening={string.Join(',', candidate.BestNode.Actions
                    .TakeWhile(action => action.Turn <= context.Root.StartTurnNumber + 1)
                    .Take(16).Select(action => action.Kind == PlanActionKind.UsePotion
                        ? $"P:{action.PotionId}"
                        : action.Kind == PlanActionKind.PlayCard ? $"C:{action.CardId}" : "E"))} " +
                $"won={IsCompleteVictory(candidate)} hp_lost={candidate.ProjectedBattleHpLost} " +
                $"expanded={candidate.ExpandedNodes} selected={improved}");
            if (selected != null && IsProvenZeroDamageRoute(context.Root, policy, selected))
                break;
        }
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] PLAN_SEARCH result candidates={plans.Count} " +
            $"attempted={attempted} selected_hp_lost={selected?.ProjectedBattleHpLost.ToString() ?? "-"}");
        return selected;
    }

    private static SolverResult RunDeferredPowerPlanSearchPass(
        SearchPassContext context, SolverResult baseline)
    {
        if (context.PlanDiscovery.OpeningPlanCount != 0
            || baseline.ResultScope != SolverResultScope.SearchCompletion
            || context.Policy.PotionStrategy.HasForcedDirectives
            || context.Policy.Interaction?.CurrentTakeoverRequest != null
            || baseline.ProjectedBattleHpLost < SolverWeights.PotionMinimumHpSaved * 3
            || !context.Root.PlayerCardIds.Any(PowerCardValuationModels.Registry.ContainsCardId))
            return baseline;

        SearchBudgetWindow scoutWindow = context.Budget.RequestWindow(context.Profile);
        if (!scoutWindow.CanStart(45_000))
            return baseline;

        List<EarlyTurnFrontierCandidate> frontiers = [];
        SolverSearchProfile scoutProfile = scoutWindow.Limit(context.Profile,
            maximumNodes: 25_000, maximumMilliseconds: 8_000,
            reserveMilliseconds: 2_000) with
        {
            StopPortfolioAtHpTarget = false,
        };
        FrontierContinuationScheduler scheduler = new(context);
        scheduler.Dispatch(new ContinuationSearchRequest(context,
            ContinuationPurpose.PlanCommitment, [], scoutProfile,
            SolverPotionPolicy.Disabled, 0, null)
        {
            EarlyTurnScoutDepth = 1,
            EarlyTurnScoutObserver = (turns, candidates) =>
            {
                if (turns == 1)
                    frontiers.AddRange(candidates);
            },
        });

        CombatBeamSolver builder = new(context.Root, context.DisplayNames,
            context.BattleDamage, context.Policy, context.CancellationToken,
            context.ProgressCallback, context.Profile);
        PlanCommitment? plan = null;
        int inspected = 0;
        foreach (EarlyTurnFrontierCandidate frontier in frontiers.Take(8))
        {
            inspected++;
            PlanAction? power = builder.BuildPowerActionsAfterPrefix(frontier.Actions)
                .Select(action => (Action: action,
                    Registered: PowerCardValuationModels.Registry.TryGetCommitmentDescriptor(
                        action.CardId!, out PowerCommitmentDescriptor descriptor),
                    Descriptor: descriptor))
                .Where(item => item.Registered)
                .OrderByDescending(item => item.Descriptor.Priority)
                .Select(item => item.Action)
                .FirstOrDefault();
            if (power == null)
                continue;
            PlanAction[] prefix = [.. frontier.Actions, power];
            plan = new(PlanCommitmentKind.CrossTurnBenefit, prefix,
                context.Root.StartTurnNumber,
                new PlanPayoffEvidence(PlanPayoffEvidenceKind.CardPlayed,
                    power.CardId!, context.Root.StartTurnNumber),
                UsesPotion: false, Priority: 1);
            break;
        }
        if (plan == null)
        {
            context.Policy.Diagnostics.Info(
                $"[CombatSolver/Test] DEFERRED_POWER_PLAN frontiers={inspected} candidate=false");
            return baseline;
        }

        if (!TryRunPlanMember(context, scheduler, plan,
                new PlanMemberExecution(120_000, 60_000,
                    AggressivePowerCommitment: true,
                    PotionPolicyOverride: null,
                    MaximumPotionUses: null,
                    AllowUnsatisfiedPotion: false),
                out SolverResult? memberResult))
            return baseline;
        SolverResult candidate = memberResult!;
        if (candidate.ResultScope != SolverResultScope.SearchCompletion)
            return candidate;
        PopulateSingleSessionTotals(candidate);
        bool improved = IsBetterPotionPolicyResult(
            context.Root, context.Policy, candidate, baseline);
        context.Policy.Diagnostics.Info(
            $"[CombatSolver/Test] DEFERRED_POWER_PLAN frontiers={inspected} " +
            $"payoff={plan.Payoff.SourceId} won={IsCompleteVictory(candidate)} " +
            $"hp_lost={candidate.ProjectedBattleHpLost} potions={candidate.PotionCount} " +
            $"expanded={candidate.ExpandedNodes} selected={improved}");
        return improved ? candidate : baseline;
    }

    private static bool TryRunPlanMember(
        SearchPassContext context,
        FrontierContinuationScheduler scheduler,
        PlanCommitment plan,
        PlanMemberExecution execution,
        out SolverResult? candidate)
    {
        SearchBudgetWindow window = context.Budget.RequestWindow(context.Profile);
        if (!window.CanStart(7_000))
        {
            candidate = null;
            return false;
        }
        SolverSearchProfile memberProfile = window.Limit(context.Profile,
            maximumNodes: execution.MaximumNodes,
            maximumMilliseconds: execution.MaximumMilliseconds,
            reserveMilliseconds: 2_000) with
        {
            AggressivePowerCommitment = execution.AggressivePowerCommitment,
        };
        ContinuationSearchRequest request = new(context,
            ContinuationPurpose.PlanCommitment, plan.Prefix, memberProfile,
            execution.PotionPolicyOverride, execution.MaximumPotionUses, null)
        {
            Commitment = plan,
        };
        candidate = execution.AllowUnsatisfiedPotion
            ? scheduler.DispatchOptional(request, "PlanCommitment")
            : scheduler.Dispatch(request);
        return true;
    }

    private static IReadOnlyList<PlanCommitment> DiscoverOpeningPlanCommitments(
        SearchPassContext context)
    {
        CombatRootSnapshot root = context.Root;
        bool hasPower = root.PlayerCardIds.Any(PowerCardValuationModels.Registry.ContainsCardId);
        CombatBeamSolver builder = new(root, context.DisplayNames, context.BattleDamage,
            context.Policy, context.CancellationToken, context.ProgressCallback, context.Profile,
            potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
            maximumPotionUses: 1);
        DeferredCopyPlanRule[] copyRules = PlanMechanismRegistry.Default.DeferredCopies
            .Where(rule => builder.ContainsChoiceEffectInRoot(rule.Effect))
            .ToArray();
        if (copyRules.Length == 0 && !hasPower)
            return [];

        List<PlanCommitment> plans = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        Dictionary<(PlanCommitmentKind Kind, string Payoff), int> perPayoff = [];
        void Add(PlanCommitmentKind kind, PlanAction[] prefix, string payoff,
            int priority, int payoffTurnDelay = 1)
        {
            if (plans.Count >= MaximumDiscoveredPlanCommitments
                || perPayoff.GetValueOrDefault((kind, payoff)) >= 2
                || !seen.Add(PowerPrefixKey(prefix)))
                return;
            bool usesPotion = prefix.Any(action => action.Kind == PlanActionKind.UsePotion);
            plans.Add(new(kind, prefix, prefix[0].Turn,
                new PlanPayoffEvidence(PlanPayoffEvidenceKind.CardPlayed,
                    payoff, kind == PlanCommitmentKind.PowerCycle
                        ? prefix[0].Turn : checked(prefix[0].Turn + payoffTurnDelay)),
                usesPotion, priority));
            perPayoff[(kind, payoff)] = perPayoff.GetValueOrDefault((kind, payoff)) + 1;
        }

        int potionOpenings = 0;
        int setupOpenings = 0;
        int copyOptions = 0;
        if (copyRules.Length > 0 && context.Policy.PotionPolicy != SolverPotionPolicy.Disabled)
        {
            foreach (DeferredCopyPlanRule rule in copyRules)
            {
                IReadOnlyList<PlanAction> selectedPotions = builder.BuildOpeningPlanPotionActions(
                    rule.Effect, maximumActions: 6);
                foreach (PlanAction potion in selectedPotions)
                {
                    potionOpenings++;
                    List<PlanAction[]> openings = [[potion]];
                    openings.AddRange(builder.BuildOpeningHandSetupActions([potion],
                            maximumActions: 5,
                            desiredFollowUpEffect: rule.Effect)
                        .Select(setup => new[] { potion, setup }));
                    setupOpenings += openings.Count - 1;
                    foreach (PlanAction[] opening in openings)
                    {
                        foreach (PlanAction copy in builder.BuildOpeningCopyActionsAfterPrefix(
                                     opening, maximumPowerTargets: 3, maximumActions: 8,
                                     desiredEffect: rule.Effect))
                        {
                            copyOptions++;
                            string copiedCardId = copy.Choice!.Cards[0].CardId;
                            bool copiedPower = PowerCardValuationModels.Registry.ContainsCardId(copiedCardId);
                            PlanAction[] copyPrefix = [.. opening, copy];
                            if (copiedPower)
                            {
                                PlanAction? originalPower = builder.BuildPowerActionsAfterPrefix(copyPrefix)
                                    .FirstOrDefault(action => action.CardId == copiedCardId);
                                if (originalPower != null)
                                    Add(PlanCommitmentKind.CopyPower,
                                        [.. copyPrefix, originalPower], copiedCardId,
                                        priority: 4, payoffTurnDelay: rule.PayoffTurnDelay);
                            }
                            Add(copiedPower ? PlanCommitmentKind.CopyPower
                                    : PlanCommitmentKind.CopyCard,
                                copyPrefix, copiedCardId, copiedPower ? 3 : 2,
                                rule.PayoffTurnDelay);
                            if (plans.Count >= MaximumDiscoveredPlanCommitments)
                                break;
                        }
                        if (plans.Count >= MaximumDiscoveredPlanCommitments)
                            break;
                    }
                    if (plans.Count >= MaximumDiscoveredPlanCommitments)
                        break;
                }
                if (plans.Count >= MaximumDiscoveredPlanCommitments)
                    break;
            }
        }
        if (hasPower && plans.Count < MaximumDiscoveredPlanCommitments)
        {
            foreach (PlanAction power in builder.BuildOpeningPowerActions()
                         .Where(action => PowerCardValuationModels.Registry.ContainsCardId(action.CardId!))
                         .Take(2))
            {
                foreach (PlanAction setup in builder.BuildOpeningHandSetupActions([power]).Take(2))
                    Add(PlanCommitmentKind.PowerCycle, [power, setup], power.CardId!, priority: 1);
            }
            foreach (PlanAction setup in builder.BuildOpeningHandSetupActions().Take(2))
            {
                foreach (PlanAction power in builder.BuildPowerActionsAfterPrefix([setup])
                             .Where(action => PowerCardValuationModels.Registry.ContainsCardId(action.CardId!))
                             .Take(1))
                    Add(PlanCommitmentKind.PowerCycle, [setup, power], power.CardId!, priority: 1);
            }
        }
        context.Policy.Diagnostics.Info(
            $"[CombatSolver/Test] PLAN_SEARCH_DISCOVERY count={plans.Count} " +
            $"potion_openings={potionOpenings} setup_openings={setupOpenings} copy_options={copyOptions} " +
            $"copy={plans.Count(plan => plan.Kind is PlanCommitmentKind.CopyPower or PlanCommitmentKind.CopyCard)} " +
            $"power_cycle={plans.Count(plan => plan.Kind == PlanCommitmentKind.PowerCycle)}");
        return plans;
    }
}
