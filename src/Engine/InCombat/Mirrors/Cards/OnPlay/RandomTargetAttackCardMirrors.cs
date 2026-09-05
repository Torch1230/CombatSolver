using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class RandomTargetAttackCardMirrors
{
    public static void FlakCannonOnPlay(FlakCannon card, CardOnPlayMirrorContext context)
    {
        var statuses = context.OwnerState.AllCards
            .Where(predictedCard =>
                predictedCard.Preview.Type is CardType.Status &&
                !context.OwnerState.ExhaustPile.Cards.Contains(predictedCard))
            .ToList();

        foreach (var status in statuses)
        {
            context.Simulator.Exhaust(status);
            if (context.Simulator.HasPendingChoice)
                return;
        }

        context.AttackRandomOpponents(statuses.Count);
    }

    public static void RicochetOnPlay(Ricochet card, CardOnPlayMirrorContext context)
    {
        context.AttackRandomOpponents(card.DynamicVars.Repeat.IntValue);
    }

    public static void RipAndTearOnPlay(RipAndTear card, CardOnPlayMirrorContext context)
    {
        context.AttackRandomOpponents(hitCount: 2);
    }

    public static void StardustOnPlay(Stardust card, CardOnPlayMirrorContext context)
    {
        context.AttackRandomOpponents(context.Card.ResolveStarXValue(context.State));
    }

    public static void SweepingGazeOnPlay(SweepingGaze card, CardOnPlayMirrorContext context)
    {
        if (context.State.GetOsty(card.Owner) is { } osty && context.State.GetCreature(osty).IsAlive)
        {
            DamageCmd.Attack(card.DynamicVars.OstyDamage.BaseValue)
                .FromOsty(osty, card, context.CardPlay)
                .TargetingRandomOpponents(context.CombatState)
                .Simulate(context.Simulator);
        }
    }

    public static void SwordBoomerangOnPlay(SwordBoomerang card, CardOnPlayMirrorContext context)
    {
        context.AttackRandomOpponents(card.DynamicVars.Repeat.IntValue);
    }

    public static void VolleyOnPlay(Volley card, CardOnPlayMirrorContext context)
    {
        context.AttackRandomOpponents(context.Card.ResolveEnergyXValue(context.State));
    }
}
