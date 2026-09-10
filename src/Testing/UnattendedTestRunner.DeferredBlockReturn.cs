using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertDeferredBlockReturnAsync(CombatState combat, Player player, bool compact = false)
    {
        var enemy = combat.Enemies.First();
        List<object> evidence = [];
        for (int mode = 0; mode < 3; mode++)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
            ClearRunDeck((RunState)combat.RunState, player);
            await ClearPlayerPilesAsync(player);
            await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = "DODGE_AND_ROLL", UpgradeLevels = mode == 1 ? 0 : 1, Pile = "Hand" });
            if (mode == 1) await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = "DODGE_AND_ROLL", UpgradeLevels = 1, Pile = "Hand" });
            if (compact)
                foreach (int upgrade in new[] { 0, 1 })
                    await InjectCardAsync(combat, player, new UnattendedCardInjection
                        { CardId = "TOOLS_OF_THE_TRADE", UpgradeLevels = upgrade, Pile = "Hand" });
            await PowerCmd.Apply<DexterityPower>(new BlockingPlayerChoiceContext(), player.Creature, mode == 2 ? -6 : -1, player.Creature, null);
            await PowerCmd.Apply<FrailPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
            if (mode == 1) await PowerCmd.Apply<BlockNextTurnPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
            await SetBlockAsync(player.Creature, mode == 0 ? 999_999_998 : 3);
            SetEnergy(player, 5); SetStars(player, 0);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var cards = player.PlayerCombatState!.Hand.Cards.ToArray();
            var captured = CombatRootSnapshot.Capture(combat);
            var root = captured.ForkSimulator();
            if (compact) await AssertCompactDeferredPowerRootAsync(captured, root, combat, player, cards, mode);
            var prediction = root.Fork();
            var shadow = (SimulatedCombatState)prediction.State.CombatState;
            List<MoveStateSnapshot> expected = [];
            List<string[]> expectedPowers = [];
            using (SimulationNotificationIsolation.Enter())
            {
                foreach (var card in cards)
                {
                    if (!prediction.ManualPlay(prediction.State.FindCard(card)!, null, out _))
                        throw new InvalidOperationException("Deferred block return unexpectedly suspended.");
                    expected.Add(CaptureSimulated(prediction, shadow, player, enemy));
                    expectedPowers.Add(CompactPowerValues(shadow.EffectivePowers()));
                }
                var child = prediction.Fork();
                var childCombat = (SimulatedCombatState)child.State.CombatState;
                var childPlayer = child.State.GetCreature(player.Creature);
                childPlayer.DamageBlock(childPlayer.Block, ValueProp.Unpowered);
                if (!CorePowerSupport.TriggerAfterBlockCleared(child, childCombat, player.Creature))
                    throw new InvalidOperationException("Deferred block lifecycle unexpectedly suspended.");
                expected.Add(CaptureSimulated(child, childCombat, player, enemy));
                expectedPowers.Add(CompactPowerValues(childCombat.EffectivePowers()));
                AssertSnapshotEqual(expected[^2], CaptureSimulated(prediction, shadow, player, enemy), "DeferredBlockReturn", "ParentAfterChildClear");
                AssertSnapshotEqual(CaptureActual(combat, player, enemy), CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy),
                    "DeferredBlockReturn", "RootUnchanged");
            }
            for (int index = 0; index < cards.Length; index++)
            {
                if (!cards[index].TryManualPlay(null)) throw new InvalidOperationException("Native deferred block card rejected.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                int expectedAmount = mode switch { 0 => 3, 1 => index == 0 ? 4 : 7, _ => 0 };
                if (player.Creature.GetPowerAmount<BlockNextTurnPower>() != expectedAmount)
                    throw new InvalidOperationException("Native deferred block did not exercise the expected return-value boundary.");
                AssertSnapshotEqual(expected[index], CaptureActual(combat, player, enemy), "DeferredBlockReturn", $"Mode{mode}-Card{index}");
                if (!expectedPowers[index].SequenceEqual(CompactPowerValues(combat.Creatures.SelectMany(creature => creature.Powers))))
                    throw new InvalidOperationException("Deferred block Power metadata differs.");
            }
            await SetBlockAsync(player.Creature, 0);
            await Hook.AfterBlockCleared(combat, player.Creature);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(expected[^1], CaptureActual(combat, player, enemy), "DeferredBlockReturn", $"Mode{mode}-AfterBlockClear");
            if (!expectedPowers[^1].SequenceEqual(CompactPowerValues(combat.Creatures.SelectMany(creature => creature.Powers))))
                throw new InvalidOperationException("Deferred block clear retained Power state.");
            evidence.Add(new { mode, nativeCards = cards.Length, finalBlock = player.Creature.Block,
                powerRemoved = !player.Creature.HasPower<BlockNextTurnPower>() });
        }
        _completedChecks.Add("DeferredBlockReturn:Native3Roots:CapAndFraction:Stacking:Zero:AllSnapshotFields:PowerMetadata:ForkIsolation:AfterBlockCleared");
        if (compact) _completedChecks.Add("CompactDeferredPowers:Native3Roots10Actions:BlockReturnAndToolsCounter:Removal:AllSnapshotProperties:HistorySources:AllRng:Rollback:Frozen8Workers:LegacyAfterBlockCleared");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "deferred-block-return.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
