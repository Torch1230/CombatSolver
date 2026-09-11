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
    private async Task AssertCompactPetCardRouteAsync(CombatState combat, Player player, int mode, string label, string evidencePrefix,
        (string Id, int? Upgrade)[] cardSteps, Action<ResumableDiscardProgram, ResumableDiscardProgram, int> assertStep,
        bool observeDrawExhaust = false)
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
                for (int step = 0; step < cardSteps.Length + 2; step++)
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
                    if (chosen == null || next == null) throw new InvalidOperationException("Pet card route fixture omitted its required action.");
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
                if (pending == 0) throw new InvalidOperationException("Pet card route did not exercise suspended turn-start state.");
                _completedChecks.Add($"{label}:Mode{mode}:OmittedChoices{pending}:FullContinuations");
                AssertSnapshotEqual(original, CaptureActual(combat, player, enemy), label, "ActualUnchanged");
            }
            finally { parent.ReleaseSimulator(); }

            CompactRouteSample Check(PlanAction action, CombatPredictionSimulator expected, string label)
            {
                var projected = adapter.Materialize(lane);
                adapter.AssertValues(lane, expected); adapter.AssertValues(lane, projected);
                var snapshot = CaptureSimulated(expected, (SimulatedCombatState)expected.State.CombatState, player, enemy);
                AssertSnapshotEqual(snapshot, CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy), label, label);
                if (!CompactHistory(expected, adapter).SequenceEqual(CompactHistory(projected, adapter)))
                    throw new InvalidOperationException("Pet card route full history/source differs.");
                var powers = CompactPowerValues(((SimulatedCombatState)expected.State.CombatState).EffectivePowers()).ToArray();
                if (!powers.SequenceEqual(CompactPowerValues(((SimulatedCombatState)projected.State.CombatState).EffectivePowers())))
                    throw new InvalidOperationException("Pet card route Power lifecycle fields differ.");
                AssertCompactRngSet(expected.Rng, projected.Rng);
                var evaluation = Release(evaluator.Evaluate(expected, lane.PlayerTurn));
                AssertCompactEvaluation(evaluation, Release(evaluator.Evaluate(projected, lane.PlayerTurn)), label + "/Projection");
                reader.Read(lane); uncached.Read(lane);
                AssertCompactEvaluation(evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), label + "/Reader");
                AssertCompactEvaluation(evaluation, evaluator.Evaluate(uncached, lane.PlayerTurn), label + "/Uncached");
                var identities = adapter.CaptureCardIdentities(expected).Where(pair => pair.Value >= 0 && !lane.CardRemoved(pair.Value)).OrderBy(pair => pair.Value);
                return new(action, lane.Freeze(), evaluation, snapshot, powers,
                    Enumerable.Range(0, 5).Select(pile => lane.Cards((ResumableDiscardProgram.Pile)pile)).ToArray(),
                    identities.Select(pair => CombatBeamSolver.CaptureCardStateFingerprintForTesting(expected.State.FindCard(pair.Key)!)).ToArray(), lane.Terminal);
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
        foreach (var expected in route)
        {
            var action = expected.Action;
            var selector = new PlannedCardSelector(action.Kind == PlanActionKind.EndTurn ? action.TurnStartChoices ?? [] : action.GetActionChoicesInExecutionOrder());
            selector.CaptureBefore(player);
            var expectedPet = expected.Values.Open().Creature(adapter.Program.PetIndex);
            var observer = new CompactResourceChoiceObserver(selector, () =>
            {
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
                    if (!card.Original.TryManualPlay(null)) throw new InvalidOperationException("Native pet card route was rejected.");
                    await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                }
            }
            selector.ReconcileImplicitChoices(player); selector.AssertConsumed();
            var actual = CaptureActual(combat, player, enemy);
            var state = player.PlayerCombatState!;
            CardPile[] piles = [state.Hand, state.DrawPile, state.DiscardPile, state.PlayPile, state.ExhaustPile];
            var actualPowers = CompactPowerValues(combat.Creatures.SelectMany(c => c.Powers).ToArray());
            var generated = CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Skip(generatedStart).Select(entry => entry.Card).ToArray();
            int Identity(CardModel card)
            {
                int id = adapter.IndexOf(card);
                if (id >= 0) return id;
                int index = Array.IndexOf(generated, card);
                return index >= 0 ? adapter.CardCount + index : throw new InvalidOperationException("Native card has no captured or generated identity.");
            }
            var actualPiles = piles.Select(pile => pile.Cards.Select(Identity).ToArray()).ToArray();
            evidence.Add(new { action, expected = expected.Snapshot, actual, expected.Powers, actualPowers, expected.Piles, actualPiles, expectedPet, actualPet = new CreatureVitals(player.Osty!.CurrentHp, player.Osty.MaxHp, player.Osty.Block) });
            if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            {
                Directory.CreateDirectory(_request.EvidenceDirectory);
                File.WriteAllText(Path.Combine(_request.EvidenceDirectory, $"{evidencePrefix}-mode{mode}.json"), JsonSerializer.Serialize(new { mode, alternatives, nativeResourceChoices, native = evidence }, new JsonSerializerOptions { WriteIndented = true }));
            }
            AssertSnapshotEqual(expected.Snapshot, actual, label, "Native");
            if (expected.Piles.Where((pile, p) => !pile.SequenceEqual(actualPiles[p])).Any()
                || !expected.Powers.SequenceEqual(actualPowers)
                || expectedPet != new CreatureVitals(player.Osty!.CurrentHp, player.Osty.MaxHp, player.Osty.Block))
                throw new InvalidOperationException("Native pet card route instance order or Power metadata differs.");
            var values = expected.Values.Open();
            var actualCards = Enumerable.Range(0, adapter.CardCount).Select(adapter.Original).Concat(generated)
                .Where((_, id) => !values.CardRemoved(id));
            if (!expected.Cards.SequenceEqual(actualCards.Select(card => CombatBeamSolver.CaptureCardStateFingerprintForTesting(PredictedCard.FromGenerated(card)))))
                throw new InvalidOperationException("Native pet card route card metadata differs.");
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
