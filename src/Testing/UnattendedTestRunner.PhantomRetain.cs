using MegaCrit.Sts2.Core.Combat;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertPhantomRetainLifecycleAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SHIV", Pile = "Hand", UpgradeLevels = 1 });
        var choice = new BlockingPlayerChoiceContext();
        await PowerCmd.Apply<PhantomBladesPower>(choice, player.Creature, 12, player.Creature, null);
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var enemy = combat.Enemies.Single();
        shadow.ApplyDampen(simulator, player.Creature, enemy);
        var dampen = await PowerCmd.Apply<DampenPower>(choice, player.Creature, 1, enemy, null)
            ?? throw new InvalidOperationException("Native Dampen was not applied.");
        dampen.AddCaster(enemy);
        shadow.NormalizeCardAfflictions(simulator);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "DowngradeRetainKeyword");
        shadow.Apply<PhantomBladesPower>(player.Creature, 1, player.Creature);
        await PowerCmd.Apply<PhantomBladesPower>(choice, player.Creature, 1, player.Creature, null);
        shadow.NormalizeCardAfflictions(simulator);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "StackingKeepsDowngradedKeywords");
        var original = FindActualHandCard(player, "SHIV", 0);
        var predictedClone = simulator.State.FindCard(original)!.CreateClone();
        simulator.AddGeneratedCardsToCombat([predictedClone], PileType.Hand, player, resultKind: CardGenerationResultKind.Fixed);
        await CardPileCmd.AddGeneratedCardToCombat(original.CreateClone(), PileType.Hand, player);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "CloneEntersCombat");
        shadow.SetAmount<PhantomBladesPower>(player.Creature, 0);
        await PowerCmd.Remove(player.Creature.GetPower<PhantomBladesPower>()!);
        shadow.Apply<PhantomBladesPower>(player.Creature, 12, player.Creature);
        await PowerCmd.Apply<PhantomBladesPower>(choice, player.Creature, 12, player.Creature, null);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "NewApplicationGrantsRetainAgain");
        _completedChecks.Add("PhantomBlades:NativeDowngrade:Stacking:CloneEntry:Reapplication:CompleteStateAndRng");
    }
}
