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
/// A fully-accounted sequence of common card effects. Recipes are accepted only when the
/// strict IL analyzer proves that every prediction-relevant operation in OnPlay is represented.
/// </summary>
internal sealed class CardEffectRecipe(IReadOnlyList<CardEffectKind> effects)
{
    public IReadOnlyList<CardEffectKind> Effects { get; } = effects;

    public void Execute(CardModel card, CardOnPlayMirrorContext context)
    {
        foreach (CardEffectKind effect in Effects)
        {
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
                return;
        }
    }
}
