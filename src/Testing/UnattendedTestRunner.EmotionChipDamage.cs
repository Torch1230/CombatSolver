using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertEmotionChipPreventedDamageAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await SetBlockAsync(player.Creature, 0);
        await RelicCmd.Obtain(ModelDb.Relic<EmotionChip>().ToMutable(), player);
        await OrbCmd.Channel<PlasmaOrb>(new BlockingPlayerChoiceContext(), player);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "BUFFER_POWER", Target = "Player", Amount = 1 });
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var enemy = combat.Enemies.Single();
        int start = simulator.History.Entries.Count;
        simulator.Damage(player.Creature, 10, ValueProp.Unpowered, enemy);
        await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), player.Creature, 10, ValueProp.Unpowered, enemy);
        shadow.RecordRelicRoundDamage(simulator, player, start);
        TriggerSimulatedPlayerSetup(simulator, shadow, player, []);
        await TriggerActualPlayerSetupAsync(combat, player, []);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy),
            CaptureActual(combat, player, enemy), _request.ScenarioId, "AfterPreventedDamageNextTurn");
        _completedChecks.Add("EmotionChip:BufferPreventedHpLoss:NextTurnFullStateAndRng");
    }
}
