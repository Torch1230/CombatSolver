using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRelicWakeDamageAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray())
            await RelicCmd.Remove(relic);
        await ClearPlayerPilesAsync(player);
        var enemy = combat.Enemies.Single(creature => creature.Monster is
            MegaCrit.Sts2.Core.Models.Monsters.SlumberingBeetle or MegaCrit.Sts2.Core.Models.Monsters.LagavulinMatriarch);
        foreach (var other in combat.Enemies.Where(creature => creature != enemy).ToArray())
            await CreatureCmd.Kill(other, force: true);
        await CreatureCmd.SetMaxHp(enemy, 100);
        await CreatureCmd.SetCurrentHp(enemy, 100);
        await SetBlockAsync(enemy, 0);
        await SetBlockAsync(player.Creature, 20);
        var shield = (ParryingShield)await RelicCmd.Obtain(ModelDb.Relic<ParryingShield>().ToMutable(), player);
        CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        if (!shadow.TriggerRelicsAfterSideTurnEnd(simulator, [player.Creature], 0))
            throw new InvalidOperationException("Shield wake fixture unexpectedly requested a choice.");
        await shield.AfterSideTurnEnd(new BlockingPlayerChoiceContext(), CombatSide.Player, [player.Creature]);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy),
            CaptureActual(combat, player, enemy), _request.ScenarioId, "RelicDamageWakeHook");
        _completedChecks.Add("RelicDamageWake:NativeFullStateAndRng");
    }
}
