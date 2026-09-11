using System.Reflection;
using HarmonyLib;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed record CompactNativePetCardState(MoveStateSnapshot Snapshot, string[] Powers,
        int[][] Piles, StateFingerprint[] Cards, CreatureVitals Pet, int[]? EnergyCosts);

    private async Task AssertCompactPetCardRouteAsync(CombatState combat, Player player, int mode, string label, string evidencePrefix,
        (string Id, int? Upgrade)[] cardSteps, Action<ResumableDiscardProgram, ResumableDiscardProgram, int> assertStep,
        bool observeDrawExhaust = false, Func<PlanAction, ResumableDiscardProgram, bool>? observeNativeChoice = null,
        int rounds = 2, bool requirePending = true, bool verifyEnergyCosts = false)
    {
        var enemy = combat.Enemies.Single();
        var captured = CombatRootSnapshot.Capture(combat);
        var root = captured.ForkSimulator();
        var original = CaptureActual(combat, player, enemy);
        int generatedStart = CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Count();
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        CompactDiscardProjection adapter;
        ResumableDiscardProgram.Candidate initial;
        List<CompactRouteSample> route = [];
        int alternatives = 0;
        using (SimulationNotificationIsolation.Enter())
        {
            var compact = new CompactCombatRoot(root, player);
            adapter = compact.Adapter; initial = compact.Initial;
            var lane = adapter.Program; var replay = new CompactPlanReplay(adapter);
            var reader = adapter.CreateReadView(); var uncached = adapter.CreateReadView(false);
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            var driver = new CombatBeamSolver(captured, display, damage, policy, searchProfile: policy.ShortProfile);
            var parent = InvokeForcedTerminalReplay(driver, [], null, captured.StartTurnNumber, null);
            try
            {
                for (int step = 0; step < cardSteps.Length + rounds; step++)
                {
                    var node = ForcedTerminalAnnotationNode(parent, null, null) with { ActionCount = step };
                    var before = lane.Freeze();
                    IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> branches = step < cardSteps.Length ? CardBranches()
                        : (IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>)InvokeForcedTerminalMethod(driver,
                            "BuildEndTurnBranches", [node, Array.Empty<PlanCardChoice>(), true])!;
                    CompactRouteSample? chosen = null;
                    CombatPredictionSimulator? next = null;
                    foreach (var branch in branches.Take(4))
                    {
                        try
                        {
                            before.RestoreInto(lane); replay.Execute(lane, branch.Action);
                            assertStep(lane, before.Open(), step);
                            var sample = Check(branch.Action, branch.Snapshot.Simulator, $"Step{step}/Branch{alternatives++}");
                            if (chosen == null) { chosen = sample; next = branch.Snapshot.Simulator.Fork(); }
                        }
                        finally { branch.Snapshot.ReleaseSimulator(); }
                    }
                    if (chosen == null || next == null)
                    {
                        var state = parent.Simulator.State.GetPlayerCombatState(player);
                        string required = step < cardSteps.Length ? $"{cardSteps[step].Id}#{cardSteps[step].Upgrade}" : "EndTurn";
                        throw new InvalidOperationException($"Pet card route omitted step {step} ({required}); energy={state.Energy}; "
                            + $"hand={string.Join(',', state.Hand.Cards.Select(card => $"{card.Preview.Id.Entry}#{card.Preview.CurrentUpgradeLevel}"))}.");
                    }
                    route.Add(chosen);
                    chosen.Values.RestoreInto(lane);
                    parent.ReleaseSimulator();
                    parent = evaluator.Evaluate(next, lane.PlayerTurn);

                    IEnumerable<(PlanAction, SimulationSnapshot)> CardBranches()
                    {
                        foreach (SearchNode child in (IEnumerable<SearchNode>)InvokeForcedTerminalMethod(driver, "Expand", [node])!)
                        {
                            if (child.Action?.CardId == cardSteps[step].Id
                                && (cardSteps[step].Upgrade == null || child.Action.CardUpgradeLevel == cardSteps[step].Upgrade)) yield return (child.Action, child.Snapshot);
                            else child.Snapshot.ReleaseSimulator();
                        }
                    }
                }
                foreach (var sample in route.AsEnumerable().Reverse())
                {
                    sample.Values.RestoreInto(lane); reader.Read(lane);
                    AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), label + "/Reverse");
                }
                initial.RestoreInto(lane);
                var mark = lane.State.Mark(); replay.Execute(lane, route[0].Action); lane.State.Rollback(mark);
                if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze()))
                    throw new InvalidOperationException("Pet card route rollback leaked summon or selection state.");
                int pending = AssertCompactReplayBoundaries(captured, display, damage, policy, player, route.Select(item => item.Action).ToArray());
                if (requirePending && pending == 0) throw new InvalidOperationException("Pet card route did not exercise suspended turn-start state.");
                _completedChecks.Add($"{label}:Mode{mode}:OmittedChoices{pending}:FullContinuations");
                AssertSnapshotEqual(original, CaptureActual(combat, player, enemy), label, "ActualUnchanged");
            }
            finally { parent.ReleaseSimulator(); }

            CompactRouteSample Check(PlanAction action, CombatPredictionSimulator expected, string stage)
            {
                var projected = adapter.Materialize(lane);
                adapter.AssertValues(lane, expected); adapter.AssertValues(lane, projected);
                var snapshot = CaptureSimulated(expected, (SimulatedCombatState)expected.State.CombatState, player, enemy);
                AssertSnapshotEqual(snapshot, CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy), label, stage);
                if (!CompactHistory(expected, adapter).SequenceEqual(CompactHistory(projected, adapter)))
                    throw new InvalidOperationException("Pet card route full history/source differs.");
                var powers = CompactPowerValues(((SimulatedCombatState)expected.State.CombatState).EffectivePowers()).ToArray();
                if (!powers.SequenceEqual(CompactPowerValues(((SimulatedCombatState)projected.State.CombatState).EffectivePowers())))
                    throw new InvalidOperationException("Pet card route Power lifecycle fields differ.");
                AssertCompactRngSet(expected.Rng, projected.Rng);
                var evaluation = Release(evaluator.Evaluate(expected, lane.PlayerTurn));
                AssertCompactEvaluation(evaluation, Release(evaluator.Evaluate(projected, lane.PlayerTurn)), stage + "/Projection");
                reader.Read(lane); uncached.Read(lane);
                if (verifyEnergyCosts)
                {
                    IReadOnlyList<PredictedCard>[] readPiles = [reader.Hand, reader.Draw, reader.Discard, reader.Exhaust];
                    ResumableDiscardProgram.Pile[] piles = [ResumableDiscardProgram.Pile.Hand, ResumableDiscardProgram.Pile.Draw,
                        ResumableDiscardProgram.Pile.Discard, ResumableDiscardProgram.Pile.Exhaust];
                    for (int pile = 0; pile < piles.Length; pile++)
                    for (int index = 0; index < readPiles[pile].Count; index++)
                    {
                        int cost = readPiles[pile][index].GetEnergyCostValueWithModifiers(reader.EvaluationContext);
                        if (cost != lane.EnergyCost(lane.CardAt(piles[pile], index)))
                            throw new InvalidOperationException("Completed reader returned a stale global energy cost or pile.");
                    }
                }
                AssertCompactEvaluation(evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), stage + "/Reader");
                AssertCompactEvaluation(evaluation, evaluator.Evaluate(uncached, lane.PlayerTurn), stage + "/Uncached");
                var identities = adapter.CaptureCardIdentities(expected).Where(pair => pair.Value >= 0 && !lane.CardRemoved(pair.Value)).OrderBy(pair => pair.Value);
                return new(action, lane.Freeze(), evaluation, snapshot, powers,
                    Enumerable.Range(0, 5).Select(pile => lane.Cards((ResumableDiscardProgram.Pile)pile)).ToArray(),
                    identities.Select(pair => CombatBeamSolver.CaptureCardStateFingerprintForTesting(lane.CardUnplaced(pair.Value)
                        ? adapter.CreateGeneratedCard(lane.DefinitionIndex(pair.Value)) : expected.State.FindCard(pair.Key)!)).ToArray(), lane.Terminal);
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            var lane = initial.Open(); var replay = new CompactPlanReplay(adapter); var reader = adapter.CreateReadView();
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            foreach (var sample in route)
            {
                replay.Execute(lane, sample.Action);
                if (!lane.State.Freeze().ContentEquals(sample.Values.Open().State.Freeze())) throw new InvalidOperationException("Pet card route worker lost side-start state.");
                reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn));
            }
        })));
        List<object> evidence = [];
        int nativeResourceChoices = 0;
        CompactRouteSample? activeSample = null;
        PlannedCardSelector? activeSelector = null;
        CompactNativePetCardState? terminalState = null;
        MercuryTerminalObservation? terminalObservation = null;
        MethodInfo? endCombat = null, prefix = null;
        Harmony? patch = null;
        if (route.Any(sample => sample.Terminal))
        {
            if (_mercuryTerminalObservation != null) throw new InvalidOperationException("A terminal observer is already active.");
            endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(method => method.Name == "EndCombatInternal" && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
            prefix = typeof(UnattendedTestRunner).GetMethod(nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
            patch = new Harmony("CombatSolver.Testing.CompactPetCardRoute." + _request.RunId);
            terminalObservation = new(this, combat, player, enemy, endCombat.GetParameters()[0].ParameterType.GetProperty("State")!, label, snapshot =>
            {
                activeSelector!.ReconcileImplicitChoices(player);
                terminalState = CaptureNative(activeSample!, snapshot);
            });
        }
        try
        {
            if (terminalObservation != null)
            {
                _mercuryTerminalObservation = terminalObservation;
                CombatManager.Instance.CombatEnded += terminalObservation.ObserveCombatEnded;
                patch!.Patch(endCombat!, prefix: new HarmonyMethod(prefix!));
            }
            foreach (var expected in route)
            {
                var action = expected.Action;
                var selector = new PlannedCardSelector(action.Kind == PlanActionKind.EndTurn ? action.TurnStartChoices ?? [] : action.GetActionChoicesInExecutionOrder());
                selector.CaptureBefore(player);
                activeSample = expected; activeSelector = selector;
                var expectedPet = expected.Values.Open().Creature(adapter.Program.PetIndex);
                var observer = new CompactResourceChoiceObserver(selector, () =>
                {
                    if (observeNativeChoice?.Invoke(action, expected.Values.Open()) == true) nativeResourceChoices++;
                    if (!observeDrawExhaust || action.CardId != "CLEANSE") return;
                    var osty = player.Osty!;
                    if (new CreatureVitals(osty.CurrentHp, osty.MaxHp, osty.Block) != expectedPet)
                        throw new InvalidOperationException("Native pet card route selector must observe the completed summon.");
                    nativeResourceChoices++;
                });
                using (CardSelectCmd.PushSelector(observer))
                {
                    if (action.Kind == PlanActionKind.EndTurn) await AdvanceMercuryActualTurnAsync(combat, player, false);
                    else
                    {
                        var card = CombatBeamSolver.FindCardForReplay(player.PlayerCombatState!.Hand.Cards.Select(PredictedCard.FromGenerated).ToArray(), action)
                            ?? throw new InvalidOperationException("Native pet card route instance is absent.");
                        var target = action.TargetCombatId == null ? null : combat.Creatures.Single(creature => creature.CombatId == action.TargetCombatId);
                        if (!card.Original.TryManualPlay(target)) throw new InvalidOperationException("Native pet card route was rejected.");
                        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                    }
                }
                if (expected.Terminal)
                {
                    while (terminalObservation!.Snapshot == null || !terminalObservation.CombatEnded || CombatManager.Instance.IsInProgress)
                    { EnsureWithinDeadline(); terminalObservation.Failure?.Throw(); await NextFrameAsync(); }
                    terminalObservation.Failure?.Throw();
                    if (terminalObservation.Turn != expected.Values.Open().PlayerTurn)
                        throw new InvalidOperationException("Native terminal turn differs.");
                }
                else selector.ReconcileImplicitChoices(player);
                selector.AssertConsumed();
                var actual = expected.Terminal ? terminalState ?? throw new InvalidOperationException("Native terminal card metadata was not captured.")
                    : CaptureNative(expected);
                var expectedValues = expected.Values.Open();
                int[]? expectedCosts = verifyEnergyCosts ? Enumerable.Range(0, expectedValues.CardCount)
                    .Where(id => !expectedValues.CardRemoved(id)).Select(expectedValues.EnergyCost).ToArray() : null;
                evidence.Add(new { action, expected = expected.Snapshot, actual = actual.Snapshot, expected.Powers,
                    actualPowers = actual.Powers, expected.Piles, actualPiles = actual.Piles, expectedPet, actualPet = actual.Pet,
                    expectedCards = expected.Cards, actualCards = actual.Cards, expectedCosts, actualCosts = actual.EnergyCosts,
                    unplaced = expectedValues.Cards(ResumableDiscardProgram.Pile.Unplaced) });
                if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
                {
                    Directory.CreateDirectory(_request.EvidenceDirectory);
                    File.WriteAllText(Path.Combine(_request.EvidenceDirectory, $"{evidencePrefix}-mode{mode}.json"), JsonSerializer.Serialize(new { mode, alternatives, nativeResourceChoices, native = evidence }, new JsonSerializerOptions { WriteIndented = true }));
                }
                AssertSnapshotEqual(expected.Snapshot, actual.Snapshot, label, "Native");
                if (expected.Piles.Where((pile, p) => !pile.SequenceEqual(actual.Piles[p])).Any()
                    || !expected.Powers.SequenceEqual(actual.Powers) || expectedPet != actual.Pet)
                    throw new InvalidOperationException("Native pet card route instance order or Power metadata differs.");
                if (!expected.Cards.SequenceEqual(actual.Cards)) throw new InvalidOperationException("Native pet card route card metadata differs.");
                if (expectedCosts != null && !expectedCosts.SequenceEqual(actual.EnergyCosts!))
                    throw new InvalidOperationException("Native global energy costs differ from the current value program.");
            }
        }
        finally
        {
            if (terminalObservation != null)
            {
                try { patch!.Unpatch(endCombat!, prefix!); }
                finally
                {
                    CombatManager.Instance.CombatEnded -= terminalObservation.ObserveCombatEnded;
                    _mercuryTerminalObservation = null;
                }
            }
        }
        CompactNativePetCardState CaptureNative(CompactRouteSample expected, MoveStateSnapshot? snapshot = null)
        {
            var state = player.PlayerCombatState!;
            CardPile[] piles = [state.Hand, state.DrawPile, state.DiscardPile, state.PlayPile, state.ExhaustPile];
            var generated = CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Skip(generatedStart).Select(entry => entry.Card).ToArray();
            int Identity(CardModel card)
            {
                int id = adapter.IndexOf(card);
                if (id >= 0) return id;
                int index = Array.IndexOf(generated, card);
                return index >= 0 ? adapter.CardCount + index : throw new InvalidOperationException("Native card has no captured or generated identity.");
            }
            var values = expected.Values.Open();
            var actualCards = Enumerable.Range(0, adapter.CardCount).Select(adapter.Original).Concat(generated)
                .Where((_, id) => !values.CardRemoved(id));
            return new(snapshot ?? CaptureActual(combat, player, enemy),
                CompactPowerValues(combat.Creatures.SelectMany(c => c.Powers).ToArray()).ToArray(),
                piles.Select(pile => pile.Cards.Select(Identity).ToArray()).ToArray(),
                actualCards.Select(card => CombatBeamSolver.CaptureCardStateFingerprintForTesting(PredictedCard.FromGenerated(card))).ToArray(),
                new CreatureVitals(player.Osty!.CurrentHp, player.Osty.MaxHp, player.Osty.Block),
                verifyEnergyCosts ? actualCards.Select(card => card.EnergyCost.GetWithModifiers(CostModifiers.All)).ToArray() : null);
        }
        if (observeDrawExhaust && mode == 0 && nativeResourceChoices == 0) throw new InvalidOperationException("Native pet card route resource-order selector was not reached.");
        using (SimulationNotificationIsolation.Enter())
        {
            var lane = initial.Open(); var replay = new CompactPlanReplay(adapter); var reader = adapter.CreateReadView();
            foreach (var sample in route) replay.Execute(lane, sample.Action);
            reader.Read(lane);
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            AssertCompactEvaluation(route[^1].Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn));
            AssertSnapshotEqual(original, CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy), label, "FrozenAfterNative");
        }
        _completedChecks.Add($"{label}:Mode{mode}:{route.Count}NativeActions:{alternatives}Branches:{nativeResourceChoices}NativeResourceChoices:FullStateKeysHistoryRng:EightWorkers:FrozenAfterNative");
    }
}
