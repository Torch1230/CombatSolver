using MegaCrit.Sts2.Core.Entities.Creatures;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    // CreatureCmd.GainMaxHp heals the actual, possibly capped max-HP delta.
    public void GainMaxHp(Creature creature, decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentException("amount must be non-negative. Use LoseMaxHp for max HP loss.", nameof(amount));
        var state = State.GetCreature(creature);
        int before = state.MaxHp;
        state.SetMaxHp((int)(before + amount));
        Heal(creature, state.MaxHp - before);
    }

    // Mirrors CreatureCmd.Heal's state mutation and HP-change hook without mutating real Creature state.
    // VFX/SFX, map-point history, waits, and player hook activation on revive are intentionally omitted.
    public void Heal(Creature creature, decimal amount)
    {
        if (IsEnding && !creature.IsPlayer)
        {
            return;
        }

        var creatureState = State.GetCreature(creature);
        int hpBeforeHeal = creatureState.CurrentHp;
        creatureState.Heal(amount);
        int restoredHp = creatureState.CurrentHp - hpBeforeHeal;
        ActionRelicTriggers?.RecordHealth("heal", creature.CombatId, ResolveDamageSource(null),
            amount, restoredHp, hpBeforeHeal, creatureState.CurrentHp);
        if (restoredHp > 0 && State.CombatState is ICombatPredictionCardEventSink eventSink)
        {
            eventSink.RecordHpRecovered(creature, restoredHp);
        }

        if (amount > 0m)
        {
            HookMirrors.AfterCurrentHpChanged(this, creature, amount);
        }
    }
}
