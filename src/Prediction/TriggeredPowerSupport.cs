using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class TriggeredPowerSupport
{
    public static void CompensateHistorySince(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        int historyEntryStart)
    {
        CombatPredictionHistory history = simulator.History;
        int nextEntry = historyEntryStart;
        while (true)
        {
            int batchEnd = history.Entries.Count;
            CombatPredictionHistory.HistoryEntryRange batch = history.EntriesBetween(nextEntry, batchEnd);
            foreach (CombatPredictionHistoryEntry entry in batch)
                combat.RecordRelicDamageEntry(entry);
            for (int batchIndex = 0; batchIndex < batch.Count; batchIndex++, nextEntry++)
            {
                switch (batch[batchIndex])
                {
                    case CombatPredictionDamageReceivedEntry damage:
                        CompensateBurrow(simulator, combat, damage);
                        break;
                }
            }
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
            if (simulator.HasPendingChoice)
                return;
            if (nextEntry >= history.Entries.Count)
                return;
        }
    }

    private static void CompensateBurrow(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CombatPredictionDamageReceivedEntry entry)
    {
        Creature target = entry.Receiver;
        if (entry.Result.WasBlockBroken && combat.GetAmount<BurrowedPower>(target) > 0)
        {
            combat.SetAmount<BurrowedPower>(target, 0);
            simulator.State.GetCreature(target).DamageBlock(
                simulator.State.GetCreature(target).Block,
                MegaCrit.Sts2.Core.ValueProps.ValueProp.Move);
            combat.SetMonsterBool(target, "_isStunned", true);
            combat.ForceStunnedMove(target, "BITE_MOVE");
        }
    }

}
