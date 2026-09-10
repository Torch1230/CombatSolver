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
    private async Task AssertCompactCardLifecycleAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        (string Id, int Upgrade)[] input = [("FOOTWORK", 1), ("FOOTWORK", 0), ("DEFEND_NECROBINDER", 0),
            ("FINESSE", 1), ("MALAISE", 1), ("MALAISE", 0), ("SUPPRESS", 1), ("ULTIMATE_DEFEND", 1),
            ("STRIKE_NECROBINDER", 0), ("SURVIVOR", 0)];
        foreach (var card in input)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = card.Id, UpgradeLevels = card.Upgrade, Pile = "Hand" });
        foreach (string card in new[] { "BACKFLIP", "PREPARED", "ACROBATICS", "STRIKE_SILENT", "DEFEND_SILENT" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = card, Pile = "Draw" });
        CardModel[] nativeCards = player.PlayerCombatState!.Hand.Cards.ToArray();
        Creature[] enemies = combat.Enemies.ToArray();
        await PowerCmd.Apply<FrailPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
        await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), enemies[0], 1, enemies[0], null);
        SetEnergy(player, 8); SetStars(player, 0);
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
        List<MoveStateSnapshot> nativeExpected = [];
        List<string[]> nativePowers = [];
        List<ResumableDiscardProgram.Candidate> nativeStates = [];
        List<object> evidence = [];
        int riskSourceCount;
        int[] route = [0, 3, 1, 2, 8, 7, 4, 5, 6];
        static bool Targeted(int index) => index is 4 or 5 or 6 or 8;
        using (SimulationNotificationIsolation.Enter())
        {
            adapter = new(simulator, player, includeAttacks: true);
            var lane = adapter.Program;
            initial = lane.Freeze();
            var reader = adapter.CreateReadView();
            riskSourceCount = reader.RiskSourceCount;
            if (riskSourceCount <= 4) throw new InvalidOperationException("Lifecycle fixture did not cross the former risk-source capacity.");
            var uncached = adapter.CreateReadView(false);
            var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
            MoveStateSnapshot original = CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, enemies[0]);
            int[][] paths = [[0, 3], [3, 0], [0, 1, 3], [1, 0, 3], [4], [5], [4, 5], [5, 4], [2], [6], [7], [8]];
            foreach (int[] path in paths)
            {
                initial.RestoreInto(lane);
                var mark = lane.State.Mark();
                var oracle = simulator.Fork();
                foreach (int card in path)
                {
                    Play(lane, card);
                    if (!oracle.ManualPlay(oracle.State.FindCard(nativeCards[card])!, Targeted(card) ? enemies[0] : null, out _))
                        throw new InvalidOperationException("Lifecycle oracle unexpectedly suspended.");
                    Compare(lane, oracle, "branch-" + string.Join('-', path) + "-" + card);
                }
                reader.Read(lane);
                var evaluation = evaluator.Evaluate(reader);
                cases.Add((path, lane.Freeze(), evaluation));
                evidence.Add(new { path, evaluation.PlayerBlock, evaluation.StateKey,
                    removed = lane.Count(ResumableDiscardProgram.Pile.Removed), exhausted = lane.Count(ResumableDiscardProgram.Pile.Exhaust), lane.Energy });
                lane.State.Rollback(mark);
                if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze()))
                    throw new InvalidOperationException("Lifecycle rollback retained removal, exhaust or captured X.");
            }
            if (cases[0].Evaluation.PlayerBlock == cases[1].Evaluation.PlayerBlock)
                throw new InvalidOperationException("Footwork/Finesse order did not exercise current Dexterity.");
            initial.RestoreInto(lane);
            var continued = simulator.Fork();
            foreach (int card in route)
            {
                Play(lane, card);
                if (!continued.ManualPlay(continued.State.FindCard(nativeCards[card])!, Targeted(card) ? enemies[0] : null, out _))
                    throw new InvalidOperationException("Native lifecycle oracle unexpectedly suspended.");
                Compare(lane, continued, "native-prefix-" + nativeExpected.Count);
                nativeExpected.Add(CaptureSimulated(continued, (SimulatedCombatState)continued.State.CombatState, player, enemies[0]));
                nativePowers.Add(CompactPowerValues(((SimulatedCombatState)continued.State.CombatState).EffectivePowers()));
                nativeStates.Add(lane.Freeze());
            }
            if (lane.Count(ResumableDiscardProgram.Pile.Removed) != 2 || lane.Count(ResumableDiscardProgram.Pile.Exhaust) != 2
                || lane.CapturedX(adapter.IndexOf(nativeCards[4])) != 3 || lane.CapturedX(adapter.IndexOf(nativeCards[5])) != 0)
                throw new InvalidOperationException("Lifecycle route did not cover power removal and paid/zero X exhaust.");
            initial.RestoreInto(lane); reader.Read(lane);
            AssertCompactEvaluation(Release(evaluator.Evaluate(simulator)), evaluator.Evaluate(reader));
            foreach (var sample in cases.AsEnumerable().Reverse())
            {
                sample.State.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
            AssertSnapshotEqual(original, CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState,
                player, enemies[0]), "CompactCardLifecycle", "RootUnchanged");

            void Compare(ResumableDiscardProgram values, CombatPredictionSimulator oracle, string stage)
            {
                var projection = adapter.Materialize(values);
                adapter.AssertValues(values, oracle); adapter.AssertValues(values, projection);
                AssertSnapshotEqual(CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemies[0]),
                    CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemies[0]), "CompactCardLifecycle", stage);
                if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projection, adapter)))
                    throw new InvalidOperationException("Lifecycle history/source differs at " + stage);
                AssertCompactRngSet(oracle.Rng, projection.Rng);
                var expected = Release(evaluator.Evaluate(oracle));
                AssertCompactEvaluation(expected, Release(evaluator.Evaluate(projection)));
                reader.Read(values); uncached.Read(values);
                try
                {
                    AssertCompactEvaluation(expected, evaluator.Evaluate(reader));
                    AssertCompactEvaluation(expected, evaluator.Evaluate(uncached));
                }
                catch (InvalidOperationException error) { throw new InvalidOperationException("Lifecycle completed read at " + stage, error); }
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
                    throw new InvalidOperationException("Independent lifecycle worker differs.");
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
            if (!nativeCards[card].TryManualPlay(Targeted(card) ? enemies[0] : null))
                throw new InvalidOperationException("Native lifecycle card rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(nativeExpected[index], CaptureActual(combat, player, enemies[0]), "CompactCardLifecycle", "Native-" + index);
            if (!nativePowers[index].SequenceEqual(CompactPowerValues(combat.Creatures.SelectMany(c => c.Powers))))
                throw new InvalidOperationException("Native lifecycle Power ownership/order differs.");
            var values = nativeStates[index].Open();
            for (int identity = 0; identity < adapter.CardCount; identity++)
            {
                CardModel actual = adapter.Original(identity);
                if (actual.HasBeenRemovedFromState != values.CardRemoved(identity)
                    || actual.EnergyCost.CostsX && actual.EnergyCost.CapturedXValue != values.CapturedX(identity))
                    throw new InvalidOperationException("Native removal or captured X metadata differs.");
            }
        }
        _completedChecks.Add("CompactCardLifecycle:12Branches:FootworkFinesseOrder:RemovedPowers:XCostAndZeroX:ExhaustHistory:OriginalKeys:AllSnapshotProperties:RootRestore:Frozen8Workers:Native9Actions");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-card-lifecycle.json"), JsonSerializer.Serialize(
                new { branches = evidence, riskSourceCount,
                    nativeRoute = route.Select(index => new { index, input[index].Id, input[index].Upgrade }), nativeSteps = route.Length,
                    nativePowers, productionBackendEnabled = false }, new JsonSerializerOptions { WriteIndented = true }));
        }

        void Play(ResumableDiscardProgram lane, int card)
        {
            lane.Begin(adapter.IndexOf(nativeCards[card]), Targeted(card) ? adapter.CreatureIndex(enemies[0]) : -1); lane.Run();
            if (!lane.Complete) throw new InvalidOperationException("Lifecycle compact command unexpectedly suspended.");
        }
    }
}
