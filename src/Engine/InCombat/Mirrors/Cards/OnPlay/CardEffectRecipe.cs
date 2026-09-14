using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal enum CardEffectKind
{
    Attack,
    Block,
    OwnerDrawOne,
    OwnerDrawCards,
}

/// <summary>
/// An ordered sequence of common command templates owned by the card mirror. Adapter recipes
/// additionally require the strict analyzer to account for every gameplay operation in OnPlay.
/// </summary>
internal sealed class CardEffectRecipe(IReadOnlyList<CardEffectKind> effects)
{
    public IReadOnlyList<CardEffectKind> Effects { get; } = effects.ToArray();

    public void Execute(CardModel card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        Continue(card, context, 0);
    }

    private bool Continue(CardModel card, CardOnPlayMirrorContext context, int nextIndex)
    {
        for (int index = nextIndex; index < Effects.Count; index++)
        {
            CardEffectKind effect = Effects[index];
            using var dispatch = context.Simulator.BeginExecutionDispatch();
            if (effect is CardEffectKind.OwnerDrawOne or CardEffectKind.OwnerDrawCards)
                context.Simulator.AcknowledgeExecutionDispatch();
            switch (effect)
            {
                case CardEffectKind.Attack:
                    GeneralCardMirrors.GeneralAttackOnPlay(card, context);
                    break;
                case CardEffectKind.Block:
                    GeneralCardMirrors.GeneralBlockOnPlay(card, context);
                    break;
                case CardEffectKind.OwnerDrawOne:
                    GeneralCardMirrors.GeneralOwnerDrawOneOnPlay(card, context);
                    break;
                case CardEffectKind.OwnerDrawCards:
                    GeneralCardMirrors.GeneralOwnerDrawOnPlay(card, context);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(effect), effect, null);
            }
            if (context.Simulator.HasPendingChoice)
            {
                context.Simulator.AppendExecutionContinuation(new EffectExecutionFrame(this, context.Card, context.CardPlay, index + 1));
                return false;
            }
        }
        return true;
    }

    private sealed record EffectExecutionFrame(CardEffectRecipe Recipe, PredictedCard Card, CardPlay Play, int NextIndex)
        : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context) => CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card), Play = context.RequireRemap(Play) };
        public bool Resume(CombatPredictionSimulator simulator)
            => Recipe.Continue(Card.MutablePreview, new CardOnPlayMirrorContext { Simulator = simulator, Card = Card, CardPlay = Play }, NextIndex);
    }
}
