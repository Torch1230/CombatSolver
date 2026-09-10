using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactConditionalPowersAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        (string Id, int Upgrade)[] input = [("ESCAPE_PLAN", 0), ("ESCAPE_PLAN", 1), ("ESCAPE_PLAN", 1),
            ("DEADLY_POISON", 1), ("HAZE", 1), ("SNAKEBITE", 1), ("DEFY", 1),
            ("STRIKE_SILENT", 0), ("FOOTWORK", 0), ("DEFEND_SILENT", 0)];
        foreach (var card in input)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = card.Id, UpgradeLevels = card.Upgrade, Pile = "Hand" });
        foreach (string card in new[] { "DEFEND_SILENT", "STRIKE_SILENT", "FOOTWORK" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = card, Pile = "Draw" });
        CardModel[] nativeCards = player.PlayerCombatState!.Hand.Cards.ToArray();
        Creature[] enemies = combat.Enemies.ToArray();
        if (enemies.Length != 3) throw new InvalidOperationException("Conditional Power fixture requires three enemies.");
        await CreatureCmd.SetCurrentHp(enemies[2], 6);
        await PowerCmd.Apply<DexterityPower>(new BlockingPlayerChoiceContext(), player.Creature, -1, player.Creature, null);
        await PowerCmd.Apply<PoisonPower>(new BlockingPlayerChoiceContext(), enemies[0], 2, enemies[1], null);
        await PowerCmd.Apply<FrailPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
        await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), enemies[0], 1, enemies[0], null);
        SetEnergy(player, 12); SetStars(player, 0);
        await SetBlockAsync(player.Creature, 3);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var simulator = root.ForkSimulator();
        CompactDiscardProjection adapter;
        ResumableDiscardProgram.Candidate initial;
        List<(int[] Path, ResumableDiscardProgram.Candidate State, SimulationSnapshot Evaluation)> cases = [];
        List<MoveStateSnapshot[]> nativeExpected = [];
        List<string[]> nativePowers = [];
        List<ResumableDiscardProgram.Candidate> nativeStates = [];
        List<object> evidence = [];
        int riskSourceCount;
        int[] route = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        static int TargetIndex(int index) => index switch { 3 => 0, 5 => 1, 6 or 7 => 2, _ => -1 };
        Creature? Target(int index) => TargetIndex(index) is var target && target >= 0 ? enemies[target] : null;
        using (SimulationNotificationIsolation.Enter())
        {
            adapter = new(simulator, player, includeAttacks: true);
            var lane = adapter.Program;
            initial = lane.Freeze();
            var reader = adapter.CreateReadView();
            riskSourceCount = reader.RiskSourceCount;
            var uncached = adapter.CreateReadView(false);
            var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
            MoveStateSnapshot original = CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, enemies[0]);
            int[][] paths = [[0], [1], [0, 1, 2], [2, 1, 0], [3], [4], [3, 4], [4, 3], [5], [3, 5], [5, 3], [6], [4, 7], [7, 4]];
            foreach (int[] path in paths)
            {
                initial.RestoreInto(lane);
                var mark = lane.State.Mark();
                var oracle = simulator.Fork();
                foreach (int card in path)
                {
                    Play(lane, card);
                    if (!oracle.ManualPlay(oracle.State.FindCard(nativeCards[card])!, Target(card), out _))
                        throw new InvalidOperationException("ConditionalPowers oracle unexpectedly suspended.");
                    Compare(lane, oracle, "branch-" + string.Join('-', path) + "-" + card);
                }
                reader.Read(lane);
                var evaluation = evaluator.Evaluate(reader);
                cases.Add((path, lane.Freeze(), evaluation));
                evidence.Add(new { path, evaluation.PlayerBlock, evaluation.StateKey,
                    removed = lane.Count(ResumableDiscardProgram.Pile.Removed), exhausted = lane.Count(ResumableDiscardProgram.Pile.Exhaust), lane.Energy });
                lane.State.Rollback(mark);
                if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze()))
                    throw new InvalidOperationException("ConditionalPowers rollback retained removal, exhaust or captured X.");
            }
            if (cases[0].Evaluation.PlayerBlock == cases[1].Evaluation.PlayerBlock)
                throw new InvalidOperationException("Upgraded/unupgraded Skill draw did not exercise conditional block.");
            initial.RestoreInto(lane);
            var continued = simulator.Fork();
            foreach (int card in route)
            {
                Play(lane, card);
                if (!continued.ManualPlay(continued.State.FindCard(nativeCards[card])!, Target(card), out _))
                    throw new InvalidOperationException("Native conditional-powers oracle unexpectedly suspended.");
                Compare(lane, continued, "native-prefix-" + nativeExpected.Count);
                nativeExpected.Add(enemies.Select(enemy => CaptureSimulated(continued, (SimulatedCombatState)continued.State.CombatState, player, enemy)).ToArray());
                nativePowers.Add(CompactPowerValues(((SimulatedCombatState)continued.State.CombatState).EffectivePowers()));
                nativeStates.Add(lane.Freeze());
            }
            if (lane.CreaturePresent(3) || lane.PowerCount == 0 || lane.Block != 10)
                throw new InvalidOperationException("Conditional Power route missed draw category, block rounding or poisoned death.");
            initial.RestoreInto(lane); reader.Read(lane);
            AssertCompactEvaluation(Release(evaluator.Evaluate(simulator)), evaluator.Evaluate(reader));
            foreach (var sample in cases.AsEnumerable().Reverse())
            {
                sample.State.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
            AssertSnapshotEqual(original, CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState,
                player, enemies[0]), "CompactConditionalPowers", "RootUnchanged");

            void Compare(ResumableDiscardProgram values, CombatPredictionSimulator oracle, string stage)
            {
                var projection = adapter.Materialize(values);
                adapter.AssertValues(values, oracle); adapter.AssertValues(values, projection);
                foreach (Creature enemy in enemies)
                    AssertSnapshotEqual(CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy),
                        CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy), "CompactConditionalPowers", stage);
                if (!CompactPowerValues(((SimulatedCombatState)oracle.State.CombatState).EffectivePowers())
                    .SequenceEqual(CompactPowerValues(((SimulatedCombatState)projection.State.CombatState).EffectivePowers())))
                    throw new InvalidOperationException("Conditional Power ordering/ownership differs at " + stage);
                if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projection, adapter)))
                    throw new InvalidOperationException("ConditionalPowers history/source differs at " + stage + "\nExpected:\n"
                        + string.Join('\n', CompactHistory(oracle, adapter)) + "\nActual:\n" + string.Join('\n', CompactHistory(projection, adapter)));
                AssertCompactRngSet(oracle.Rng, projection.Rng);
                var expected = Release(evaluator.Evaluate(oracle));
                AssertCompactEvaluation(expected, Release(evaluator.Evaluate(projection)));
                reader.Read(values); uncached.Read(values);
                try
                {
                    AssertCompactEvaluation(expected, evaluator.Evaluate(reader));
                    AssertCompactEvaluation(expected, evaluator.Evaluate(uncached));
                }
                catch (InvalidOperationException error) { throw new InvalidOperationException("ConditionalPowers completed read at " + stage, error); }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            var lane = initial.Open();
            var reader = adapter.CreateReadView();
            var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
            foreach (var sample in cases)
            {
                initial.RestoreInto(lane);
                foreach (int card in sample.Path) Play(lane, card);
                if (!lane.State.Freeze().ContentEquals(sample.State.Open().State.Freeze()))
                    throw new InvalidOperationException("Independent conditional-powers worker differs.");
                reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
            foreach (var sample in cases.AsEnumerable().Reverse())
            {
                sample.State.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
        })));
        for (int index = 0; index < route.Length; index++)
        {
            int card = route[index];
            if (!nativeCards[card].TryManualPlay(Target(card)))
                throw new InvalidOperationException("Native conditional-powers card rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            for (int target = 0; target < enemies.Length; target++)
                AssertSnapshotEqual(nativeExpected[index][target], CaptureActual(combat, player, enemies[target]), "CompactConditionalPowers", "Native-" + index + "-" + target);
            if (!nativePowers[index].SequenceEqual(CompactPowerValues(combat.Creatures.SelectMany(c => c.Powers))))
                throw new InvalidOperationException("Native conditional-powers Power ownership/order differs.");
            var values = nativeStates[index].Open();
            for (int identity = 0; identity < adapter.CardCount; identity++)
            {
                CardModel actual = adapter.Original(identity);
                if (actual.HasBeenRemovedFromState != values.CardRemoved(identity)
                    || actual.EnergyCost.CostsX && actual.EnergyCost.CapturedXValue != values.CapturedX(identity))
                    throw new InvalidOperationException("Native removal or captured X metadata differs.");
            }
        }
        _completedChecks.Add("CompactConditionalPowers:14Branches:ConditionalSkillAttackPowerDraw:OrderedBulkPoisonWeak:RetainEthereal:PoisonApplierAndDeath:OriginalKeys:AllSnapshotProperties:RootRestore:Frozen8Workers:Native9Actions");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-card-conditional-powers.json"), JsonSerializer.Serialize(
                new { branches = evidence, riskSourceCount,
                    nativeRoute = route.Select(index => new { index, input[index].Id, input[index].Upgrade }), nativeSteps = route.Length,
                    nativePowers, productionBackendEnabled = false }, new JsonSerializerOptions { WriteIndented = true }));
        }

        void Play(ResumableDiscardProgram lane, int card)
        {
            lane.Begin(adapter.IndexOf(nativeCards[card]), TargetIndex(card) is var target && target >= 0 ? target + 1 : -1); lane.Run();
            if (!lane.Complete) throw new InvalidOperationException("ConditionalPowers compact command unexpectedly suspended.");
        }
    }
}
