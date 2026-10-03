using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertZeroBaseBlockAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await SetBlockAsync(player.Creature, 0);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "DEXTERITY_POWER", Target = "Player", Amount = 3 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "MIRAGE", Pile = "Hand" });
        var card = FindActualHandCard(player, "MIRAGE", 0);
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var enemy = combat.Enemies.Single();
        PlaySimulatedCard(simulator, shadow, simulator.State.FindCard(card)!, null, [enemy]);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), card),
            () => { if (!card.TryManualPlay(null)) throw new InvalidOperationException("Native Mirage was refused."); }, deadline.Token);
        await action.CompletionTask.WaitAsync(deadline.Token);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "MirageWithZeroPoisonAndDexterity");
        _completedChecks.Add("ZeroBaseBlock:MirageAndDexterity:FullStateAndRng");
    }
}
