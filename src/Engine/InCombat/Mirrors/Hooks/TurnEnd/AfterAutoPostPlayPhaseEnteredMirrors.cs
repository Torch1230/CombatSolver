using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;

using Registry = MethodMirrorRegistry<AbstractModel, AfterAutoPostPlayMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterAutoPostPlayPhaseEntered.
internal static class AfterAutoPostPlayPhaseEnteredMirrors
{
    private static readonly MirrorMethodSpec AfterAutoPostPlayPhaseEntered = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterAutoPostPlayPhaseEntered),
        [typeof(PlayerChoiceContext), typeof(Player)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(
        AbstractModel listener,
        AfterAutoPostPlayMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterAutoPostPlayPhaseEntered);

        registry.Register<HowlFromBeyond>(HandleHowlFromBeyond);
        registry.Register<IAmInvincible>(HandleIAmInvincible);
        registry.Register<StampedePower>(HandleStampedePower);
        registry.Register<SimpletonPower>((power, c) =>
        {
            if (c.Player.Creature != power.Owner) return;
            var state = c.State.GetPlayerCombatState(c.Player);
            for (int index = 0; index < power.Amount; index++)
            {
                var cheapest = state.Hand.Cards.Where(card => !card.HasKeyword(c.State, CardKeyword.Unplayable))
                    .GroupBy(card => card.Preview.EnergyCost.CostsX ? 999999999 : card.GetEnergyCostWithModifiers(c.Simulator, state))
                    .OrderBy(group => group.Key).FirstOrDefault();
                if (cheapest is null) continue;
                var card = c.Rng.CombatCardSelection.NextItem(cheapest);
                if (card is null) continue;
                c.Simulator.AutoPlay(card, nestedChoiceSourceId: power.Id.Entry);
                if (c.Simulator.HasPendingChoice) return;
            }
        });

        return registry;
    }

    private static void HandleHowlFromBeyond(HowlFromBeyond card, AfterAutoPostPlayMirrorContext context)
    {
        if (context.Player != card.Owner ||
            context.State.GetPlayerCombatState(card.Owner).ExhaustPile.Find(card) is not { } predictedCard)
        {
            return;
        }

        context.Simulator.AutoPlay(predictedCard, nestedChoiceSourceId: card.Id.Entry);
    }

    private static void HandleIAmInvincible(IAmInvincible card, AfterAutoPostPlayMirrorContext context)
    {
        if (context.Player != card.Owner ||
            context.State.GetPlayerCombatState(card.Owner).DrawPile.TopCard?.References(card) is not true)
        {
            return;
        }

        context.Simulator.AutoPlayFromDrawPile(card.Owner, 1, CardPilePosition.Top);
    }

    private static void HandleStampedePower(StampedePower power, AfterAutoPostPlayMirrorContext context)
    {
        if (context.Player.Creature != power.Owner)
        {
            return;
        }

        var hand = context.State.GetPlayerCombatState(context.Player).Hand;

        for (var i = 0; i < power.Amount; i++)
        {
            var candidates = hand.Cards
                .Where(card =>
                    card.Preview.Type == CardType.Attack &&
                    !card.HasKeyword(context.State, CardKeyword.Unplayable))
                .ToList();
            var card = context.Rng.CombatCardSelection.NextItem(candidates);

            if (card != null)
            {
                context.Simulator.AutoPlay(card, nestedChoiceSourceId: power.Id.Entry);
                if (context.Simulator.HasPendingChoice)
                {
                    break;
                }
            }
        }
    }
}

internal sealed class AfterAutoPostPlayMirrorContext : CombatMirrorContext
{
    public required Player Player { get; init; }
}
