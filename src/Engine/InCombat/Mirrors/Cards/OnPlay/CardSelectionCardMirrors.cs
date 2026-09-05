using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class CardSelectionCardMirrors
{
    public static void AnointedOnPlay(Anointed card, CardOnPlayMirrorContext context)
    {
        int count = context.Simulator.GetMaxHandSize(card.Owner) - context.OwnerState.Hand.Cards.Count;
        if (count <= 0)
        {
            return;
        }

        var cardsToAdd = context.OwnerState.DrawPile.Cards
            .Where(predictedCard => predictedCard.Preview.Rarity is CardRarity.Rare)
            .TakeRandom(count, context.Rng.CombatCardSelection)
            .ToList();
        if (cardsToAdd.Count == 0)
        {
            return;
        }

        context.Simulator.History.CardsSelected(cardsToAdd);
        context.Simulator.AddToPile(cardsToAdd, PileType.Hand);
    }

    public static void BeatDownOnPlay(BeatDown card, CardOnPlayMirrorContext context)
    {
        var selectedCards = context.OwnerState.DiscardPile.Cards
            .Where(predictedCard =>
                predictedCard.Preview.Type == CardType.Attack &&
                !predictedCard.HasKeyword(context.State, CardKeyword.Unplayable))
            .ToList()
            .StableShuffle(context.Rng.Shuffle)
            .Take(card.DynamicVars.Cards.IntValue)
            .ToList();
        if (selectedCards.Count == 0)
        {
            return;
        }

        context.Simulator.History.CardsSelected(selectedCards);

        foreach (var selectedCard in selectedCards)
        {
            if (context.Simulator.IsOverOrEnding)
            {
                break;
            }

            Creature? target = null;
            if (selectedCard.Preview.TargetType == TargetType.AnyEnemy)
            {
                // BeatDown.OnPlay resolves this target before CardCmd.AutoPlay checks whether the
                // selected card can play, so preserve that CombatTargets RNG consumption order.
                target = context.Rng.CombatTargets.NextItem(context.State.HittableEnemies);
            }

            context.Simulator.AutoPlay(selectedCard, target, nestedChoiceSourceId: card.Id.Entry);
            if (context.Simulator.HasPendingChoice)
            {
                break;
            }
        }
    }

    public static void CatastropheOnPlay(Catastrophe card, CardOnPlayMirrorContext context)
    {
        for (var i = 0; i < card.DynamicVars.Cards.IntValue; i++)
        {
            var drawPileCards = context.OwnerState.DrawPile.Cards;
            List<PredictedCard> eligibleCards = drawPileCards
                .Where(predictedCard =>
                    !predictedCard.HasKeyword(context.State, CardKeyword.Unplayable))
                .ToList();
            CombatBeamSolver.StableShuffleProjection(eligibleCards, context.Rng.Shuffle);
            var selectedCard = eligibleCards.FirstOrDefault();

            if (selectedCard is null)
            {
                List<PredictedCard> fallbackCards = drawPileCards.ToList();
                CombatBeamSolver.StableShuffleProjection(fallbackCards, context.Rng.Shuffle);
                selectedCard = fallbackCards.FirstOrDefault();
            }

            if (selectedCard is null)
            {
                break;
            }

            context.Simulator.History.CardsSelected([selectedCard]);
            context.Simulator.AutoPlay(selectedCard, nestedChoiceSourceId: card.Id.Entry);
            if (context.Simulator.HasPendingChoice)
            {
                break;
            }
        }
    }

    public static void CinderOnPlay(Cinder card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;

        if (SelectRandomHandCard(context, static _ => true) is { } selectedCard)
        {
            context.Simulator.History.CardsSelected([selectedCard]);
            context.Simulator.Exhaust(selectedCard);
        }
    }

    public static void DrainPowerOnPlay(DrainPower card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;

        var cardsToUpgrade = context.OwnerState.DiscardPile.Cards
            .Where(predictedCard => predictedCard.Preview.IsUpgradable)
            .TakeRandom(card.DynamicVars.Cards.IntValue, context.Rng.CombatCardSelection)
            .ToList();
        if (cardsToUpgrade.Count == 0)
        {
            return;
        }

        foreach (var cardToUpgrade in cardsToUpgrade)
        {
            context.Simulator.Upgrade(cardToUpgrade);
        }

        context.Simulator.History.CardsSelected(cardsToUpgrade);
    }

    public static void HiddenGemOnPlay(HiddenGem card, CardOnPlayMirrorContext context)
    {
        var drawPile = context.OwnerState.DrawPile;
        if (drawPile.IsEmpty)
        {
            return;
        }

        var eligibleCards = drawPile.Cards
            .Where(predictedCard =>
                !predictedCard.HasKeyword(context.State, CardKeyword.Unplayable) &&
                predictedCard.Preview.Type is not CardType.Status and not CardType.Curse &&
                predictedCard.Preview.GetEnchantedReplayCount() < 1)
            .ToList();
        var preferredCards = eligibleCards
            .Where(predictedCard =>
                predictedCard.Preview.Type is CardType.Attack or CardType.Skill or CardType.Power)
            .ToList();

        var selectedCard = context.Rng.CombatCardSelection.NextItem(
            preferredCards.Count == 0 ? eligibleCards : preferredCards);
        if (selectedCard is null)
        {
            return;
        }

        selectedCard.MutablePreview.BaseReplayCount += card.DynamicVars["Replay"].IntValue;
        context.Simulator.History.CardsSelected([selectedCard]);
    }

    public static void SeekerStrikeOnPlay(SeekerStrike card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
    }

    public static void ThrashOnPlay(Thrash card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle(hitCount: 2);
        if (context.Simulator.HasPendingChoice)
            return;

        var cardToExhaust = SelectRandomHandCard(context, cardModel => cardModel.Type == CardType.Attack);
        if (cardToExhaust is null)
        {
            return;
        }

        context.Simulator.History.CardsSelected([cardToExhaust]);

        var damage = 0m;
        var dynamicVars = cardToExhaust.Preview.DynamicVars;
        if (dynamicVars.ContainsKey("CalculatedDamage"))
        {
            damage = dynamicVars.CalculatedDamage.InvokeCalculate(context.Simulator, cardToExhaust, null);
        }
        else if (dynamicVars.ContainsKey("Damage"))
        {
            damage = dynamicVars.Damage.BaseValue;
        }
        else if (dynamicVars.ContainsKey("OstyDamage"))
        {
            damage = dynamicVars.OstyDamage.BaseValue;
        }
        else
        {
            EngineDiagnostics.Warn(
                $"Exhausted attack card {cardToExhaust.Preview.Id.Entry} did not have an appropriate DamageVar");
        }

        damage = HookMirrors.ModifyDamage(
            context.Simulator,
            target: null,
            dealer: cardToExhaust.Preview.Owner.Creature,
            damage,
            ValueProp.Move,
            cardSource: cardToExhaust,
            cardPlay: null);

        card.DynamicVars.Damage.BaseValue += damage;
        card.ExtraDamage += damage;

        context.Simulator.Exhaust(cardToExhaust);
    }

    public static void TrueGritOnPlay(TrueGrit card, CardOnPlayMirrorContext context)
    {
        context.GainBlock(card.Owner.Creature);
        if (context.Simulator.HasPendingChoice)
            return;

        if (card.IsUpgraded)
        {
            if (!context.OwnerState.Hand.IsEmpty)
            {
                // Vanilla asks the player which hand card to exhaust. The choice and resulting
                // pile state cannot be determined during prediction.
                context.History.RecordRisk(PredictionRiskReason.UnresolvedPlayerChoice);
            }
            return;
        }

        if (SelectRandomHandCard(context, static _ => true) is { } selectedCard)
        {
            context.Simulator.History.CardsSelected([selectedCard]);
            context.Simulator.Exhaust(selectedCard);
        }
    }

    public static void UproarOnPlay(Uproar card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle(hitCount: 2);
        if (context.Simulator.HasPendingChoice)
            return;

        var attackCards = context.OwnerState.DrawPile.Cards
            .Where(predictedCard => predictedCard.Preview.Type == CardType.Attack)
            .ToList();

        var selectedCard = attackCards
            .Where(predictedCard => !predictedCard.HasKeyword(context.State, CardKeyword.Unplayable))
            .ToList()
            .StableShuffle(context.Rng.Shuffle)
            .FirstOrDefault();

        selectedCard ??= attackCards
            .StableShuffle(context.Rng.Shuffle)
            .FirstOrDefault();

        if (selectedCard is null)
        {
            return;
        }

        context.Simulator.History.CardsSelected([selectedCard]);
        context.Simulator.AutoPlay(selectedCard, nestedChoiceSourceId: card.Id.Entry);
    }

    private static PredictedCard? SelectRandomHandCard(
        CardOnPlayMirrorContext context,
        Func<CardModel, bool> filter)
    {
        var candidates = context.OwnerState.Hand.Cards.Where(card => filter(card.Preview));
        return context.Rng.CombatCardSelection.NextItem(candidates);
    }
}
