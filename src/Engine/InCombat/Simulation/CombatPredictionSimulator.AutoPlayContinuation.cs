using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    private bool ContinueMoveCardsForAutoPlay(Player player, int count, CardPilePosition position,
        List<PredictedCard> cards, int nextIndex)
    {
        var state = State.GetPlayerCombatState(player);
        for (int index = nextIndex; index < count; index++)
        {
            ShuffleIfNecessary(player);
            if (HasPendingChoice)
            {
                AppendExecutionContinuation(new MoveAutoPlayExecutionFrame(player, count, position, cards, index));
                return false;
            }
            PredictedCard? card = position switch
            {
                CardPilePosition.Top => state.DrawPile.TopCard,
                CardPilePosition.Bottom => state.DrawPile.BottomCard,
                CardPilePosition.Random => Rng.CombatCardSelection.NextItem(state.DrawPile.Cards),
                _ => null,
            };
            if (card is null) break;
            cards.Add(card);
            AddToPile(card, state.PlayPile);
            History.AutoPlayFromDrawPile(card);
        }
        return true;
    }

    private bool ContinueAutoPlayCards(IReadOnlyList<PredictedCard> cards, bool forceExhaust, int nextIndex)
    {
        for (int index = nextIndex; index < cards.Count; index++)
        {
            PredictedCard card = cards[index];
            if (State.GetCreature(card.Preview.Owner.Creature).IsDead) break;
            card.MutablePreview.ExhaustOnNextPlay = forceExhaust;
            AutoPlay(card, nestedChoiceSourceId: card.Preview.Id.Entry);
            if (!HasPendingChoice) continue;
            AppendExecutionContinuation(new AutoPlayCardsExecutionFrame(cards, forceExhaust, index + 1));
            return false;
        }
        return true;
    }

    private sealed record MoveAutoPlayExecutionFrame(Player Player, int Count, CardPilePosition Position,
        List<PredictedCard> Cards, int NextIndex) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context) => ForkExecutionCardList(Cards, context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Cards = context.RequireRemap(Cards) };
        public bool Resume(CombatPredictionSimulator simulator)
            => simulator.ContinueMoveCardsForAutoPlay(Player, Count, Position, Cards, NextIndex);
    }

    private sealed record AutoPlayCardsExecutionFrame(IReadOnlyList<PredictedCard> Cards, bool ForceExhaust, int NextIndex)
        : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            if (Cards is List<PredictedCard> list) ForkExecutionCardList(list, context);
            else foreach (PredictedCard card in Cards)
                if (!context.TryRemap(card, out PredictedCard? _)) card.Fork(context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Cards = Cards is List<PredictedCard> list ? context.RequireRemap(list)
                : Cards.Select(context.RequireRemap).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator) => simulator.ContinueAutoPlayCards(Cards, ForceExhaust, NextIndex);
    }
}
