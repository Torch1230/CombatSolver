using System.Diagnostics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    internal static string DescribePlanMemberStateForTesting(
        CombatPredictionSimulator simulator, CombatRootSnapshot root)
        => DescribeContinuationContractState(simulator, root, root.PlayerIdentity);

    private async Task AssertPlanMemberIncumbentAsync(CombatState live, Player player,
        SearchPolicySnapshot policy, CancellationToken token)
    {
        // A one-hit kill never reaches intermediate retention. Require two real
        // attacks so the forwarded witness is exercised before a terminal child.
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 12);
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        SolverDisplayNames names = SolverDisplayNames.Capture(live);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(live);
        SolverResult witness = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
            policy with { PotionPolicy = SolverPotionPolicy.Disabled,
                PotionStrategy = new(SolverPotionPolicy.Disabled, []) }, token, null), token);
        PrimarySearchIncumbent? seed = CombatSearchCoordinator.BuildPlanMemberPrimaryIncumbent(
            root, policy, null, witness);
        if (seed is not { ExplicitPotionStrategicCost: 0 }
            || seed.Value.CombatEndedTurn != witness.CombatEndedTurn)
            throw new InvalidOperationException("Plan member lost its actual complete potion-free witness.");
        if (CombatSearchCoordinator.BuildPlanMemberPrimaryIncumbent(root, policy,
                SolverPotionPolicy.RequireAtLeastOne, witness) is not null
            || CombatSearchCoordinator.BuildPlanMemberPrimaryIncumbent(root, policy,
                SolverPotionPolicy.Disabled, witness) is null)
            throw new InvalidOperationException("Plan proof ignored the member's effective potion policy.");
        foreach (var guarded in new[]
        {
            policy with { DisableRefinementIncumbentForTesting = true },
            policy with { PotionPolicy = SolverPotionPolicy.RequireAtLeastOne },
            policy with { TheftPolicy = SolverTheftPolicy.PreserveResources },
            policy with { GrowthOpportunityTargets = GrowthOpportunityTargets.UnboundedForTesting("plan_seed_guard") },
            policy with { RelicTargets = [new(RelicCounterId.PenNib, 2, 2, 1, 10)] },
            policy with { PotionStrategy = new(SolverPotionPolicy.Smart,
                [new(0, "COLORLESS_POTION", SolverPotionDirective.Force)]) },
        })
            if (CombatSearchCoordinator.BuildPlanMemberPrimaryIncumbent(root, guarded, null, witness) is not null)
                throw new InvalidOperationException("Plan witness ignored policy/resource guards.");
        if (CombatSearchCoordinator.BuildPlanMemberPrimaryIncumbent(root,
                policy with { RelicTargets = [new(RelicCounterId.PenNib, 2, 2, 0, 10)] }, null, witness) is null)
            throw new InvalidOperationException("Plan witness rejected a zero HP allowance relic goal.");
        SolverResultScope originalScope = witness.ResultScope;
        try
        {
            witness.ResultScope = SolverResultScope.CurrentTurnAdoption;
            if (CombatSearchCoordinator.BuildPlanMemberPrimaryIncumbent(root, policy, null, witness) is not null)
                throw new InvalidOperationException("A partial preview became a complete plan witness.");
        }
        finally { witness.ResultScope = originalScope; }

        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        var parent = root.ForkSimulator();
        string parentBefore = DescribePlanMemberStateForTesting(parent, root);
        PlanAction prefix = await Task.Run(() => CombatBeamSolver.VerifyPlanMemberSeedForTesting(
            root, names, damage, policy, seed.Value, token), token);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            CombatBeamSolver.VerifyPlanMemberSeedForTesting(root, names, damage, policy, seed.Value, token), token)));

        foreach (int dop in new[] { 1, 16 })
        {
            SearchPolicySnapshot memberPolicy = policy with
            {
                MaxDegreeOfParallelism = dop, UseBeamWidthPortfolio = false,
                DisableSharedPrimaryIncumbentsForTesting = true, PrimaryIncumbents = new(),
            };
            SearchPassContext Context()
            {
                Stopwatch clock = Stopwatch.StartNew();
                SearchBudgetLedger budget = new(clock, memberPolicy);
                SearchPolicySnapshot input = memberPolicy with { RequestWorkTotals = budget.WorkTotals };
                return new(root, names, damage, input, input.Profile, clock,
                    budget, token, null, null);
            }
            PlanCommitment plan = new(PlanCommitmentKind.CrossTurnBenefit, [prefix], root.StartTurnNumber,
                new(PlanPayoffEvidenceKind.CardPlayed, prefix.CardId!, root.StartTurnNumber), false, 1);
            var controlContext = Context();
            SolverResult control = await Task.Run(() => CombatSearchCoordinator.DispatchPlanMemberForTesting(
                controlContext, plan, null), token);
            var boundedContext = Context();
            SolverResult bounded = await Task.Run(() => CombatSearchCoordinator.DispatchPlanMemberForTesting(
                boundedContext, plan, witness), token);
            if (!control.Snapshot.AllEnemiesDead || control.Snapshot.HasRisk
                || bounded.ExpandedNodes >= control.ExpandedNodes)
                throw new InvalidOperationException($"Plan continuation did not consume its complete witness: "
                    + $"dop={dop} control_won={control.Snapshot.AllEnemiesDead} control_risk={control.Snapshot.HasRisk} "
                    + $"expanded={control.ExpandedNodes}/{bounded.ExpandedNodes}.");
            var candidateQuality = CombatSearchCoordinator.CapturePortfolioQuality(root, policy, bounded);
            var witnessQuality = CombatSearchCoordinator.CapturePortfolioQuality(root, policy, witness);
            bool improved = CombatSearchCoordinator.IsBetterPotionPolicyResult(policy.TheftPolicy,
                candidateQuality, witnessQuality);
            SolverResult selected = improved ? bounded : witness;
            if (selected.ProjectedBattleHpLost > witness.ProjectedBattleHpLost || selected.ExplicitPotionCount != 0
                || selected.ResultScope != SolverResultScope.SearchCompletion
                || !selected.Snapshot.AllEnemiesDead || selected.Snapshot.HasRisk)
                throw new InvalidOperationException("Caller lost the actual victory after plan pruning.");
            if (!bounded.Snapshot.AllEnemiesDead
                && CombatSearchCoordinator.BuildPlanMemberPrimaryIncumbent(root, policy, null, bounded) is not null)
                throw new InvalidOperationException("A failed continuation became a plan witness.");

            var loopControlContext = Context();
            loopControlContext = loopControlContext with
            {
                Policy = loopControlContext.Policy with { DisableRefinementIncumbentForTesting = true },
            };
            var loopBoundedContext = Context();
            SolverResult loopControl = await Task.Run(() =>
                CombatSearchCoordinator.RunPlanMembersForTesting(loopControlContext, witness, [plan, plan]), token);
            SolverResult loopBounded = await Task.Run(() =>
                CombatSearchCoordinator.RunPlanMembersForTesting(loopBoundedContext, witness, [plan, plan]), token);
            if (!ReferenceEquals(loopBounded, witness) || !ReferenceEquals(loopControl, witness)
                || loopBoundedContext.Budget.WorkTotals.Snapshot().ExpandedNodes
                    >= loopControlContext.Budget.WorkTotals.Snapshot().ExpandedNodes)
                throw new InvalidOperationException($"Plan loop witness/proof mismatch: dop={dop} "
                    + $"same_control={ReferenceEquals(loopControl, witness)} "
                    + $"same_bounded={ReferenceEquals(loopBounded, witness)} "
                    + $"expanded={loopControlContext.Budget.WorkTotals.Snapshot().ExpandedNodes}/"
                    + $"{loopBoundedContext.Budget.WorkTotals.Snapshot().ExpandedNodes} "
                    + $"quality_witness={CombatSearchCoordinator.CapturePortfolioQuality(root, memberPolicy, witness)} "
                    + $"quality_control={CombatSearchCoordinator.CapturePortfolioQuality(root, memberPolicy, loopControl)} "
                    + $"quality_bounded={CombatSearchCoordinator.CapturePortfolioQuality(root, memberPolicy, loopBounded)}.");
            var discoveredContext = Context();
            SolverResult discovered = await Task.Run(() =>
                CombatSearchCoordinator.RunPlanMembersForTesting(discoveredContext, null, [plan, plan]), token);
            if (!discovered.Snapshot.AllEnemiesDead || discovered.Snapshot.HasRisk
                || discovered.ExplicitPotionCount != 0
                || discovered.ProjectedBattleHpLost > witness.ProjectedBattleHpLost
                || discovered.ResultScope != SolverResultScope.SearchCompletion)
                throw new InvalidOperationException("Plan loop failed to retain its first actual victory.");
            if (loopControlContext.Budget.WorkTotals.Snapshot().RecordedSolverCount != 2
                || loopBoundedContext.Budget.WorkTotals.Snapshot().RecordedSolverCount != 2
                || discoveredContext.Budget.WorkTotals.Snapshot().RecordedSolverCount != 2
                || discoveredContext.Budget.WorkTotals.Snapshot().ExpandedNodes
                    >= loopControlContext.Budget.WorkTotals.Snapshot().ExpandedNodes)
                throw new InvalidOperationException("Plan loop did not exercise both members or reuse its new victory.");
        }
        if (DescribePlanMemberStateForTesting(parent, root) != parentBefore
            || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
            throw new InvalidOperationException("Plan proof dispatch mutated parent/live/RNG state.");
        _completedChecks.Add("PlanMemberIncumbent:ActualCompleteWinner:CanonicalGuards:StrictPrefixReplay:"
            + "UnknownFeedExhaustKept:16ForkFullStateRngIsolation:Dop1vs16ContinuationDispatch:CallerRetainsVictory:"
            + "MemberPotionOverride:ExistingAndNewVictoryPlanLoop");
    }
}

