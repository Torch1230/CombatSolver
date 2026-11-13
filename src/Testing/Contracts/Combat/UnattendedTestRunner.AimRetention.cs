using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertAimRetentionAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await InjectCardAsync(combat, player, new() { CardId = "FOOLISH_SHOT", Pile = "Hand", Count = 2 });
        await InjectPowerAsync(combat, player, new() { PowerId = "AIM_POWER", Target = "Player", Amount = 7 });
        var source = player.PlayerCombatState!.Hand.Cards[0];
        var other = player.PlayerCombatState.Hand.Cards[1];
        var enemy = combat.Enemies[0];
        var actualPower = player.Creature.GetPower<AimPower>()!;
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var predictedPower = shadow.GetPower<AimPower>(player.Creature)!;
        var predictedSource = simulator.State.FindCard(source)!;
        var nativeCommand = DamageCmd.Attack(1m).FromCard(source, null).FromArrow().Targeting(enemy);
        var predictedCommand = DamageCmd.Attack(1m).FromCard(predictedSource.Preview, null).FromArrow().Targeting(enemy);
        await actualPower.BeforeAttack(nativeCommand);
        AimPowerMirrors.BeforeAttack(predictedPower, new() { Simulator = simulator, Command = predictedCommand });
        await PowerCmd.Apply<AimPower>(new BlockingPlayerChoiceContext(), player.Creature, 3, player.Creature, null);
        shadow.Apply<AimPower>(player.Creature, 3, player.Creature);
        await actualPower.AfterAttack(new BlockingPlayerChoiceContext(), nativeCommand);
        AimPowerMirrors.AfterAttack(predictedPower, new() { Simulator = simulator, Command = predictedCommand });
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "AimRetainedAfterAttack");

        var captured = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var child = captured.Fork();
        var childCombat = (SimulatedCombatState)child.State.CombatState;
        var childPower = childCombat.GetPower<AimPower>(player.Creature)!;
        foreach (var card in new[] { source, other })
        {
            var predicted = child.State.FindCard(card)!;
            decimal expected = actualPower.ModifyDamageAdditive(enemy, 1, ValueProp.Move | ValueProp.FromArrow,
                player.Creature, card, null);
            decimal actual = AimPowerMirrors.ModifyDamageAdditive(childPower, new()
            {
                Simulator = child, Target = enemy, Amount = 1, Props = ValueProp.Move | ValueProp.FromArrow,
                Dealer = player.Creature, CardSource = predicted, CardPlay = null
            });
            if (expected != actual || actual != (ReferenceEquals(card, source) ? 3m : 0m))
                throw new InvalidOperationException("Retained Aim source identity differs from native behavior.");
        }
        var nextNative = DamageCmd.Attack(1m).FromCard(other, null).FromArrow().Targeting(enemy);
        await actualPower.BeforeAttack(nextNative);
        await actualPower.AfterAttack(new BlockingPlayerChoiceContext(), nextNative);
        if (actualPower.Amount != 3 || childPower.Amount != 3)
            throw new InvalidOperationException("Retained Aim was consumed by a later attack.");
        AssertSnapshotEqual(CaptureSimulated(child, childCombat, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "AimRootForkAfterLiveAdvance");
        _completedChecks.Add("AimRetention:Native:NestedGain:SourceIdentity:Root:Fork:LiveAdvance");
    }
}
