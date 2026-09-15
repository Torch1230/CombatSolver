using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    private enum DiscardExecutionStage { Discard, Draw, AutoPlay }

    private bool ContinueDiscardAndDrawExecution(IReadOnlyList<PredictedCard> cards, int drawCount,
        List<PredictedCard> slyCards, DiscardExecutionStage stage, int nextIndex)
    {
        if (stage == DiscardExecutionStage.Discard)
        {
            for (int index = nextIndex; index < cards.Count; index++)
            {
                PredictedCard card = cards[index];
                if (card.Preview.IsSlyThisTurn) slyCards.Add(card);
                AddToPile(card, PileType.Discard);
                if (State.CombatState is ICombatPredictionCardEventSink eventSink)
                    eventSink.RecordCardDiscarded(card.Preview.Owner.Creature);
                HookMirrors.AfterCardDiscarded(this, card);
                if (!HasPendingChoice) continue;
                AppendExecutionContinuation(new DiscardAndDrawExecutionFrame(cards, drawCount, slyCards, stage, index + 1));
                return false;
            }
            stage = DiscardExecutionStage.Draw;
        }
        if (stage == DiscardExecutionStage.Draw)
        {
            if (drawCount > 0)
            {
                Draw(cards[0].Preview.Owner, drawCount);
                if (HasPendingChoice)
                {
                    AppendExecutionContinuation(new DiscardAndDrawExecutionFrame(cards, drawCount, slyCards, DiscardExecutionStage.AutoPlay, 0));
                    return false;
                }
            }
            nextIndex = 0;
        }
        for (int index = nextIndex; index < slyCards.Count; index++)
        {
            PredictedCard card = slyCards[index];
            AutoPlay(card, type: AutoPlayType.SlyDiscard, nestedChoiceSourceId: card.Preview.Id.Entry);
            if (!HasPendingChoice) continue;
            AppendExecutionContinuation(new DiscardAndDrawExecutionFrame(cards, drawCount, slyCards, DiscardExecutionStage.AutoPlay, index + 1));
            return false;
        }
        return true;
    }

    private sealed record DiscardAndDrawExecutionFrame(IReadOnlyList<PredictedCard> Cards, int DrawCount,
        List<PredictedCard> SlyCards, DiscardExecutionStage Stage, int NextIndex) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            foreach (PredictedCard card in Cards.Concat(SlyCards))
                if (!context.TryRemap(card, out PredictedCard? _)) card.Fork(context);
            ForkExecutionCardList(SlyCards, context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Cards = Cards.Select(context.RequireRemap).ToArray(), SlyCards = context.RequireRemap(SlyCards) };
        public bool Resume(CombatPredictionSimulator simulator)
            => simulator.ContinueDiscardAndDrawExecution(Cards, DrawCount, SlyCards, Stage, NextIndex);
    }

    private sealed record DiscardSlyExecutionFrame(PredictedCard Card) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card) };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            simulator.AutoPlay(Card, type: AutoPlayType.SlyDiscard, nestedChoiceSourceId: Card.Preview.Id.Entry);
            return !simulator.HasPendingChoice;
        }
    }
}
