using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

internal static class PhantomBladesPowerMirrors
{
    public static void AfterApplied(PhantomBladesPower power, CombatPredictionState state)
    {
        foreach (PredictedCard card in state.GetPlayerCombatState(power.Owner.Player!).AllCards)
            AfterCardEnteredCombat(power, card);
    }

    public static void AfterCardEnteredCombat(PhantomBladesPower power, PredictedCard card)
    {
        if (card.Preview.Owner == power.Owner.Player && card.Preview.Tags.Contains(CardTag.Shiv))
            card.MutablePreview.AddKeyword(CardKeyword.Retain);
    }
}
