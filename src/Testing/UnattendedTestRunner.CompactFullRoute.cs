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
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed record CompactRouteSample(PlanAction Action, ResumableDiscardProgram.Candidate Values,
        SimulationSnapshot Evaluation, MoveStateSnapshot Snapshot, string[] Powers, int[][] Piles,
        StateFingerprint[] Cards, bool Terminal);

    private async Task AssertCompactFullRouteAsync(CombatState combat, Player player)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (player.Deck.Cards.Count != 30 || player.Relics.Count != 1 || player.Relics[0] is not RingOfTheSnake
            || combat.Enemies.Count != 1 || player.PlayerCombatState!.TurnNumber != 1)
            throw new InvalidOperationException("Full-route fixture requires the unchanged original 30-card Silent root.");
        var captured = CombatRootSnapshot.Capture(combat);
        var root = captured.ForkSimulator();
        var enemy = combat.Enemies.Single();
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var original = CaptureActual(combat, player, enemy);
        int generatedStart = CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Count();
        var result = await Task.Run(() => CombatSearchCoordinator.Solve(captured, display, damage, policy, default, null));
        if (!result.Snapshot.AllEnemiesDead || result.Snapshot.PlayerDead || result.BestNode.Actions.Count == 0)
            throw new InvalidOperationException("Original full search did not produce a complete victory route.");
        PlanAction[] route = result.BestNode.Actions.ToArray();
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-route-plan.json"),
                JsonSerializer.Serialize(route, new JsonSerializerOptions { WriteIndented = true }));
        }
        List<CompactRouteSample> native = [];
        List<(ResumableDiscardProgram.Candidate Values, SimulationSnapshot Evaluation)> alternatives = [];
        CompactDiscardProjection adapter;
        ResumableDiscardProgram.Candidate initial;
        using (SimulationNotificationIsolation.Enter())
        {
            adapter = new(root, player, includeAttacks: true, includeHandEnd: true, includeMechaMoves: true,
                includePowerPhases: true, includeMechaAi: true, includeRounds: true);
            if (adapter.CardCount != 30 || ((SimulatedCombatState)root.State.CombatState).RootRunHookListenerCount != 31)
                throw new InvalidOperationException("Original deck instances or run listener prefix were altered.");
            var lane = adapter.Program;
            initial = lane.Freeze();
            var replay = new CompactPlanReplay(adapter);
            var reader = adapter.CreateReadView(); var uncached = adapter.CreateReadView(false);
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            var driver = new CombatBeamSolver(captured, display, damage, policy, searchProfile: policy.ShortProfile);
            var parent = InvokeForcedTerminalReplay(driver, [], null, captured.StartTurnNumber, null);
            var parentNode = ForcedTerminalAnnotationNode(parent, null, null);
            List<SimulationSnapshot> parents = [parent];
            try
            {
                for (int step = 0; step < route.Length; step++)
                {
                    var before = lane.Freeze();
                    // Exercise every alternative admitted by the existing action/choice policy
                    // at each chosen prefix. This is a semantic differential, not a speed sample.
                    var branchDriver = new CombatBeamSolver(captured, display, damage, policy, searchProfile: policy.ShortProfile);
                    var branches = (IEnumerable<SearchNode>)InvokeForcedTerminalMethod(branchDriver, "Expand", [parentNode])!;
                    foreach (var branch in branches)
                    {
                        try
                        {
                            before.RestoreInto(lane);
                            replay.Execute(lane, branch.Action!);
                            var sample = Check(branch.Action!, branch.Snapshot.Simulator, $"Alternative{step}/{alternatives.Count}");
                            alternatives.Add((sample.Values, sample.Evaluation));
                        }
                        finally { branch.Snapshot.ReleaseSimulator(); }
                    }
                    before.RestoreInto(lane);
                    replay.Execute(lane, route[step]);
                    var expected = InvokeForcedTerminalReplay(driver, [route[step]], parent, parent.Turn, null);
                    parents.Add(expected);
                    native.Add(Check(route[step], expected.Simulator, $"Route{step}"));
                    parentNode = ForcedTerminalAnnotationNode(expected, parentNode, route[step]) with { ActionCount = step + 1 };
                    parent = expected;
                }
                if (!lane.Terminal || lane.DefeatTerminal)
                    throw new InvalidOperationException("Compact full route did not reach the same victory.");
                foreach (var sample in alternatives.AsEnumerable().Reverse())
                {
                    sample.Values.RestoreInto(lane); reader.Read(lane);
                    AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), "FullRoute/ReverseAlternative");
                }
                initial.RestoreInto(lane);
                AssertSnapshotEqual(original, CaptureActual(combat, player, enemy), "CompactFullRoute", "PreparationUnchanged");
            }
            finally { foreach (var snapshot in parents) snapshot.ReleaseSimulator(); }

            CompactRouteSample Check(PlanAction action, CombatPredictionSimulator expected, string label)
            {
                var projected = adapter.Materialize(lane);
                adapter.AssertValues(lane, expected); adapter.AssertValues(lane, projected);
                var snapshot = CaptureSimulated(expected, (SimulatedCombatState)expected.State.CombatState, player, enemy);
                AssertSnapshotEqual(snapshot, CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy),
                    "CompactFullRoute", label);
                if (!CompactHistory(expected, adapter).SequenceEqual(CompactHistory(projected, adapter)))
                    throw new InvalidOperationException($"Full route history/source differs at {label}.");
                var powers = CompactPowerValues(((SimulatedCombatState)expected.State.CombatState).EffectivePowers()).ToArray();
                if (!powers.SequenceEqual(CompactPowerValues(((SimulatedCombatState)projected.State.CombatState).EffectivePowers())))
                    throw new InvalidOperationException($"Full route Power lifecycle differs at {label}.");
                AssertCompactRngSet(expected.Rng, projected.Rng);
                var evaluation = Release(evaluator.Evaluate(expected, lane.PlayerTurn));
                var projectedEvaluation = Release(evaluator.Evaluate(projected, lane.PlayerTurn));
                if (evaluation.StateKey != projectedEvaluation.StateKey)
                {
                    var expectedStamp = ContinuationStamp.CapturePredicted(player, expected, lane.PlayerTurn, captured.Forecast, captured.StartTurnNumber);
                    var projectedStamp = ContinuationStamp.CapturePredicted(player, projected, lane.PlayerTurn, captured.Forecast, captured.StartTurnNumber);
                    object Fields(CombatPredictionSimulator sim) => typeof(SimulatedCombatState)
                        .GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Select(field => new { field.Name, Value = field.GetValue(sim.State.CombatState) })
                        .Where(field => field.Value is System.Collections.IEnumerable || field.Value?.GetType().IsValueType == true)
                        .Select(field => new { field.Name, value = field.Value is System.Collections.IEnumerable entries
                            ? string.Join('|', entries.Cast<object>().Select(item => item.ToString())) : field.Value?.ToString() }).ToArray();
                    File.WriteAllText(Path.Combine(_request.EvidenceDirectory!, "compact-route-key-difference.json"), JsonSerializer.Serialize(new
                    {
                        label, action, difference = expectedStamp.DescribeDifferences(projectedStamp), expectedStamp, projectedStamp,
                        expectedFields = Fields(expected), projectedFields = Fields(projected)
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                AssertCompactEvaluation(evaluation, projectedEvaluation, label + "/projection");
                reader.Read(lane); uncached.Read(lane);
                AssertCompactEvaluation(evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), label + "/reader");
                AssertCompactEvaluation(evaluation, evaluator.Evaluate(uncached, lane.PlayerTurn), label + "/uncached");
                var identities = adapter.CaptureCardIdentities(expected).Where(pair => pair.Value >= 0 && !lane.CardRemoved(pair.Value)).OrderBy(pair => pair.Value);
                return new(action, lane.Freeze(), evaluation, snapshot, powers,
                    Enumerable.Range(0, 5).Select(pile => lane.Cards((ResumableDiscardProgram.Pile)pile)).ToArray(),
                    identities.Select(pair => CombatBeamSolver.CaptureCardStateFingerprintForTesting(expected.State.FindCard(pair.Key)!)).ToArray(), lane.Terminal);
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            var lane = initial.Open(); var replay = new CompactPlanReplay(adapter); var reader = adapter.CreateReadView();
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            foreach (var sample in native)
            {
                replay.Execute(lane, sample.Action);
                if (!lane.State.Freeze().ContentEquals(sample.Values.Open().State.Freeze()))
                    throw new InvalidOperationException("Full-route worker differs from frozen values.");
                reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), "FullRoute/Worker");
            }
        })));

        MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "EndCombatInternal" && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
        var prefix = typeof(UnattendedTestRunner).GetMethod(nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
        Harmony patch = new("CombatSolver.Testing.CompactFullRoute." + _request.RunId);
        var observation = new MercuryTerminalObservation(this, combat, player, enemy,
            endCombat.GetParameters()[0].ParameterType.GetProperty("State")!, "CompactFullRoute");
        List<object> evidence = [];
        try
        {
            _mercuryTerminalObservation = observation;
            CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
            patch.Patch(endCombat, prefix: new HarmonyMethod(prefix));
            for (int step = 0; step < native.Count; step++)
            {
                var expected = native[step]; var action = expected.Action;
                var selector = new PlannedCardSelector(action.Kind == PlanActionKind.EndTurn
                    ? action.TurnStartChoices ?? [] : action.GetActionChoicesInExecutionOrder());
                selector.CaptureBefore(player);
                using (CardSelectCmd.PushSelector(selector))
                {
                    if (action.Kind == PlanActionKind.EndTurn)
                        await AdvanceMercuryActualTurnAsync(combat, player, expected.Terminal);
                    else
                    {
                        var card = CombatBeamSolver.FindCardForReplay(player.PlayerCombatState!.Hand.Cards.Select(PredictedCard.FromGenerated).ToArray(), action)
                            ?? throw new InvalidOperationException("Native full-route card instance is absent.");
                        if (!card.Original.TryManualPlay(action.TargetCombatId == null ? null : enemy))
                            throw new InvalidOperationException("Native full-route card was rejected.");
                        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                    }
                }
                if (!expected.Terminal) selector.ReconcileImplicitChoices(player);
                selector.AssertConsumed();
                var actual = expected.Terminal ? observation.Snapshot
                    ?? throw new InvalidOperationException("Native full-route terminal was not observed.") : CaptureActual(combat, player, enemy);
                evidence.Add(new { step, action, expected = expected.Snapshot, actual });
                WriteEvidence();
                AssertSnapshotEqual(expected.Snapshot, actual, "CompactFullRoute", $"Native{step}");
                if (expected.Terminal) continue;
                var generated = CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Skip(generatedStart).Select(entry => entry.Card).ToArray();
                int Identity(MegaCrit.Sts2.Core.Models.CardModel card) => adapter.IndexOf(card) is >= 0 and var id
                    ? id : adapter.CardCount + Array.IndexOf(generated, card);
                var state = player.PlayerCombatState!;
                CardPile[] piles = [state.Hand, state.DrawPile, state.DiscardPile, state.PlayPile, state.ExhaustPile];
                if (expected.Piles.Where((pile, p) => !pile.SequenceEqual(piles[p].Cards.Select(Identity))).Any()
                    || !expected.Powers.SequenceEqual(CompactPowerValues(combat.Creatures.SelectMany(c => c.Powers).ToArray())))
                    throw new InvalidOperationException($"Native full-route instances or Power fields differ at {step}.");
                var values = expected.Values.Open();
                var cards = Enumerable.Range(0, adapter.CardCount).Select(adapter.Original).Concat(generated)
                    .Where((_, id) => !values.CardRemoved(id));
                if (!expected.Cards.SequenceEqual(cards.Select(card => CombatBeamSolver.CaptureCardStateFingerprintForTesting(PredictedCard.FromGenerated(card)))))
                    throw new InvalidOperationException($"Native full-route card metadata differs at {step}.");
            }
        }
        finally
        {
            patch.Unpatch(endCombat, prefix);
            CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded;
            _mercuryTerminalObservation = null;
        }
        using (SimulationNotificationIsolation.Enter())
        {
            var lane = initial.Open(); var replay = new CompactPlanReplay(adapter); var reader = adapter.CreateReadView();
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            foreach (var action in route) replay.Execute(lane, action);
            reader.Read(lane);
            AssertCompactEvaluation(native[^1].Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), "FullRoute/FrozenAfterNative");
            AssertSnapshotEqual(original, CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy), "CompactFullRoute", "RootAfterNative");
        }
        _completedChecks.Add($"CompactFullRoute:Original30Cards31Listeners:{route.Length}NativeActions:{alternatives.Count}AdmittedAlternatives:CompleteVictory:FullStateKeysHistoryRng:TwoWorkers:FrozenAfterNative");

        void WriteEvidence()
        {
            if (string.IsNullOrWhiteSpace(_request.EvidenceDirectory)) return;
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-full-route.json"), JsonSerializer.Serialize(new
            {
                route, alternatives = alternatives.Count, result.CombatEndedTurn, result.ProjectedBattleHpLost,
                result.ExpandedNodes, result.TransitionCount, result.ChoiceBranchesEvaluated, native = evidence
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
