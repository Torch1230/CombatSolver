using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactPowerBoundariesAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "DEFEND_SILENT", "DEFEND_SILENT", "STRIKE_SILENT" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        CardModel[] cards = player.PlayerCombatState!.Hand.Cards.ToArray();
        cards[0].DynamicVars.Block.BaseValue = 4;
        cards[1].DynamicVars.Block.BaseValue = 0;
        cards[2].DynamicVars.Damage.BaseValue = 0;
        await PowerCmd.Apply<DexterityPower>(new BlockingPlayerChoiceContext(), player.Creature, -3, player.Creature, null);
        await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), player.Creature, -3, player.Creature, null);
        await PowerCmd.Apply<FrailPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
        SetEnergy(player, 10); SetStars(player, 0);
        await SetBlockAsync(player.Creature, 3);
        var enemy = combat.Enemies[0];
        await SetBlockAsync(enemy, 2);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        List<MoveStateSnapshot> expected = [];
        CombatPredictionSimulator simulator = root.ForkSimulator();
        using (SimulationNotificationIsolation.Enter())
        {
            var adapter = new CompactDiscardProjection(simulator, player, includeAttacks: true);
            var values = adapter.Program;
            var reader = adapter.CreateReadView();
            var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
            var oracle = simulator.Fork();
            for (int index = 0; index < cards.Length; index++)
            {
                values.Begin(adapter.IndexOf(cards[index]), index == 2 ? adapter.CreatureIndex(enemy) : -1);
                values.Run();
                if (!oracle.ManualPlay(oracle.State.FindCard(cards[index])!, index == 2 ? enemy : null, out _))
                    throw new InvalidOperationException("Power boundary oracle suspended.");
                adapter.AssertValues(values, oracle);
                var projection = adapter.Materialize(values);
                expected.Add(CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy));
                AssertSnapshotEqual(expected[^1], CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState,
                    player, enemy), "CompactPowerBoundary", $"Projection{index}");
                reader.Read(values);
                AssertCompactEvaluation(Release(evaluator.Evaluate(oracle)), evaluator.Evaluate(reader));
            }
            if (values.Block != 3 || values.Creature(adapter.CreatureIndex(enemy)).Block != 2)
                throw new InvalidOperationException("Fractional block or negative Strength escaped native integer/clamp boundaries.");
        }
        for (int index = 0; index < cards.Length; index++)
        {
            if (!cards[index].TryManualPlay(index == 2 ? enemy : null))
                throw new InvalidOperationException("Native Power boundary card rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(expected[index], CaptureActual(combat, player, enemy), "CompactPowerBoundary", $"Native{index}");
        }
        _completedChecks.Add("CompactPowers:NativeNegativeStrengthDexterity:FractionalBlockBelowOne:ZeroBaseBlock:ThreeSteps:FullState:AllSnapshotProperties");

        // The general Power factory must distinguish an owner from an explicitly assigned
        // extra target. This adjacent test remains on the legacy engine, outside compact admission.
        var targetedRoot = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var targetedState = (SimulatedCombatState)targetedRoot.State.CombatState;
        targetedState.ApplyTargeted<ThieveryPower>(enemy, player.Creature, 4, enemy);
        PowerLifecycleSupport.ResolvePowerAmountChanges(targetedRoot, targetedState);
        var retained = targetedRoot.Fork();
        var retainedPower = ((SimulatedCombatState)retained.State.CombatState).GetPower<ThieveryPower>(enemy)!;
        var nativePower = CanonicalModels.Power<ThieveryPower>().ToMutable();
        nativePower.Target = player.Creature;
        await PowerCmd.Apply(new BlockingPlayerChoiceContext(), nativePower, enemy, 4, enemy, null);
        var actual = enemy.GetPower<ThieveryPower>()!;
        if (retainedPower.Owner != actual.Owner || retainedPower.Applier != actual.Applier
            || retainedPower.Target != actual.Target || retainedPower.Amount != actual.Amount)
            throw new InvalidOperationException("Targeted Power creation or Fork lost native ownership/target/applier.");
        AssertSnapshotEqual(CaptureSimulated(retained, (SimulatedCombatState)retained.State.CombatState, player, enemy),
            CaptureActual(combat, player, enemy), "PowerFactory", "TargetedNative");
        _completedChecks.Add("PowerFactory:ExplicitTarget:OwnerAndApplier:Fork:NativeFullState:LegacyEngine");
    }
}
