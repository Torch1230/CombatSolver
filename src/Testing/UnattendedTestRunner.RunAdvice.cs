using Godot;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
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
        foreach (CardModel[] models in new CardModel[][]
            { [ModelDb.Card<BladeDance>()], [ModelDb.Card<Shiv>(), ModelDb.Card<Shiv>()],
                [ModelDb.Card<BladeDance>(), ModelDb.Card<Shiv>()], [ModelDb.Card<DefendIronclad>()] })
        {
            PredictedCard[] predicted = models.Select(m => new PredictedCard(player.RunState.CreateCard(m, player))).ToArray();
            var strengthOnly = StrategicEffectContext.Build(predicted, 100, 0, 0, StrategicEffectRequirements.AttackHits);
            var combined = StrategicEffectContext.Build(predicted, 100, 0, 0,
                StrategicEffectRequirements.AttackHits | StrategicEffectRequirements.ShivPlays);
            AdviceAssert(strengthOnly.AttackHits == combined.ShivPlays && combined.AttackHits == combined.ShivPlays,
                "Strength and Accuracy share existing/generated Shiv estimates without double counting");
            AdviceAssert(models[0] is DefendIronclad ? strengthOnly.AttackHits == 0 : strengthOnly.AttackHits > 0,
                "native Strength context recognizes Shiv sources without inventing attacks");
        }
        _completedChecks.Add("RunAdvice:NativeStrategicStrengthShivs:Requirements:NoDoubleCount:NoAttack");
        foreach (var (model, stock) in new (CardModel, int)[]
            { (ModelDb.Card<BladeDance>(), 3), (ModelDb.Card<FanOfKnives>(), 4), (ModelDb.Card<CloakAndDagger>(), 1) })
        {
            PredictedCard[] cardsForCycles = new CardModel[] { model, ModelDb.Card<DefendIronclad>(),
                ModelDb.Card<DefendIronclad>(), ModelDb.Card<DefendIronclad>() }
                .Select(m => new PredictedCard(player.RunState.CreateCard(m, player))).ToArray();
            var forecast = StrategicEffectContext.Build(cardsForCycles, 100, 0, 0,
                StrategicEffectRequirements.AttackHits | StrategicEffectRequirements.ShivPlays);
            AdviceAssert(model is CloakAndDagger ? forecast.ShivPlays > stock : forecast.ShivPlays == stock,
                "native generator lifetime bounds repeated-cycle supply");
            AdviceAssert(forecast.AttackHits == forecast.ShivPlays, "finite source bound is shared by Strength");
        }
        _completedChecks.Add("RunAdvice:NativeFiniteShivGenerators:Exhaust:Power:Reusable");
        var bladeDance = RunAdviceCapture.Card(ModelDb.Card<BladeDance>());
        AdviceAssert(!bladeDance.Tags.HasFlag(AdviceTag.Draw), "generated cards must not be labeled as draw");
        AdviceAssert(RunAdviceCapture.Card(ModelDb.Card<ShrugItOff>()).Tags.HasFlag(AdviceTag.Draw),
            "actual card draw classification");
        foreach (var (model, countVariable) in new (CardModel, string)[]
            { (ModelDb.Card<BladeDance>(), "Cards"), (ModelDb.Card<CloakAndDagger>(), "Cards"), (ModelDb.Card<FanOfKnives>(), "Shivs") })
        {
            CardModel generator = player.RunState.CreateCard(model, player);
            for (int level = 0; level < 2; level++)
            {
                AdviceAssert(RunAdviceCapture.Card(generator).Damage == (double)(
                    ModelDb.Card<Shiv>().DynamicVars.Damage.BaseValue * generator.DynamicVars[countVariable].BaseValue),
                    $"native generated Shiv output capture: {model.Id}, upgrade={level}");
                if (level == 0) CardCmd.Upgrade(generator, CardPreviewStyle.None);
            }
        }
        _completedChecks.Add("RunAdvice:NativeShivGeneratorDamage:BaseAndUpgraded");
        CardModel[] payoffModels = [ModelDb.Card<Accuracy>(), ModelDb.Card<FeelNoPain>(),
            ModelDb.Card<Reflex>(), ModelDb.Card<Tactician>(), ModelDb.Card<Haunt>(),
            ModelDb.Card<DevourLife>(), ModelDb.Card<Defragment>(), ModelDb.Card<Accelerant>()];
        foreach (CardModel model in payoffModels)
        {
            CardModel card = player.RunState.CreateCard(model, player);
            AdviceCard beforeUpgrade = RunAdviceCapture.Card(card);
            CardCmd.Upgrade(card, CardPreviewStyle.None);
            AdviceCard afterUpgrade = RunAdviceCapture.Card(card);
            AdviceAssert(beforeUpgrade.PayoffWeight == 1 && afterUpgrade.PayoffWeight > 1,
                $"native upgraded payoff capture: {model.Id}");
        }
        _completedChecks.Add("RunAdvice:NativeUpgradeCapture:EightPayoffs");
        AdviceCard strike = new("STRIKE_IRONCLAD", 6, 0, 0, 1, true, true, false, true, AdviceTag.None);
        AdviceCard defend = strike with { Id = "DEFEND_IRONCLAD", Damage = 0, Block = 5, Attack = false };
        AdviceContext context = new(Enumerable.Repeat(strike, 5).Concat(Enumerable.Repeat(defend, 5)).ToArray(),
            new HashSet<string>(), 100, 50, 80, 0, 1);
        AdviceAssert(ModelDb.Card<Soul>().DynamicVars.Cards.BaseValue == 2,
            "reviewed unupgraded Soul supplies two cards of draw");
        foreach (CardModel model in new CardModel[] { ModelDb.Card<GraveWarden>(), ModelDb.Card<Reave>(), ModelDb.Card<Severance>() })
        {
            AdviceCard generatedDraw = RunAdviceCapture.Card(player.RunState.CreateCard(model, player));
            AdviceCard withoutSource = generatedDraw with { Roles = generatedDraw.Roles & ~AdviceRole.SoulSource };
            AdviceAssert(!generatedDraw.Tags.HasFlag(AdviceTag.Draw) && AdviceMechanics.IndirectDrawSupply(generatedDraw) > 0,
                "native Soul source stays distinct from direct draw");
            foreach (bool shopMode in new[] { false, true })
            {
                AdviceRating[] values = RunAdvice.Rank(context,
                    [new("source", AdviceKind.Card, generatedDraw.Id, Card: generatedDraw),
                     new("control", AdviceKind.Card, generatedDraw.Id, Card: withoutSource)], shopMode);
                AdviceAssert(values[0].Score > values[1].Score, "native indirect draw improves reward and equal-price shop value");
            }
            AdviceCard noDraw = RunAdviceCapture.Card(ModelDb.Card<BattleTrance>());
            AdviceAssert(AdviceMechanics.Value(context with { Deck = [generatedDraw] }, noDraw, [])
                < AdviceMechanics.Value(context with { Deck = [withoutSource] }, noDraw, []),
                "native NoDraw scoring recognizes Soul supply");
        }
        _completedChecks.Add("RunAdvice:NativeSoulDraw:IndependentValue:Shop:NoDraw:DistinctTiming");
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
                && label.MouseFilter == Control.MouseFilterEnum.Pass
                && !string.IsNullOrWhiteSpace(label.TooltipText), "native merchant slot badge and non-blocking detail tooltip");
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
            AdviceAssert(remaining[i].GetNode<Label>(RunAdviceBadge.NodeName).Text.Contains($"{expected[i].Score:F1}"),
                "remaining scores refreshed using new budget and deck");
        }
        _completedChecks.Add("RunAdvice:NativePurchase:Gold:Deck:SoldBadge:RemainingScoresUpdated");

        CardCreationResult[] cards = [new(player.RunState.CreateCard(ModelDb.Card<StrikeIronclad>(), player)),
            new(player.RunState.CreateCard(ModelDb.Card<DefendIronclad>(), player)),
            new(player.RunState.CreateCard(ModelDb.Card<ShrugItOff>(), player))];
        CardRewardAlternative[] alternatives = [new("Skip", PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward)];
        var screen = NCardRewardSelectionScreen.ShowScreen(cards, alternatives)!;
        await NextFrameAsync();
        foreach (var card in cards)
            AdviceAssert(screen.GetCardHolder(card.Card).GetNodeOrNull<Label>(RunAdviceBadge.NodeName) is not null,
                "native reward patch attaches each badge");
        AdviceCard accuracy = RunAdviceCapture.Card(ModelDb.Card<Accuracy>()) with { PayoffWeight = 1.5 };
        var profile = DeckMechanismProfile.Capture(context with
            { Deck = [bladeDance, accuracy, RunAdviceCapture.Card(ModelDb.Card<GraveWarden>())] });
        RunAdviceBadge.Summary(screen, "尚未识别成型配合", reward: true, profile: profile);
        string language = LocManager.Instance.Language;
        try
        {
            foreach (string locale in new[] { "eng", "zhs", "zht" })
            {
                LocManager.Instance.SetLanguage(locale);
                await NextFrameAsync();
                await NextFrameAsync();
                string text = screen.GetCardHolder(cards[0].Card).GetNode<Label>(RunAdviceBadge.NodeName).Text;
                AdviceAssert(text.Contains(locale == "eng" ? "Estimate " : "参考分 "), "live label language refresh");
                Label summary = screen.GetNode<Label>("CombatSolverAdviceSummary");
                AdviceAssert(summary.TooltipText.Contains(SolverText.Format($"供给权重 {profile.Mechanisms[5].Balance.Supply:F1} / 兑现权重 {1.5:F1}")),
                    "profile tooltip preserves fractional payoff and refreshes language");
                AdviceAssert(profile.IndirectDrawSupply > 0 && summary.TooltipText.Contains(
                    SolverText.Format($"间接抽牌供给权重 {profile.IndirectDrawSupply:F1}")),
                    "profile tooltip localizes native indirect draw supply");
                AdviceAssert(summary.Text.Contains(SolverText.Format($"配合指数 {100 * profile.Mechanisms[5].Balance.Readiness:F1}")),
                    "profile summary uses the scoring readiness");
                AdviceAssert(summary.MouseFilter == Control.MouseFilterEnum.Pass,
                    "profile tooltip is reachable without stopping parent input");
                foreach (var rating in poor.Concat(good).Concat(unavailable).Concat(removal).Concat(generic))
                    foreach (string reason in rating.Reasons) _ = SolverText.Get(reason);
            }
            screen.RefreshOptions(cards.Reverse().ToArray(), alternatives);
            await NextFrameAsync();
            AdviceAssert(screen.GetNode<Control>("UI/CardRow").GetChildren()
                .OfType<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder>()
                .Count(c => !c.IsQueuedForDeletion()) == 3, "reward reroll has no stale holders");
            AdviceAssert(screen.GetChildren().OfType<Label>().Count(c => c.Name == "CombatSolverAdviceSummary") == 1,
                "reward reroll replaces the profile summary");
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
