using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;

using Registry = MethodMirrorRegistry<AbstractModel, ModifyUnblockedDamageTargetMirrorContext, Creature>;

internal static class ModifyUnblockedDamageTargetMirrors
{
    private static readonly Registry Registry = CreateRegistry();

    private static Registry CreateRegistry()
    {
        var registry = new Registry(MirrorMethodSpec.Hook(nameof(AbstractModel.ModifyUnblockedDamageTarget),
            [typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature)]));
        registry.Register<DieForYouPower>((power, context) =>
            context.Target == power.Owner.PetOwner?.Creature && context.State.GetCreature(power.Owner).IsAlive
                && context.Props.IsPoweredAttack() ? power.Owner : context.Target);
        return registry;
    }

    internal static Creature Invoke(AbstractModel listener, ModifyUnblockedDamageTargetMirrorContext context)
    {
        // A new redirection override needs an explicit state-aware handler. Returning the
        // original target for an unknown override would silently change the damage recipient.
        var result = Registry.Invoke(listener, context, context.Target);
        if (result.Kind is not (MirrorDispatchKind.NotOverridden or MirrorDispatchKind.Handled))
            throw new NotSupportedException($"Unrepresented damage redirection: {listener.GetType().FullName}.");
        return result.Value;
    }
}

internal sealed class ModifyUnblockedDamageTargetMirrorContext : CombatMirrorContext
{
    public required Creature Target { get; set; }
    public required decimal Amount { get; init; }
    public required ValueProp Props { get; init; }
    public required Creature? Dealer { get; init; }
}
