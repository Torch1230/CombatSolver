using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactOstyCapAsync(CombatState combat, Player player)
    {
        foreach (int dealer in new[] { -1, 0, 253 })
        foreach (int identity in new[] { int.MinValue, -1, 0, 256, int.MaxValue })
        foreach (bool automatic in new[] { false, true })
        {
            var item = new ResumableDiscardProgram.Event(ResumableDiscardProgram.EventKind.Damage,
                identity, unchecked(-identity), automatic, identity, 255, dealer);
            if (ResumableDiscardProgram.Event.Decode(item.Data, item.Metadata) != item)
                throw new InvalidOperationException("Compact event identities or pet dealer overlap.");
        }
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).Where(p => p is not DieForYouPower).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        if (player.Osty == null) await OstyCmd.Summon(new BlockingPlayerChoiceContext(), player, 5, null);
        var osty = player.Osty!; var enemy = combat.Enemies.Single();
        List<(MoveStateSnapshot Native, MoveStateSnapshot Model, MoveStateSnapshot Values)> results = [];
        foreach (int maxHp in new[] { 999_999_997, 999_999_999 })
        {
            await ClearPlayerPilesAsync(player);
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BODYGUARD", Pile = "Hand" });
            await CreatureCmd.SetMaxHp(osty, maxHp); await CreatureCmd.SetCurrentHp(osty, 999_999_990);
            SetEnergy(player, 3); SetStars(player, 0);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var card = player.PlayerCombatState!.Hand.Cards.Single();
            var root = CombatRootSnapshot.Capture(combat).ForkSimulator();
            MoveStateSnapshot model, values;
            using (SimulationNotificationIsolation.Enter())
            {
                var adapter = new CompactCombatRoot(root, player).Adapter;
                adapter.Program.Begin(adapter.IndexOf(card)); adapter.Program.Run();
                var oracle = root.Fork();
                if (!oracle.ManualPlay(oracle.State.FindCard(card)!, null, out _)) throw new InvalidOperationException("Capped summon oracle rejected.");
                var projected = adapter.Materialize(adapter.Program);
                model = CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy);
                values = CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy);
            }
            if (!card.TryManualPlay(null)) throw new InvalidOperationException("Native capped summon rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            results.Add((CaptureActual(combat, player, enemy), model, values));
        }
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-osty-cap.json"),
                JsonSerializer.Serialize(results.Select(result => new { result.Native, result.Model, result.Values }), new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var result in results)
        {
            AssertSnapshotEqual(result.Native, result.Model, "OstyMaxHpCap", "Model");
            AssertSnapshotEqual(result.Native, result.Values, "OstyMaxHpCap", "Values");
        }
        _completedChecks.Add("OstyMaxHpCap:TwoNativeSummons:HealOnlyActualMaxHpGain:FullSnapshots");
    }

    private async Task PrepareCompactOstyAsync(CombatState combat, Player player, int mode, bool withTurnRelic = false)
    {
        var enemy = combat.Enemies.Single();
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).Where(p => p is not DieForYouPower).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        string[] ids = ["UNLEASH", "BODYGUARD", "BODYGUARD", "UNLEASH", "UNLEASH", "HAZE", "DEFEND_NECROBINDER", "BODYGUARD"];
        for (int index = 0; index < ids.Length; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = ids[index], Pile = "Hand", UpgradeLevels = index is 2 or 3 or 4 ? 1 : 0 });
        for (int index = 0; index < 5; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Draw" });
        if (player.Osty == null)
        {
            var absent = CombatRootSnapshot.Capture(combat).ForkSimulator();
            using (SimulationNotificationIsolation.Enter())
                if (CompactCombatRoot.TryCreate(absent, player, out _, out var reason) || !reason.Contains("captured pet identity", StringComparison.Ordinal))
                    throw new InvalidOperationException("First pet creation was not rejected before execution.");
            await OstyCmd.Summon(new BlockingPlayerChoiceContext(), player, 5, null);
        }
        var osty = player.Osty!;
        await CreatureCmd.SetMaxHp(player.Creature, 300); await CreatureCmd.SetCurrentHp(player.Creature, 300);
        await CreatureCmd.SetMaxHp(enemy, 300); await CreatureCmd.SetCurrentHp(enemy, 300);
        await CreatureCmd.SetMaxHp(osty, 7); await CreatureCmd.SetCurrentHp(osty, 5);
        await SetBlockAsync(osty, 2);
        await SetBlockAsync(player.Creature, mode == 1 ? 100 : 4); await SetBlockAsync(enemy, 3);
        await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), player.Creature, 11, player.Creature, null);
        await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), osty, 2, player.Creature, null);
        ConfigureMonsterMove(enemy, new UnattendedMonsterMoveCheck { MoveId = "CHARGE_MOVE" });
        SetEnergy(player, 20); SetStars(player, 0);
        if (mode == 0)
        {
            // Freeze a nonzero pet attack/hit history, with owner Strength deliberately different.
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "UNLEASH", Pile = "Hand" });
            if (!player.PlayerCombatState!.Hand.Cards.Last().TryManualPlay(enemy)) throw new InvalidOperationException("Native pet seed attack rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            await CreatureCmd.SetCurrentHp(enemy, 300); await SetBlockAsync(enemy, 3);
        }
        if (mode == 2)
            await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), [osty], 9, ValueProp.Unpowered | ValueProp.Unblockable, enemy, null, null);
        if (withTurnRelic)
        {
            await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = "BOUND_PHYLACTERY", AddWithoutObtainedEffects = true });
            await PowerCmd.Apply<ToolsOfTheTradePower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
            await PowerCmd.Apply<StratagemPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
        }
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers)) power.AmountOnTurnStart = 37;
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactOstyAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies.Single();
        var monster = enemy.Monster as MechaKnight ?? throw new InvalidOperationException("Osty fixture requires MechaKnight.");
        bool withTurnRelic = _request.ScenarioId == "COMPACT-OSTY-TURN-NATIVE";
        List<object> evidence = [];
        for (int mode = 0; mode < 3; mode++)
        {
            await PrepareCompactOstyAsync(combat, player, mode, withTurnRelic);
            var osty = player.Osty!;
            var cards = player.PlayerCombatState!.Hand.Cards.ToArray();
            var captured = CombatRootSnapshot.Capture(combat);
            var root = captured.ForkSimulator();
            var original = CaptureActual(combat, player, enemy);
            var display = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
            CompactMechaStep[] steps = withTurnRelic ? [new(-3), new(-3), new(-3)] : mode == 0
                ? [new(-1, 0), new(3), new(-1, 3), new(-1, 1), new(-1, 2), new(-1, 4), new(-1, 5), new(-3), new(-3)]
                : mode == 1 ? [new(0), new(-1, 0), new(-1, 2)] : [new(-1, 0), new(-1, 1), new(-1, 4)];
            CompactDiscardProjection adapter;
            ResumableDiscardProgram.Candidate initial;
            ForecastMove[] forecasts;
            List<(ResumableDiscardProgram.Candidate Values, SimulationSnapshot Evaluation, MoveStateSnapshot Snapshot, string[] Powers, PlanAction? Round)> samples = [];
            using (SimulationNotificationIsolation.Enter())
            {
                var compact = new CompactCombatRoot(root, player);
                adapter = compact.Adapter; initial = compact.Initial;
                var lane = adapter.Program; var reader = adapter.CreateReadView(); var uncached = adapter.CreateReadView(false);
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                var driver = new CombatBeamSolver(captured, display, damage, policy);
                var metadata = (SimulatedCombatState)root.Fork().State.CombatState;
                forecasts = CompactDiscardProjection.MechaMoveIds.Select(id => { metadata.ForceMonsterMove(enemy, id); return metadata.CurrentMonsterMove(enemy); }).ToArray();
                var oracle = root.Fork();
                for (int index = 0; index < steps.Length; index++)
                {
                    var step = steps[index]; PlanAction? round = null;
                    if (step.Move == -3)
                    {
                        var parent = evaluator.Evaluate(oracle, lane.PlayerTurn);
                        try
                        {
                            var branches = (IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>)InvokeForcedTerminalMethod(driver,
                                "BuildEndTurnBranches", [ForcedTerminalAnnotationNode(parent, null, null), Array.Empty<PlanCardChoice>(), true])!;
                            using var choices = branches.GetEnumerator();
                            if (!choices.MoveNext()) throw new InvalidOperationException("Osty round has no oracle branch.");
                            round = choices.Current.Action;
                            oracle = choices.Current.Snapshot.Simulator.Fork();
                            choices.Current.Snapshot.ReleaseSimulator();
                        }
                        finally { parent.ReleaseSimulator(); }
                        Execute(lane, step, round);
                    }
                    else
                    {
                        Execute(lane, step, null);
                        if (step.Move >= 0)
                            MonsterMoveSemantics.ApplyForecastMove(oracle, (SimulatedCombatState)oracle.State.CombatState,
                                forecasts[step.Move], player.Creature, new HashSet<uint>());
                        else if (!oracle.ManualPlay(oracle.State.FindCard(cards[step.Card])!, cards[step.Card].Type == CardType.Attack ? enemy : null, out _))
                            throw new InvalidOperationException("Osty oracle card rejected.");
                        oracle.CheckWinCondition(lane.PlayerTurn);
                    }
                    var projected = adapter.Materialize(lane);
                    var snapshot = CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy);
                    var projectedSnapshot = CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy);
                    evidence.Add(new { mode, step = index, stage = "projection", expected = snapshot, actual = projectedSnapshot });
                    WriteEvidence();
                    adapter.AssertValues(lane, oracle); adapter.AssertValues(lane, projected);
                    AssertSnapshotEqual(snapshot, projectedSnapshot, "CompactOsty", $"Mode{mode}/Projection{index}");
                    if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projected, adapter)))
                        throw new InvalidOperationException("Osty history differs:\n" + string.Join('\n', CompactHistory(oracle, adapter)) + "\nProjected:\n" + string.Join('\n', CompactHistory(projected, adapter)));
                    var powers = CompactPowerValues(((SimulatedCombatState)oracle.State.CombatState).EffectivePowers());
                    var projectedPowers = CompactPowerValues(((SimulatedCombatState)projected.State.CombatState).EffectivePowers());
                    if (!powers.SequenceEqual(projectedPowers))
                        throw new InvalidOperationException($"Osty full Power lifecycle fields differ at mode={mode}, step={index}:\n"
                            + string.Join('\n', powers) + "\nProjected:\n" + string.Join('\n', projectedPowers));
                    AssertCompactRngSet(oracle.Rng, projected.Rng);
                    var expected = Release(evaluator.Evaluate(oracle, lane.PlayerTurn));
                    AssertCompactEvaluation(expected, Release(evaluator.Evaluate(projected, lane.PlayerTurn)), $"Osty{mode}/Projection{index}");
                    reader.Read(lane); uncached.Read(lane);
                    AssertCompactEvaluation(expected, evaluator.Evaluate(reader, lane.PlayerTurn), $"Osty{mode}/Reader{index}");
                    AssertCompactEvaluation(expected, evaluator.Evaluate(uncached, lane.PlayerTurn), $"Osty{mode}/Uncached{index}");
                    samples.Add((lane.Freeze(), expected, snapshot, powers, round));
                }
                foreach (var sample in samples.AsEnumerable().Reverse())
                {
                    sample.Values.RestoreInto(lane); reader.Read(lane);
                    AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), "Osty/Reverse");
                }
                initial.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(Release(evaluator.Evaluate(root, lane.PlayerTurn)), evaluator.Evaluate(reader, lane.PlayerTurn), "Osty/RootRestore");
                // Roll back both the death-retained identity and the max-HP map's absent/zero shape.
                var mark = lane.State.Mark(); Execute(lane, steps[0], samples[0].Round); lane.State.Rollback(mark);
                if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze())) throw new InvalidOperationException("Pet rollback leaked values.");
                if (withTurnRelic)
                {
                    int pending = AssertCompactReplayBoundaries(captured, display, damage, policy, player, samples.Select(sample => sample.Round!).ToArray());
                    if (pending == 0) throw new InvalidOperationException("Pet turn fixture omitted its required choices.");
                    _completedChecks.Add($"CompactOstyTurns:Mode{mode}:OmittedChoices{pending}:FullPendingEvaluation");
                }
                AssertSnapshotEqual(original, CaptureActual(combat, player, enemy), "CompactOsty", "ActualUnchanged");
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var lane = initial.Open(); var reader = adapter.CreateReadView();
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                for (int index = 0; index < steps.Length; index++)
                {
                    Execute(lane, steps[index], samples[index].Round);
                    if (!lane.State.Freeze().ContentEquals(samples[index].Values.Open().State.Freeze())) throw new InvalidOperationException("Pet worker values differ.");
                    reader.Read(lane); AssertCompactEvaluation(samples[index].Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), "Osty/Worker");
                }
            })));
            int nativeChoices = 0;
            for (int index = 0; index < steps.Length; index++)
            {
                var step = steps[index];
                if (step.Move == -3)
                {
                    var selector = new PlannedCardSelector(samples[index].Round!.TurnStartChoices ?? []);
                    selector.CaptureBefore(player);
                    var observer = new CompactResourceChoiceObserver(selector, () =>
                    {
                        nativeChoices++;
                        if (!withTurnRelic) return;
                        var expectedPet = samples[index].Values.Open().Creature(adapter.Program.PetIndex);
                        if (new CreatureVitals(osty.CurrentHp, osty.MaxHp, osty.Block) != expectedPet)
                            throw new InvalidOperationException("Native turn summon must finish before the first hand/Tools choice.");
                    });
                    using (CardSelectCmd.PushSelector(observer)) await AdvanceMercuryActualTurnAsync(combat, player, false);
                    selector.ReconcileImplicitChoices(player); selector.AssertConsumed();
                }
                else if (step.Move >= 0)
                    await ((MoveState)monster.MoveStateMachine!.States[CompactDiscardProjection.MechaMoveIds[step.Move]]).PerformMove([player.Creature]);
                else if (!cards[step.Card].TryManualPlay(cards[step.Card].Type == CardType.Attack ? enemy : null))
                    throw new InvalidOperationException("Native Osty route rejected card play.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                var actual = CaptureActual(combat, player, enemy);
                var actualPowers = CompactPowerValues(combat.Creatures.SelectMany(c => c.Powers));
                var expectedPet = samples[index].Values.Open().Creature(adapter.Program.PetIndex);
                var actualPet = new CreatureVitals(osty.CurrentHp, osty.MaxHp, osty.Block);
                evidence.Add(new { mode, step = index, stage = "native", expected = samples[index].Snapshot, actual, samples[index].Powers, actualPowers, expectedPet, actualPet });
                WriteEvidence();
                AssertSnapshotEqual(samples[index].Snapshot, actual, "CompactOsty", $"Mode{mode}/Native{index}");
                if (!samples[index].Powers.SequenceEqual(actualPowers) || expectedPet != actualPet)
                    throw new InvalidOperationException("Native pet Power lifecycle or vitals differ.");
            }
            using (SimulationNotificationIsolation.Enter())
            {
                var lane = initial.Open(); var reader = adapter.CreateReadView();
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                for (int index = 0; index < steps.Length; index++) Execute(lane, steps[index], samples[index].Round);
                reader.Read(lane); AssertCompactEvaluation(samples[^1].Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), "Osty/AfterNative");
                AssertSnapshotEqual(original, CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy), "CompactOsty", "FrozenRootAfterNative");
            }
            if (withTurnRelic && nativeChoices == 0) throw new InvalidOperationException("Pet turn fixture missed native choice observation.");
            _completedChecks.Add($"CompactOsty:TurnRelic{withTurnRelic}:Mode{mode}:NativeChoices{nativeChoices}:{steps.Length}NativeSteps:FullSnapshotsKeysHistoryRngPowerMetadata:ReverseRollback:EightWorkers:FrozenAfterNative");

            void Execute(ResumableDiscardProgram lane, CompactMechaStep step, PlanAction? round)
            {
                if (step.Move == -3) new CompactPlanReplay(adapter).Execute(lane, round!);
                else if (step.Move >= 0) lane.ExecuteMonsterMove(1, step.Move);
                else { lane.Begin(adapter.IndexOf(cards[step.Card]), cards[step.Card].Type == CardType.Attack ? 1 : -1); lane.Run(); }
                if (!lane.Complete) throw new InvalidOperationException("Osty fixture unexpectedly suspended.");
                lane.CheckWinCondition();
            }
        }
        void WriteEvidence()
        {
            if (string.IsNullOrWhiteSpace(_request.EvidenceDirectory)) return;
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-osty.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
