using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertBrilliantScarfAsync(CombatState combat, Player player)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Brilliant scarf: " + message); }
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        var scarf = (BrilliantScarf)ModelDb.Relic<BrilliantScarf>().ToMutable();
        player.AddRelicInternal(scarf);
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 100);
        await ClearPlayerPilesAsync(player);
        for (int i = 0; i < 4; i++) await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        SetEnergy(player, 4);
        var root = CombatRootSnapshot.Capture(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat), policy);
        var actions = Enumerable.Range(0, 4).Select(_ => new PlanAction(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "DEFEND_IRONCLAD")).ToArray();
        var prediction = InvokeForcedTerminalReplay(driver, actions, null, 0, null);
        try
        {
            var simulator = prediction.Simulator;
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            var predictedPlayer = simulator.State.GetPlayerCombatState(player);
            var strike = predictedPlayer.FindCard(FindActualHandCard(player, "STRIKE_IRONCLAD", 0))!;
            Check(predictedPlayer.Energy == 0, "four cards spend all energy");
            Check(shadow.CanPlayCard(simulator, strike, out int cost, out _) && cost == 0, "fifth card is playable for zero energy");
            foreach (var action in actions)
            {
                Check(FindActualHandCard(player, action.CardId, 0).TryManualPlay(null), "native defend");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            Check(scarf._cardsPlayedThisTurn == 4 && FindActualHandCard(player, "STRIKE_IRONCLAD", 0).EnergyCost.GetAmountToSpend() == 0, "native fifth cost");
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, combat.Enemies[0]), CaptureActual(combat, player, combat.Enemies[0]), "BrilliantScarf", "AfterFourCards");
            simulator.ManualPlay(strike, combat.Enemies[0], out _);
            Check(FindActualHandCard(player, "STRIKE_IRONCLAD", 0).TryManualPlay(combat.Enemies[0]), "native fifth play");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, combat.Enemies[0]), CaptureActual(combat, player, combat.Enemies[0]), "BrilliantScarf", "AfterFreeFifth");
            _completedChecks.Add("BrilliantScarf:FourPaidCards:FreeFifth:NativeFullState");
        }
        finally { prediction.ReleaseSimulator(); }
        scarf._cardsPlayedThisTurn = 0;
        await ClearPlayerPilesAsync(player);
        for (int i = 0; i < 4; i++) await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFLECT", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BLUDGEON", Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
        SetEnergy(player, 0);
        root = CombatRootSnapshot.Capture(combat);
        policy = policy with { FixedBudget = true, BudgetOverrideMilliseconds = 1500, MaxDegreeOfParallelism = 1,
            PotionPolicy = SolverPotionPolicy.Disabled, VerifyIncrementalSearch = true,
            Profile = SolverSearchProfile.Default with { MaxExpandedNodes = 256 } };
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null));
        Check(result.Snapshot.AllEnemiesDead && result.CombatEndedTurn == root.StartTurnNumber
            && result.BestNode.Actions.Count(a => a.Kind == PlanActionKind.PlayCard) == 5,
            "search plays four setup cards then the free killing fifth at zero energy");
        var played = result.BestNode.Actions.Where(a => a.Kind == PlanActionKind.PlayCard).ToArray();
        Check(played.Take(4).All(a => a.CardId == "DEFLECT") && played[4].CardId == "BLUDGEON"
            && played[4].RelicEffects?.Any(effect => effect.RelicId == "BRILLIANT_SCARF" && effect.Summary == "：本张免费") == true
            && played.Take(4).All(a => a.RelicEffects?.Any(effect => effect.RelicId == "BRILLIANT_SCARF") != true),
            "only the free three-cost fifth card receives the scarf label");
        _completedChecks.Add("BrilliantScarf:SearchFromZeroEnergy:FourDeflectThenFreeBludgeon:FirstTurnKill:IncrementalReplay:FreeCardLabel");
    }
}
