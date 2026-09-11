using System.Text.Json;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactDoomAsync(CombatState combat, Player player)
    {
        bool enemySide = _request.ScenarioId == "COMPACT-DOOM-ENEMY";
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Hand" });
        var enemy = combat.Enemies.Single(); var target = enemySide ? enemy : player.Creature;
        await SetBlockAsync(target, 17);
        await PowerCmd.Apply<DoomPower>(new BlockingPlayerChoiceContext(), target, 5, player.Creature, null);
        await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), target, 3, player.Creature, null);
        await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), target, 2, player.Creature, null);
        foreach (var power in target.Powers) power.AmountOnTurnStart = 29;
        List<object> evidence = [];
        foreach (int hp in new[] { 6, 5 })
        {
            await CreatureCmd.SetCurrentHp(target, hp);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var captured = CombatRootSnapshot.Capture(combat);
            var root = captured.ForkSimulator();
            var original = CaptureActual(combat, player, enemy);
            var display = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
            CompactDiscardProjection adapter;
            ResumableDiscardProgram.Candidate initial, completed;
            SimulationSnapshot expectedEvaluation;
            MoveStateSnapshot expected;
            using (SimulationNotificationIsolation.Enter())
            {
                adapter = new(root, player, includeAttacks: true, includePowerPhases: true);
                var lane = adapter.Program; initial = lane.Freeze();
                var mark = lane.State.Mark();
                // Doom uses opposite hook boundaries for the two sides.
                if (enemySide) lane.EndSidePowerEffects(true); else lane.BeforeEndSidePowerEffects(false);
                if (lane.Creature(enemySide ? 1 : 0).CurrentHp != hp)
                    throw new InvalidOperationException("Doom ran at the other side-end hook boundary.");
                lane.State.Rollback(mark);
                if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze())) throw new InvalidOperationException("Doom phase rollback leaked.");
                Execute(lane);
                var oracle = root.Fork(); var shadow = (SimulatedCombatState)oracle.State.CombatState;
                if (enemySide) HookMirrors.BeforeSideTurnEnd(oracle, CombatSide.Enemy, [enemy]);
                else if (!EndTurnPowerSupport.TriggerRegular(oracle, shadow, CombatSide.Player, [player.Creature]))
                    throw new InvalidOperationException("Doom oracle unexpectedly suspended.");
                if (!CorePowerSupport.ApplyEnemyDeathPowers(oracle, shadow, shadow.KnownEnemies, new HashSet<uint>()))
                    throw new InvalidOperationException("Doom death cleanup unexpectedly suspended.");
                var projected = adapter.Materialize(lane);
                adapter.AssertValues(lane, oracle); adapter.AssertValues(lane, projected);
                expected = CaptureSimulated(oracle, shadow, player, enemy);
                AssertSnapshotEqual(expected, CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy), "CompactDoom", "Projection");
                if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projected, adapter))
                    || !CompactPowerValues(shadow.EffectivePowers()).SequenceEqual(CompactPowerValues(((SimulatedCombatState)projected.State.CombatState).EffectivePowers())))
                    throw new InvalidOperationException("Doom direct-kill history or Power lifetime differs.");
                if (lane.Block != (enemySide ? original.PlayerBlock : 17)
                    || lane.EventCount != (hp == 5 ? 2 : 0) || oracle.History.Entries.Count != root.History.Entries.Count)
                    throw new InvalidOperationException("Doom was treated as damage or consumed block.");
                AssertCompactRngSet(oracle.Rng, projected.Rng);
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                var reader = adapter.CreateReadView(); reader.Read(lane);
                expectedEvaluation = Release(evaluator.Evaluate(oracle));
                AssertCompactEvaluation(expectedEvaluation, Release(evaluator.Evaluate(projected)));
                AssertCompactEvaluation(expectedEvaluation, evaluator.Evaluate(reader));
                completed = lane.Freeze();
                initial.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(Release(evaluator.Evaluate(root)), evaluator.Evaluate(reader));
                completed.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(expectedEvaluation, evaluator.Evaluate(reader));
                if (hp == 5 && (!lane.CheckWinCondition() || lane.DefeatTerminal == enemySide))
                    throw new InvalidOperationException("Doom did not lock the correct terminal outcome.");
                AssertSnapshotEqual(original, CaptureActual(combat, player, enemy), "CompactDoom", "ActualUnchanged");
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var lane = initial.Open(); Execute(lane);
                if (!lane.State.Freeze().ContentEquals(completed.Open().State.Freeze())) throw new InvalidOperationException("Doom worker differs.");
                var reader = adapter.CreateReadView(); reader.Read(lane);
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                AssertCompactEvaluation(expectedEvaluation, evaluator.Evaluate(reader));
            })));
            if (enemySide) await Hook.BeforeSideTurnEnd(combat, CombatSide.Enemy, [enemy]);
            else await Hook.AfterSideTurnEnd(combat, CombatSide.Player, [player.Creature]);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var actual = CaptureActual(combat, player, enemy);
            evidence.Add(new { enemySide, hp, expected, actual });
            if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            {
                Directory.CreateDirectory(_request.EvidenceDirectory);
                File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-doom.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            }
            AssertSnapshotEqual(expected, actual, "CompactDoom", "Native");
            using (SimulationNotificationIsolation.Enter())
            {
                var lane = initial.Open(); Execute(lane);
                var reader = adapter.CreateReadView(); reader.Read(lane);
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                AssertCompactEvaluation(expectedEvaluation, evaluator.Evaluate(reader));
                AssertSnapshotEqual(original, CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy), "CompactDoom", "RootAfterNative");
            }
        }
        _completedChecks.Add($"CompactDoom:EnemySide{enemySide}:ThresholdAndOppositePhase:NativeDirectKill:NoDamageHistoryOrBlockLoss:PowerRetirement:FullKeys:EightWorkers:FrozenAfterNative");

        void Execute(ResumableDiscardProgram lane)
        {
            if (enemySide) lane.BeforeEndSidePowerEffects(true); else lane.EndSidePowerEffects(false);
        }
    }
}
