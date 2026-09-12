using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Retains the request's complete run deck, starting relic, draw order and resources.
    // This probes the action boundary; it deliberately does not advance a round.
    private async Task AssertCompactFullRootAsync(CombatState combat, Player player)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (player.Deck.Cards.Count != 30 || player.Relics.Count != 1 || player.Relics[0] is not RingOfTheSnake
            || combat.Enemies.Count != 1 || player.PlayerCombatState!.TurnNumber != 1)
            throw new InvalidOperationException("Full-root fixture requires the original 30-card Silent opening with its starter relic.");
        var captured = CombatRootSnapshot.Capture(combat);
        var root = captured.ForkSimulator();
        var shadow = (SimulatedCombatState)root.State.CombatState;
        var enemy = combat.Enemies[0];
        CardModel[] originals = root.State.GetPlayerCombatState(player).AllCards.Select(card => card.Original).ToArray();
        if (originals.Length != 30 || shadow.RootRunHookListenerCount != 31)
            throw new InvalidOperationException("Full-root fixture lost a combat instance or the enchanted deck listener prefix.");
        var original = CaptureActual(combat, player, enemy);
        int generatedBefore = CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Count();
        List<CompactCardAction[]> paths = [];
        List<MoveStateSnapshot> expected = [];
        CompactCardAction[] native;
        object rootEvidence;
        using (SimulationNotificationIsolation.Enter())
        {
            var adapter = new CompactDiscardProjection(root, player, includeAttacks: true);
            var lane = adapter.Program;
            var initial = lane.Freeze();
            rootEvidence = new { deck = player.Deck.Cards.Select(card => new { id = card.Id.Entry, card.CurrentUpgradeLevel,
                    enchantment = card.Enchantment?.Id.Entry }).ToArray(),
                hand = lane.Cards(ResumableDiscardProgram.Pile.Hand).Select(id => originals[id].Id.Entry).ToArray(),
                rootRunListeners = shadow.RootRunHookListenerCount, lane.Energy, lane.Block };
            foreach (int id in Eligible()) paths.Add([Action(id)]);
            if (paths.Count == 0) throw new InvalidOperationException("Full-root fixture has no selected action probe.");
            for (int direction = 0; direction < 2; direction++)
            {
                initial.RestoreInto(lane);
                List<CompactCardAction> route = [];
                while (!lane.Ending && Eligible().ToArray() is { Length: > 0 } eligible)
                {
                    int id = direction == 0 ? eligible[0] : eligible[^1];
                    var action = Action(id);
                    route.Add(action); lane.Begin(id, action.Target); lane.Run();
                    if (!lane.Complete || route.Count > 60)
                        throw new InvalidOperationException("Full-root action probe exceeded its deterministic test route.");
                }
                paths.Add(route.ToArray());
            }
            native = paths[^2];
            initial.RestoreInto(lane);
            foreach (var action in native)
            {
                int id = action.RootCard != null ? adapter.IndexOf(action.RootCard) : adapter.CardCount + action.GeneratedOrdinal;
                lane.Begin(id, action.Target); lane.Run();
                var projection = adapter.Materialize(lane);
                expected.Add(CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy));
            }
            AssertSnapshotEqual(original, CaptureActual(combat, player, enemy), "CompactFullRoot", "PreparationUnchanged");

            IEnumerable<int> Eligible() => lane.Cards(ResumableDiscardProgram.Pile.Hand).Where(id =>
            {
                CardModel card = adapter.DefinitionModels[lane.DefinitionIndex(id)];
                // Selected deterministic probes; choice enumeration is covered separately.
                return card is not (Acrobatics or Prepared or Survivor) && lane.EnergyCost(id) <= lane.Energy;
            });
            CompactCardAction Action(int id)
            {
                CardModel card = adapter.DefinitionModels[lane.DefinitionIndex(id)];
                int target = card.TargetType == TargetType.AnyEnemy ? 1 : -1;
                return id < adapter.CardCount ? new(originals[id], Target: target) : new(null, id - adapter.CardCount, target);
            }
        }
        await AssertCompactCardSequencesAsync(captured, root, combat, player, originals, "CompactFullRoot", paths);
        for (int step = 0; step < native.Length; step++)
        {
            var action = native[step];
            CardModel card = action.RootCard ?? CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>()
                .Skip(generatedBefore).ElementAt(action.GeneratedOrdinal).Card;
            if (!card.TryManualPlay(action.Target < 0 ? null : enemy))
                throw new InvalidOperationException("Native full-root route rejected a selected card.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(expected[step], CaptureActual(combat, player, enemy), "CompactFullRoot", $"Native{step}");
        }
        using (SimulationNotificationIsolation.Enter())
            AssertSnapshotEqual(original, CaptureSimulated(root, shadow, player, enemy), "CompactFullRoot", "RootAfterNativeUnchanged");
        _completedChecks.Add($"CompactFullRoot:Original30Cards31DeckListeners:RingOfTheSnake:{paths.Count}Branches:{native.Length}NativeActions:RootAfterNativeUnchanged");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-full-root.json"), JsonSerializer.Serialize(new
            {
                root = rootEvidence,
                paths = paths.Select(path => path.Select(action => new { card = action.RootCard?.Id.Entry, action.GeneratedOrdinal, action.Target }).ToArray()).ToArray(),
                nativeActions = native.Length, roundAdvanced = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
