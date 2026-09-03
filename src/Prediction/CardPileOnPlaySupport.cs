using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class CardPileOnPlaySupport
{
    public static void Apply(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard)
    {
        CardModel card = playedCard.Preview;
        switch (card)
        {
            case Apotheosis:
                UpgradeAllOtherCards(simulator, playedCard);
                break;
            case BladeDance:
                GenerateShivs(simulator, card.Owner, card.DynamicVars.Cards.IntValue, upgraded: false);
                break;
            case BladeOfInk:
                GenerateInkyShivs(simulator, card.Owner, card.DynamicVars.Cards.IntValue);
                break;
            case CloakAndDagger:
                GenerateShivs(simulator, card.Owner, card.DynamicVars.Cards.IntValue, upgraded: false);
                break;
            case Enlightenment:
                ReduceHandCosts(simulator, card);
                break;
            case FanOfKnives:
                GenerateShivs(simulator, card.Owner, card.DynamicVars["Shivs"].IntValue, upgraded: false);
                break;
            case StormOfSteel:
                ReplaceHandWithShivs(simulator, card);
                break;
            case SummonForth:
                MoveSovereignBladesToHand(simulator, card.Owner);
                break;
            case UpMySleeve:
                GenerateShivs(simulator, card.Owner, card.DynamicVars.Cards.IntValue, upgraded: false);
                playedCard.MutablePreview.EnergyCost.AddThisCombat(-1);
                break;
        }
    }

    private static void UpgradeAllOtherCards(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard)
    {
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(playedCard.Preview.Owner);
        foreach (PredictedCard card in playerState.AllCards.ToList())
        {
            if (!ReferenceEquals(card, playedCard) && card.Preview.IsUpgradable)
                card.Upgrade();
        }
    }

    private static void ReduceHandCosts(CombatPredictionSimulator simulator, CardModel source)
    {
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(source.Owner);
        foreach (PredictedCard card in playerState.Hand)
        {
            if (source.IsUpgraded)
                card.MutablePreview.EnergyCost.SetThisCombat(1, reduceOnly: true);
            else
                card.MutablePreview.EnergyCost.SetThisTurnOrUntilPlayed(1, reduceOnly: true);
        }
    }

    private static void ReplaceHandWithShivs(CombatPredictionSimulator simulator, CardModel source)
    {
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(source.Owner);
        PredictedCard[] discarded = playerState.Hand.Cards.ToArray();
        simulator.Discard(discarded);
        GenerateShivs(simulator, source.Owner, discarded.Length, source.IsUpgraded);
    }

    private static void MoveSovereignBladesToHand(
        CombatPredictionSimulator simulator,
        Player owner)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(owner);
        PredictedCard[] blades = state.AllCards
            .Where(card => card.Preview is SovereignBlade && !state.Hand.Cards.Contains(card))
            .ToArray();
        simulator.AddToPile(blades, PileType.Hand);
    }

    private static void GenerateInkyShivs(
        CombatPredictionSimulator simulator,
        Player owner,
        int count)
    {
        IReadOnlyList<PredictedCard> shivs = GenerateShivs(simulator, owner, count, upgraded: false);
        foreach (PredictedCard shiv in shivs)
            shiv.Enchant(CanonicalModels.Enchantment<Inky>().ToMutable(), 1m);
    }

    internal static IReadOnlyList<PredictedCard> GenerateShivs(
        CombatPredictionSimulator simulator,
        Player owner,
        int count,
        bool upgraded)
    {
        List<PredictedCard> shivs = new(count);
        for (int index = 0; index < count; index++)
        {
            PredictedCard shiv = PredictedCard.Create(CanonicalModels.Card<Shiv>(), owner);
            if (upgraded)
                shiv.Upgrade();
            shivs.Add(shiv);
        }
        simulator.AddGeneratedCardsToCombat(
            shivs,
            PileType.Hand,
            owner,
            CardPilePosition.Bottom,
            CardGenerationResultKind.Fixed);
        return shivs;
    }
}
