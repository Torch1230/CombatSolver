using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

// Shared manual-potion stages. A continuation enters Complete after the slot has been
// consumed and Use has run; ordinary replay uses the same prefix and completion body.
internal static class PotionExecutionSupport
{
    internal static bool Prepare(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        PotionModel potion, int slot, Creature? target)
    {
        combat.ConsumePotion(potion.Owner, slot);
        combat.BeforePotionUsed(simulator, potion, target);
        return !combat.HasPendingChoice && PotionOnUseSupport.Use(simulator, combat, potion, target);
    }

    internal static bool Complete(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        PotionModel potion, Creature? target, PlanCardChoice? choice, int historyStart,
        ISet<uint> processedEnemyDeaths)
    {
        if (choice != null && !PotionChoiceSupport.Apply(simulator, potion, choice)) return false;
        if (combat.HasPendingChoice) return false;
        if (simulator.State.GetCreature(potion.Owner.Creature).IsAlive)
            combat.AfterPotionUsed(simulator, potion, target);
        if (combat.HasPendingChoice) return false;
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        if (combat.HasPendingChoice) return false;
        TriggeredPowerSupport.CompensateHistorySince(simulator, combat, historyStart);
        if (combat.HasPendingChoice) return false;
        return CorePowerSupport.ApplyEnemyDeathPowers(simulator, combat, combat.KnownEnemies, processedEnemyDeaths);
    }
}
