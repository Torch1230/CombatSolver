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
    private async Task AssertPowerDurationApplicationAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_SILENT", Pile = "Hand" });
        var enemy = combat.Enemies[0];
        var captured = CombatRootSnapshot.Capture(combat);
        var root = captured.ForkSimulator();
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        // Three application entrances, followed by a skip, stacking without renewal,
        // expiration, and reacquisition. The final three calls are blocked by Artifact.
        int[] steps = [0, 1, 2, 3, 0, 1, 2, 3, 3, 0, 1, 2, 4, 0, 1, 2];
        List<(MoveStateSnapshot State, string[] Powers)> expected = [];
        List<object> evidence = [];
        using (SimulationNotificationIsolation.Enter())
        {
            var prediction = root.Fork();
            foreach (int step in steps)
            {
                Apply((SimulatedCombatState)prediction.State.CombatState, step);
                expected.Add((CaptureSimulated(prediction, (SimulatedCombatState)prediction.State.CombatState, player, enemy),
                    SurgicalPowerValues(((SimulatedCombatState)prediction.State.CombatState).EffectivePowers()).ToArray()));
            }
            // All three entrances represent the same native command. This also detects
            // a stale skip entry when a new duration Power is blocked before creation.
            var evaluator = new SurgicalEvaluationDriver(captured, display, damage, policy);
            foreach (bool blocked in new[] { false, true })
            {
                var variants = Enumerable.Range(0, 3).Select(mode =>
                {
                    var fork = root.Fork(); var shadow = (SimulatedCombatState)fork.State.CombatState;
                    if (blocked) shadow.Apply<ArtifactPower>(player.Creature, 1, player.Creature);
                    if (mode == 0) shadow.Apply<WeakPower>(player.Creature, 1, enemy);
                    if (mode == 1) shadow.ApplyFromMonster<WeakPower>(player.Creature, 1, enemy);
                    if (mode == 2) shadow.ApplyPowerSkippingNextDurationTick(typeof(WeakPower), player.Creature, 1, enemy);
                    return ReleaseSurgicalSnapshot(evaluator.Evaluate(fork)).StateKey;
                }).ToArray();
                evidence.Add(new { blocked, equivalentEntrances = variants.Distinct().Count() == 1, keys = variants.Select(key => key.ToString()).ToArray() });
            }
        }
        for (int index = 0; index < steps.Length; index++)
        {
            switch (steps[index])
            {
                case 0: await PowerCmd.Apply<WeakPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, enemy, null); break;
                case 1: await PowerCmd.Apply<VulnerablePower>(new BlockingPlayerChoiceContext(), player.Creature, 1, enemy, null); break;
                case 2: await PowerCmd.Apply<FrailPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, enemy, null); break;
                case 3: await Hook.AfterSideTurnEnd(combat, CombatSide.Enemy, [enemy]); break;
                case 4:
                    foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
                    await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), player.Creature, 3, player.Creature, null);
                    break;
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var actual = CaptureActual(combat, player, enemy);
            var actualPowers = SurgicalPowerValues(combat.Creatures.SelectMany(creature => creature.Powers).ToArray()).ToArray();
            evidence.Add(new { index, step = steps[index], expected = expected[index].State, actual,
                expectedPowers = expected[index].Powers, actualPowers });
            Write();
            AssertSnapshotEqual(expected[index].State, actual, "PowerDurationApplication", "Native" + index);
            if (!expected[index].Powers.SequenceEqual(actualPowers)) throw new InvalidOperationException("Native duration application lifetime metadata differs.");
        }
        using (SimulationNotificationIsolation.Enter())
        {
            var evaluator = new SurgicalEvaluationDriver(captured, display, damage, policy);
            foreach (bool blocked in new[] { false, true })
            {
                var variants = Enumerable.Range(0, 3).Select(mode =>
                {
                    var fork = root.Fork(); var shadow = (SimulatedCombatState)fork.State.CombatState;
                    if (blocked) shadow.Apply<ArtifactPower>(player.Creature, 1, player.Creature);
                    if (mode == 0) shadow.Apply<WeakPower>(player.Creature, 1, enemy);
                    if (mode == 1) shadow.ApplyFromMonster<WeakPower>(player.Creature, 1, enemy);
                    if (mode == 2) shadow.ApplyPowerSkippingNextDurationTick(typeof(WeakPower), player.Creature, 1, enemy);
                    return ReleaseSurgicalSnapshot(evaluator.Evaluate(fork)).StateKey;
                }).ToArray();
                if (variants.Distinct().Count() != 1) throw new InvalidOperationException("Equivalent application entrances retained different duration state.");
            }
            var replay = root.Fork();
            for (int index = 0; index < steps.Length; index++)
            {
                Apply((SimulatedCombatState)replay.State.CombatState, steps[index]);
                AssertSnapshotEqual(expected[index].State, CaptureSimulated(replay, (SimulatedCombatState)replay.State.CombatState, player, enemy),
                    "PowerDurationApplication", "ReplayAfterNative" + index);
            }
        }
        _completedChecks.Add("PowerDurationApplication:ThreeEntrances:Native16Steps:NewStackExpireReacquire:ArtifactBlockedNoSkip:EquivalentKeys:FullStateAndLifetimeFields:ReplayAfterNative");
        void Apply(SimulatedCombatState shadow, int step)
        {
            switch (step)
            {
                case 0: shadow.Apply<WeakPower>(player.Creature, 1, enemy); break;
                case 1: shadow.ApplyFromMonster<VulnerablePower>(player.Creature, 1, enemy); break;
                case 2: shadow.ApplyPowerSkippingNextDurationTick(typeof(FrailPower), player.Creature, 1, enemy); break;
                case 3: CorePowerSupport.TickDurations(shadow); break;
                case 4:
                    shadow.SetAmount<WeakPower>(player.Creature, 0); shadow.SetAmount<VulnerablePower>(player.Creature, 0);
                    shadow.SetAmount<FrailPower>(player.Creature, 0); shadow.Apply<ArtifactPower>(player.Creature, 3, player.Creature);
                    break;
            }
        }
        void Write()
        {
            if (string.IsNullOrWhiteSpace(_request.EvidenceDirectory)) return;
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "power-duration-application.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
