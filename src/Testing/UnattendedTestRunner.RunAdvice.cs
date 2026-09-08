using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AdviceAssert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("RunAdvice: " + message);
    }

    private async Task AssertRunAdviceAsync(Player player)
    {
        var bladeDance = RunAdviceCapture.Card(ModelDb.Card<BladeDance>());
        AdviceAssert(!bladeDance.Tags.HasFlag(AdviceTag.Draw), "generated cards must not be labeled as draw");
        AdviceAssert(RunAdviceCapture.Card(ModelDb.Card<ShrugItOff>()).Tags.HasFlag(AdviceTag.Draw),
            "actual card draw classification");
        AdviceCard strike = new("STRIKE_IRONCLAD", 6, 0, 0, 1, true, true, false, true, AdviceTag.None);
        AdviceCard defend = strike with { Id = "DEFEND_IRONCLAD", Damage = 0, Block = 5, Attack = false };
        AdviceContext context = new(Enumerable.Repeat(strike, 5).Concat(Enumerable.Repeat(defend, 5)).ToArray(),
            new HashSet<string>(), 100, 50, 80, 0, 1);
        AdviceOffer skip = new("skip", AdviceKind.Skip, "skip");
        var poor = RunAdvice.Rank(context, [new("basic", AdviceKind.Card, strike.Id, Card: strike), skip], false);
        AdviceAssert(poor[1].Rank == 1 && poor[0].Rank > 1, "skip must beat redundant basics");
        AdviceCard draw = new("DRAW", 0, 0, 2, 1, false, false, false, true, AdviceTag.Draw);
        var good = RunAdvice.Rank(context, [new("draw", AdviceKind.Card, draw.Id, Card: draw), skip], false);
        AdviceAssert(good[0].Rank == 1, "useful draw should beat skipping");
        AdviceOffer[] shopOffers = [new("expensive", AdviceKind.Card, draw.Id, 101, draw),
            new("potion", AdviceKind.Potion, "BLOCK_POTION", 30),
            new("remove", AdviceKind.Removal, "remove", 75), skip];
        var unavailable = RunAdvice.Rank(context with { EmptyPotionSlots = 0 }, shopOffers, true);
        AdviceAssert(!unavailable[0].Available && !unavailable[1].Available, "budget and potion capacity");
        AdviceCard curse = strike with { Id = "CURSE", Curse = true, Basic = false, Damage = 0 };
        var removal = RunAdvice.Rank(context with { Deck = [.. context.Deck, curse] }, shopOffers, true);
        AdviceAssert(removal[2].RemovalCardId == curse.Id && removal[2].Rank == 1, "curse removal priority");
        var eternal = RunAdvice.Rank(context with { Deck = [curse with { Removable = false }] }, shopOffers, true);
        AdviceAssert(!eternal[2].Available, "eternal cards must not be offered for removal");
        var unknown = RunAdvice.Rank(context, [new("mod", AdviceKind.Relic, "UNREGISTERED"), skip], true);
        AdviceAssert(!unknown[0].Known && unknown[0].Rank == 0, "unknown effect must not invent a rating");
        var generic = RunAdvice.Rank(context, [new("vanilla", AdviceKind.Relic, "NEW_RELIC", FallbackValue: 18), skip], true);
        AdviceAssert(!generic[0].Known && generic[0].Rank == 1, "generic rating must remain visibly uncertain");
        _completedChecks.Add("RunAdvice:Skip:Draw:Budget:PotionCapacity:CurseRemoval:Eternal:Unknown:GenericConfidence");

        AdviceAssert(RunAdvicePresentation.CurrentPlayer() is null, "reward advisor must stay out of combat");
        CombatManager.Instance.Reset(graceful: false);
        await RunManager.Instance.EnterRoomDebug(RoomType.Shop, MapPointType.Unassigned);
        await NextFrameAsync();
        var shop = NMerchantRoom.Instance!.Inventory;
        player.Gold = 500;
        shop.Open();
        await NextFrameAsync();
        await NextFrameAsync();
        foreach (var slot in shop.GetAllSlots().Where(s => s.Entry.IsStocked))
            AdviceAssert(slot.GetNodeOrNull<Label>(RunAdviceBadge.NodeName) is { } label
                && !string.IsNullOrWhiteSpace(label.Text)
                && label.MouseFilter == Control.MouseFilterEnum.Ignore, "native merchant slot badge");
        _completedChecks.Add("RunAdvice:NativeShop:Cards:Relics:Potions:Removal:NonBlockingLabels");
        if (DisplayServer.GetName() != "headless")
        {
            await Task.Delay(1100);
            await NextFrameAsync();
            _host.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(OS.GetUserDataDir(), "advice-shop.png"));
        }
        var boughtSlot = shop.GetAllSlots().First(s => s.Entry is MerchantCardEntry && s.Entry.IsStocked);
        int oldGold = player.Gold;
        int oldDeck = player.Deck.Cards.Count;
        AdviceAssert(await boughtSlot.Entry.OnTryPurchaseWrapper(shop.Inventory), "native card purchase");
        await NextFrameAsync();
        await NextFrameAsync();
        AdviceAssert(player.Gold < oldGold && player.Deck.Cards.Count == oldDeck + 1
            && boughtSlot.GetNodeOrNull<Label>(RunAdviceBadge.NodeName) is null, "sold badge cleared after purchase");
        AdviceContext after = RunAdviceCapture.Capture(player);
        var remaining = shop.GetAllSlots().ToArray();
        var expected = RunAdvice.Rank(after, remaining.Select((s, i) => RunAdviceCapture.Offer(s.Entry, i))
            .Append(new AdviceOffer("save", AdviceKind.Skip, "save")).ToArray(), true);
        for (int i = 0; i < remaining.Length; i++)
        {
            if (!remaining[i].Entry.IsStocked || !expected[i].Available || expected[i].Rank == 0) continue;
            AdviceAssert(remaining[i].GetNode<Label>(RunAdviceBadge.NodeName).Text.Contains($"#{expected[i].Rank}"),
                "remaining rankings refreshed using new budget and deck");
        }
        _completedChecks.Add("RunAdvice:NativePurchase:Gold:Deck:SoldBadge:RemainingRanksUpdated");

        CardCreationResult[] cards = [new(player.RunState.CreateCard(ModelDb.Card<StrikeIronclad>(), player)),
            new(player.RunState.CreateCard(ModelDb.Card<DefendIronclad>(), player)),
            new(player.RunState.CreateCard(ModelDb.Card<ShrugItOff>(), player))];
        CardRewardAlternative[] alternatives = [new("Skip", PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward)];
        var screen = NCardRewardSelectionScreen.ShowScreen(cards, alternatives)!;
        await NextFrameAsync();
        foreach (var card in cards)
            AdviceAssert(screen.GetCardHolder(card.Card).GetNodeOrNull<Label>(RunAdviceBadge.NodeName) is not null,
                "native reward patch attaches each badge");
        string language = LocManager.Instance.Language;
        try
        {
            foreach (string locale in new[] { "eng", "zhs", "zht" })
            {
                LocManager.Instance.SetLanguage(locale);
                await NextFrameAsync();
                await NextFrameAsync();
                string text = screen.GetCardHolder(cards[0].Card).GetNode<Label>(RunAdviceBadge.NodeName).Text;
                AdviceAssert(text.Contains(locale == "eng" ? "Pick #" : "推荐 #"), "live label language refresh");
                foreach (var rating in poor.Concat(good).Concat(unavailable).Concat(removal).Concat(generic))
                    foreach (string reason in rating.Reasons) _ = SolverText.Get(reason);
            }
            screen.RefreshOptions(cards.Reverse().ToArray(), alternatives);
            await NextFrameAsync();
            AdviceAssert(screen.GetNode<Control>("UI/CardRow").GetChildren()
                .OfType<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder>()
                .Count(c => !c.IsQueuedForDeletion()) == 3, "reward reroll has no stale holders");
            if (DisplayServer.GetName() != "headless")
            {
                await Task.Delay(1100);
                await NextFrameAsync();
                _host.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(OS.GetUserDataDir(), "advice-reward.png"));
            }
        }
        finally
        {
            NOverlayStack.Instance!.Remove(screen);
            LocManager.Instance.SetLanguage(language);
        }
        _completedChecks.Add("RunAdvice:NativeReward:Reroll:LiveLocaleEngZhsZht:SkipAlternative");
    }
}
