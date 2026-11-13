using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertBetaRelicPreparationAsync(CombatState live, Player player, params string[] relicIds)
    {
        await ClearOrderedEffectFixtureAsync(live, player);
        bool infusedCore = relicIds.Contains("INFUSED_CORE");
        if (infusedCore)
        {
            var queue = player.PlayerCombatState!.OrbQueue;
            int capacity = queue.Capacity;
            queue.Clear();
            queue.AddCapacity(capacity);
        }
        foreach (string id in relicIds)
            await InjectRelicAsync(player, new() { RelicId = id, AddWithoutObtainedEffects = true });
        await CreatureCmd.SetCurrentHp(player.Creature, 30);
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_FOOL", Pile = "Discard", Count = 5 });
        var parent = CombatRootSnapshot.Capture(live).ForkSimulator();
        var child = parent.Fork();
        var shadow = (SimulatedCombatState)child.State.CombatState;
        var initial = CaptureActual(live, player, live.Enemies[0]);
        TriggerSimulatedPlayerSetup(child, shadow, player, [], advanceTurn: false);
        await TriggerActualPlayerSetupAsync(live, player, [], advanceTurn: false);
        AssertSnapshotEqual(CaptureSimulated(child, shadow, player, live.Enemies[0]),
            CaptureActual(live, player, live.Enemies[0]), _request.ScenarioId, "FirstPreparation");
        if (relicIds.Contains("TORMENTOUS_COLLAR")
            && live.Enemies.Any(enemy => enemy.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.WeakPower>() != 2))
            throw new InvalidOperationException("Elite first-turn collar must apply two Weak to each enemy.");
        AssertSnapshotEqual(initial, CaptureSimulated(parent, (SimulatedCombatState)parent.State.CombatState,
            player, live.Enemies[0]), _request.ScenarioId, "PreparationParentIsolation");
        _ = child.Fork();
        TriggerSimulatedPlayerSetup(child, shadow, player, []);
        await TriggerActualPlayerSetupAsync(live, player, []);
        AssertSnapshotEqual(CaptureSimulated(child, shadow, player, live.Enemies[0]),
            CaptureActual(live, player, live.Enemies[0]), _request.ScenarioId, "SecondPreparation");
        if (infusedCore && player.PlayerCombatState!.OrbQueue.Orbs.Count != 3)
            throw new InvalidOperationException("Infused Core did not retain exactly three orbs after the second preparation.");
        _completedChecks.Add("RelicPreparation:" + string.Join(',', relicIds)
            + ":FirstAndSecondTurn:NativeFullState:ParentFork:FullRng");
    }

    private async Task AssertBathTowelHealingAsync(CombatState live, Player player)
    {
        await ClearOrderedEffectFixtureAsync(live, player);
        foreach (var potion in player.PotionSlots.ToArray()) potion?.Discard();
        await InjectRelicAsync(player, new() { RelicId = "BATH_TOWEL" });
        await CreatureCmd.SetCurrentHp(player.Creature, 30);
        var relic = player.Relics.OfType<BathTowel>().Single();
        var parent = CombatRootSnapshot.Capture(live).ForkSimulator();
        var child = parent.Fork();
        var shadow = (SimulatedCombatState)child.State.CombatState;
        var initial = CaptureActual(live, player, live.Enemies[0]);
        if (StrategicHpRecoveryBound.KnownNativeHealingPotential(parent, player, 0) != 8)
            throw new InvalidOperationException("First-turn Bath Towel healing was omitted from the allowance.");
        shadow.ApplyRelicAfterPlayerTurnStart(child, player, new TurnStartChoiceCursor(null), relic);
        await relic.AfterPlayerTurnStartLate(new BlockingPlayerChoiceContext(), player);
        AssertSnapshotEqual(CaptureSimulated(child, shadow, player, live.Enemies[0]),
            CaptureActual(live, player, live.Enemies[0]), _request.ScenarioId, "BathTowelNativeHeal");
        AssertSnapshotEqual(initial, CaptureSimulated(parent, (SimulatedCombatState)parent.State.CombatState,
            player, live.Enemies[0]), _request.ScenarioId, "BathTowelParentIsolation");
        shadow.AdvancePlayerTurn(player);
        if (StrategicHpRecoveryBound.KnownNativeHealingPotential(child, player, 0) != 0)
            throw new InvalidOperationException("Bath Towel healing remained available after the first turn.");
        _completedChecks.Add("BathTowelHealing:FirstTurnAllowance:NativeHeal:ForkLiveIsolation:LaterTurnExpiry");
    }

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
