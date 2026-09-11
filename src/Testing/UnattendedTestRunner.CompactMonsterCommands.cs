using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // 0..3 captured Mecha commands, 4 hand-end, -1 root/generated card play.
    private readonly record struct CompactMechaStep(int Move, int Card = -1, bool Generated = false);

    private async Task AssertCompactMonsterCommandsAsync(CombatState combat, Player player)
    {
        List<object> evidence = [];
        var enemy = combat.Enemies.Single();
        if (enemy.Monster is not MechaKnight monster) throw new InvalidOperationException("Mecha command fixture requires MechaKnight.");
        for (int mode = 0; mode < 3; mode++)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
            ClearRunDeck((RunState)combat.RunState, player);
            await ClearPlayerPilesAsync(player);
            string[] hand = mode == 0 ? ["CLOAK_AND_DAGGER", "BACKFLIP", "STRIKE_SILENT", .. Enumerable.Repeat("DEFEND_SILENT", 7)] : ["DEFEND_SILENT"];
            foreach (string id in hand) await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            await CreatureCmd.SetMaxHp(player.Creature, 300);
            await CreatureCmd.SetCurrentHp(player.Creature, mode == 2 ? 1 : 300);
            await SetBlockAsync(player.Creature, mode == 0 ? 3 : mode == 1 ? 100 : 0);
            await SetBlockAsync(enemy, 0);
            if (mode == 0)
            {
                await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), enemy, 2, player.Creature, null);
                await PowerCmd.Apply<DexterityPower>(new BlockingPlayerChoiceContext(), enemy, 3, player.Creature, null);
                await PowerCmd.Apply<WeakPower>(new BlockingPlayerChoiceContext(), enemy, 1, player.Creature, null);
                await PowerCmd.Apply<FrailPower>(new BlockingPlayerChoiceContext(), enemy, 1, player.Creature, null);
                await PowerCmd.Apply<VulnerablePower>(new BlockingPlayerChoiceContext(), player.Creature, 1, enemy, null);
            }
            await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), enemy, 3, enemy, null);
            await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
            SetEnergy(player, 20); SetStars(player, 0);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            CardModel[] cards = player.PlayerCombatState!.Hand.Cards.ToArray();
            int generatedBefore = CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Count();
            var captured = CombatRootSnapshot.Capture(combat);
            var root = captured.ForkSimulator();
            var rootSnapshot = CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy);
            var display = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
            CompactMechaStep[] native = mode == 0
                ? [new(-1, 0), new(-1, 0, true), new(2), new(1), new(-1, 1), new(3), new(0), new(4)]
                : mode == 1 ? [new(2), new(1), new(2), new(3), new(0), new(4)] : [new(1)];
            CompactMechaStep[][] routes = mode == 2 ? [[new(1)], [new(0)], [new(-1, 0), new(1)]]
                : [[new(0)], [new(1)], [new(2)], [new(3)], native];
            CompactDiscardProjection adapter;
            ResumableDiscardProgram.Candidate initial;
            ForecastMove[] forecasts;
            List<(CompactMechaStep[] Route, ResumableDiscardProgram.Candidate State, SimulationSnapshot Evaluation)> samples = [];
            List<(MoveStateSnapshot State, int[][] Piles, StateFingerprint[] Generated)> expected = [];
            using (SimulationNotificationIsolation.Enter())
            {
                adapter = new(root, player, includeAttacks: true, includeHandEnd: true, includeMechaMoves: true);
                var metadata = (SimulatedCombatState)root.Fork().State.CombatState;
                forecasts = CompactDiscardProjection.MechaMoveIds.Select(id =>
                {
                    metadata.ForceMonsterMove(enemy, id);
                    return metadata.CurrentMonsterMove(enemy);
                }).ToArray();
                var lane = adapter.Program;
                initial = lane.Freeze();
                var reader = adapter.CreateReadView(); var uncached = adapter.CreateReadView(false);
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                foreach (var route in routes)
                {
                    var mark = lane.State.Mark();
                    var oracle = root.Fork();
                    foreach (var step in route)
                    {
                        Execute(lane, oracle, step);
                        var projection = adapter.Materialize(lane);
                        adapter.AssertValues(lane, oracle); adapter.AssertValues(lane, projection);
                        var oracleState = CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy);
                        AssertSnapshotEqual(oracleState, CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy),
                            "CompactMonsterCommands", $"Mode{mode}-Projection");
                        if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projection, adapter)))
                            throw new InvalidOperationException("Monster command source history differs:\n" + string.Join('\n', CompactHistory(oracle, adapter))
                                + "\nActual:\n" + string.Join('\n', CompactHistory(projection, adapter)));
                        if (!CompactPowerValues(((SimulatedCombatState)oracle.State.CombatState).EffectivePowers())
                            .SequenceEqual(CompactPowerValues(((SimulatedCombatState)projection.State.CombatState).EffectivePowers())))
                            throw new InvalidOperationException("Monster command full Power fields differ.");
                        AssertCompactRngSet(oracle.Rng, projection.Rng);
                        var evaluation = Release(evaluator.Evaluate(oracle));
                        AssertCompactEvaluation(evaluation, Release(evaluator.Evaluate(projection)));
                        reader.Read(lane); uncached.Read(lane);
                        AssertCompactEvaluation(evaluation, evaluator.Evaluate(reader));
                        AssertCompactEvaluation(evaluation, evaluator.Evaluate(uncached));
                        if (ReferenceEquals(route, native) || mode == 2 && ReferenceEquals(route, routes[0]))
                        {
                            var identities = adapter.CaptureCardIdentities(oracle);
                            expected.Add((oracleState, Enumerable.Range(0, 5).Select(pile => lane.Cards((ResumableDiscardProgram.Pile)pile)).ToArray(),
                                identities.Where(pair => pair.Value >= adapter.CardCount).OrderBy(pair => pair.Value)
                                    .Select(pair => CombatBeamSolver.CaptureCardStateFingerprintForTesting(oracle.State.FindCard(pair.Key)!)).ToArray()));
                        }
                    }
                    reader.Read(lane);
                    samples.Add((route, lane.Freeze(), evaluator.Evaluate(reader)));
                    lane.State.Rollback(mark);
                    if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze()))
                        throw new InvalidOperationException("Monster commands leaked state through rollback.");
                }
                foreach (var sample in samples.AsEnumerable().Reverse())
                {
                    sample.State.RestoreInto(lane); reader.Read(lane);
                    AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
                }
                initial.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(Release(evaluator.Evaluate(root)), evaluator.Evaluate(reader));
                AssertSnapshotEqual(rootSnapshot, CaptureActual(combat, player, enemy), "CompactMonsterCommands", $"Mode{mode}-RootUnchanged");
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var lane = initial.Open(); var reader = adapter.CreateReadView();
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                foreach (var sample in samples.AsEnumerable().Reverse())
                {
                    initial.RestoreInto(lane);
                    foreach (var step in sample.Route) Execute(lane, null, step);
                    if (!lane.State.Freeze().ContentEquals(sample.State.Open().State.Freeze()))
                        throw new InvalidOperationException("Monster command worker differs from frozen state.");
                    reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
                }
            })));
            for (int index = 0; index < native.Length; index++)
            {
                var step = native[index];
                if (step.Move == -1)
                {
                    CardModel card = step.Generated ? Generated()[step.Card] : cards[step.Card];
                    if (!card.TryManualPlay(card.Type == CardType.Attack ? enemy : null))
                        throw new InvalidOperationException("Native mixed Mecha route rejected card play.");
                }
                else if (step.Move == 4)
                    await CombatManager.Instance.DoTurnEnd(CombatManager.Instance._turnState!, player, new BlockingPlayerChoiceContext());
                else
                    // Invoke the native command body. AI selection, phase hooks and the
                    // MonsterPerformedMove history entry are a separate round boundary.
                    await ((MoveState)monster.MoveStateMachine!.States[CompactDiscardProjection.MechaMoveIds[step.Move]]).PerformMove([player.Creature]);
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                var actual = CaptureActual(combat, player, enemy);
                var generated = Generated();
                int Identity(CardModel card) => adapter.IndexOf(card) is >= 0 and var id ? id : adapter.CardCount + Array.IndexOf(generated, card);
                CardPile[] piles = [player.PlayerCombatState.Hand, player.PlayerCombatState.DrawPile, player.PlayerCombatState.DiscardPile,
                    player.PlayerCombatState.PlayPile, player.PlayerCombatState.ExhaustPile];
                int[][] actualPiles = piles.Select(pile => pile.Cards.Select(Identity).ToArray()).ToArray();
                var actualGenerated = generated.Select(card => CombatBeamSolver.CaptureCardStateFingerprintForTesting(PredictedCard.FromGenerated(card))).ToArray();
                evidence.Add(new { mode, step = index, command = step, branches = routes.Length, expected = expected[index].State, actual,
                    expectedPiles = expected[index].Piles, actualPiles, generated = generated.Select(card => card.Id.Entry).ToArray() });
                if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
                {
                    Directory.CreateDirectory(_request.EvidenceDirectory);
                    File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-monster-commands.json"),
                        JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
                }
                AssertSnapshotEqual(expected[index].State, actual, "CompactMonsterCommands", $"Mode{mode}-Native{index}");
                if (expected[index].Piles.Where((pile, pileIndex) => !pile.SequenceEqual(actualPiles[pileIndex])).Any()
                    || !expected[index].Generated.SequenceEqual(actualGenerated))
                    throw new InvalidOperationException("Native monster generation identity, metadata or ordered piles differ.");
            }
            using (SimulationNotificationIsolation.Enter())
            {
                var lane = initial.Open();
                foreach (var step in native) Execute(lane, null, step);
                var projection = adapter.Materialize(lane);
                AssertSnapshotEqual(expected[^1].State, CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy),
                    "CompactMonsterCommands", $"Mode{mode}-AfterNative");
                AssertSnapshotEqual(rootSnapshot, CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy),
                    "CompactMonsterCommands", $"Mode{mode}-FrozenRootAfterNative");
            }
            void Execute(ResumableDiscardProgram lane, CombatPredictionSimulator? oracle, CompactMechaStep step)
            {
                if (step.Move == -1)
                {
                    int identity = step.Generated ? adapter.CardCount + step.Card : adapter.IndexOf(cards[step.Card]);
                    lane.Begin(identity, step.Generated || cards[step.Card].Type == CardType.Attack ? 1 : -1); lane.Run();
                    if (oracle != null)
                    {
                        CardModel original = step.Generated ? adapter.CaptureCardIdentities(oracle).Single(pair => pair.Value == identity).Key : cards[step.Card];
                        PredictedCard card = oracle.State.FindCard(original)!;
                        if (!oracle.ManualPlay(card, card.Preview.Type == CardType.Attack ? enemy : null, out _))
                            throw new InvalidOperationException("Mixed Mecha oracle rejected card play.");
                    }
                }
                else if (step.Move == 4)
                {
                    lane.EndHandEffects(ResumableDiscardProgram.HandEndStaging.Together);
                    if (oracle != null && !oracle.SimulateEndPlayerTurnAfterOrbPassives(1, true))
                        throw new InvalidOperationException("Mixed Mecha hand end suspended.");
                }
                else
                {
                    lane.ExecuteMonsterMove(1, step.Move);
                    if (oracle != null)
                        MonsterMoveSemantics.ApplyForecastMove(oracle, (SimulatedCombatState)oracle.State.CombatState,
                            forecasts[step.Move], player.Creature, new HashSet<uint>());
                }
                if (!lane.Complete || oracle?.HasPendingChoice == true) throw new InvalidOperationException("Mecha command suspended.");
                lane.CheckWinCondition(); oracle?.CheckWinCondition(1);
            }
            CardModel[] Generated() => CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Skip(generatedBefore).Select(entry => entry.Card).ToArray();
        }
        _completedChecks.Add("CompactMonsterCommands:Native3Roots13Branches15Actions:FourMechaBodies:NoCreatorBurn:MixedShiv:SpillShuffleHandEnd:DamageBlockStrengthApplier:Fatal:FullStateAndPiles:AllHistory:AllRng:AllSnapshotProperties:Frozen8Workers");
    }
}
