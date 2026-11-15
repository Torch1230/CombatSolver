using System.IO;
using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Random;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertOrderedHandDuplicatesAsync(CombatState combat, Player player, bool playSecond)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "TRUE_GRIT" })
            await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Hand" });

        // Advance the native stream before capture; both native and predicted plays consume this same state.
        var selection = player.RunState.Rng.CombatCardSelection;
        int advances = 0;
        while (new Rng(selection.ToSerializable()).NextItem(new[] { 0, 1 }) != 0)
        {
            if (++advances > 64) throw new InvalidOperationException("Could not prepare the first-slot random selection.");
            selection.NextItem(new[] { 0, 1 });
        }

        var enemy = combat.Enemies[0];
        var firstCard = FindActualHandCard(player, "STRIKE_IRONCLAD", 0);
        var secondCard = FindActualHandCard(player, "STRIKE_IRONCLAD", 1);
        var gritCard = FindActualHandCard(player, "TRUE_GRIT", 0);
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        PlanAction Strike(int occurrence) => new(PlanActionKind.PlayCard, root.StartTurnNumber,
            CardId: "STRIKE_IRONCLAD", CardOccurrence: occurrence, TargetCombatId: enemy.CombatId);
        PlanAction grit = new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "TRUE_GRIT");
        List<SimulationSnapshot> snapshots = [];
        SimulationSnapshot Replay(params PlanAction[] actions)
        {
            var snapshot = driver.ReplayDiagnosticPrefix(actions);
            snapshots.Add(snapshot);
            return snapshot;
        }
        try
        {
            SimulationSnapshot parent = Replay();
            string parentBefore = driver.CaptureDiagnosticContinuation(parent).StateText;
            var sibling = parent.Simulator.Fork();
            string siblingBefore = DescribeContinuationContractState(sibling, root, player);
            SimulationSnapshot first = Replay(Strike(0));
            SimulationSnapshot second = Replay(Strike(1));
            SimulationSnapshot firstEnd = Replay(Strike(0), grit);
            SimulationSnapshot secondEnd = Replay(Strike(1), grit);
            PlanAction[] strikes = driver.EnumerateRootCardActionsForTesting()
                .Where(action => action.CardId == "STRIKE_IRONCLAD").ToArray();
            string[] Hand(SimulationSnapshot snapshot) => snapshot.Simulator.State.GetPlayerCombatState(player)
                .Hand.Cards.Select(card => card.Preview.Id.Entry).ToArray();
            string[] Exhaust(SimulationSnapshot snapshot) => snapshot.Simulator.State.GetPlayerCombatState(player)
                .ExhaustPile.Cards.Select(card => card.Preview.Id.Entry).ToArray();
            if (!Hand(first).SequenceEqual(new[] { "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "TRUE_GRIT" })
                || !Hand(second).SequenceEqual(new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "TRUE_GRIT" })
                || first.StateKey == second.StateKey
                || !Exhaust(firstEnd).SequenceEqual(new[] { "DEFEND_IRONCLAD" })
                || !Exhaust(secondEnd).SequenceEqual(new[] { "STRIKE_IRONCLAD" }))
                throw new InvalidOperationException("Separated duplicate plays did not preserve their distinct ordered residues.");

            SimulationSnapshot afterStrike = playSecond ? second : first;
            SimulationSnapshot afterGrit = playSecond ? secondEnd : firstEnd;
            if (!await PlayNativeCardAsync(playSecond ? secondCard : firstCard, enemy))
                throw new InvalidOperationException("Native duplicate Strike was refused.");
            AssertSnapshotEqual(CaptureSimulated(afterStrike.Simulator,
                    (SimulatedCombatState)afterStrike.Simulator.State.CombatState, player, enemy),
                CaptureActual(combat, player, enemy), "OrderedHandDuplicates", "AfterStrike");
            if (ContinuationStamp.CaptureLive(combat).StateText != driver.CaptureDiagnosticContinuation(afterStrike).StateText)
                throw new InvalidOperationException("Native duplicate Strike continuation differs from prediction.");
            if (!await PlayNativeCardAsync(gritCard, null))
                throw new InvalidOperationException("Native True Grit was refused.");
            AssertSnapshotEqual(CaptureSimulated(afterGrit.Simulator,
                    (SimulatedCombatState)afterGrit.Simulator.State.CombatState, player, enemy),
                CaptureActual(combat, player, enemy), "OrderedHandDuplicates", "AfterTrueGrit");
            if (ContinuationStamp.CaptureLive(combat).StateText != driver.CaptureDiagnosticContinuation(afterGrit).StateText
                || driver.CaptureDiagnosticContinuation(parent).StateText != parentBefore
                || DescribeContinuationContractState(sibling, root, player) != siblingBefore)
                throw new InvalidOperationException("Duplicate-card replay changed continuation or parent/Fork isolation.");

            _completedChecks.Add($"OrderedHandDuplicates:Native:{(playSecond ? "Second" : "First")}:FullState:Rng:Continuation:Fork");
            if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            {
                Directory.CreateDirectory(_request.EvidenceDirectory);
                File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "ordered-hand-duplicates.json"),
                    JsonSerializer.Serialize(new
                    {
                        playSecond, rootContinuation = parentBefore, selectionAdvances = advances,
                        firstHand = Hand(first), secondHand = Hand(second),
                        firstExhaust = Exhaust(firstEnd), secondExhaust = Exhaust(secondEnd),
                        firstContinuation = driver.CaptureDiagnosticContinuation(firstEnd).StateText,
                        secondContinuation = driver.CaptureDiagnosticContinuation(secondEnd).StateText,
                        strikeOccurrences = strikes.Select(action => action.CardOccurrence).ToArray(),
                    }, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (!strikes.Select(action => action.CardOccurrence).SequenceEqual(new[] { 0, 1 }))
                throw new InvalidOperationException($"Ordered-hand branch was pruned: Strike occurrences [{string.Join(',', strikes.Select(action => action.CardOccurrence))}], expected [0,1].");
        }
        finally
        {
            foreach (var snapshot in snapshots) snapshot.ReleaseSimulator();
        }

        foreach (bool adjacent in new[] { false, true })
        {
            foreach (string cardId in new[] { "STRIKE_IRONCLAD", "INFLAME" })
            {
                await ClearOrderedEffectFixtureAsync(combat, player);
                foreach (string id in adjacent ? new[] { cardId, cardId, "WOUND" } : new[] { cardId, "WOUND", cardId })
                    await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Hand" });
                var checkRoot = CombatRootSnapshot.Capture(combat);
                var checkDriver = new CombatBeamSolver(checkRoot, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
                    SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
                int expected = adjacent ? 1 : 2;
                int actual = checkDriver.EnumerateRootCardActionsForTesting().Count(action => action.CardId == cardId);
                if (actual != expected || checkDriver.RootHasAlternativePlayableCardForTesting(0) != !adjacent)
                    throw new InvalidOperationException($"Duplicate residue contract failed: {cardId}, adjacent={adjacent}, actions={actual}, expected={expected}.");
                if (cardId == "INFLAME" && checkDriver.BuildPowerActionsAfterPrefix([]).Count != expected)
                    throw new InvalidOperationException($"Opening power duplicates lost ordered residues: adjacent={adjacent}.");
                _completedChecks.Add($"OrderedHandDuplicates:{cardId}:Adjacent={adjacent}:Ordinary:CycleAlternative:OpeningPower");
            }
        }
    }
}
