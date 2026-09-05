using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PotionChoiceSupport
{
    public static bool RequiresChoice(PotionModel potion)
        => GeneratesCardChoice(potion)
            || potion is Ashwater
            or DropletOfPrecognition
            or GamblersBrew
            or LiquidMemories
            or TouchOfInsanity;

    public static CardChoiceSpec GetSpec(
        CombatPredictionSimulator simulator,
        PotionModel potion)
    {
        Player owner = potion.Owner;
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(owner);
        if (GeneratesCardChoice(potion))
        {
            CombatPredictionCardGenerationOptionsEntry generated = simulator.History
                .OfType<CombatPredictionCardGenerationOptionsEntry>()
                .LastOrDefault()
                ?? throw new InvalidOperationException($"药水 {potion.Id.Entry} 没有生成三选一候选。");
            return RangeSpec(
                state,
                PlanChoiceEffect.GenerateToHand,
                PileType.None,
                0,
                1,
                generated.Options);
        }
        return potion switch
        {
            Ashwater => RangeSpec(
                state,
                PlanChoiceEffect.Exhaust,
                PileType.Hand,
                0,
                state.Hand.Cards.Count,
                state.Hand.Cards),
            DropletOfPrecognition => ExactSpec(
                state,
                PlanChoiceEffect.MoveToHand,
                PileType.Draw,
                state.DrawPile.Cards),
            GamblersBrew => RangeSpec(
                state,
                PlanChoiceEffect.DiscardAndDraw,
                PileType.Hand,
                0,
                state.Hand.Cards.Count,
                state.Hand.Cards),
            LiquidMemories => ExactSpec(
                state,
                PlanChoiceEffect.MoveToHandFreeThisTurn,
                PileType.Discard,
                state.DiscardPile.Cards),
            TouchOfInsanity => ExactSpec(
                state,
                PlanChoiceEffect.SetFreeThisCombat,
                PileType.Hand,
                state.Hand.Cards.Where(card =>
                    card.Preview.CostsEnergyOrStars(includeGlobalModifiers: false)
                    || card.Preview.CostsEnergyOrStars(includeGlobalModifiers: true))),
            _ => throw new InvalidOperationException($"药水 {potion.Id.Entry} 没有选牌定义。"),
        };
    }

    public static bool Apply(
        CombatPredictionSimulator simulator,
        PotionModel potion,
        PlanCardChoice choice)
    {
        SimPlayerCombatState owner = simulator.State.GetPlayerCombatState(potion.Owner);
        List<PredictedCard> selected = new(choice.Cards.Count);
        if (choice.Effect == PlanChoiceEffect.GenerateToHand)
        {
            CombatPredictionCardGenerationOptionsEntry generated = simulator.History
                .OfType<CombatPredictionCardGenerationOptionsEntry>()
                .LastOrDefault()
                ?? throw new InvalidOperationException($"药水 {potion.Id.Entry} 缺少生成候选。");
            foreach (PlanCardToken token in choice.Cards)
                selected.Add(Find(generated.Options, token).Clone());
        }
        else
        {
            SimCardPile pile = owner.GetCardPile(choice.SourcePile)
                ?? throw new InvalidOperationException($"找不到药水模拟牌堆 {choice.SourcePile}。");
            foreach (PlanCardToken token in choice.Cards)
                selected.Add(Find(pile.Cards, token));
        }

        switch (choice.Effect)
        {
            case PlanChoiceEffect.MoveToHand:
                simulator.AddToPile(selected, PileType.Hand);
                break;
            case PlanChoiceEffect.Exhaust:
                foreach (PredictedCard card in selected)
                {
                    simulator.Exhaust(card);
                    if (simulator.HasPendingChoice)
                        return false;
                }
                break;
            case PlanChoiceEffect.DiscardAndDraw:
                simulator.Discard(selected);
                if (simulator.HasPendingChoice)
                    return false;
                simulator.Draw(potion.Owner, selected.Count);
                break;
            case PlanChoiceEffect.MoveToHandFreeThisTurn:
                foreach (PredictedCard card in selected)
                    card.SetToFreeThisTurn();
                simulator.AddToPile(selected, PileType.Hand);
                break;
            case PlanChoiceEffect.SetFreeThisCombat:
                foreach (PredictedCard card in selected)
                    card.SetToFreeThisCombat();
                break;
            case PlanChoiceEffect.GenerateToHand:
                foreach (PredictedCard card in selected)
                    card.SetToFreeThisTurn();
                if (selected.Count > 0)
                {
                    simulator.AddGeneratedCardsToCombat(
                        selected,
                        PileType.Hand,
                        potion.Owner,
                        CardPilePosition.Bottom,
                        CardGenerationResultKind.Random);
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"药水 {potion.Id.Entry} 不支持选牌效果 {choice.Effect}。");
        }
        return !simulator.HasPendingChoice;
    }

    private static CardChoiceSpec ExactSpec(
        SimPlayerCombatState state,
        PlanChoiceEffect effect,
        PileType pile,
        IEnumerable<PredictedCard> options)
        => RangeSpec(state, effect, pile, 1, 1, options);

    private static CardChoiceSpec RangeSpec(
        SimPlayerCombatState state,
        PlanChoiceEffect effect,
        PileType pile,
        int minCount,
        int maxCount,
        IEnumerable<PredictedCard> options)
    {
        List<PredictedCard> optionList = options.ToList();
        IReadOnlyList<PredictedCard> source = state.GetCardPile(pile)?.Cards ?? [];
        return new CardChoiceSpec(effect, pile, minCount, maxCount, optionList, source, 0d);
    }

    internal static bool GeneratesCardChoice(PotionModel potion)
        => potion is AttackPotion or SkillPotion or PowerPotion or ColorlessPotion;

    private static PredictedCard Find(IReadOnlyList<PredictedCard> cards, PlanCardToken token)
    {
        int matchingOccurrence = 0;
        for (int index = 0; index < cards.Count; index++)
        {
            PredictedCard card = cards[index];
            if (!CardChoiceSupport.MatchesToken(card, token))
                continue;
            if (matchingOccurrence == token.SourceOccurrence)
                return card;
            matchingOccurrence++;
        }

        throw new InvalidPlannedChoiceBranchException(
            $"药水选牌回放时找不到 {token.CardId}+{token.UpgradeLevel}#{token.SourceOccurrence}。");
    }
}
