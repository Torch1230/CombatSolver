using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertSetupHistoryAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "RADIATE", Pile = "Draw" });
        for (int index = 0; index < 4; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_REGENT", Pile = "Draw" });
        await PlayerCmd.GainStars(3, player);
        var enemy = combat.Enemies.Single();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, true, null));
        var setup = (SimulationSnapshot)InvokeForcedTerminalMethod(driver, "ReplayTurnSetup", [Array.Empty<PlanCardChoice>()])!;
        try
        {
            var simulator = setup.Simulator;
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            var context = new BlockingPlayerChoiceContext();
            player.PlayerCombatState!.ResetEnergy();
            await Hook.AfterEnergyReset(combat, player);
            await Hook.BeforeHandDraw(combat, player, context);
            await CardPileCmd.Draw(context, 5, player, fromHandDraw: true);
            await Hook.AfterPlayerTurnStart(combat, context, player);
            await Hook.AfterSideTurnStart(combat, CombatSide.Player, [player.Creature]);
            var card = FindActualHandCard(player, "RADIATE", 0);
            PlaySimulatedCard(simulator, shadow, simulator.State.FindCard(card)!, null, [enemy]);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), card),
                () => { if (!card.TryManualPlay(null)) throw new InvalidOperationException("Native Radiate was refused."); }, deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
                _request.ScenarioId, "RadiateAfterSameTurnSetup");
            _completedChecks.Add("SetupHistory:CapturedStars:NativeRadiate:FullStateAndRng");
        }
        finally { setup.ReleaseSimulator(); }
    }
}
