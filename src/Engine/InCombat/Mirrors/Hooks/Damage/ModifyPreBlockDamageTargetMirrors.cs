using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;

internal static class ModifyPreBlockDamageTargetMirrors
{
    private static readonly MethodMirrorRegistry<AbstractModel, Context, Creature?> Registry = new(
        MirrorMethodSpec.Hook(nameof(AbstractModel.ModifyPreBlockDamageTarget),
            [typeof(Creature), typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature)]));

    static ModifyPreBlockDamageTargetMirrors()
        => Registry.Register<WuppysLoyaltyPower>((power, c) =>
            c.OriginalTarget == power.Owner.PetOwner?.Creature && c.State.GetCreature(power.Owner).IsAlive
                && c.Props.IsPoweredAttack() ? power.Owner : c.Target);

    internal static Creature? Resolve(CombatPredictionSimulator simulator, Creature originalTarget,
        decimal amount, ValueProp props, Creature? dealer)
    {
        var context = new Context { Simulator = simulator, OriginalTarget = originalTarget,
            Amount = amount, Props = props, Dealer = dealer };
        foreach (var listener in simulator.State.IterateHookListeners())
            context.Target = Registry.Invoke(listener, context, context.Target).Value;
        return context.Target;
    }

    private sealed class Context : CombatMirrorContext
    {
        internal required Creature OriginalTarget { get; init; }
        internal Creature? Target { get; set; }
        internal required decimal Amount { get; init; }
        internal required ValueProp Props { get; init; }
        internal Creature? Dealer { get; init; }
    }
}
