using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, ModifyCardPlayResultLocationMirrorContext, CardLocation>;
using AfterRegistry = MethodMirrorRegistry<AbstractModel, AfterModifyingCardPlayResultLocationMirrorContext>;

// Mirrors Hook.ModifyCardPlayResultLocation and CardModel.OnPlayWrapper's direct selected-modifier after dispatch.
internal static class ModifyCardPlayResultLocationMirrors
{
    private static readonly MirrorMethodSpec ModifyCardPlayResultLocation = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ModifyCardPlayResultLocation),
        [typeof(CardModel), typeof(bool), typeof(ResourceInfo), typeof(CardLocation)]);

    private static readonly MirrorMethodSpec AfterModifyingCardPlayResultLocation = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterModifyingCardPlayResultLocation),
        [typeof(CardModel), typeof(CardLocation)]);

    private static readonly Registry Registry = CreateRegistry();
    private static readonly AfterRegistry AfterRegistry = CreateAfterRegistry();

    public static CardLocation Invoke(
        AbstractModel listener,
        ModifyCardPlayResultLocationMirrorContext context)
    {
        return Registry.TryInvokeRegistered(listener, context, out var result)
            ? result.Value
            : InvokeOriginal(listener, context);
    }

    public static void InvokeAfter(
        AbstractModel modifier,
        AfterModifyingCardPlayResultLocationMirrorContext context)
    {
        AfterRegistry.Invoke(modifier, context);
    }

    private static CardLocation InvokeOriginal(
        AbstractModel listener,
        ModifyCardPlayResultLocationMirrorContext context)
    {
        return listener.ModifyCardPlayResultLocation(
            context.Card.Preview,
            context.IsAutoPlay,
            context.Resources,
            context.Location);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(ModifyCardPlayResultLocation);

        registry.Register<FeralPower>(HandleFeralPower);
        registry.Register<NostalgiaPower>(HandleNostalgiaPower);
        registry.Register<ReboundPower>(HandleReboundPower);

        return registry;
    }

    private static AfterRegistry CreateAfterRegistry()
    {
        var registry = new AfterRegistry(AfterModifyingCardPlayResultLocation);

        registry.RegisterIgnored<CorruptionPower>();
        registry.Register<FeralPower>(HandleFeralPowerAfter);
        registry.RegisterIgnored<NostalgiaPower>();
        registry.Register<ReboundPower>(HandleReboundPowerAfter);

        return registry;
    }

    private static CardLocation HandleFeralPower(
        FeralPower power,
        ModifyCardPlayResultLocationMirrorContext context)
    {
        var card = context.Card.Preview;
        var state = context.StateStore.Get(power, () => new FeralPredictionState(power));
        if (card.Owner.Creature != power.Owner ||
            card.Type != CardType.Attack ||
            context.Resources.EnergyValue > 0 ||
            card.IsDupe ||
            state.ZeroCostAttacksPlayed >= power.Amount)
        {
            return context.Location;
        }

        var location = context.Location;
        location.pileType = PileType.Hand;
        location.position = CardPilePosition.Top;
        location.player = card.Owner;
        return location;
    }

    private static CardLocation HandleNostalgiaPower(
        NostalgiaPower power,
        ModifyCardPlayResultLocationMirrorContext context)
    {
        var card = context.Card.Preview;
        if (card.Owner.Creature != power.Owner ||
            card.Type is not (CardType.Attack or CardType.Skill) ||
            context.Location.pileType != PileType.Discard)
        {
            return context.Location;
        }

        var count = ((SimulatedCombatState)context.State.CombatState)
            .GetAttackSkillStartsThisTurn(power.Owner);
        if (count >= power.Amount)
        {
            return context.Location;
        }

        var location = context.Location;
        location.pileType = PileType.Draw;
        location.position = CardPilePosition.Top;
        return location;
    }

    private static CardLocation HandleReboundPower(
        ReboundPower power,
        ModifyCardPlayResultLocationMirrorContext context)
    {
        return context.StateStore.GetPowerAmount(power).IsActive
            ? InvokeOriginal(power, context)
            : context.Location;
    }

    private static void HandleFeralPowerAfter(
        FeralPower power,
        AfterModifyingCardPlayResultLocationMirrorContext context)
    {
        var state = context.StateStore.Get(power, () => new FeralPredictionState(power));
        state.ZeroCostAttacksPlayed++;
    }

    private static void HandleReboundPowerAfter(
        ReboundPower power,
        AfterModifyingCardPlayResultLocationMirrorContext context)
    {
        context.StateStore.GetPowerAmount(power).Decrement();
    }
}

internal sealed class ModifyCardPlayResultLocationMirrorContext : CombatMirrorContext
{
    public required PredictedCard Card { get; init; }

    public required bool IsAutoPlay { get; init; }

    public required ResourceInfo Resources { get; init; }

    public required CardLocation Location { get; set; }
}

internal sealed class AfterModifyingCardPlayResultLocationMirrorContext : CombatMirrorContext
{
    public required PredictedCard Card { get; init; }

    public required CardLocation Location { get; init; }
}

internal sealed class FeralPredictionState(FeralPower power) : IPredictionStateForkable
{
    public int ZeroCostAttacksPlayed { get; set; } =
        power.GetInternalData<FeralPower.Data>().zeroCostAttacksPlayed;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}
