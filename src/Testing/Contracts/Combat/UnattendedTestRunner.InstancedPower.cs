using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertTargetedPowerInstancesAsync(CombatState combat, Player player)
    {
        var owner = combat.Enemies.Single();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        for (int amount = 1; amount <= 2; amount++)
            shadow.ApplyTargeted<ThieveryPower>(owner, player.Creature, amount, owner);
        for (int amount = 1; amount <= 2; amount++)
        {
            ThieveryPower power = (ThieveryPower)ModelDb.Power<ThieveryPower>().ToMutable();
            power.Target = player.Creature;
            await PowerCmd.Apply(new BlockingPlayerChoiceContext(), power, owner, amount, owner, null);
        }
        var expected = ContinuationStamp.CapturePredicted(player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
        var actual = ContinuationStamp.CaptureLive(combat);
        if (expected != actual)
            throw new InvalidOperationException($"Targeted instances differ: {expected.DescribeFirstDifference(actual)}");
        ThieveryPower[] instances = shadow.EffectivePowers().OfType<ThieveryPower>().ToArray();
        if (instances.Length != 2 || instances.Any(power => power.Target != player.Creature || power.Owner != owner)
            || shadow.GetPower<ThieveryPower>(owner) != instances[0])
            throw new InvalidOperationException("Targeted instance identity or first-instance lookup differs.");
        shadow.RecordThievery(simulator, owner);
        if (instances[0].DynamicVars.Gold.BaseValue != 1 || instances[1].DynamicVars.Gold.BaseValue != 0)
            throw new InvalidOperationException("Thievery wrote the wrong instance.");
        _completedChecks.Add("InstancedPower:TargetedApplications:FirstInstanceMutation");
    }

    private async Task AssertInstancedPowerApplicationAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        var enemy = combat.Enemies.Single();
        PowerModel canonical = _request.ScenarioId switch
        {
            "INSTANCED-POWER-AUTOMATION" => ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.AutomationPower>(),
            "INSTANCED-POWER-BOULDER" => ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.RollingBoulderPower>(),
            _ => throw new InvalidOperationException("Unknown instanced-power fixture."),
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        ContinuationStamp Capture(CombatPredictionSimulator current) => ContinuationStamp.CapturePredicted(
            player, current, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        shadow.ApplyPower(canonical.GetType(), player.Creature, 1, player.Creature);
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
        CombatPredictionSimulator sibling = simulator.Fork();
        var siblingState = (SimulatedCombatState)sibling.State.CombatState;
        var first = Capture(sibling);
        shadow.ApplyPower(canonical.GetType(), player.Creature, 2, player.Creature);
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
        for (int amount = 1; amount <= 2; amount++)
            await PowerCmd.Apply(new BlockingPlayerChoiceContext(), canonical.ToMutable(), player.Creature,
                amount, player.Creature, null);
        ContinuationStamp predicted = Capture(simulator);
        ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
        if (predicted != actual)
            throw new InvalidOperationException($"Independent applications differ: {predicted.DescribeFirstDifference(actual)}");
        if (first != Capture(sibling))
            throw new InvalidOperationException("Instanced power application changed its sibling.");
        CombatPredictionSimulator recaptured = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var recapturedState = (SimulatedCombatState)recaptured.State.CombatState;
        recapturedState.ApplyPower(canonical.GetType(), player.Creature, 3, player.Creature);
        PowerLifecycleSupport.ResolvePowerAmountChanges(recaptured, recapturedState);
        await PowerCmd.Apply(new BlockingPlayerChoiceContext(), canonical.ToMutable(), player.Creature,
            3, player.Creature, null);
        ContinuationStamp recapturedStamp = Capture(recaptured);
        actual = ContinuationStamp.CaptureLive(combat);
        if (recapturedStamp != actual)
            throw new InvalidOperationException($"Recaptured instances differ: {recapturedStamp.DescribeFirstDifference(actual)}");
        PowerModel removed = recapturedState.EffectivePowers().First(power => power.GetType() == canonical.GetType());
        recapturedState.SetPowerAmount(removed, 0);
        await PowerCmd.Remove(player.Creature.Powers.First(power => power.GetType() == canonical.GetType()));
        recapturedStamp = Capture(recaptured);
        actual = ContinuationStamp.CaptureLive(combat);
        if (recapturedStamp != actual)
            throw new InvalidOperationException($"Instance removal differs: {recapturedStamp.DescribeFirstDifference(actual)}");
        _completedChecks.Add($"InstancedPower:{canonical.Id.Entry}:NativeApplications:ForkIsolation");
    }
}
