using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;

internal static class FairyInABottleMirrors
{
    public static bool ShouldDie(FairyInABottle potion, ShouldDieMirrorContext context)
        => context.Creature != potion.Owner.Creature;

    public static void AfterPreventingDeath(
        FairyInABottle potion,
        AfterPreventingDeathMirrorContext context)
    {
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("瓶中仙女结算缺少可写的预测状态。");
        effects.ConsumePotion(potion);
        effects.BeforePotionUsed(context.Simulator, potion, context.Creature);
        SimCreatureState creature = context.State.GetCreature(context.Creature);
        int hpBeforeRevive = creature.CurrentHp;
        int maxHp = creature.MaxHp;
        context.Simulator.Heal(context.Creature, HealAmount(maxHp));
        int restored = creature.CurrentHp - hpBeforeRevive;
        if (restored > 0 && context.CombatState is SimulatedCombatState combat)
            combat.RecordDeathSavePotionHpRestored(restored);
        if (context.State.GetCreature(context.Creature).IsAlive)
            effects.AfterPotionUsed(context.Simulator, potion, context.Creature);
    }

    internal static decimal HealAmount(int maxHp) => Math.Max(1m, maxHp * 0.3m);
}

internal static class LizardTailMirrors
{
    public static bool ShouldDieLate(LizardTail relic, ShouldDieMirrorContext context)
    {
        if (context.Creature == relic.Owner.Creature)
        {
            return GetState(relic, context).WasUsed;
        }

        return true;
    }

    public static void AfterPreventingDeath(LizardTail relic, AfterPreventingDeathMirrorContext context)
    {
        GetState(relic, context).WasUsed = true;
        if (context.Simulator.IsRecordingActionRelicTriggers)
            context.Simulator.RecordRelicTrigger(relic, "：复活");

        SimCreatureState creature = context.State.GetCreature(context.Creature);
        int hpBeforeRevive = creature.CurrentHp;
        context.Simulator.Heal(context.Creature, HealAmount(relic, creature.MaxHp));
        int restored = creature.CurrentHp - hpBeforeRevive;
        // The revive spends a cross-combat resource, it is not HP the route earned. Without this the route
        // that walks into lethal damage scores as if it had healed half its max HP for free.
        if (restored > 0 && context.CombatState is SimulatedCombatState combat)
            combat.RecordDeathSaveRelicHpRestored(restored);
    }

    internal static decimal HealAmount(LizardTail relic, int maxHp)
        => Math.Max(1m, maxHp * (relic.DynamicVars.Heal.BaseValue / 100m));

    internal static bool WasUsed(LizardTail relic, CombatPredictionSimulator simulator)
        => simulator.StateStore
            .Peek(relic, () => new LizardTailPredictionState(relic))
            .WasUsed;

    private static LizardTailPredictionState GetState(LizardTail relic, CombatMirrorContext context)
        => context.StateStore.Get(relic, () => new LizardTailPredictionState(relic));
}

internal sealed class LizardTailPredictionState(LizardTail relic) : IPredictionStateForkable
{
    public bool WasUsed { get; set; } = relic.WasUsed;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}
