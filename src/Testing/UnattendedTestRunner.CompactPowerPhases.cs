using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactPowerPhasesAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "DODGE_AND_ROLL", "DODGE_AND_ROLL", "PIERCING_WAIL", "PIERCING_WAIL", "NEUTRALIZE", "DEFEND_SILENT" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        var enemy = combat.Enemies.Single();
        await CreatureCmd.SetMaxHp(enemy, 300);
        await CreatureCmd.SetCurrentHp(enemy, 300);
        await SetBlockAsync(player.Creature, 11);
        await SetBlockAsync(enemy, 17);
        await PowerCmd.Apply<DexterityPower>(new BlockingPlayerChoiceContext(), player.Creature, 3, player.Creature, null);
        await PowerCmd.Apply<BlockNextTurnPower>(new BlockingPlayerChoiceContext(), player.Creature, 7, enemy, null);
        await PowerCmd.Apply<StratagemPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
        foreach (var owner in new[] { player.Creature, enemy })
        {
            await PowerCmd.Apply<WeakPower>(new BlockingPlayerChoiceContext(), owner, 1, player.Creature, null);
            await PowerCmd.Apply<VulnerablePower>(new BlockingPlayerChoiceContext(), owner, 2, player.Creature, null);
            await PowerCmd.Apply<FrailPower>(new BlockingPlayerChoiceContext(), owner, 1, player.Creature, null);
            await PowerCmd.Apply<PoisonPower>(new BlockingPlayerChoiceContext(), owner, 3, player.Creature, null);
        }
        await PowerCmd.Apply<PiercingWailPower>(new BlockingPlayerChoiceContext(), enemy, 6, player.Creature, null);
        await PowerCmd.Remove(enemy.GetPower<StrengthPower>()!); // Restoration must create a new self-applied instance.
        await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), player.Creature, 3, player.Creature, null);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers))
            power.AmountOnTurnStart = power is DexterityPower ? int.MinValue : 47;
        enemy.GetPower<WeakPower>()!.SkipNextDurationTick = true;
        enemy.GetPower<PoisonPower>()!.SkipNextDurationTick = true; // Irrelevant for this phase, but owned metadata.
        enemy.GetPower<PiercingWailPower>()!._target = player.Creature;
        SetEnergy(player, 20); SetStars(player, 0);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CardModel[] cards = player.PlayerCombatState!.Hand.Cards.ToArray();
        var captured = CombatRootSnapshot.Capture(combat);
        var root = captured.ForkSimulator();
        var rootState = CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        // Negative steps are phase bodies: snapshot both owners, clear player/enemy,
        // player side end, enemy side end. Nonnegative steps play a root card.
        int[] native = [-1, -2, -4, -5, -3, 0, -1, -2, 2, 4, -5, 1, -2, 3, -5, -5];
        int[][] routes = [[-2], [-3, -5], [-4], [2, -5], native];
        CompactDiscardProjection adapter;
        ResumableDiscardProgram.Candidate initial;
        List<(int[] Route, ResumableDiscardProgram.Candidate State, SimulationSnapshot Evaluation)> samples = [];
        List<(MoveStateSnapshot State, string[] Powers)> expected = [];
        List<object> evidence = [];
        using (SimulationNotificationIsolation.Enter())
        {
            adapter = new(root, player, includeAttacks: true, includePowerPhases: true);
            var lane = adapter.Program;
            initial = lane.Freeze();
            var reader = adapter.CreateReadView(); var uncached = adapter.CreateReadView(false);
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            foreach (var route in routes)
            {
                var mark = lane.State.Mark();
                var oracle = root.Fork();
                for (int stepIndex = 0; stepIndex < route.Length; stepIndex++)
                {
                    int step = route[stepIndex];
                    Execute(lane, oracle, step);
                    var projection = adapter.Materialize(lane);
                    adapter.AssertValues(lane, oracle); adapter.AssertValues(lane, projection);
                    var oracleState = CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy);
                    AssertSnapshotEqual(oracleState, CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy),
                        "CompactPowerPhases", "Projection");
                    string[] powers = CompactPowerValues(((SimulatedCombatState)oracle.State.CombatState).EffectivePowers()).ToArray();
                    if (!powers.SequenceEqual(CompactPowerValues(((SimulatedCombatState)projection.State.CombatState).EffectivePowers()))
                        || !CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projection, adapter)))
                        throw new InvalidOperationException("Power phase full lifetime fields or source history differ.");
                    AssertCompactRngSet(oracle.Rng, projection.Rng);
                    var evaluation = Release(evaluator.Evaluate(oracle));
                    AssertCompactEvaluation(evaluation, Release(evaluator.Evaluate(projection)));
                    reader.Read(lane); uncached.Read(lane);
                    AssertCompactEvaluation(evaluation, evaluator.Evaluate(reader));
                    AssertCompactEvaluation(evaluation, evaluator.Evaluate(uncached));
                    samples.Add((route[..(stepIndex + 1)],
                        lane.Freeze(), evaluation));
                    if (ReferenceEquals(route, native)) expected.Add((oracleState, powers));
                }
                lane.State.Rollback(mark);
                if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze()))
                    throw new InvalidOperationException("Power phase rollback leaked values.");
            }
            foreach (var sample in samples.AsEnumerable().Reverse())
            {
                sample.State.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
            initial.RestoreInto(lane); reader.Read(lane);
            AssertCompactEvaluation(Release(evaluator.Evaluate(root)), evaluator.Evaluate(reader));
            AssertSnapshotEqual(rootState, CaptureActual(combat, player, enemy), "CompactPowerPhases", "RootUnchanged");
            var unadmitted = new CompactDiscardProjection(root, player, includeAttacks: true).Program;
            foreach (var rejected in new[] { unadmitted, unadmitted.Freeze().Open() })
            {
                var before = rejected.State.Freeze();
                foreach (int step in new[] { -1, -2, -5 })
                {
                    bool failed = false;
                    try { Execute(rejected, null, step); }
                    catch (InvalidOperationException) { failed = true; }
                    if (!failed || !before.ContentEquals(rejected.State.Freeze()))
                        throw new InvalidOperationException("Unadmitted Power phase modified its root.");
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            var lane = initial.Open(); var reader = adapter.CreateReadView();
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            foreach (var sample in samples.AsEnumerable().Reverse())
            {
                initial.RestoreInto(lane);
                foreach (int step in sample.Route) Execute(lane, null, step);
                if (!lane.State.Freeze().ContentEquals(sample.State.Open().State.Freeze()))
                    throw new InvalidOperationException("Power phase worker differs from frozen state.");
                reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
        })));
        for (int index = 0; index < native.Length; index++)
        {
            int step = native[index];
            if (step >= 0)
            {
                if (!cards[step].TryManualPlay(cards[step].Type == CardType.Attack ? enemy : null))
                    throw new InvalidOperationException("Native Power phase card rejected.");
            }
            else if (step == -1)
                foreach (var owner in new[] { player.Creature, enemy }) owner.BeforeTurnStart(owner.Side);
            else if (step is -2 or -3)
            {
                var owner = step == -2 ? player.Creature : enemy;
                await SetBlockAsync(owner, 0);
                await Hook.AfterBlockCleared(combat, owner);
            }
            else await Hook.AfterSideTurnEnd(combat, step == -4 ? CombatSide.Player : CombatSide.Enemy,
                step == -4 ? [player.Creature] : [enemy]);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var actual = CaptureActual(combat, player, enemy);
            string[] actualPowers = CompactPowerValues(combat.Creatures.SelectMany(creature => creature.Powers).ToArray()).ToArray();
            evidence.Add(new { index, step, expected = expected[index].State, actual, expectedPowers = expected[index].Powers, actualPowers });
            if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            {
                Directory.CreateDirectory(_request.EvidenceDirectory);
                File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-power-phases.json"),
                    JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            }
            AssertSnapshotEqual(expected[index].State, actual, "CompactPowerPhases", "Native" + index);
            if (!expected[index].Powers.SequenceEqual(actualPowers)) throw new InvalidOperationException("Native Power phase full lifetime fields differ.");
        }
        using (SimulationNotificationIsolation.Enter())
        {
            var lane = initial.Open();
            foreach (int step in native) Execute(lane, null, step);
            var projection = adapter.Materialize(lane);
            AssertSnapshotEqual(expected[^1].State, CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy),
                "CompactPowerPhases", "FrozenAfterNative");
            AssertSnapshotEqual(rootState, CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy),
                "CompactPowerPhases", "RootAfterNative");
        }
        _completedChecks.Add("CompactPowerPhases:FiveRoutes16NativeSteps:SnapshotSignedStart:DurationSkipAndExpire:ArtifactBypass:PoisonMetadata:StrengthOwnerAndReacquire:UnpoweredNextBlock:FullStatePowerFieldsHistoryRngSnapshot:ReverseRestore:EightWorkers:Admission");

        void Execute(ResumableDiscardProgram lane, CombatPredictionSimulator? oracle, int step)
        {
            var shadow = (SimulatedCombatState?)oracle?.State.CombatState;
            if (step >= 0)
            {
                lane.Begin(adapter.IndexOf(cards[step]), cards[step].Type == CardType.Attack ? 1 : -1); lane.Run();
                if (oracle != null && !oracle.ManualPlay(oracle.State.FindCard(cards[step])!, cards[step].Type == CardType.Attack ? enemy : null, out _))
                    throw new InvalidOperationException("Power phase oracle card rejected.");
            }
            else if (step == -1)
            {
                lane.CapturePowerTurnStart(0); lane.CapturePowerTurnStart(1);
                shadow?.SnapshotPowerAmountsAtTurnStart([player.Creature, enemy]);
            }
            else if (step is -2 or -3)
            {
                int owner = step == -2 ? 0 : 1;
                lane.ClearCreatureBlock(owner);
                if (oracle != null)
                {
                    var creature = owner == 0 ? player.Creature : enemy;
                    var state = oracle.State.GetCreature(creature);
                    state.DamageBlock(state.Block, ValueProp.Unpowered);
                    if (!CorePowerSupport.TriggerAfterBlockCleared(oracle, shadow!, creature))
                        throw new InvalidOperationException("Power phase block oracle suspended.");
                }
            }
            else
            {
                lane.EndSidePowerEffects(step == -5);
                if (shadow != null)
                {
                    shadow.RestoreTemporaryStrength(step == -5 ? [enemy] : [player.Creature]);
                    if (step == -5) CorePowerSupport.TickDurations(shadow);
                }
            }
            if (!lane.Complete || oracle?.HasPendingChoice == true) throw new InvalidOperationException("Power phase unexpectedly suspended.");
        }
    }
}
