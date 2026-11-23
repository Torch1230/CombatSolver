using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Rooms;

namespace CombatSolver.Beam.Policy;

// TheftEncounterStrategy.cs and SimulatedCombatState.Theft.cs @ d231e9e5:
// count only loot still held by a living or escaped thief. Dead thieves return
// their loot at settlement, even though projection skips mutating room rewards.
internal static class TheftLedger
{
    public static bool IsApplicable(CombatState state)
    {
        if ((state.RunState.CurrentRoom as CombatRoom)?.Encounter?.IdEntry
            is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK")
        {
            return true;
        }

        IReadOnlyList<Sts2Sim.Core.Entities.Creatures.Creature> spawned = state.SpawnedEnemies;
        for (int i = 0; i < spawned.Count; i++)
        {
            if (spawned[i].Monster is GremlinMerc or ThievingHopper or FatGremlin)
                return true;
        }
        return false;
    }

    public static int Outstanding(CombatState state, int escapedTheft = 0)
    {
        int held = escapedTheft;
        IReadOnlyList<Sts2Sim.Core.Entities.Creatures.Creature> spawned = state.SpawnedEnemies;
        for (int i = 0; i < spawned.Count; i++)
        {
            Sts2Sim.Core.Entities.Creatures.Creature thief = spawned[i];
            if (thief.IsDead) continue;
            (int gold, int cards) = HeldBy(thief);
            held += gold + cards;
        }
        return held;
    }

    // Escape removes all powers in Core. Preserve only loot observed on the
    // thief before the action; unrelated gold losses must not count as theft.
    // Core has no passive theft event for a thief that steals and escapes
    // within this single action, so that rare case remains unmeasured.
    public static int NewlyEscaped(CombatState before, CombatState after)
    {
        IReadOnlyList<Sts2Sim.Core.Entities.Creatures.Creature> afterEscaped = after.EscapedCreatures;
        if (afterEscaped.Count == 0)
            return 0;

        IReadOnlyList<Sts2Sim.Core.Entities.Creatures.Creature> beforeEscaped = before.EscapedCreatures;
        IReadOnlyList<Sts2Sim.Core.Entities.Creatures.Creature> beforeSpawned = before.SpawnedEnemies;

        int observed = 0;
        for (int i = 0; i < beforeSpawned.Count; i++)
        {
            Sts2Sim.Core.Entities.Creatures.Creature thief = beforeSpawned[i];
            uint? combatId = thief.CombatId;
            bool wasAlreadyEscaped = false;
            for (int b = 0; b < beforeEscaped.Count; b++)
            {
                if (beforeEscaped[b].CombatId == combatId)
                {
                    wasAlreadyEscaped = true;
                    break;
                }
            }
            if (wasAlreadyEscaped)
                continue;

            bool isNewlyEscaped = false;
            for (int a = 0; a < afterEscaped.Count; a++)
            {
                if (afterEscaped[a].CombatId == combatId)
                {
                    isNewlyEscaped = true;
                    break;
                }
            }
            if (!isNewlyEscaped)
                continue;

            (int gold, int cards) = HeldBy(thief);
            observed += gold + cards;
        }
        return observed;
    }

    private static (int Gold, int Cards) HeldBy(Sts2Sim.Core.Entities.Creatures.Creature thief)
    {
        int gold = 0;
        int cards = 0;
        IReadOnlyList<Sts2Sim.Core.Models.PowerModel> powers = thief.Powers;
        for (int i = 0; i < powers.Count; i++)
        {
            switch (powers[i])
            {
                case ThieveryPower thievery:
                    gold += Math.Max(0, thievery.GoldStolen);
                    break;
                case HeistPower heist:
                    gold += Math.Max(0, (int)heist.Amount);
                    break;
                case SwipePower { StolenCard: not null }:
                    cards++;
                    break;
            }
        }
        return (gold, cards);
    }
}
