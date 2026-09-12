using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task PrepareCompactCardHooksAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, 1, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetMaxHp(combat.Enemies.Single(), 10_000);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 10_000);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DANSE_MACABRE", Pile = "Hand", UpgradeLevels = mode == 1 ? 1 : 0 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SPIRIT_OF_ASH", Pile = "Hand", UpgradeLevels = mode == 1 ? 1 : 0,
            EnchantmentId = "SWIFT", EnchantmentAmount = 2 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SCULPTING_STRIKE", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Hand", UpgradeLevels = 1,
            EnchantmentId = "SWIFT", EnchantmentAmount = 2 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "GRAVEBLAST", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "MALAISE", Pile = "Hand", EnchantmentId = "SWIFT", EnchantmentAmount = 2 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "PREPARED", Pile = "Hand", UpgradeLevels = 1 });
        var hand = player.PlayerCombatState!.Hand.Cards;
        hand[1].EnergyCost._base = 2;
        hand[3].EnergyCost._base = 2;
        CardCmd.ApplyKeyword(hand[1], CardKeyword.Ethereal);
        CardCmd.ApplyKeyword(hand[5], CardKeyword.Ethereal, CardKeyword.Retain);
        CardCmd.ApplyKeyword(hand[6], CardKeyword.Ethereal);
        if (mode == 2) hand[5].Enchantment!.Status = EnchantmentStatus.Disabled;
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Draw", EnchantmentId = "SLITHER" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Discard" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFY", Pile = "Discard" });
        var choice = new BlockingPlayerChoiceContext();
        if (mode != 2) await PowerCmd.Apply<VeilpiercerPower>(choice, player.Creature, 1, player.Creature, null);
        if (mode != 1)
        {
            await PowerCmd.Apply<DanseMacabrePower>(choice, player.Creature, 7, player.Creature, null);
            await PowerCmd.Apply<SpiritOfAshPower>(choice, player.Creature, 3, player.Creature, null);
        }
        if (mode == 2) await PowerCmd.Apply<VeilpiercerPower>(choice, player.Creature, 1, player.Creature, null);
        await PowerCmd.Apply<DexterityPower>(choice, player.Creature, 13, player.Creature, null);
        await PowerCmd.Apply<FrailPower>(choice, player.Creature, 2, player.Creature, null);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers)) power.AmountOnTurnStart = 37;
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactCardHooksAsync(CombatState combat, Player player)
    {
        for (int mode = 0; mode < 3; mode++)
        {
            await PrepareCompactCardHooksAsync(combat, player, mode);
            var spirit = player.PlayerCombatState!.Hand.Cards[1];
            int nativeSwiftChoices = 0;
            (string Id, int? Upgrade)[] steps = [("DANSE_MACABRE", mode == 1 ? 1 : 0), ("SPIRIT_OF_ASH", mode == 1 ? 1 : 0),
                ("SCULPTING_STRIKE", 0), ("DEFEND_NECROBINDER", 1), ("GRAVEBLAST", 0), ("DEFEND_NECROBINDER", 1), ("MALAISE", 0)];
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactCardHooks", "compact-card-hooks", steps,
                (lane, before, step) =>
                {
                    if (step >= steps.Length) return;
                    var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                    if (step == 1)
                    {
                        int expected = mode switch { 0 => 14, 1 => 6, _ => 3 };
                        if (lane.Block != before.Block + expected || lane.Energy != before.Energy
                            || !lane.EnchantmentDisabled(1) || !lane.CardRemoved(1))
                            throw new InvalidOperationException("Before-card order, unpowered block or one-shot Power enchantment differs.");
                    }
                    if (step is 3 or 5)
                    {
                        int spiritBlock = lane.Power(Enumerable.Range(0, lane.PowerCount).Single(index => lane.PowerDefinition(index).Kind == BasicPowerKind.SpiritOfAsh)).Amount;
                        int danseBlock = mode == 1 ? 6 : 11;
                        int cardBlock = (int)((before.Definition(3).Effects[0].Amount + 13) * 0.75m);
                        if (lane.Block != before.Block + spiritBlock + danseBlock + cardBlock || !lane.EnchantmentDisabled(3)
                            || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.EnchantmentStart) != (step == 3 ? 1 : 0))
                            throw new InvalidOperationException("Repeated Swift play or unpowered block incorrectly used Dexterity/Frail.");
                    }
                    if (step == 6 && (!lane.EnchantmentDisabled(5) || lane.CapturedX(5) != before.Energy || lane.Energy != 0
                        || !lane.IsEthereal(5) || !lane.IsRetained(5)
                        || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.EnchantmentStart) != (mode == 2 ? 0 : 1)))
                        throw new InvalidOperationException("X payment lost keyword/one-shot state or re-enabled a captured disabled enchantment.");
                }, verifyEnergyCosts: true, forceOpeningPower: true, chooseBranch: (action, step) => step is not (2 or 4)
                    || action.GetActionChoicesInExecutionOrder().SelectMany(choice => choice.Cards)
                        .Any(token => token.CardId == "DEFEND_NECROBINDER" && token.UpgradeLevel == 1),
                observeNativeChoice: (action, values) =>
                {
                    if (action.CardId != "SPIRIT_OF_ASH") return false;
                    if (spirit.Enchantment is not Swift { Status: EnchantmentStatus.Disabled }
                        || player.Creature.GetPowerAmount<SpiritOfAshPower>() != (mode == 1 ? 5 : 7)
                        || player.Creature.Block != values.Block)
                        throw new InvalidOperationException("Native Swift selector opened before its owner's Power or before-card effects completed.");
                    nativeSwiftChoices++;
                    return true;
                });
            if (nativeSwiftChoices == 0) throw new InvalidOperationException("Native Swift shuffle selection was not observed.");
            _completedChecks.Add($"CompactCardHooks:Mode{mode}:BeforeCardOrder:UnpoweredBlock:NewAndStackedPowers:SwiftOnce:XAndKeywords:TwoRounds:NativeSwiftChoices{nativeSwiftChoices}");
        }
    }

    private async Task AssertCompactSwiftAutoAsync(CombatState combat, Player player)
    {
        await PrepareCompactOstyAsync(combat, player, 1, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "ACROBATICS", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "PREPARED", Pile = "Hand", UpgradeLevels = 1,
            EnchantmentId = "SWIFT", EnchantmentAmount = 2 });
        var prepared = player.PlayerCombatState!.Hand.Cards[1];
        prepared.EnergyCost._base = 2;
        CardCmd.ApplyKeyword(prepared, CardKeyword.Ethereal);
        for (int i = 0; i < 2; i++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Draw" });
        await PowerCmd.Apply<SpiritOfAshPower>(new BlockingPlayerChoiceContext(), player.Creature, 3, player.Creature, null);
        await PowerCmd.Apply<DanseMacabrePower>(new BlockingPlayerChoiceContext(), player.Creature, 7, player.Creature, null);
        await AssertCompactPetCardRouteAsync(combat, player, 3, "CompactSwiftAuto", "compact-swift-auto", [("ACROBATICS", 0)],
            (lane, before, step) =>
            {
                if (step != 0) return;
                var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                if (!events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.Start && item.Card == 1)) return;
                if (!lane.EnchantmentDisabled(1) || lane.Block != before.Block + 10 || lane.Energy != before.Energy - 1
                    || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.EnchantmentStart) != 1
                    || !events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.Start && item.Card == 1 && item.Automatic))
                    throw new InvalidOperationException("Nested Sly/Swift repeated before-card effects, payment or one-shot activation.");
            }, chooseBranch: (action, step) => step != 0 || action.GetActionChoicesInExecutionOrder()
                .SelectMany(choice => choice.Cards).Any(token => token.CardId == "PREPARED"));
        _completedChecks.Add("CompactSwiftAuto:NativeSly:OwnDiscardThenEnchantmentDraw:NestedShuffleChoice:TwoRounds");
    }

    private async Task AssertCompactSwiftEmptyAsync(CombatState combat, Player player)
    {
        await PrepareCompactOstyAsync(combat, player, 1);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SOUL", Pile = "Hand", EnchantmentId = "SWIFT", EnchantmentAmount = 2 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "MALAISE", Pile = "Hand", EnchantmentId = "SWIFT", EnchantmentAmount = 0 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Hand", UpgradeLevels = 1,
            EnchantmentId = "SWIFT", EnchantmentAmount = 2 });
        player.PlayerCombatState!.Hand.Cards[2].Enchantment!.Status = EnchantmentStatus.Disabled;
        player.PlayerCombatState.Hand.Cards[2].EnergyCost._base = 0;
        for (int index = 0; index < 7; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Hand" });
        for (int index = 0; index < 3; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Draw" });
        await AssertCompactPetCardRouteAsync(combat, player, 4, "CompactSwiftEmpty", "compact-swift-empty",
            [("SOUL", 0), ("MALAISE", 0), ("DEFEND_NECROBINDER", 1)], (lane, before, step) =>
            {
                var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                if (!lane.EnchantmentDisabled(step)
                    || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.Draw) != (step == 0 ? 1 : 0)
                    || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.EnchantmentStart) != (step == 2 ? 0 : 1)
                    || events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.Shuffle))
                    throw new InvalidOperationException("Full hand, zero draw amount or captured disabled Swift changed one-shot semantics.");
            }, rounds: 0, requirePending: false);
        _completedChecks.Add("CompactSwiftEmpty:NativeFullHand:ZeroAmount:CapturedDisabled:NoExtraDrawOrShuffle");
    }

    private async Task AssertCompactSwiftTerminalAsync(CombatState combat, Player player)
    {
        await PrepareCompactOstyAsync(combat, player, 1, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_NECROBINDER", Pile = "Hand", EnchantmentId = "SWIFT", EnchantmentAmount = 2 });
        var strike = player.PlayerCombatState!.Hand.Cards.Single();
        strike.EnergyCost._base = 2;
        CardCmd.ApplyKeyword(strike, CardKeyword.Ethereal);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Discard" });
        var choice = new BlockingPlayerChoiceContext();
        await PowerCmd.Apply<VeilpiercerPower>(choice, player.Creature, 1, player.Creature, null);
        await PowerCmd.Apply<SpiritOfAshPower>(choice, player.Creature, 5, player.Creature, null);
        await PowerCmd.Apply<DanseMacabrePower>(choice, player.Creature, 7, player.Creature, null);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 1);
        await SetBlockAsync(combat.Enemies.Single(), 0);
        await AssertCompactPetCardRouteAsync(combat, player, 5, "CompactSwiftTerminal", "compact-swift-terminal", [("STRIKE_NECROBINDER", 0)],
            (lane, before, _) =>
            {
                var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                if (!lane.Terminal || !lane.EnchantmentDisabled(0) || lane.Block != before.Block + 12 || lane.Energy != before.Energy
                    || events.Any(item => item.Kind is ResumableDiscardProgram.EventKind.Draw or ResumableDiscardProgram.EventKind.Shuffle))
                    throw new InvalidOperationException("Last kill must preserve before-card block and disable Swift without drawing or shuffling.");
            }, rounds: 0, requirePending: false, verifyEnergyCosts: true);
        if (strike.Enchantment is not Swift { Status: EnchantmentStatus.Disabled })
            throw new InvalidOperationException("Native last-kill Swift was not consumed.");
        _completedChecks.Add("CompactSwiftTerminal:NativeBeforeTeardown:BeforeCardBlock:FreePayment:EnchantmentDisabledAfterKill:DrawRejected");
    }

    private async Task AssertDanseResolvedNativeAsync(CombatState combat, Player player)
    {
        await PrepareCompactOstyAsync(combat, player, 1);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFILE", Pile = "Hand" });
        var card = player.PlayerCombatState!.Hand.Cards.Single();
        card.EnergyCost._base = 2;
        var choice = new BlockingPlayerChoiceContext();
        // Veil is visited first. Its last free-play charge disappears before Danse
        // queries the now-current resolved cost, while payment remains zero.
        await PowerCmd.Apply<VeilpiercerPower>(choice, player.Creature, 1, player.Creature, null);
        await PowerCmd.Apply<DanseMacabrePower>(choice, player.Creature, 7, player.Creature, null);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var enemy = combat.Enemies.Single();
        var root = CombatRootSnapshot.Capture(combat);
        MoveStateSnapshot predicted;
        using (SimulationNotificationIsolation.Enter())
        {
            var simulator = root.ForkSimulator();
            if (!simulator.ManualPlay(simulator.State.FindCard(card)!, enemy, out _))
                throw new InvalidOperationException("Danse resolved-cost oracle unexpectedly suspended.");
            predicted = CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, enemy);
        }
        if (!card.TryManualPlay(enemy)) throw new InvalidOperationException("Native Danse resolved-cost play was rejected.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var actual = CaptureActual(combat, player, enemy);
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "danse-resolved-native.json"),
                JsonSerializer.Serialize(new { predicted, actual }, new JsonSerializerOptions { WriteIndented = true }));
        }
        AssertSnapshotEqual(actual, predicted, "DanseResolved", "Native");
        if (player.Creature.Block != 107 || player.PlayerCombatState.Energy != 20)
            throw new InvalidOperationException("Danse must gain unpowered block from the resolved cost after a free payment.");
        _completedChecks.Add("DanseResolved:NativeFullState:FreePayment:VeilRetiresBeforeCostQuery:SevenUnpoweredBlock");
    }
}
