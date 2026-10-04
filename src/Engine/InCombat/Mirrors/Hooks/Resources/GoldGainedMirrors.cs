using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Resources;

internal static class GoldGainedMirrors
{
    private static readonly MethodMirrorRegistry<AbstractModel, GoldGainMirrorContext, decimal>
        ModifyRegistry = CreateModifyRegistry();
    private static readonly MethodMirrorRegistry<AbstractModel, GoldGainMirrorContext>
        AfterModifyRegistry = CreateAfterModifyRegistry();
    private static readonly MethodMirrorRegistry<AbstractModel, GoldGainMirrorContext>
        AfterGainRegistry = CreateAfterGainRegistry();

    internal static decimal Modify(AbstractModel listener, GoldGainMirrorContext context)
    {
        var result = ModifyRegistry.Invoke(listener, context, context.Amount);
        RequireSupported(result.Kind, listener, nameof(AbstractModel.ModifyGoldGained));
        return result.Value;
    }

    internal static void AfterModify(AbstractModel listener, GoldGainMirrorContext context)
        => RequireSupported(AfterModifyRegistry.Invoke(listener, context).Kind,
            listener, nameof(AbstractModel.AfterModifyingGoldGained),
            allowReviewedIgnored: listener is BowlerHat or Ectoplasm);

    internal static void AfterGain(AbstractModel listener, GoldGainMirrorContext context)
        => RequireSupported(AfterGainRegistry.Invoke(listener, context).Kind,
            listener, nameof(AbstractModel.AfterGoldGained));

    private static void RequireSupported(MirrorDispatchKind kind, AbstractModel listener, string method,
        bool allowReviewedIgnored = false)
    {
        // The generic registry can classify an unregistered override as Ignored
        // from a mod manifest. That label is not a proof about gold or healing.
        if (kind is not (MirrorDispatchKind.NotOverridden or MirrorDispatchKind.Handled)
            && !(kind == MirrorDispatchKind.Ignored && allowReviewedIgnored))
            throw PredictionUnsupportedException.ForContent(
                $"Gold callback {listener.GetType().FullName}.{method} is not mirrored.", listener.GetType());
    }

    private static MethodMirrorRegistry<AbstractModel, GoldGainMirrorContext, decimal> CreateModifyRegistry()
    {
        var registry = new MethodMirrorRegistry<AbstractModel, GoldGainMirrorContext, decimal>(
            MirrorMethodSpec.Hook(nameof(AbstractModel.ModifyGoldGained), [typeof(Player), typeof(decimal)]));
        registry.Register<BowlerHat>((relic, context) => ReferenceEquals(relic.Owner, context.Player)
            && !relic.IsMelted ? context.Amount * relic.DynamicVars["GoldIncrease"].BaseValue : context.Amount);
        registry.Register<Ectoplasm>((relic, context) => ReferenceEquals(relic.Owner, context.Player)
            && !relic.IsMelted ? 0m : context.Amount);
        return registry;
    }

    private static MethodMirrorRegistry<AbstractModel, GoldGainMirrorContext> CreateAfterModifyRegistry()
    {
        var registry = new MethodMirrorRegistry<AbstractModel, GoldGainMirrorContext>(
            MirrorMethodSpec.Hook(nameof(AbstractModel.AfterModifyingGoldGained), [typeof(Player), typeof(decimal)]));
        registry.RegisterIgnored<BowlerHat>();
        registry.RegisterIgnored<Ectoplasm>();
        return registry;
    }

    private static MethodMirrorRegistry<AbstractModel, GoldGainMirrorContext> CreateAfterGainRegistry()
    {
        var registry = new MethodMirrorRegistry<AbstractModel, GoldGainMirrorContext>(
            MirrorMethodSpec.Hook(nameof(AbstractModel.AfterGoldGained), [typeof(Player)]));
        registry.Register<DragonFruit>((relic, context) =>
        {
            if (!ReferenceEquals(relic.Owner, context.Player) || relic.IsMelted)
                return;
            context.Simulator.GainMaxHp(context.Player.Creature, relic.DynamicVars.MaxHp.BaseValue);
        });
        return registry;
    }
}

internal sealed class GoldGainMirrorContext : CombatMirrorContext
{
    public required Player Player { get; init; }
    public required decimal Amount { get; set; }
}