internal static partial class CombatSearchCoordinator
{
    internal static SolverResult DispatchPlanMemberForTesting(SearchPassContext context,
        PlanCommitment plan, SolverResult? incumbent)
    {
        if (!TryRunPlanMember(context, new FrontierContinuationScheduler(context), plan,
                new(context.Profile.MaxExpandedNodes, context.Profile.SoftTimeBudgetMilliseconds,
                    false, null, null, false), out SolverResult? result, incumbent))
            throw new InvalidOperationException("Fixture did not enter the actual plan-member request boundary.");
        return result!;
    }

    internal static SolverResult RunPlanMembersForTesting(SearchPassContext context,
        SolverResult? baseline, IReadOnlyList<PlanCommitment> plans)
        => RunOpeningPlans(context, baseline, plans)
            ?? throw new InvalidOperationException("Plan fixture failed to produce a complete victory.");
}

internal sealed partial class CombatBeamSolver
{
    internal static PlanAction VerifyPlanMemberSeedForTesting(CombatRootSnapshot root,
        SolverDisplayNames names, BattleDamageSnapshot damage, SearchPolicySnapshot policy,
        PrimarySearchIncumbent incumbent, CancellationToken token)
    {
        var member = new CombatBeamSolver(root, names, damage,
            policy with { DisableSharedPrimaryIncumbentsForTesting = true }, token,
            primaryIncumbent: incumbent, attributionPurpose: ContinuationPurpose.PlanCommitment);
        SearchNode opening = CreateOpeningSearchSeed(member.Replay([]));
        string before = UnattendedTestRunner.DescribePlanMemberStateForTesting(
            (CombatPredictionSimulator)opening.Snapshot.Simulator, root);
        PlanAction prefix = member.PrepareCardActions(opening)
            .Single(action => action.Action.CardId == "DEFEND_IRONCLAD").Action;
        SearchNode partial = member.CostChildForTesting(opening, prefix);
        if (member.ApplyPrimaryIncumbentBound([partial]).Count != 0)
            throw new InvalidOperationException("Seeded plan failed to prune a non-improving certified branch.");
        var unknown = ((CombatPredictionSimulator)partial.Snapshot.Simulator).Fork();
        unknown.AddGeneratedCardToCombat(PredictedCard.Create(ModelDb.Card<Feed>(), root.PlayerIdentity),
            PileType.Exhaust, root.PlayerIdentity, resultKind: CardGenerationResultKind.Fixed);
        SimulationSnapshot unknownSnapshot = member.Snapshot(unknown, partial.Turn, partial.ActionCount,
            partial.Snapshot.ShufflesCrossed, SearchBoundaryReason.None, new ForkableSet<uint>());
        SearchNode unknownNode = partial with { Snapshot = unknownSnapshot, StateKey = unknownSnapshot.StateKey,
            Score = unknownSnapshot.Score, CombatProgress = CombatProgressState.Capture(unknownSnapshot) };
        try
        {
            if (member.RemainingHealingPotential(unknownSnapshot) != int.MaxValue
                || member.ApplyPrimaryIncumbentBound([unknownNode]).Count != 1
                || UnattendedTestRunner.DescribePlanMemberStateForTesting(
                    (CombatPredictionSimulator)opening.Snapshot.Simulator, root) != before)
                throw new InvalidOperationException("Plan seed discarded unknown recovery or mutated its parent.");
            return prefix;
        }
        finally
        {
            unknownSnapshot.ReleaseSimulator(); partial.Snapshot.ReleaseSimulator(); opening.Snapshot.ReleaseSimulator();
        }
    }
}
