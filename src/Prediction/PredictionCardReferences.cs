using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>Branch-local references, never a lookup by card type or mutable card values.</summary>
internal static class PredictionCardReferences
{
    public static PredictedCard RequireCard(SimPlayerCombatState player, CardModel source)
        => RequireCard(player.AllCards, source);

    internal static PredictedCard RequireCard(IEnumerable<PredictedCard> cards, CardModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        PredictedCard? found = null;
        foreach (PredictedCard card in cards)
        {
            if (!card.References(source)) continue;
            if (found is not null)
                throw new InvalidOperationException("Card reference matches multiple prediction entries.");
            found = card;
        }
        return found ?? throw new InvalidOperationException("Card reference is absent from the current prediction piles.");
    }

    public static List<PredictedCard?>? Remap(IReadOnlyList<PredictedCard?>? cards, PredictionForkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (cards is null) return null;
        List<PredictedCard?> copy = new(cards.Count);
        foreach (PredictedCard? card in cards)
            copy.Add(card is null ? null : context.RequireRemap(card));
        return copy;
    }
}
