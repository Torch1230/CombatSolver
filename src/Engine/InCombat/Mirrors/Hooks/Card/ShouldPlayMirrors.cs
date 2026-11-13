using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, ShouldPlayMirrorContext, bool>;

// Mirrors Hook.ShouldPlay while selectively replacing listeners that read card-play state.
internal static class ShouldPlayMirrors
{
    private static readonly MirrorMethodSpec ShouldPlay = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ShouldPlay),
        [typeof(CardModel), typeof(AutoPlayType)]);

    private static readonly Registry Registry = CreateRegistry();

    public static bool Invoke(AbstractModel listener, ShouldPlayMirrorContext context)
    {
        if (Registry.TryInvokeRegistered(listener, context, out var result))
        {
            return result.Value;
        }

        return listener.ShouldPlay(context.Card.Preview, context.AutoPlayType);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(ShouldPlay);

        registry.Register<ChainsOfBindingPower>(HandleChainsOfBindingPower);
        registry.Register<RingingPower>(HandleRingingPower);
        registry.Register<SlothPower>(HandleSlothPower);

        registry.Register<VelvetChoker>(HandleVelvetChoker);
        registry.Register<EncorePower>((power, c) => c.Card.Preview.Owner.Creature != power.Owner
            || power.GetInternalData<EncorePower.Data>().numberOfCardsPlayedSinceApplied
                + power.GetInternalData<EncorePower.Data>().cardsBeingPlayed.Count < power.Amount);
        registry.Register<Normality>(HandleNormality);

        return registry;
    }

    private static bool HandleChainsOfBindingPower(
        ChainsOfBindingPower power,
        ShouldPlayMirrorContext context)
    {
        return context.Card.Preview.Owner.Creature != power.Owner ||
            context.Card.Preview.Affliction is not Bound ||
            !context.StateStore.Get(power, static _ => new ChainsOfBindingPredictionState()).BoundCardPlayed;
    }

    private static bool HandleNormality(Normality normality, ShouldPlayMirrorContext context)
    {
        if (context.Card.Preview.Owner != normality.Owner
            || context.State.FindCard(normality)?.GetPile(context.State)?.Type != PileType.Hand)
            return true;
        SimulatedCombatState combat = context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("Normality requires branch card-play start history.");
        return combat.GetCardPlayStartsThisTurn(normality.Owner.Creature) < 3;
    }

    private static bool HandleSlothPower(SlothPower power, ShouldPlayMirrorContext context)
    {
        return context.Card.Preview.Owner.Creature != power.Owner ||
            context.StateStore.Get(power, static value => new CounterPredictionState(value._cardsPlayedThisTurn)).Value <
            power.Amount;
    }

    private static bool HandleRingingPower(RingingPower power, ShouldPlayMirrorContext context)
    {
        if (context.Card.Preview.Owner.Creature != power.Owner
            || context.Card.Preview.Affliction is not Ringing)
        {
            return true;
        }

        SimulatedCombatState combat = context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("昏眩出牌限制缺少分支出牌历史。");
        return combat.GetCardPlaySeriesStartedThisTurn(power.Owner) == 0;
    }

    private static bool HandleVelvetChoker(VelvetChoker relic, ShouldPlayMirrorContext context)
    {
        if (context.Card.Preview.Owner != relic.Owner)
            return true;
        var combat = context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("Velvet Choker requires captured card-play history.");
        return combat.GetCardPlayStartsThisTurn(relic.Owner.Creature) < relic.DynamicVars.Cards.IntValue;
    }
}

internal sealed class ShouldPlayMirrorContext : CombatMirrorContext
{
    public required PredictedCard Card { get; init; }

    public required AutoPlayType AutoPlayType { get; init; }
}
