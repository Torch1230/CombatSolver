using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRelicMaxHpHealingBoundsAsync(CombatState live, Player player)
    {
        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        foreach (var potion in player.PotionSlots.ToArray()) potion?.Discard();
        await ClearPlayerPilesAsync(player);
        var evidence = new List<object>();
        foreach (string relicId in new[] { "CHOSEN_CHEESE", "DARKSTONE_PERIAPT" })
        {
            foreach (var old in player.Relics.ToArray()) await RelicCmd.Remove(old);
            await InjectRelicAsync(player, new() { RelicId = relicId });
            CardModel? deckCurse = null;
            if (relicId == "DARKSTONE_PERIAPT")
            {
                var card = (await InjectCardAsync(live, player,
                    new() { CardId = "INJURY", Pile = "Hand", TreatAsDeckCard = true })).Single();
                deckCurse = card.DeckVersion;
                if (deckCurse?.Pile?.Type != PileType.Deck)
                    throw new InvalidOperationException("Permanent-curse callback fixture lacks its native Deck card.");
            }
            await CreatureCmd.SetMaxHp(player.Creature, 80);
            await CreatureCmd.SetCurrentHp(player.Creature, 50);
            var root = CombatRootSnapshot.Capture(live);
            var parent = root.ForkSimulator();
            var child = parent.Fork();
            var combat = (SimulatedCombatState)child.State.CombatState;
            string parentBefore = DescribeContinuationContractState(parent, root, player);
            string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            int bound = StrategicHpRecoveryBound.KnownNativeHealingPotential(parent, player, 0);
            int gain = relicId == "CHOSEN_CHEESE" ? 1 : 6;
            child.GainMaxHp(player.Creature, gain);
            if (DescribeContinuationContractState(parent, root, player) != parentBefore
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException($"Relic max-HP callback changed parent/live: {relicId}.");
            var predicted = CaptureSimulated(child, combat, player, live.Enemies[0]);
            // Invoke the exact native callback, without running a search or pretending
            // that permanent-deck mutation / complete combat-end dispatch is modeled.
            if (relicId == "CHOSEN_CHEESE")
                await player.Relics.OfType<ChosenCheese>().Single().AfterCombatEnd(null!);
            else
                await player.Relics.OfType<DarkstonePeriapt>().Single()
                    .AfterCardChangedPiles(deckCurse!, PileType.None, null);
            var actual = CaptureActual(live, player, live.Enemies[0]);
            AssertSnapshotEqual(predicted, actual, _request.ScenarioId, relicId);
            evidence.Add(new { relicId, bound, observedHeal = player.Creature.CurrentHp - 50,
                observedMaxHpGain = player.Creature.MaxHp - 80,
                root.CanCertifyRemainingHealing, root.UsesKnownNativeHealingPolicy });
            _writer.WriteGeneratedArtifact("relic-max-hp-bound.json", evidence);
            if (bound != int.MaxValue || root.CanCertifyRemainingHealing
                || DescribeContinuationContractState(parent, root, player) != parentBefore)
                throw new InvalidOperationException($"Unclosed relic healing lost its conservative allowance: {relicId}, bound={bound}, native heal={gain}.");
            _completedChecks.Add($"RelicMaxHpHealing:{relicId}:NativeCallback:FullStateRng:ForkParentLiveIsolation:UnclosedBound");

            var relic = player.Relics.Single();
            relic.IsWax = true;
            await RelicCmd.Melt(relic);
            var meltedRoot = CombatRootSnapshot.Capture(live);
            int meltedBound = StrategicHpRecoveryBound.KnownNativeHealingPotential(meltedRoot.ForkSimulator(), player, 0);
            if (meltedBound != 0)
                throw new InvalidOperationException($"Melted relic still reserves healing: {relicId}.");
            _completedChecks.Add($"RelicMaxHpHealing:{relicId}:MeltedSourceExcluded");
        }
    }
}
