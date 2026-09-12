using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertOstyStateLifecycleAsync(CombatState combat, Player player)
    {
        if (player.Osty != null) throw new InvalidOperationException("Osty ownership fixture requires an initially absent pet.");
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Hand" });
        var enemy = combat.Enemies.Single();
        await CreatureCmd.SetMaxHp(player.Creature, 100);
        await CreatureCmd.SetCurrentHp(player.Creature, 100);
        await CreatureCmd.SetMaxHp(enemy, 300);
        await CreatureCmd.SetCurrentHp(enemy, 300);
        await SetBlockAsync(player.Creature, 4);
        var absentRoot = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var absent = CaptureSimulated(absentRoot, (SimulatedCombatState)absentRoot.State.CombatState, player, enemy);
        await OstyCmd.Summon(new BlockingPlayerChoiceContext(), player, 5, null);
        var osty = player.Osty ?? throw new InvalidOperationException("Native summon did not create Osty.");
        var initialSummon = CaptureActual(combat, player, enemy);
        await CreatureCmd.SetMaxHp(osty, 7);
        await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), osty, 2, player.Creature, null);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        bool absentRootUnchanged = ((SimulatedCombatState)absentRoot.State.CombatState).GetOsty(player) == null;
        var captured = CombatRootSnapshot.Capture(combat);
        var root = captured.ForkSimulator();
        if (((ICombatPredictionCreatureSemantics)root.State.CombatState).ShouldRemoveAfterDeath(osty)
            != MegaCrit.Sts2.Core.Hooks.Hook.ShouldCreatureBeRemovedFromCombatAfterDeath(combat, osty))
            throw new InvalidOperationException("Osty death roster retention differs from native.");
        var original = CaptureActual(combat, player, enemy);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var snapshots = new List<MoveStateSnapshot>();
        var evaluations = new List<SimulationSnapshot>();
        var damageResults = new List<string[]>();
        using (SimulationNotificationIsolation.Enter())
        {
            var branch = root.Fork();
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            for (int step = 0; step < 5; step++)
            {
                damageResults.Add(Apply(branch, step));
                snapshots.Add(CaptureSimulated(branch, (SimulatedCombatState)branch.State.CombatState, player, enemy));
                evaluations.Add(Release(evaluator.Evaluate(branch)));
                var fork = branch.Fork();
                AssertSnapshotEqual(snapshots[^1], CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy), "OstyLifecycle", "ForkAfterStep" + step);
                AssertCompactEvaluation(evaluations[^1], Release(evaluator.Evaluate(fork)), "OstyLifecycle/Fork" + step);
                branch = fork;
            }
            AssertSnapshotEqual(original, CaptureActual(combat, player, enemy), "OstyLifecycle", "ActualUnchanged");
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            var branch = root.Fork();
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            for (int step = 0; step < 5; step++)
            {
                if (!damageResults[step].SequenceEqual(Apply(branch, step))) throw new InvalidOperationException("Osty worker damage results differ.");
                AssertSnapshotEqual(snapshots[step], CaptureSimulated(branch, (SimulatedCombatState)branch.State.CombatState, player, enemy), "OstyLifecycle", "Worker" + step);
                AssertCompactEvaluation(evaluations[step], Release(evaluator.Evaluate(branch)), "OstyLifecycle/Worker" + step);
            }
        })));
        var actual = new List<MoveStateSnapshot>();
        var nativeDamage = new List<string[]>();
        for (int step = 0; step < 5; step++)
        {
            IEnumerable<DamageResult> results = [];
            if (step == 0) results = await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), player.Creature, 14, ValueProp.Move, enemy, null, null);
            else if (step == 1) await OstyCmd.Summon(new BlockingPlayerChoiceContext(), player, 7, null);
            else if (step == 2) await OstyCmd.Summon(new BlockingPlayerChoiceContext(), player, 4, null);
            else if (step == 3) results = await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), osty, 14, ValueProp.Unblockable | ValueProp.Unpowered, enemy, null, null);
            else await OstyCmd.Summon(new BlockingPlayerChoiceContext(), player, 3, null);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            actual.Add(CaptureActual(combat, player, enemy));
            nativeDamage.Add(Describe(results));
        }
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "osty-state-lifecycle.json"), JsonSerializer.Serialize(new
                { absentRootUnchanged, expected = snapshots, actual, damageResults, nativeDamage }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (!absentRootUnchanged) throw new InvalidOperationException("An initially absent captured Osty changed after native summon.");
        for (int step = 0; step < 5; step++)
        {
            AssertSnapshotEqual(snapshots[step], actual[step], "OstyLifecycle", "Native" + step);
            if (!damageResults[step].SequenceEqual(nativeDamage[step])) throw new InvalidOperationException("Native Osty damage result sequence differs.");
        }
        using (SimulationNotificationIsolation.Enter())
        {
            AssertSnapshotEqual(absent, CaptureSimulated(absentRoot, (SimulatedCombatState)absentRoot.State.CombatState, player, enemy), "OstyLifecycle", "AbsentRootAfterNative");
            AssertSnapshotEqual(original, CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy), "OstyLifecycle", "LivingRootAfterNative");
            var laterSummon = absentRoot.Fork();
            var laterState = (SimulatedCombatState)laterSummon.State.CombatState;
            laterState.SummonOsty(laterSummon, player, 5);
            PowerLifecycleSupport.ResolvePowerAmountChanges(laterSummon, laterState);
            if (ReferenceEquals(laterState.GetOsty(player), osty)) throw new InvalidOperationException("Frozen absent root borrowed the native-created pet.");
            AssertSnapshotEqual(initialSummon, CaptureSimulated(laterSummon, laterState, player, enemy), "OstyLifecycle", "IndependentSummonAfterNative");
        }
        _completedChecks.Add("OstyStateLifecycle:AbsentAndLivingRoot:DamageRedirectAndSpill:PowerCleanup:TwoRevives:MaxHpGrowth:FullSnapshotKeysContinuations:FiveSteps:EightWorkers:FrozenAfterNative");

        string[] Apply(CombatPredictionSimulator simulator, int step)
        {
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            return step switch
            {
                0 => Describe(simulator.Damage(player.Creature, 14, ValueProp.Move, enemy)),
                1 => Summon(7),
                2 => Summon(4),
                3 => Describe(simulator.Damage(osty, 14, ValueProp.Unblockable | ValueProp.Unpowered, enemy)),
                4 => Summon(3),
                _ => throw new InvalidOperationException("Unknown Osty test step.")
            };
            string[] Summon(int amount) { shadow.SummonOsty(simulator, player, amount); return []; }
        }
        static string[] Describe(IEnumerable<DamageResult> results) => results.Select(r =>
            $"{r.Receiver.CombatId}:{r.Props}:{r.BlockedDamage}:{r.UnblockedDamage}:{r.OverkillDamage}:{r.WasTargetKilled}:{r.WasBlockBroken}:{r.WasFullyBlocked}").ToArray();
    }
}
