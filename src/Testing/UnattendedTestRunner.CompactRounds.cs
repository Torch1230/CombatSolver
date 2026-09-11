using System.Reflection;
using System.Text.Json;
using HarmonyLib;
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
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactRoundsAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies.Single();
        List<object> evidence = [];
        for (int mode = 0; mode < 3; mode++)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
            ClearRunDeck((RunState)combat.RunState, player);
            await ClearPlayerPilesAsync(player);
            foreach (string relic in new[] { "RING_OF_THE_SNAKE", "TOUGH_BANDAGES", "THE_ABACUS" })
                await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = relic });
            await CreatureCmd.SetMaxHp(player.Creature, 300);
            await CreatureCmd.SetCurrentHp(player.Creature, 300);
            await CreatureCmd.SetMaxHp(enemy, 300);
            await CreatureCmd.SetCurrentHp(enemy, mode == 2 ? 3 : 300);
            await SetBlockAsync(player.Creature, 7);
            await SetBlockAsync(enemy, 9);
            ConfigureMonsterMove(enemy, new UnattendedMonsterMoveCheck { MoveId = mode == 0 ? "CHARGE_MOVE" : "FLAMETHROWER_MOVE" });
            string[] hand = mode == 0 ? ["CLOAK_AND_DAGGER", "DEFY", "BURN", "SNAKEBITE", "PREPARED", "DEFEND_SILENT", "NEUTRALIZE"]
                : ["DEFEND_SILENT", "SNAKEBITE"];
            foreach (string id in hand) await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            if (mode == 0) FindActualHandCard(player, "PREPARED", 0).GiveSingleTurnSly();
            if (mode < 2)
            {
                foreach (string id in mode == 0 ? new[] { "BACKFLIP", "DEADLY_POISON", "DEFEND_SILENT" }
                    : new[] { "PREPARED", "DEFEND_SILENT", "DEFEND_SILENT", "DEFEND_SILENT", "DEFEND_SILENT", "DEFEND_SILENT", "DEFEND_SILENT" })
                    await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Draw" });
                if (mode == 1) player.PlayerCombatState!.DrawPile.Cards.OfType<Prepared>().Single().AddKeyword(CardKeyword.Sly);
                for (int index = 0; index < 5; index++)
                    await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_SILENT", Pile = "Discard" });
                await PowerCmd.Apply<ToolsOfTheTradePower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
                if (mode == 0) await PowerCmd.Apply<StratagemPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
                await PowerCmd.Apply<BlockNextTurnPower>(new BlockingPlayerChoiceContext(), player.Creature, 4, enemy, null);
                await PowerCmd.Apply<WeakPower>(new BlockingPlayerChoiceContext(), enemy, 1, player.Creature, null);
                await PowerCmd.Apply<VulnerablePower>(new BlockingPlayerChoiceContext(), player.Creature, 2, enemy, null);
                await PowerCmd.Apply<PiercingWailPower>(new BlockingPlayerChoiceContext(), enemy, 3, player.Creature, null);
            }
            await PowerCmd.Apply<PoisonPower>(new BlockingPlayerChoiceContext(), enemy, 3, player.Creature, null);
            foreach (var power in combat.Creatures.SelectMany(c => c.Powers)) power.AmountOnTurnStart = 37;
            SetEnergy(player, 20); SetStars(player, 0);
            // Seed native nonzero current-turn counters before capturing the compact root.
            if (mode == 0)
            {
                if (!FindActualHandCard(player, "DEFEND_SILENT", 0).TryManualPlay(null)) throw new InvalidOperationException("Round seed skill rejected.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                if (!FindActualHandCard(player, "NEUTRALIZE", 0).TryManualPlay(enemy)) throw new InvalidOperationException("Round seed attack rejected.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            if (mode == 0) await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), enemy, 2, enemy, null);
            var captured = CombatRootSnapshot.Capture(combat);
            var root = captured.ForkSimulator();
            int rootStatusDraws = CombatManager.Instance.History.Entries.OfType<CardDrawnEntry>()
                .Count(entry => entry.HappenedThisTurn(combat) && entry.Actor.Player == player && entry.Card.Type == CardType.Status);
            var rootSnapshot = CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy);
            var display = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
            var nativeGeneratedStart = CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Count();
            int nativeMovesStart = CombatManager.Instance.History.Entries.OfType<MonsterPerformedMoveEntry>().Count();
            CompactDiscardProjection adapter;
            ResumableDiscardProgram.Candidate initial;
            List<(PlanAction[] Actions, ResumableDiscardProgram.Candidate Candidate, SimulationSnapshot Evaluation)> samples = [];
            List<(PlanAction Action, MoveStateSnapshot Snapshot, string[] Powers, int[][] Piles, StateFingerprint[] Cards)> native = [];
            PlanAction[] nativePath = [];
            using (SimulationNotificationIsolation.Enter())
            {
                adapter = new(root, player, includeAttacks: true, includeHandEnd: true, includeMechaMoves: true,
                    includePowerPhases: true, includeMechaAi: true, includeRounds: true);
                var lane = adapter.Program;
                initial = lane.Freeze();
                var reader = adapter.CreateReadView(); var uncached = adapter.CreateReadView(false);
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                var driver = new CombatBeamSolver(captured, display, damage, policy);
                var oracle = root.Fork();
                var metadata = NewMetadata();
                if (mode == 0)
                {
                    CardModel cloak = FindActualHandCard(player, "CLOAK_AND_DAGGER", 0);
                    PlanAction play = new(PlanActionKind.PlayCard, lane.PlayerTurn, cloak.Id.Entry);
                    lane.Begin(adapter.IndexOf(cloak)); lane.Run(); lane.CheckWinCondition();
                    if (!oracle.ManualPlay(oracle.State.FindCard(cloak)!, null, out _)) throw new InvalidOperationException("Round prefix card suspended.");
                    nativePath = [play];
                    Check(lane, oracle, nativePath);
                    AddNative(play, lane, oracle);
                }
                int rounds = mode == 0 ? 3 : mode == 1 ? 2 : 1;
                for (int round = 0; round < rounds; round++)
                {
                    var parent = InvokeRoundSnapshot(driver, oracle);
                    var before = lane.Freeze();
                    var branches = (IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>)InvokeForcedTerminalMethod(driver,
                        "BuildEndTurnBranches", [ForcedTerminalAnnotationNode(parent, null, null), Array.Empty<PlanCardChoice>(), true])!;
                    var selected = branches.Take(mode == 2 ? 1 : 4).ToArray();
                    if (selected.Length == 0) throw new InvalidOperationException("Round oracle has no branch.");
                    // Prefer the nested permanent-Sly branch for the native path.
                    int chosen = mode == 1 && round == 0 ? Array.FindIndex(selected, item => item.Action.TurnStartChoices?.Count > 1) : 0;
                    if (chosen < 0) throw new InvalidOperationException("Round fixture did not expose nested Sly choices.");
                    ResumableDiscardProgram.Candidate? next = null;
                    for (int branch = 0; branch < selected.Length; branch++)
                    {
                        before.RestoreInto(lane);
                        Replay(lane, selected[branch].Action, metadata);
                        PlanAction[] path = [.. nativePath, selected[branch].Action];
                        Check(lane, selected[branch].Snapshot.Simulator, path);
                        if (branch == chosen)
                        {
                            next = lane.Freeze();
                            AddNative(selected[branch].Action, lane, selected[branch].Snapshot.Simulator);
                        }
                    }
                    nativePath = [.. nativePath, selected[chosen].Action];
                    oracle = selected[chosen].Snapshot.Simulator.Fork();
                    next!.RestoreInto(lane);
                    foreach (var item in selected) item.Snapshot.ReleaseSimulator();
                    parent.ReleaseSimulator();
                }
                foreach (var sample in samples.AsEnumerable().Reverse())
                {
                    sample.Candidate.RestoreInto(lane); reader.Read(lane);
                    AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn));
                }
                initial.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(Release(evaluator.Evaluate(root, lane.PlayerTurn)), evaluator.Evaluate(reader, lane.PlayerTurn));
                int pendingChecks = AssertCompactReplayBoundaries(captured, display, damage, policy, player, nativePath);
                _completedChecks.Add($"CompactRoundPending:Mode{mode}:{pendingChecks}OmittedChoices:FullEvaluationAndOwnedSpecs:ReverseRestore");
                AssertSnapshotEqual(rootSnapshot, CaptureActual(combat, player, enemy), "CompactRound", $"Mode{mode}-Unchanged");

                void Check(ResumableDiscardProgram values, CombatPredictionSimulator expected, PlanAction[] path)
                {
                    var projected = adapter.Materialize(values);
                    adapter.AssertValues(values, expected); adapter.AssertValues(values, projected);
                    AssertSnapshotEqual(CaptureSimulated(expected, (SimulatedCombatState)expected.State.CombatState, player, enemy),
                        CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy), "CompactRound", $"Mode{mode}-Projection");
                    string[] expectedHistory = CompactHistory(expected, adapter).ToArray(), actualHistory = CompactHistory(projected, adapter).ToArray();
                    if (!expectedHistory.SequenceEqual(actualHistory))
                        throw new InvalidOperationException("Round history differs:\n" + string.Join('\n', expectedHistory) + "\nProjected:\n" + string.Join('\n', actualHistory));
                    if (!CompactPowerValues(((SimulatedCombatState)expected.State.CombatState).EffectivePowers())
                        .SequenceEqual(CompactPowerValues(((SimulatedCombatState)projected.State.CombatState).EffectivePowers())))
                        throw new InvalidOperationException("Round Power lifecycle fields differ.");
                    AssertCompactRngSet(expected.Rng, projected.Rng);
                    var evaluation = Release(evaluator.Evaluate(expected, values.PlayerTurn));
                    AssertCompactEvaluation(evaluation, Release(evaluator.Evaluate(projected, values.PlayerTurn)), $"mode{mode}/projection");
                    reader.Read(values); uncached.Read(values);
                    AssertCompactEvaluation(evaluation, evaluator.Evaluate(reader, values.PlayerTurn), $"mode{mode}/cached-reader");
                    AssertCompactEvaluation(evaluation, evaluator.Evaluate(uncached, values.PlayerTurn), $"mode{mode}/uncached-reader");
                    samples.Add((path, values.Freeze(), evaluation));
                }
                void AddNative(PlanAction action, ResumableDiscardProgram values, CombatPredictionSimulator expected)
                {
                    var identities = adapter.CaptureCardIdentities(expected).Where(pair => pair.Value >= 0).OrderBy(pair => pair.Value).ToArray();
                    native.Add((action, CaptureSimulated(expected, (SimulatedCombatState)expected.State.CombatState, player, enemy),
                        CompactPowerValues(((SimulatedCombatState)expected.State.CombatState).EffectivePowers()).ToArray(),
                        Enumerable.Range(0, 5).Select(pile => values.Cards((ResumableDiscardProgram.Pile)pile)).ToArray(),
                        identities.Select(pair => CombatBeamSolver.CaptureCardStateFingerprintForTesting(expected.State.FindCard(pair.Key)!)).ToArray()));
                }
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var lane = initial.Open(); var reader = adapter.CreateReadView(); var metadata = NewMetadata();
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                foreach (var sample in samples.AsEnumerable().Reverse())
                {
                    initial.RestoreInto(lane);
                    foreach (var action in sample.Actions) Replay(lane, action, metadata);
                    if (!lane.State.Freeze().ContentEquals(sample.Candidate.Open().State.Freeze()))
                        throw new InvalidOperationException("Round worker differs from frozen candidate.");
                    reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn));
                }
            })));
            MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(method => method.Name == "EndCombatInternal" && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
            var prefix = typeof(UnattendedTestRunner).GetMethod(nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
            Harmony patch = new("CombatSolver.Testing.CompactRound." + _request.RunId);
            var observation = new MercuryTerminalObservation(this, combat, player, enemy,
                endCombat.GetParameters()[0].ParameterType.GetProperty("State")!, "CompactRound");
            try
            {
                if (mode == 2)
                {
                    _mercuryTerminalObservation = observation;
                    CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
                    patch.Patch(endCombat, prefix: new HarmonyMethod(prefix));
                }
                for (int index = 0; index < native.Count; index++)
                {
                    var expected = native[index];
                    if (expected.Action.Kind == PlanActionKind.PlayCard)
                    {
                        if (!FindActualHandCard(player, expected.Action.CardId, 0).TryManualPlay(null)) throw new InvalidOperationException("Native round prefix rejected.");
                        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                    }
                    else
                    {
                        var selector = new PlannedCardSelector(expected.Action.TurnStartChoices ?? []);
                        selector.CaptureBefore(player);
                        using (CardSelectCmd.PushSelector(selector)) await AdvanceMercuryActualTurnAsync(combat, player, mode == 2);
                        if (mode != 2) selector.ReconcileImplicitChoices(player);
                        selector.AssertConsumed();
                    }
                    var actual = mode == 2 ? observation.Snapshot! : CaptureActual(combat, player, enemy);
                    evidence.Add(new { mode, step = index, expected.Action, expected = expected.Snapshot, actual, branches = samples.Count });
                    if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
                    {
                        Directory.CreateDirectory(_request.EvidenceDirectory);
                        File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-round.json"),
                            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    AssertSnapshotEqual(expected.Snapshot, actual, "CompactRound", $"Mode{mode}-Native{index}");
                    if (mode == 2)
                    {
                        if (observation.Turn != expected.Action.Turn) throw new InvalidOperationException("Native poison terminal advanced turn.");
                        continue;
                    }
                    var generated = CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Skip(nativeGeneratedStart).Select(entry => entry.Card).ToArray();
                    int Identity(CardModel card) => adapter.IndexOf(card) is >= 0 and var id ? id : adapter.CardCount + Array.IndexOf(generated, card);
                    var state = player.PlayerCombatState!;
                    CardPile[] piles = [state.Hand, state.DrawPile, state.DiscardPile, state.PlayPile, state.ExhaustPile];
                    if (expected.Piles.Where((pile, p) => !pile.SequenceEqual(piles[p].Cards.Select(Identity))).Any()
                        || !expected.Powers.SequenceEqual(CompactPowerValues(combat.Creatures.SelectMany(c => c.Powers).ToArray())))
                        throw new InvalidOperationException("Native round instance order or Power fields differ.");
                    var all = Enumerable.Range(0, adapter.CardCount).Select(adapter.Original).Concat(generated).ToArray();
                    if (!expected.Cards.SequenceEqual(all.Select(card => CombatBeamSolver.CaptureCardStateFingerprintForTesting(PredictedCard.FromGenerated(card)))))
                        throw new InvalidOperationException("Native round per-instance card metadata differs.");
                }
                if (mode < 2 && CombatManager.Instance.History.Entries.OfType<MonsterPerformedMoveEntry>().Count() - nativeMovesStart != (mode == 0 ? 3 : 2))
                    throw new InvalidOperationException("Round did not perform exactly one native move per enemy turn.");
            }
            finally
            {
                if (mode == 2)
                {
                    patch.Unpatch(endCombat, prefix);
                    CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded;
                    _mercuryTerminalObservation = null;
                }
            }
            using (SimulationNotificationIsolation.Enter())
            {
                // Force a cold history read after native time (and eventually cleanup)
                // has advanced. The captured entry window must use branch turn values.
                var cold = (SimulatedCombatState)root.Fork().State.CombatState;
                typeof(SimulatedCombatState).GetField("_statusCardsDrawnThisTurn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cold, null);
                if (cold.GetStatusCardsDrawnThisTurn(player) != rootStatusDraws)
                    throw new InvalidOperationException("Cold root history read consulted native player-turn state.");
                var lane = initial.Open(); var metadata = NewMetadata();
                foreach (var action in nativePath) Replay(lane, action, metadata);
                var projected = adapter.Materialize(lane);
                AssertSnapshotEqual(native[^1].Snapshot, CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy),
                    "CompactRound", $"Mode{mode}-FrozenAfterNative");
                AssertSnapshotEqual(rootSnapshot, CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy),
                    "CompactRound", $"Mode{mode}-RootAfterNative");
            }
            _completedChecks.Add($"CompactRound:Mode{mode}:Branches{samples.Count}:NativeActions{native.Count}:FullSnapshotHistoryKeysEvaluationRng:ReverseAndEightWorkerRestore:FrozenAfterNative");

            CompactCardMetadataReadBinding NewMetadata()
            {
                var owned = root.Fork();
                return new(adapter, Enumerable.Range(0, adapter.CardCount).Select(id => owned.State.FindCard(adapter.Original(id))!).ToArray());
            }
            void Replay(ResumableDiscardProgram lane, PlanAction action, CompactCardMetadataReadBinding metadata)
            {
                if (action.Kind == PlanActionKind.PlayCard)
                {
                    int card = lane.Cards(ResumableDiscardProgram.Pile.Hand).First(id => adapter.Original(id).Id.Entry == action.CardId);
                    lane.Begin(card); lane.Run(); lane.CheckWinCondition();
                    if (!lane.Complete) throw new InvalidOperationException("Round prefix has an unexpected choice.");
                    return;
                }
                lane.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Together);
                int choice = 0;
                while (!lane.Complete)
                {
                    lane.Run();
                    if (!lane.NeedsChoice) continue;
                    var plan = action.TurnStartChoices?[choice++] ?? throw new InvalidOperationException("Round plan omitted a compact choice.");
                    metadata.Read(lane);
                    int[] options = lane.Cards(lane.ChoicePile);
                    int[] selected = plan.Cards.Select(token => options.Where(id => CardChoiceSupport.MatchesToken(metadata[id], token))
                        .Skip(token.OptionOccurrence).First()).ToArray();
                    if (plan.SourcePile != (lane.ChoicePile == ResumableDiscardProgram.Pile.Draw ? PileType.Draw : PileType.Hand))
                        throw new InvalidOperationException("Round choice pile differs.");
                    lane.SupplyChoice(selected);
                }
                lane.CheckWinCondition();
                if (choice != (action.TurnStartChoices?.Count ?? 0)) throw new InvalidOperationException("Round choice counts differ.");
            }
            SimulationSnapshot InvokeRoundSnapshot(CombatBeamSolver driver, CombatPredictionSimulator state)
                => (SimulationSnapshot)InvokeForcedTerminalMethod(driver, "Snapshot", [state,
                    ((SimulatedCombatState)state.State.CombatState).GetPlayerTurnNumber(player), 0, 0,
                    SearchBoundaryReason.None, new ForkableSet<uint>()])!;
        }
    }
}
