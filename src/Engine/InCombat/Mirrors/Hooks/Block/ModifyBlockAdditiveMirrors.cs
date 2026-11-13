using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;

internal static class ModifyBlockAdditiveMirrors
{
    private static readonly MethodMirrorRegistry<AbstractModel, ModifyBlockMultiplicativeMirrorContext, decimal> Registry = Create();
    private static MethodMirrorRegistry<AbstractModel, ModifyBlockMultiplicativeMirrorContext, decimal> Create()
    {
        var registry = new MethodMirrorRegistry<AbstractModel, ModifyBlockMultiplicativeMirrorContext, decimal>(
            MirrorMethodSpec.Hook(nameof(AbstractModel.ModifyBlockAdditive),
                [typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(CardModel), typeof(CardPlay)]));
        registry.Register<StretchPower>(StretchPowerMirrors.Modify);
        return registry;
    }
    internal static decimal Invoke(AbstractModel listener, ModifyBlockMultiplicativeMirrorContext context)
        => Registry.TryInvokeRegistered(listener, context, out var result) ? result.Value
            : listener.ModifyBlockAdditive(context.Target, context.Amount, context.Props, context.CardSource?.Preview, context.CardPlay);
}
