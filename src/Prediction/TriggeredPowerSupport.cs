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
                        CompensateWakeAndBurrow(simulator, combat, damage);
                        break;
                    case CombatPredictionCardPlayFinishedEntry played:
                        CompensateTender(combat, played);
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

    private static void CompensateWakeAndBurrow(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CombatPredictionDamageReceivedEntry entry)
    {
        Creature target = entry.Receiver;
        if (entry.Result.UnblockedDamage != 0)
        {
            AsleepPower? asleep = combat.GetPower<AsleepPower>(target);
            if (asleep is { Amount: > 0 })
            {
                combat.SetAmount<PlatingPower>(target, 0);
                combat.SetPowerAmount(asleep, 0);
                combat.SetMonsterBool(target, "_isAwake", true);
                combat.ForceStunnedMove(target, "SLASH_MOVE");
            }

            SlumberPower? slumber = combat.GetPower<SlumberPower>(target);
            if (slumber is { Amount: > 0 })
            {
                int remaining = slumber.Amount - 1;
                combat.SetPowerAmount(slumber, remaining);
                if (remaining <= 0)
                    combat.ForceStunnedMove(target, "ROLL_OUT_MOVE");
            }
        }

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

    private static void CompensateTender(
        SimulatedCombatState combat,
        CombatPredictionCardPlayFinishedEntry entry)
    {
        Creature owner = entry.Card.Preview.Owner.Creature;
        TenderPower? tender = combat.GetPower<TenderPower>(owner);
        if (tender is not { Amount: > 0 })
            return;

        combat.RecordTenderCardPlayed(owner);
        combat.Apply<StrengthPower>(owner, -1, tender.Applier);
        combat.Apply<DexterityPower>(owner, -1, tender.Applier);
    }
}
