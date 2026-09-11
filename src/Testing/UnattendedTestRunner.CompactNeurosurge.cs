using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class CompactResourceChoiceObserver(PlannedCardSelector inner, Action observe) : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            observe();
            return inner.GetSelectedCards(options, minSelect, maxSelect);
        }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
            => inner.GetSelectedCardReward(options, alternatives);
    }

    private async Task PrepareCompactNeurosurgeAsync(CombatState combat, Player player, bool choices)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        var enemy = combat.Enemies.Single();
        await CreatureCmd.SetMaxHp(player.Creature, 300);
        await CreatureCmd.SetCurrentHp(player.Creature, 300);
        await CreatureCmd.SetMaxHp(enemy, 300);
        await CreatureCmd.SetCurrentHp(enemy, 300);
        await SetBlockAsync(player.Creature, 7);
        await SetBlockAsync(enemy, 9);
        ConfigureMonsterMove(enemy, new UnattendedMonsterMoveCheck { MoveId = "WINDUP_MOVE" });
        for (int upgrade = 0; upgrade < 2; upgrade++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "NEUROSURGE", Pile = "Hand", UpgradeLevels = upgrade });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Draw" });
        for (int index = 0; index < 7; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Discard" });
        await PowerCmd.Apply<DoomPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, enemy, null);
        if (choices)
        {
            await PowerCmd.Apply<NeurosurgePower>(new BlockingPlayerChoiceContext(), player.Creature, 2, enemy, null);
            await PowerCmd.Apply<StratagemPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
            await PowerCmd.Apply<ToolsOfTheTradePower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
            // Both OnPlay debuffs and the first next-turn Doom must consume distinct charges.
            await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), player.Creature, 3, player.Creature, null);
        }
        foreach (var power in player.Creature.Powers) power.AmountOnTurnStart = 41;
        SetEnergy(player, 1); SetStars(player, 0);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactNeurosurgeAsync(CombatState combat, Player player)
    {
        bool choices = _request.ScenarioId == "COMPACT-NEUROSURGE-CHOICES";
        await PrepareCompactNeurosurgeAsync(combat, player, choices);
        var enemy = combat.Enemies.Single();
        var captured = CombatRootSnapshot.Capture(combat);
        var root = captured.ForkSimulator();
        var original = CaptureActual(combat, player, enemy);
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
                for (int step = 0; step < 4; step++)
                {
                    var node = ForcedTerminalAnnotationNode(parent, null, null) with { ActionCount = step };
                    var before = lane.Freeze();
                    IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> branches = step < 2 ? CardBranches()
                        : (IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>)InvokeForcedTerminalMethod(driver,
                            "BuildEndTurnBranches", [node, Array.Empty<PlanCardChoice>(), true])!;
                    CompactRouteSample? chosen = null;
                    CombatPredictionSimulator? next = null;
                    foreach (var branch in branches.Take(4))
                    {
                        try
                        {
                            before.RestoreInto(lane); replay.Execute(lane, branch.Action);
                            var sample = Check(branch.Action, branch.Snapshot.Simulator, $"Step{step}/Branch{alternatives++}");
                            if (chosen == null) { chosen = sample; next = branch.Snapshot.Simulator.Fork(); }
                        }
                        finally { branch.Snapshot.ReleaseSimulator(); }
                    }
                    if (chosen == null || next == null) throw new InvalidOperationException("Neurosurge fixture omitted its required action.");
                    route.Add(chosen);
                    chosen.Values.RestoreInto(lane);
                    parent.ReleaseSimulator();
                    parent = evaluator.Evaluate(next, lane.PlayerTurn);

                    IEnumerable<(PlanAction, SimulationSnapshot)> CardBranches()
                    {
                        foreach (SearchNode child in (IEnumerable<SearchNode>)InvokeForcedTerminalMethod(driver, "Expand", [node])!)
                        {
                            if (child.Action?.CardId == "NEUROSURGE") yield return (child.Action, child.Snapshot);
                            else child.Snapshot.ReleaseSimulator();
                        }
                    }
                }
                if (lane.Energy != 3 || lane.DefeatTerminal || lane.Terminal
                    || route[1].Values.Open().Energy != 8
                    || lane.Power(Enumerable.Range(0, lane.PowerCount).Single(i => lane.PowerDefinition(i).Kind == BasicPowerKind.Doom)).Amount != (choices ? 3 : 13))
                    throw new InvalidOperationException("Neurosurge resource/Artifact/turn-start fixture missed its intended result.");
                foreach (var sample in route.AsEnumerable().Reverse())
                {
                    sample.Values.RestoreInto(lane); reader.Read(lane);
                    AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), "Neurosurge/Reverse");
                }
                initial.RestoreInto(lane);
                int pending = AssertCompactReplayBoundaries(captured, display, damage, policy, player, route.Select(item => item.Action).ToArray());
                if (choices && pending == 0) throw new InvalidOperationException("Neurosurge did not exercise suspended turn-start state.");
                _completedChecks.Add($"CompactNeurosurge:Choices{choices}:OmittedChoices{pending}:GainBeforeDraw:ArtifactAndDoom:FullContinuations");
                AssertSnapshotEqual(original, CaptureActual(combat, player, enemy), "CompactNeurosurge", "ActualUnchanged");
            }
            finally { parent.ReleaseSimulator(); }

            CompactRouteSample Check(PlanAction action, CombatPredictionSimulator expected, string label)
            {
                var projected = adapter.Materialize(lane);
                adapter.AssertValues(lane, expected); adapter.AssertValues(lane, projected);
                var snapshot = CaptureSimulated(expected, (SimulatedCombatState)expected.State.CombatState, player, enemy);
                AssertSnapshotEqual(snapshot, CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy), "CompactNeurosurge", label);
                if (!CompactHistory(expected, adapter).SequenceEqual(CompactHistory(projected, adapter)))
                    throw new InvalidOperationException("Neurosurge full history/source differs.");
                var powers = CompactPowerValues(((SimulatedCombatState)expected.State.CombatState).EffectivePowers()).ToArray();
                if (!powers.SequenceEqual(CompactPowerValues(((SimulatedCombatState)projected.State.CombatState).EffectivePowers())))
                    throw new InvalidOperationException("Neurosurge Power lifecycle fields differ.");
                AssertCompactRngSet(expected.Rng, projected.Rng);
                var evaluation = Release(evaluator.Evaluate(expected, lane.PlayerTurn));
                AssertCompactEvaluation(evaluation, Release(evaluator.Evaluate(projected, lane.PlayerTurn)), label + "/Projection");
                reader.Read(lane); uncached.Read(lane);
                AssertCompactEvaluation(evaluation, evaluator.Evaluate(reader, lane.PlayerTurn), label + "/Reader");
                AssertCompactEvaluation(evaluation, evaluator.Evaluate(uncached, lane.PlayerTurn), label + "/Uncached");
                return new(action, lane.Freeze(), evaluation, snapshot, powers,
                    Enumerable.Range(0, 5).Select(pile => lane.Cards((ResumableDiscardProgram.Pile)pile)).ToArray(),
                    Enumerable.Range(0, adapter.CardCount).Where(id => !lane.CardRemoved(id))
                        .Select(id => CombatBeamSolver.CaptureCardStateFingerprintForTesting(expected.State.FindCard(adapter.Original(id))!)).ToArray(), lane.Terminal);
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
                if (!lane.State.Freeze().ContentEquals(sample.Values.Open().State.Freeze())) throw new InvalidOperationException("Neurosurge worker lost side-start state.");
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
            int beforeEnergy = player.PlayerCombatState!.Energy;
            int beforePower = player.Creature.GetPower<NeurosurgePower>()?.Amount ?? 0;
            int beforeArtifact = player.Creature.GetPower<ArtifactPower>()?.Amount ?? 0;
            var observer = new CompactResourceChoiceObserver(selector, () =>
            {
                if (action.Kind != PlanActionKind.PlayCard) return;
                if (player.PlayerCombatState!.Energy != beforeEnergy + (action.CardUpgradeLevel == 0 ? 3 : 4)
                    || (player.Creature.GetPower<NeurosurgePower>()?.Amount ?? 0) != beforePower
                    || (player.Creature.GetPower<ArtifactPower>()?.Amount ?? 0) != beforeArtifact)
                    throw new InvalidOperationException("Native Neurosurge choice must observe gained energy before applying its debuff.");
                nativeResourceChoices++;
            });
            using (CardSelectCmd.PushSelector(observer))
            {
                if (action.Kind == PlanActionKind.EndTurn) await AdvanceMercuryActualTurnAsync(combat, player, false);
                else
                {
                    var card = CombatBeamSolver.FindCardForReplay(player.PlayerCombatState!.Hand.Cards.Select(PredictedCard.FromGenerated).ToArray(), action)
                        ?? throw new InvalidOperationException("Native Neurosurge instance is absent.");
                    if (!card.Original.TryManualPlay(null)) throw new InvalidOperationException("Native Neurosurge was rejected.");
                    await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                }
            }
            selector.ReconcileImplicitChoices(player); selector.AssertConsumed();
            var actual = CaptureActual(combat, player, enemy);
            var state = player.PlayerCombatState!;
            CardPile[] piles = [state.Hand, state.DrawPile, state.DiscardPile, state.PlayPile, state.ExhaustPile];
            var actualPowers = CompactPowerValues(combat.Creatures.SelectMany(c => c.Powers).ToArray());
            var actualPiles = piles.Select(pile => pile.Cards.Select(adapter.IndexOf).ToArray()).ToArray();
            evidence.Add(new { action, expected = expected.Snapshot, actual, expected.Powers, actualPowers, expected.Piles, actualPiles });
            if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            {
                Directory.CreateDirectory(_request.EvidenceDirectory);
                File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-neurosurge.json"), JsonSerializer.Serialize(new { choices, alternatives, nativeResourceChoices, native = evidence }, new JsonSerializerOptions { WriteIndented = true }));
            }
            AssertSnapshotEqual(expected.Snapshot, actual, "CompactNeurosurge", "Native");
            if (expected.Piles.Where((pile, p) => !pile.SequenceEqual(actualPiles[p])).Any()
                || !expected.Powers.SequenceEqual(actualPowers))
                throw new InvalidOperationException("Native Neurosurge instance order or Power metadata differs.");
            var values = expected.Values.Open();
            if (!expected.Cards.SequenceEqual(Enumerable.Range(0, adapter.CardCount).Where(id => !values.CardRemoved(id))
                    .Select(id => CombatBeamSolver.CaptureCardStateFingerprintForTesting(PredictedCard.FromGenerated(adapter.Original(id))))))
                throw new InvalidOperationException("Native Neurosurge card metadata differs.");
        }
        if (choices && nativeResourceChoices == 0) throw new InvalidOperationException("Native Neurosurge resource-order selector was not reached.");
        using (SimulationNotificationIsolation.Enter())
        {
            var lane = initial.Open(); var replay = new CompactPlanReplay(adapter); var reader = adapter.CreateReadView();
            foreach (var sample in route) replay.Execute(lane, sample.Action);
            reader.Read(lane);
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            AssertCompactEvaluation(route[^1].Evaluation, evaluator.Evaluate(reader, lane.PlayerTurn));
            AssertSnapshotEqual(original, CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy), "CompactNeurosurge", "FrozenAfterNative");
        }
        _completedChecks.Add($"CompactNeurosurge:Choices{choices}:{route.Count}NativeActions:{alternatives}Branches:{nativeResourceChoices}NativeResourceChoices:FullStateKeysHistoryRng:EightWorkers:FrozenAfterNative");
    }
}
