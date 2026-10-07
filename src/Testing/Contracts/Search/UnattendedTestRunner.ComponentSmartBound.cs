using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertComponentSmartBoundAsync(CombatState live, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray())
            if (relic is not BurningBlood) await RelicCmd.Remove(relic);
        await ClearPlayerPilesAsync(player);
        foreach (PotionModel? potion in player.PotionSlots.ToArray()) potion?.Discard();
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(live, player, new() { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 6);
        await CreatureCmd.SetCurrentHp(player.Creature, 50);
        InjectPotionForTest(player, "COLORLESS_POTION");
        InjectPotionForTest(player, "BOTTLED_POTENTIAL");
        SetEnergy(player, 3);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (!root.UsesComponentHealingCertificate || root.InitialRemainingHealingUpperBound != 0)
            throw new InvalidOperationException("Smart component root rejected: " + root.ComponentHealingRejection);
        string before = ContinuationStamp.CaptureLive(live).StateText;
        var damage = BattleDamageTracker.Observe(live);
        var names = SolverDisplayNames.Capture(live);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = true, MaxDegreeOfParallelism = 2,
            DetailedDiagnostics = false, MeasurePhasePerformance = false,
            BudgetOverrideMilliseconds = null, StopAtAcceptableBattleHpLoss = false,
            UseBeamWidthPortfolio = true, BeamWidthPortfolioWidths = [8, 12],
            UseNoveltyPortfolio = false,
            PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new(SolverPotionPolicy.Disabled, []),
            RelicTargets = [],
        };
        policy = policy with { Profile = policy.Profile with
        {
            BeamWidth = 8, MaxExpandedNodes = 160, SoftTimeBudgetMilliseconds = 10_000,
            StopPortfolioAtHpTarget = false,
        } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SolverResult control = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
            policy, deadline.Token, null));
        if (!control.Snapshot.AllEnemiesDead || control.Snapshot.PlayerDead || control.Snapshot.HasRisk
            || control.ProjectedBattleHpLost > damage.HpLostSoFar || control.ExplicitPotionCount != 0)
            throw new InvalidOperationException($"No complete potion-free witness without additional loss: "
                + $"won={control.Snapshot.AllEnemiesDead} dead={control.Snapshot.PlayerDead} "
                + $"risk={control.Snapshot.HasRisk} loss={control.ProjectedBattleHpLost} "
                + $"priorLoss={damage.HpLostSoFar} potions={control.ExplicitPotionCount}.");
        int hpDeficit = ActEndingBossPolicy.StrategicHpDeficit(
            control.Snapshot.CumulativePlayerHpLost,
            Math.Max(0, root.InitialPlayerMaxHp - control.Snapshot.PlayerMaxHp),
            control.Snapshot.RecoveredPlayerHp + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                root.PostCombatRelicHeal, true, control.Snapshot.PlayerHp, control.Snapshot.PlayerMaxHp),
            BossHpRelief.None, control.Snapshot.DeathSaveHpRestored) - control.Snapshot.StrategicHpCredit;
        PotionFreePolicyBaseline baseline = new(true, hpDeficit, control.Snapshot.PlayerHp, control.CombatEndedTurn);
        SearchPolicySnapshot smart = policy with
        {
            PotionPolicy = SolverPotionPolicy.Smart,
            PotionStrategy = new(SolverPotionPolicy.Smart, []),
        };
        CombatBeamSolver Solver(SearchPolicySnapshot p, DirectSearchPurpose purpose = DirectSearchPurpose.SmartPotionGradient)
            => new(root, names, damage, p, deadline.Token,
                potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                potionFreePolicyBaseline: baseline, maximumPotionUses: 1, minimumPotionUses: 1,
                directSearchPurpose: purpose);
        var bound = Solver(smart);
        if (!bound.ComponentSmartBoundEnabledForTesting)
            throw new InvalidOperationException("Certified Smart exact layer did not enable bound.");
        try { await Task.Run(bound.Solve, deadline.Token); }
        catch (PotionPolicyUnsatisfiedException) { }
        if (bound.ComponentSmartBoundPrunedForTesting <= 0)
            throw new InvalidOperationException("Impossible optional layer produced no eligibility prunes.");
        var builder = Solver(smart, DirectSearchPurpose.PrimaryBeam);
        PlanAction bottled = builder.BuildOpeningPotionActions().First(action => action.PotionId == "BOTTLED_POTENTIAL");
        var posterior = new CombatBeamSolver(root, names, damage, smart, deadline.Token,
            potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
            potionFreePolicyBaseline: baseline, maximumPotionUses: 1, minimumPotionUses: 1,
            fixedPrefixActions: [bottled], resetFixedPrefixSchedulingBaseline: false,
            attributionPurpose: ContinuationPurpose.SmartOpeningPotionPosterior);
        if (!posterior.ComponentSmartBoundEnabledForTesting)
            throw new InvalidOperationException("Smart posterior lost the complete potion-free witness.");
        try { await Task.Run(posterior.Solve, deadline.Token); }
        catch (PotionPolicyUnsatisfiedException) { }
        if (posterior.ComponentSmartBoundPrunedForTesting <= 0)
            throw new InvalidOperationException("Smart potion prefix was not pruned at its state boundary.");
        foreach (SearchPolicySnapshot guarded in new[]
        {
            policy,
            smart with { PotionPolicy = SolverPotionPolicy.RequireAtLeastOne },
            smart with { TheftPolicy = SolverTheftPolicy.PreserveResources },
            smart with { RelicTargets = [new(RelicCounterId.PenNib, 2, 2, 1, 10)] },
            smart with { GrowthOpportunityTargets = GrowthOpportunityTargets.UnboundedForTesting("component_guard") },
            smart with { PotionStrategy = new(SolverPotionPolicy.Smart,
                [new(0, "COLORLESS_POTION", SolverPotionDirective.Force)]) },
        })
            if (Solver(guarded).ComponentSmartBoundEnabledForTesting)
                throw new InvalidOperationException("Growth/relic/theft/forced/potion-policy guard ignored.");
        if (!Solver(smart with { RelicTargets = [new(RelicCounterId.PenNib, 2, 2, 0, 10)] })
                .ComponentSmartBoundEnabledForTesting)
            throw new InvalidOperationException("Zero HP allowance relic goal lost its strict Smart certificate.");
        if (Solver(smart, DirectSearchPurpose.PrimaryBeam).ComponentSmartBoundEnabledForTesting)
            throw new InvalidOperationException("Bound enabled outside Smart counterfactual audit.");
        SolverResult candidate = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
            smart, deadline.Token, null));
        if (!candidate.Snapshot.AllEnemiesDead || candidate.Snapshot.PlayerDead || candidate.Snapshot.HasRisk
            || candidate.ProjectedBattleHpLost > control.ProjectedBattleHpLost || candidate.ExplicitPotionCount != 0
            || ContinuationStamp.CaptureLive(live).StateText != before)
            throw new InvalidOperationException("Smart coordinator changed strict quality or live state.");
        _completedChecks.Add("ComponentSmartBound:CompletePotionFreeWitness:ExactLayerPrunes="
            + bound.ComponentSmartBoundPrunedForTesting
            + ":PosteriorPrefixPrunes=" + posterior.ComponentSmartBoundPrunedForTesting
            + ":SmartDisabledRequiredForcedGrowthRelicTheftGuards:Dop2:StrictIncremental:WholeCoordinator:LiveIsolation");
        await AssertPlanMemberIncumbentAsync(live, player, smart, deadline.Token);
    }
}
