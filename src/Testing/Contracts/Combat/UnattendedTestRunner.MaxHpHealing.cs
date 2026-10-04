using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Resources;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertMaxHpHealingCallbacksAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await InjectRelicAsync(player, new() { RelicId = "DRAGON_FRUIT" });
        var evidence = new List<object>();
        async Task CheckCommand(string label, int maxHp, int hp, decimal amount, bool gold)
        {
            await CreatureCmd.SetMaxHp(player.Creature, maxHp);
            await CreatureCmd.SetCurrentHp(player.Creature, hp);
            var root = CombatRootSnapshot.Capture(live);
            var parent = root.ForkSimulator(); var child = parent.Fork();
            var combat = (SimulatedCombatState)child.State.CombatState;
            string parentBefore = DescribeContinuationContractState(parent, root, player);
            string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            if (gold) combat.GainPlayerGold(child, player, amount);
            else child.GainMaxHp(player.Creature, amount);
            var expected = CaptureSimulated(child, combat, player, live.Enemies[0]);
            if (DescribeContinuationContractState(parent, root, player) != parentBefore
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException($"MaxHP command changed parent/live: {label}.");
            if (gold) await PlayerCmd.GainGold(amount, player);
            else await CreatureCmd.GainMaxHp(player.Creature, amount);
            AssertSnapshotEqual(expected, CaptureActual(live, player, live.Enemies[0]), _request.ScenarioId, label);
            evidence.Add(new { label, amount, gold, hp = player.Creature.CurrentHp, maxHp = player.Creature.MaxHp });
            _completedChecks.Add($"MaxHpHealing:{label}:FullNativeStateRng:ParentLiveIsolation");
        }
        await CheckCommand("Zero", 80, 50, 0m, false);
        await CheckCommand("FractionalTruncated", 80, 50, .75m, false);
        await CheckCommand("Positive", 80, 50, 3m, false);
        await CheckCommand("PartialCap", 999_999_997, 50, 5m, false);
        await CheckCommand("FullCapGold", 999_999_999, 50, 20m, true);

        // This identity sentinel exercises the actual native callback's owner check;
        // it is never installed in a run or used as a multiplayer root.
        Player otherIdentity = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
        var ownerRoot = CombatRootSnapshot.Capture(live);
        var ownerParent = ownerRoot.ForkSimulator(); var ownerChild = ownerParent.Fork();
        string ownerBefore = DescribeContinuationContractState(ownerParent, ownerRoot, player);
        string liveOwnerBefore = ContinuationStamp.CaptureLive(live).StateText;
        var nativeDragon = player.Relics.OfType<DragonFruit>().Single();
        var shadowDragon = ((SimulatedCombatState)ownerChild.State.CombatState).RelicsOf(player).OfType<DragonFruit>().Single();
        var wrongOwner = new GoldGainMirrorContext { Simulator = ownerChild, Player = otherIdentity, Amount = 20m };
        GoldGainedMirrors.AfterGain(shadowDragon, wrongOwner);
        await nativeDragon.AfterGoldGained(otherIdentity);
        if (DescribeContinuationContractState(ownerChild, ownerRoot, player) != ownerBefore
            || DescribeContinuationContractState(ownerParent, ownerRoot, player) != ownerBefore
            || ContinuationStamp.CaptureLive(live).StateText != liveOwnerBefore)
            throw new InvalidOperationException("Gold callback affected a different player identity.");
        _completedChecks.Add("MaxHpHealing:WrongOwnerNativeCallbackIdentity:NoStateChanges");

        await CreatureCmd.SetMaxHp(player.Creature, 80);
        await CreatureCmd.SetCurrentHp(player.Creature, 40);
        await InjectRelicAsync(player, new() { RelicId = "RED_SKULL" });
        await CheckCommand("RedSkullThresholdCrossing", 80, 40, 20m, true);

        // Fruit Juice must also use the HP callback, including the threshold
        // effect above; the old direct creature.Heal path did not dispatch it.
        await CreatureCmd.SetMaxHp(player.Creature, 80);
        await CreatureCmd.SetCurrentHp(player.Creature, 40);
        foreach (var potion in player.PotionSlots.ToArray()) potion?.Discard();
        var juice = InjectPotionForTest(player, "FRUIT_JUICE");
        int slot = player.GetPotionSlotIndex(juice);
        var potionRoot = CombatRootSnapshot.Capture(live);
        var potionParent = potionRoot.ForkSimulator(); var potionChild = potionParent.Fork();
        var potionCombat = (SimulatedCombatState)potionChild.State.CombatState;
        string potionBefore = DescribeContinuationContractState(potionParent, potionRoot, player);
        string potionLiveBefore = ContinuationStamp.CaptureLive(live).StateText;
        using (SimulationNotificationIsolation.Enter())
        {
            var shadowJuice = potionCombat.GetPotionAtSlot(player, slot)
                ?? throw new InvalidOperationException("Fruit Juice was not captured in its branch slot.");
            int historyStart = potionChild.History.Entries.Count;
            if (!PotionExecutionSupport.Prepare(potionChild, potionCombat, shadowJuice, slot, player.Creature)
                || !PotionExecutionSupport.Complete(potionChild, potionCombat, shadowJuice,
                    player.Creature, null, historyStart, new ForkableSet<uint>()))
                throw new InvalidOperationException("Fruit Juice unexpectedly suspended.");
            if (!CombatBeamSolver.SettleReplayActionBoundary(potionChild, potionCombat))
                throw new InvalidOperationException("Fruit Juice boundary unexpectedly suspended.");
        }
        var potionExpected = CaptureSimulated(potionChild, potionCombat, player, live.Enemies[0]);
        if (DescribeContinuationContractState(potionParent, potionRoot, player) != potionBefore
            || ContinuationStamp.CaptureLive(live).StateText != potionLiveBefore)
            throw new InvalidOperationException("Fruit Juice changed parent/live before native use.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            candidate => candidate is UsePotionAction use && ReferenceEquals(use.Player, player) && use.PotionIndex == (uint)slot,
            () => juice.EnqueueManualUse(player.Creature), deadline.Token);
        await action.CompletionTask.WaitAsync(deadline.Token);
        AssertSnapshotEqual(potionExpected, CaptureActual(live, player, live.Enemies[0]), _request.ScenarioId, "FruitJuiceHpCallback");
        _completedChecks.Add("MaxHpHealing:FruitJuiceNativeUse:RedSkullHpCallback:FullStateRng:ParentLiveIsolation");
        _writer.WriteGeneratedArtifact("max-hp-healing.json", evidence);
    }

    private async Task AssertFeedMaxHpCapAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (int maxHp in new[] { 999_999_997, 999_999_999 })
        {
            await CreatureCmd.SetMaxHp(player.Creature, maxHp);
            await CreatureCmd.SetCurrentHp(player.Creature, 50);
            await ClearPlayerPilesAsync(player);
            var nativeCard = (await InjectCardAsync(live, player,
                new() { CardId = "FEED", Pile = "Hand", TreatAsDeckCard = true })).Single();
            SetEnergy(player, 3);
            var target = live.Enemies[0];
            if (live.Enemies.Count == 1)
                await AddMonsterForTestAsync(live, target.Monster!.Id.Entry, null);
            await CreatureCmd.SetCurrentHp(target, 1);
            var root = CombatRootSnapshot.Capture(live);
            var parent = root.ForkSimulator(); var child = parent.Fork();
            var combat = (SimulatedCombatState)child.State.CombatState;
            string before = DescribeContinuationContractState(parent, root, player);
            string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            using (SimulationNotificationIsolation.Enter())
            {
                var card = child.State.GetPlayerCombatState(player).Hand.Cards.Single(card => card.Preview.Id.Entry == "FEED");
                using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                    if (!child.ManualPlay(card, target, out _))
                        throw new InvalidOperationException("Feed capped route unexpectedly suspended.");
                CombatBeamSolver.SettleReplayActionBoundary(child, combat);
            }
            int gain = 999_999_999 - maxHp;
            if (child.State.GetCreature(player.Creature).CurrentHp != 50 + gain
                || child.State.GetCreature(player.Creature).MaxHp != 999_999_999
                || combat.GrowthRewards.Feed != 1
                || DescribeContinuationContractState(parent, root, player) != before
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException("Feed healed the requested amount instead of actual max-HP gain, or lost growth/isolation.");
            var expected = CaptureSimulated(child, combat, player, target);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction play && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () => { if (!nativeCard.TryManualPlay(target)) throw new InvalidOperationException("Native capped Feed refused."); }, deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            AssertSnapshotEqual(expected, CaptureActual(live, player, target), _request.ScenarioId, $"FeedCap:{maxHp}");
            _completedChecks.Add($"MaxHpHealing:FeedNativeFatal:ActualGain={gain}:GrowthProtected:FullStateRng:ParentLiveIsolation");
        }
    }
}
