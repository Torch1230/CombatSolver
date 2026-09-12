using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertLampIndirectPoisonAsync(CombatState combat, Player player)
    {
        foreach (string sourcePower in new[] { "ENVENOM_POWER", "CONCOCT_POWER" })
        {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        player.AddRelicInternal(ModelDb.Relic<UnsettlingLamp>().ToMutable());
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 100);
        await ClearPlayerPilesAsync(player);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = sourcePower, Target = "Player", Amount = 2 });
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEADLY_POISON" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        SetEnergy(player, 3);
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var actions = new List<PlanAction>();
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEADLY_POISON" })
        {
            actions.Add(new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: id, TargetCombatId: combat.Enemies[0].CombatId));
            var prediction = InvokeForcedTerminalReplay(driver, actions.ToArray(), null, 0, null);
            try
            {
                if (!FindActualHandCard(player, id, 0).TryManualPlay(combat.Enemies[0])) throw new InvalidOperationException("Lamp fixture native play failed.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                var expected = ContinuationStamp.CapturePredicted(player, prediction.Simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
                var actual = ContinuationStamp.CaptureLive(combat);
                if (expected.StateText != actual.StateText)
                    throw new InvalidOperationException("Lamp source mismatch: " + string.Join("; ", expected.DescribeDifferences(actual)));
            }
            finally { prediction.ReleaseSimulator(); }
        }
        _completedChecks.Add($"LampIndirectPoison:{sourcePower}:PreservesCharge:DirectPoisonDoubles:FullContinuationState");
        }
    }
}
