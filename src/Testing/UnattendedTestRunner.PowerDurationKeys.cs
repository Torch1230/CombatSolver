using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertPowerDurationKeysAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_SILENT", Pile = "Hand" });
        var enemy = combat.Enemies[0];
        await PowerCmd.Apply<WeakPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, enemy, null);
        await PowerCmd.Apply<VulnerablePower>(new BlockingPlayerChoiceContext(), player.Creature, 1, enemy, null);
        await PowerCmd.Apply<FrailPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, enemy, null);
        await PowerCmd.Apply<PoisonPower>(new BlockingPlayerChoiceContext(), player.Creature, 3, enemy, null);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var captured = CombatRootSnapshot.Capture(combat);
        var root = captured.ForkSimulator();
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var rootState = CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy);
        var duration = new[] { typeof(WeakPower), typeof(VulnerablePower), typeof(FrailPower) };
        List<object> evidence = [];
        bool distinct = true, poisonFutureNeutral = false, poisonNeutral;
        MoveStateSnapshot expected;
        string[] expectedPowers;
        using (SimulationNotificationIsolation.Enter())
        {
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            StateFingerprint rootKey = Release(evaluator.Evaluate(root)).StateKey;
            foreach (Type type in duration.Append(typeof(PoisonPower)))
            {
                var sibling = root.Fork();
                var shadow = (SimulatedCombatState)sibling.State.CombatState;
                PowerModel power = shadow.EffectivePowers().Single(value => value.GetType() == type);
                if (!power.SkipNextDurationTick) throw new InvalidOperationException("Duration fixture did not capture the native skip flag.");
                PowerModel mutable = power switch
                {
                    WeakPower => shadow.GetMutablePower<WeakPower>(player.Creature)!,
                    VulnerablePower => shadow.GetMutablePower<VulnerablePower>(player.Creature)!,
                    FrailPower => shadow.GetMutablePower<FrailPower>(player.Creature)!,
                    PoisonPower => shadow.GetMutablePower<PoisonPower>(player.Creature)!,
                    _ => throw new InvalidOperationException("Unexpected duration fixture Power.")
                };
                mutable.SkipNextDurationTick = false;
                StateFingerprint siblingKey = Release(evaluator.Evaluate(sibling)).StateKey;
                var siblingState = CaptureSimulated(sibling, shadow, player, enemy);
                bool keyDiffers = rootKey != siblingKey;
                bool continuationDiffers = rootState.ExactContinuationState != siblingState.ExactContinuationState;
                var rootFuture = root.Fork(); var siblingFuture = sibling.Fork();
                CorePowerSupport.TickDurations((SimulatedCombatState)rootFuture.State.CombatState);
                CorePowerSupport.TickDurations((SimulatedCombatState)siblingFuture.State.CombatState);
                var rootAfter = CaptureSimulated(rootFuture, (SimulatedCombatState)rootFuture.State.CombatState, player, enemy);
                var siblingAfter = CaptureSimulated(siblingFuture, (SimulatedCombatState)siblingFuture.State.CombatState, player, enemy);
                bool futureDiffers = !rootAfter.PlayerPowers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .SequenceEqual(siblingAfter.PlayerPowers.OrderBy(pair => pair.Key, StringComparer.Ordinal));
                if (type != typeof(PoisonPower)) distinct &= keyDiffers && continuationDiffers && futureDiffers;
                else poisonFutureNeutral = !futureDiffers;
                evidence.Add(new { power = type.Name, keyDiffers, continuationDiffers, futureDiffers,
                    rootKey = rootKey.ToString(), siblingKey = siblingKey.ToString(),
                    rootContinuation = rootState.ExactContinuationState, siblingContinuation = siblingState.ExactContinuationState,
                    rootFuture = rootAfter.PlayerPowers, siblingFuture = siblingAfter.PlayerPowers });
            }
            var poisonSibling = root.Fork();
            ((SimulatedCombatState)poisonSibling.State.CombatState).GetMutablePower<PoisonPower>(player.Creature)!.SkipNextDurationTick = false;
            poisonNeutral = poisonFutureNeutral && rootKey == Release(evaluator.Evaluate(poisonSibling)).StateKey
                && rootState.ExactContinuationState == CaptureSimulated(poisonSibling, (SimulatedCombatState)poisonSibling.State.CombatState, player, enemy).ExactContinuationState;
            var prediction = root.Fork();
            CorePowerSupport.TickDurations((SimulatedCombatState)prediction.State.CombatState);
            expected = CaptureSimulated(prediction, (SimulatedCombatState)prediction.State.CombatState, player, enemy);
            expectedPowers = CompactPowerValues(((SimulatedCombatState)prediction.State.CombatState).EffectivePowers()).ToArray();
        }
        await Hook.AfterSideTurnEnd(combat, CombatSide.Enemy, [enemy]);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var actual = CaptureActual(combat, player, enemy);
        AssertSnapshotEqual(expected, actual, "PowerDurationKeys", "NativeTick");
        if (!expectedPowers.SequenceEqual(CompactPowerValues(combat.Creatures.SelectMany(creature => creature.Powers).ToArray())))
            throw new InvalidOperationException("Native duration tick fields or Power order differ.");
        using (SimulationNotificationIsolation.Enter())
        {
            var sibling = root.Fork();
            CorePowerSupport.TickDurations((SimulatedCombatState)sibling.State.CombatState);
            AssertSnapshotEqual(expected, CaptureSimulated(sibling, (SimulatedCombatState)sibling.State.CombatState, player, enemy),
                "PowerDurationKeys", "FrozenAfterNative");
            AssertSnapshotEqual(rootState, CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy),
                "PowerDurationKeys", "RootUnchanged");
        }
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "power-duration-keys.json"),
                JsonSerializer.Serialize(new { cases = evidence, poisonNeutral, distinct, expected, actual }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (!distinct || !poisonNeutral)
            throw new InvalidOperationException("Duration keys/continuation merge distinct futures or distinguish irrelevant Poison metadata.");
        _completedChecks.Add("PowerDurationKeys:WeakVulnerableFrail:DistinctKeyContinuationAndFuture:PoisonNeutral:NativeSideEndTick:FullStateAndPowerFields:FrozenAfterNative:RootUnchanged");
    }
}
