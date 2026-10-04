using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;

using Registry = MethodMirrorRegistry<AbstractModel, AfterModifyingBlockAmountMirrorContext>;

/// <summary>
/// Mirrors prediction-relevant commits from <see cref="Hook.AfterModifyingBlockAmount"/>.
/// </summary>
internal static class AfterModifyingBlockAmountMirrors
{
    private static readonly MirrorMethodSpec AfterModifyingBlockAmount = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterModifyingBlockAmount),
        [typeof(decimal), typeof(CardModel), typeof(CardPlay)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterModifyingBlockAmountMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterModifyingBlockAmount);

        registry.RegisterIgnored<FastenPower>();
        registry.Register<PaelsLegion>(HandlePaelsLegion);
        registry.Register<Vambrace>(HandleVambrace);

        return registry;
    }

    private static void HandlePaelsLegion(PaelsLegion relic, AfterModifyingBlockAmountMirrorContext context)
    {
        if (context.ModifiedBlock <= 0 || context.CardPlay is null)
        {
            return;
        }

        var state = context.StateStore.Get(relic, () => new PaelsLegionPredictionState(relic));
        if (!state.HasCompletedAffectedCardPlay)
            state.AffectedCardPlay ??= context.CardPlay;
    }

    private static void HandleVambrace(Vambrace relic, AfterModifyingBlockAmountMirrorContext context)
    {
        if (context.ModifiedBlock <= 0 || context.CardSource is null)
        {
            return;
        }

        var state = context.StateStore.Get(relic, () => new VambracePredictionState(relic));
        state.TriggeringCard = context.CardSource.Original;
    }
}

internal sealed class AfterModifyingBlockAmountMirrorContext : CombatMirrorContext
{
    public required decimal ModifiedBlock { get; init; }

    public required PredictedCard? CardSource { get; init; }

    public required CardPlay? CardPlay { get; init; }
}
