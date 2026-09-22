using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;

using Registry = MethodMirrorRegistry<AbstractModel, AfterModifyingHpLostMirrorContext>;

internal static class AfterModifyingHpLostAfterOstyMirrors
{
    private static readonly MirrorMethodSpec AfterModifyingHpLostAfterOsty = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterModifyingHpLostAfterOsty),
        []);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel modifier, AfterModifyingHpLostMirrorContext context)
    {
        Registry.Invoke(modifier, context);
    }

    // Listener-mask verification only: what the unfiltered path would have resolved for this listener.
    internal static MirrorDispatchKind ResolveDispatchKind(AbstractModel listener)
        => Registry.ResolveDispatchKind(listener);

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterModifyingHpLostAfterOsty);

        registry.RegisterIgnored<BeatingRemnant>();
        registry.Register<BufferPower>(HandleBufferPower);
        registry.RegisterIgnored<IntangiblePower>();
        registry.RegisterIgnored<TheBoot>();
        registry.RegisterIgnored<TungstenRod>();

        return registry;
    }

    private static void HandleBufferPower(BufferPower power, AfterModifyingHpLostMirrorContext context)
    {
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("缓冲层数消耗缺少可写的预测状态。");
        effects.SetPowerAmount(power, Math.Max(0, power.Amount - 1));
    }
}

internal sealed class AfterModifyingHpLostMirrorContext : CombatMirrorContext
{
}
