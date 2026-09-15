using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, AfterCardDiscardedMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterCardDiscarded.
internal static class AfterCardDiscardedMirrors
{
    private static readonly MirrorMethodSpec AfterCardDiscarded = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterCardDiscarded),
        [typeof(PlayerChoiceContext), typeof(CardModel)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterCardDiscardedMirrorContext context)
    {
        using var dispatch = context.Simulator.BeginExecutionDispatch();
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterCardDiscarded);

        registry.Register<Tingsha>(HandleTingsha);
        registry.Register<ToughBandages>(HandleToughBandages);

        return registry;
    }

    private static void HandleTingsha(Tingsha relic, AfterCardDiscardedMirrorContext context)
    {
        if (relic.Owner == context.PreviewCard.Owner &&
            relic.Owner.Creature.Side == context.CombatState.CurrentSide)
        {
            var target = context.Rng.CombatTargets.NextItem(context.State.HittableEnemies);
            if (target is not null)
            {
                context.Simulator.Damage(target, relic.DynamicVars.Damage, relic.Owner.Creature);
            }
        }
    }

    private static void HandleToughBandages(ToughBandages relic, AfterCardDiscardedMirrorContext context)
    {
        if (relic.Owner == context.PreviewCard.Owner &&
            relic.Owner.Creature.Side == context.CombatState.CurrentSide)
        {
            context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
        }
    }
}

internal sealed class AfterCardDiscardedMirrorContext : CombatCardMirrorContext
{
}
