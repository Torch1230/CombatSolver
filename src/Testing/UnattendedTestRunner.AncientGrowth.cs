using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertAncientGrowthPolicyAsync(CombatState combat, Player player)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Ancient growth: " + message); }
        SolverSettingsData original = SolverSettings.Current;
        var settings = original with { GrowthBudgets = new GrowthValues(ForbiddenGrimoire: 12), BrightestFlameMaxHpLossLimit = 4 };
        Check(SolverSettings.RoundTripForTesting(settings).BrightestFlameMaxHpLossLimit == 4
            && SolverSettings.RoundTripForTesting(settings).GrowthBudgets.ForbiddenGrimoire == 12, "settings round trip");
        try
        {
            SolverSettings.ApplyForTesting(settings);
            using var panel = new SolverGrowthStrategyPanel();
            Check(panel.SettingsConfiguredForTesting, "growth rows reload");
            Check(GrowthCostPolicy.AllowsBrightestFlame(4, 0, 4) && !GrowthCostPolicy.AllowsBrightestFlame(4, 0, 6)
                && !GrowthCostPolicy.AllowsBrightestFlame(0, 0, 2) && GrowthCostPolicy.AllowsBrightestFlame(null, 0, 100)
                && GrowthCostPolicy.AllowsBrightestFlame(2, 4, 4) && !GrowthCostPolicy.AllowsBrightestFlame(2, 4, 6), "hard cap and manual overage");
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
            await ClearPlayerPilesAsync(player);
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "ECHO_FORM_POWER", Target = "Player", Amount = 1 });
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BRIGHTEST_FLAME", Pile = "Hand" });
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "FORBIDDEN_GRIMOIRE", Pile = "Hand" });
            SetEnergy(player, 3);
            var root = CombatRootSnapshot.Capture(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
            Check(policy.BrightestFlameMaxHpLossLimit == 4 && policy.HasGrowthTargets, "root policy capture");
            var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat), policy);
            PlanAction[] actions = [new(PlanActionKind.PlayCard,root.StartTurnNumber,CardId:"BRIGHTEST_FLAME"),new(PlanActionKind.PlayCard,root.StartTurnNumber,CardId:"FORBIDDEN_GRIMOIRE")];
            SimulationSnapshot prediction = InvokeForcedTerminalReplay(driver,actions,null,0,null);
            try
            {
                var simulator = prediction.Simulator;
                var shadow = (SimulatedCombatState)simulator.State.CombatState;
                var expected = CaptureSimulated(simulator,shadow,player,combat.Enemies[0]);
                Check(shadow.BrightestFlameMaxHpSpent == 4 && shadow.GrowthRewards.ForbiddenGrimoire == 1, "replayed flame cost and grimoire reward");
                Check(settings.GrowthBudgets.Credit(shadow.GrowthRewards) == 12, "removal allowance");
                foreach (var action in actions)
                {
                    Check(FindActualHandCard(player,action.CardId,0).TryManualPlay(null), "native play");
                    await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                }
                AssertSnapshotEqual(expected,CaptureActual(combat,player,combat.Enemies[0]),"AncientGrowth","ReplayedFlameAndGrimoire");
                var recaptured = CombatRootSnapshot.Capture(combat);
                Check(recaptured.InitialBrightestFlameMaxHpSpent == 4, "recaptured native history keeps spent budget");
                var fork = simulator.Fork();
                var child = (SimulatedCombatState)fork.State.CombatState;
                child.BeginSideTurn(player.Creature);
                Check(child.BrightestFlameMaxHpSpent == 4, "turn boundary keeps spent budget");
                child.RecordBrightestFlameMaxHpLoss(2);
                child.RecordGrowthReward(GrowthSource.ForbiddenGrimoire);
                Check(child.BrightestFlameMaxHpSpent == 6 && shadow.BrightestFlameMaxHpSpent == 4
                    && child.GrowthRewards.ForbiddenGrimoire == 2 && shadow.GrowthRewards.ForbiddenGrimoire == 1, "fork isolation");
            }
            finally { prediction.ReleaseSimulator(); }

            foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
            await ClearPlayerPilesAsync(player);
            foreach (string id in new[] {"BRIGHTEST_FLAME","FORBIDDEN_GRIMOIRE","STRIKE_IRONCLAD","CASCADE"})
                await InjectCardAsync(combat,player,new UnattendedCardInjection {CardId=id,Pile="Hand"});
            await InjectCardAsync(combat,player,new UnattendedCardInjection {CardId="BRIGHTEST_FLAME",Pile="Draw"});
            await CreatureCmd.SetCurrentHp(combat.Enemies[0],1);
            SetEnergy(player,3);
            var searchRoot = CombatRootSnapshot.Capture(combat);
            policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(),combat,false,null) with
            { FixedBudget=true, BudgetOverrideMilliseconds=1500, PotionPolicy=SolverPotionPolicy.Disabled, MaxDegreeOfParallelism=1, VerifyIncrementalSearch=true };
            var names = SolverDisplayNames.Capture(combat); var damage = BattleDamageTracker.Observe(combat);
            SolverResult result = await Task.Run(()=>CombatSearchCoordinator.Solve(searchRoot,names,damage,policy,CancellationToken.None,null));
            Check(result.Snapshot.AllEnemiesDead && result.Snapshot.GrowthRewards.ForbiddenGrimoire == 1
                && result.BestNode.Actions.All(a=>a.CardId is not ("BRIGHTEST_FLAME" or "CASCADE")), "search takes removal reward within exhausted flame cap, including nested auto-play");
            _completedChecks.Add("AncientGrowth:NativeReplay:ManualHistory:Fork:Turn:HardCapSearch:GrimoireAllowance");
        }
        finally { SolverSettings.ApplyForTesting(original); }
    }
}
