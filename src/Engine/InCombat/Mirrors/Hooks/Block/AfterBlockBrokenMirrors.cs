using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;

using Registry = MethodMirrorRegistry<AbstractModel, AfterBlockBrokenMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterBlockBroken.
internal static class AfterBlockBrokenMirrors
{
    private static readonly MirrorMethodSpec AfterBlockBroken = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterBlockBroken),
        [typeof(PlayerChoiceContext), typeof(Creature), typeof(Creature)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterBlockBrokenMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    // Listener-mask verification only: what the unfiltered path would have resolved for this listener.
    internal static MirrorDispatchKind ResolveDispatchKind(AbstractModel listener)
        => Registry.ResolveDispatchKind(listener);

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterBlockBroken);

        registry.RegisterIgnored<BurrowedPower>();
        registry.Register<HandDrill>(HandleHandDrill);

        return registry;
    }

    private static void HandleHandDrill(HandDrill relic, AfterBlockBrokenMirrorContext context)
    {
        if ((context.Breaker == relic.Owner.Creature || context.Breaker?.PetOwner == relic.Owner) &&
            !context.Target.IsPlayer)
        {
            if (context.CombatState is not ICombatPredictionEffectSink effects)
                throw new InvalidOperationException("破甲钻效果缺少可写的预测状态。");
            // 原版破甲钻走遗物来源，cardSource 是 null。
            effects.ApplyPowerFromSource(
                typeof(VulnerablePower),
                context.Target,
                relic.DynamicVars.Vulnerable.IntValue,
                relic.Owner.Creature,
                cardSource: null);
        }
    }
}

internal sealed class AfterBlockBrokenMirrorContext : CombatMirrorContext
{
    public required Creature Target { get; init; }

    public required Creature? Breaker { get; init; }
}
