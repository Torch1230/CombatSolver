using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, ModifyCardPlayCountMirrorContext, int>;
using AfterRegistry = MethodMirrorRegistry<AbstractModel, AfterModifyingCardPlayCountMirrorContext>;

// Mirrors Hook.ModifyCardPlayCount and the selected-modifier AfterModifyingCardPlayCount dispatch.
internal static class ModifyCardPlayCountMirrors
{
    private static readonly MirrorMethodSpec ModifyCardPlayCount = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ModifyCardPlayCount),
        [typeof(CardModel), typeof(Creature), typeof(int)]);

    private static readonly MirrorMethodSpec AfterModifyingCardPlayCount = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterModifyingCardPlayCount),
        [typeof(CardModel)]);

    private static readonly Registry Registry = CreateRegistry();
    private static readonly AfterRegistry AfterRegistry = CreateAfterRegistry();

    public static int Invoke(AbstractModel listener, ModifyCardPlayCountMirrorContext context)
    {
        return Registry.TryInvokeRegistered(listener, context, out var result)
            ? result.Value
            : InvokeOriginal(listener, context);
    }

    public static void InvokeAfter(AbstractModel listener, AfterModifyingCardPlayCountMirrorContext context)
    {
        AfterRegistry.Invoke(listener, context);
    }

    private static int InvokeOriginal(AbstractModel listener, ModifyCardPlayCountMirrorContext context)
    {
        return listener.ModifyCardPlayCount(context.Card.Preview, context.Target, context.PlayCount);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(ModifyCardPlayCount);

        registry.Register<BurstPower>(HandleConsumablePower);
        registry.Register<DuplicationPower>(HandleConsumablePower);
        registry.Register<EchoFormPower>(HandleEchoFormPower);
        registry.Register<OneTwoPunchPower>(HandleConsumablePower);
        registry.Register<KiVersePower>(HandleConsumablePower);
        registry.Register<SignalBoostPower>(HandleConsumablePower);
        registry.Register<TagTeamPower>(HandleConsumablePower);

        registry.Register<ThrowingAxe>(HandleThrowingAxe);

        return registry;
    }

    private static AfterRegistry CreateAfterRegistry()
    {
        var registry = new AfterRegistry(AfterModifyingCardPlayCount);

        registry.Register<BurstPower>(HandleDecrementPower);
        registry.Register<DuplicationPower>(HandleDecrementPower);
        registry.RegisterIgnored<EchoFormPower>();
        registry.Register<OneTwoPunchPower>(HandleDecrementPower);
        registry.Register<KiVersePower>(HandleDecrementPower);
        registry.Register<SignalBoostPower>(HandleDecrementPower);
        registry.Register<TagTeamPower>(HandleConsumePower);

        registry.Register<ThrowingAxe>(HandleThrowingAxeAfter);

        return registry;
    }

    private static int HandleConsumablePower(PowerModel power, ModifyCardPlayCountMirrorContext context)
    {
        return context.StateStore.GetPowerAmount(power).IsActive
            ? InvokeOriginal(power, context)
            : context.PlayCount;
    }

    private static int HandleEchoFormPower(EchoFormPower power, ModifyCardPlayCountMirrorContext context)
    {
        if (context.Card.Preview.Owner.Creature != power.Owner)
        {
            return context.PlayCount;
        }

        int count = context.CombatState is SimulatedCombatState combat
            ? combat.GetCardPlaySeriesStartedThisTurn(power.Owner)
            : CombatManager.Instance.History.CardPlaysStarted.Count(entry =>
                entry.Actor == power.Owner &&
                entry.CardPlay.IsFirstInSeries &&
                entry.HappenedThisTurn(power.CombatState));

        return count < power.Amount
            ? context.PlayCount + 1
            : context.PlayCount;
    }

    private static int HandleThrowingAxe(ThrowingAxe relic, ModifyCardPlayCountMirrorContext context)
    {
        var state = context.StateStore.Get(relic, () => new ThrowingAxePredictionState(relic));
        return !state.UsedThisCombat && context.Card.Preview.Owner == relic.Owner
            ? context.PlayCount + 1
            : context.PlayCount;
    }

    private static void HandleDecrementPower(PowerModel power, AfterModifyingCardPlayCountMirrorContext context)
    {
        context.StateStore.GetPowerAmount(power).Decrement();
    }

    private static void HandleConsumePower(TagTeamPower power, AfterModifyingCardPlayCountMirrorContext context)
    {
        context.StateStore.GetPowerAmount(power).Consume();
    }

    private static void HandleThrowingAxeAfter(ThrowingAxe relic, AfterModifyingCardPlayCountMirrorContext context)
    {
        var state = context.StateStore.Get(relic, () => new ThrowingAxePredictionState(relic));
        state.UsedThisCombat = true;
    }
}

internal sealed class ModifyCardPlayCountMirrorContext : CombatMirrorContext
{
    public required PredictedCard Card { get; init; }

    public required Creature? Target { get; init; }

    public required int PlayCount { get; set; }
}

internal sealed class AfterModifyingCardPlayCountMirrorContext : CombatMirrorContext
{
    public required PredictedCard Card { get; init; }
}

internal sealed class ThrowingAxePredictionState(ThrowingAxe relic) : IPredictionStateForkable
{
    public bool UsedThisCombat { get; set; } = relic._usedThisCombat;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}
