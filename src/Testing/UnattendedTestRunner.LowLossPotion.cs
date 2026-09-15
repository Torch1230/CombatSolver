using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertLowLossPotionAsync(CombatState combat, Player player)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Low-loss potion: " + message);
        }
        var settingsBefore = SolverSettings.Current;
        bool enabledBefore = SolverController.LowLossPotionEnabled;
        try
        {
            SolverSettings.ApplyForTesting(settingsBefore with { PotionPolicy = SolverPotionPolicy.Smart,
                PotionDirectives = [], GrowthBudgets = default, RelicStrategyEnabled = false });
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
            foreach (var potion in player.PotionSlots.Where(p => p != null).ToArray()) potion!.Discard();
            if (player.MaxPotionCount < 3) player.AddToMaxPotionCount(3 - player.MaxPotionCount);
            await ClearPlayerPilesAsync(player);
            await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
            await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
            ConfigureMonsterMove(combat.Enemies[0], new UnattendedMonsterMoveCheck { MoveId = "FIRST_ACID_GOOP" });
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection
                { PowerId = "STRENGTH_POWER", Target = "Enemy", Amount = 50 });
            // Three native energy sources establish exact 8/7/5-HP alternatives.
            // Waiting gives the enemy a large attack, so it cannot replace the resource trade for free.
            List<CardModel> energySources = [];
            foreach (var (loss, energy) in new[] { (8, 3), (7, 2), (5, 1) })
                energySources.Add((await InjectCardAsync(combat, player, new UnattendedCardInjection
                    { CardId = "BLOODLETTING", Pile = "Hand", DynamicVars = new() { ["HpLoss"] = loss, ["Energy"] = energy } })).Single());
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BLUDGEON", Pile = "Hand" });
            SetEnergy(player, 0);
            var potions = Enumerable.Range(1, 3).Select(energy =>
            {
                PotionModel potion = InjectPotionForTest(player, "ENERGY_POTION");
                potion.DynamicVars["Energy"].BaseValue = energy;
                return potion;
            }).ToArray();
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            int[] slots = potions.Select(player.GetPotionSlotIndex).ToArray();
            PotionStrategySnapshot Strategy(params int[] disabled)
                => new(SolverPotionPolicy.Smart, disabled.Select(i => new PotionSlotDirective(slots[i],
                    potions[i].Id.Entry, SolverPotionDirective.Disabled)));
            SolverController.SetLowLossPotionForTesting(true);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
            {
                PotionStrategy = Strategy(2), FixedBudget = true, BudgetOverrideMilliseconds = 3000,
                MaxDegreeOfParallelism = 1, DetailedDiagnostics = false, VerifyIncrementalSearch = false,
                AcceptableBattleHpLoss = 8, StopAtAcceptableBattleHpLoss = true,
                Profile = SolverSearchProfile.Default with { MaxExpandedNodes = 512 },
                Interaction = new SearchInteractionState(),
            };
            Check(policy.LowLossPotionEnabled && !new SolverCombatSession().LowLossPotionEnabled,
                "capture and next-session default");
            var root = CombatRootSnapshot.Capture(combat);
            var names = SolverDisplayNames.Capture(combat);
            List<SolverInterimResult> displayed = [];
            async Task<SolverResult> Search(SearchPolicySnapshot requested, int alreadyLost = 0, int alreadyUsed = 0)
                => await Task.Run(() => CombatSearchCoordinator.Solve(root, names,
                    new(alreadyLost, 0, alreadyUsed), requested with { Interaction = new SearchInteractionState() },
                    CancellationToken.None, p => { if (p.CurrentBestResult is { } best) displayed.Add(best); }));
            SolverResult old = await Search(policy with { LowLossPotionEnabled = false });
            Check(old.ProjectedBattleHpLost == 8 && old.ExplicitPotionCount == 0, "disabled baseline 8/0");
            SolverResult chosen = await Search(policy with { VerifyIncrementalSearch = true });
            Check(chosen.ProjectedBattleHpLost == 5 && chosen.ExplicitPotionCount == 1
                && chosen.LowLossPotionApplied && chosen.PotionHpRequired == 1
                && chosen.BestNode.Actions.Any(a => a.Kind == PlanActionKind.UsePotion && a.PotionSlot == slots[1])
                && chosen.BestNode.Actions.All(a => a.Kind != PlanActionKind.UsePotion || a.PotionSlot != slots[2]),
                $"choose B, protect C: loss={chosen.ProjectedBattleHpLost} count={chosen.ExplicitPotionCount}");
            Check(displayed.Any(p => p.ProjectedBattleHpLost == 5 && p.ProjectedBattlePotionCount == 1
                && p.PotionStrategicCost == 1 && p.LowLossPotionApplied), "interim display uses audited one-HP cost");
            SolverInterimResult baselineDisplay = new(true, 0, 8, 8, 0, 0, 0, 0, 2);
            SolverInterimResult healingDisplay = baselineDisplay with { ProjectedBattleHpLost = 9,
                StrategicHpDeficit = 5, PotionStrategicCost = 1, ProjectedBattlePotionCount = 1,
                LowLossPotionApplied = true };
            Check(SolverInterimResultOrdering.CanPromoteDisplayedResult(healingDisplay, baselineDisplay)
                && !SolverInterimResultOrdering.CanPromoteDisplayedResult(
                    healingDisplay with { LowLossPotionApplied = false }, baselineDisplay),
                "audited net healing can promote without weakening ordinary display guards");
            SolverResult oneHp = await Search(policy with { PotionStrategy = Strategy(1, 2) });
            Check(oneHp.ProjectedBattleHpLost == 7 && oneHp.LowLossPotionApplied, "one HP saved qualifies");
            SolverResult protectedAll = await Search(policy with { PotionStrategy = Strategy(0, 1, 2) });
            Check(protectedAll.ProjectedBattleHpLost == 8 && protectedAll.ExplicitPotionCount == 0, "all protected fallback");
            SolverResult alreadyUsed = await Search(policy, alreadyUsed: 1);
            Check(alreadyUsed.ProjectedBattleHpLost == 8 && !alreadyUsed.LowLossPotionApplied
                && alreadyUsed.ExplicitPotionCount == 0, "used potion prevents another waiver");
            SolverResult sunkLoss = await Search(policy, alreadyLost: 10);
            Check(sunkLoss.ProjectedBattleHpLost == 15 && sunkLoss.LowLossPotionApplied, "sunk loss excluded");

            var allowance = new LowLossPotionAllowance(8, 8, 20, true);
            Check(allowance.Qualifies(true, 1, 7, 7, 20)
                && !allowance.Qualifies(true, 1, 0, 0, 21)
                && !allowance.Qualifies(true, 2, 0, 0, 0)
                && !allowance.Qualifies(true, 1, 0, 8, 20)
                && !allowance.Qualifies(false, 1, 0, 0, 0), "resource, count, growth-only and victory boundaries");
            Check(!allowance.AllowsCandidate(true, 1, -20, 8, 20, 18)
                && allowance.AllowsCandidate(true, 1, 15, 15, 0, 18)
                && allowance.AllowsCandidate(true, 1, 8, 8, 20, 0),
                "growth-only cannot hide saved HP; recovery and free potions retain normal access");
            Check(!LowLossPotionAllowance.CanOffer(policy, 0, true, 0, 0), "zero loss boundary");
            foreach (int deficit in new[] { 9, 13, 18, 30 })
                Check(LowLossPotionAllowance.CanOffer(policy, 0, true, deficit, deficit), $"no baseline loss ceiling {deficit}");
            Check(!LowLossPotionAllowance.CanOffer(policy, 0, false, 1, 1), "rescue unchanged");
            Check(!LowLossPotionAllowance.CanOffer(policy with { PotionStrategy = new(SolverPotionPolicy.Smart,
                [new(slots[0], potions[0].Id.Entry, SolverPotionDirective.Force)]) }, 0, true, 8, 8), "force unchanged");
            await AssertTheftRecoveryPolicyAsync(combat);

            var onCache = SolvedRouteCache.Capture(combat, root, policy, new(0, 0, 0));
            var offCache = SolvedRouteCache.Capture(combat, root, policy with { LowLossPotionEnabled = false }, new(0, 0, 0));
            Check(onCache.Path != offCache.Path, "cache isolation");
            JsonObject captured = JsonSerializer.SerializeToNode(CombatBugReportExporter.LatestEffectivePolicy,
                UnattendedTestFiles.JsonOptions)!.AsObject();
            Check(ReadRecordedLowLossPotionPolicy(captured) && !ReadRecordedLowLossPotionPolicy(new())
                && !ReadRecordedLowLossPotionPolicy(null), "recorded and legacy policy");
            using var panel = new SolverPotionStrategyPanel();
            panel.Refresh(combat, controlsDisabled: false);
            Check(panel.LowLossToggleForTesting.ButtonPressed && !panel.LowLossToggleForTesting.Disabled,
                "UI reflects session preference");
            panel.Refresh(combat, controlsDisabled: true);
            Check(panel.LowLossToggleForTesting.Disabled, "deployment disables control");
            SolverController.SetLowLossPotionForTesting(false);
            Check(policy.LowLossPotionEnabled, "captured policy stays frozen");
            SolverController.SetLowLossPotionForTesting(true);
            Check(!LowLossPotionAllowance.CanOffer(policy, 1, true, 3, 3), "toggle cannot reset used allowance");

            // A 13-HP baseline must still allow small savings. The positive stop target
            // cannot bypass the comparison, and all unprotected bottles compete together.
            energySources[0].DynamicVars["HpLoss"].BaseValue = 13;
            energySources[1].DynamicVars["HpLoss"].BaseValue = 12;
            root = CombatRootSnapshot.Capture(combat);
            var higherLossPolicy = policy with { AcceptableBattleHpLoss = 13 };
            SolverResult higherOff = await Search(higherLossPolicy with { LowLossPotionEnabled = false });
            SolverResult higherOn = await Search(higherLossPolicy with { VerifyIncrementalSearch = true });
            Check(higherOff.ProjectedBattleHpLost == 13 && higherOff.ExplicitPotionCount == 0
                && higherOn.ProjectedBattleHpLost == 5 && higherOn.ExplicitPotionCount == 1
                && higherOn.LowLossPotionApplied && higherOn.PotionHpSaved == 8 && higherOn.PotionHpRequired == 1
                && higherOn.BestNode.Actions.Any(a => a.Kind == PlanActionKind.UsePotion && a.PotionSlot == slots[1]),
                "13 to 5 saves eight and uses the best unprotected bottle");
            SolverResult higherOneHp = await Search(higherLossPolicy with { PotionStrategy = Strategy(1, 2) });
            SolverResult higherOneHpOff = await Search(higherLossPolicy with
                { PotionStrategy = Strategy(1, 2), LowLossPotionEnabled = false });
            Check(higherOneHp.ProjectedBattleHpLost == 12 && higherOneHp.ExplicitPotionCount == 1
                && higherOneHp.LowLossPotionApplied && higherOneHp.PotionHpSaved == 1
                && higherOneHpOff.ProjectedBattleHpLost == 13 && higherOneHpOff.ExplicitPotionCount == 0,
                "13 to 12 saves one only when enabled");
            SolverResult higherAlreadyUsed = await Search(higherLossPolicy, alreadyUsed: 1);
            Check(higherAlreadyUsed.ProjectedBattleHpLost == 13 && higherAlreadyUsed.ExplicitPotionCount == 0,
                "higher loss does not renew a spent allowance");
            energySources[0].DynamicVars["HpLoss"].BaseValue = 20;
            energySources[1].DynamicVars["HpLoss"].BaseValue = 19;
            root = CombatRootSnapshot.Capture(combat);
            SolverResult normalSavingOff = await Search(policy with { LowLossPotionEnabled = false });
            SolverResult normalSavingOn = await Search(policy);
            Check(normalSavingOff.ProjectedBattleHpLost == 5 && normalSavingOff.ExplicitPotionCount == 1
                && normalSavingOn.ProjectedBattleHpLost == 5 && normalSavingOn.ExplicitPotionCount == 1,
                "normal high-savings potion use stays available");
            _completedChecks.Add("SmallPotionSavings:13to5:13to12:PositiveEarlyStop13:UsedOnce:Normal20to5:Incremental");

            // Restore the original no-benefit and zero-loss sentinels.
            energySources[0].DynamicVars["HpLoss"].BaseValue = 8;
            energySources[1].DynamicVars["HpLoss"].BaseValue = 7;
            foreach (var potion in potions) potion.DynamicVars["Energy"].BaseValue = 0;
            root = CombatRootSnapshot.Capture(combat);
            SolverResult noBenefit = await Search(policy);
            Check(noBenefit.ProjectedBattleHpLost == 8 && noBenefit.ExplicitPotionCount == 0, "zero benefit fallback");
            SetEnergy(player, 3);
            root = CombatRootSnapshot.Capture(combat);
            SolverResult zero = await Search(policy);
            Check(zero.ProjectedBattleHpLost == 0 && zero.ExplicitPotionCount == 0, "zero loss preserves potions");
            _completedChecks.Add("LowLossPotion:8to5:OneHpSaved:ProtectedBest:PositiveEarlyStop:Incremental:InterimCost1:UsedOnce:SunkLoss:NoBenefit:ZeroLoss:Cache:PolicyRoundTrip:UI:TheftSentinel");
        }
        finally
        {
            SolverSettings.ApplyForTesting(settingsBefore);
            SolverController.SetLowLossPotionForTesting(enabledBefore);
        }
    }

    private async Task PrepareLowLossPotionDeploymentAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        foreach (var potion in player.PotionSlots.Where(p => p != null).ToArray()) potion!.Discard();
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
        ConfigureMonsterMove(combat.Enemies[0], new UnattendedMonsterMoveCheck { MoveId = "FIRST_ACID_GOOP" });
        var initialRoot = CombatRootSnapshot.Capture(combat);
        var hits = initialRoot.Forecast.Rounds[0].SelectMany(m => m.AttackHits).ToArray();
        if (hits.Length != 1) throw new InvalidOperationException("Low-loss deployment requires one attack hit.");
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = "STRENGTH_POWER", Target = "Enemy", Amount = 13 - hits[0].Damage });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Draw" });
        SetEnergy(player, 3);
        InjectPotionForTest(player, "BLOCK_POTION");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        _completedChecks.Add("LowLossPotionDeployment:OneHit13:Defend5:DrawnStrikeOnTurn2");
    }
}
