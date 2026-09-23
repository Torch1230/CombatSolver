using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;

using Registry = MethodMirrorRegistry<AbstractModel, AfterCurrentHpChangedMirrorContext>;

internal static class AfterCurrentHpChangedMirrors
{
    private static readonly MirrorMethodSpec AfterCurrentHpChanged = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterCurrentHpChanged),
        [typeof(Creature), typeof(decimal)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterCurrentHpChangedMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterCurrentHpChanged);

        registry.RegisterIgnored<Crusher>();
        registry.RegisterIgnored<Rocket>();
        registry.Register<RedSkull>(HandleRedSkull);
        registry.Register<NecroMasteryPower>(HandleNecroMasteryPower);
        registry.RegisterIgnored<MeatOnTheBone>();

        return registry;
    }

    private static void HandleRedSkull(RedSkull relic, AfterCurrentHpChangedMirrorContext context)
    {
        var ownerState = context.State.GetCreature(relic.Owner.Creature);
        var threshold = ownerState.MaxHp * (relic.DynamicVars[RedSkull._hpThresholdKey].BaseValue / 100m);
        bool shouldApplyStrength = ownerState.CurrentHp <= threshold;
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("红头骨效果缺少可写的预测状态。");
        bool applied = effects.GetRedSkullStrengthApplied(relic);
        if (shouldApplyStrength != applied)
        {
            // 原版红头骨同样传 cardSource: null（遗物来源）。
            effects.ApplyPowerFromSource(
                typeof(StrengthPower),
                relic.Owner.Creature,
                shouldApplyStrength ? relic.DynamicVars.Strength.IntValue : -relic.DynamicVars.Strength.IntValue,
                relic.Owner.Creature,
                cardSource: null);
            effects.SetRedSkullStrengthApplied(relic, shouldApplyStrength);
        }
    }

    private static void HandleNecroMasteryPower(NecroMasteryPower power, AfterCurrentHpChangedMirrorContext context)
    {
        if (context.Delta < 0m &&
            context.Creature.Monster is Osty &&
            context.Creature.PetOwner == power.Owner.Player)
        {
            context.Simulator.Damage(
                context.State.HittableEnemies,
                -context.Delta * power.Amount,
                ValueProp.Unblockable | ValueProp.Unpowered,
                power.Owner);
        }
    }
}

internal sealed class AfterCurrentHpChangedMirrorContext : CombatMirrorContext
{
    public required Creature Creature { get; init; }

    public required decimal Delta { get; init; }
}
