using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PersistentRelicSupport
{
    public static bool ShouldPlayerResetEnergy(SimulatedCombatState combat, Player player)
    {
        int simulatedTurn = combat.GetPlayerTurnNumber(player);
        foreach (AbstractModel listener in combat.IterateHookListeners())
        {
            if (listener is IceCream iceCream && ReferenceEquals(iceCream.Owner, player))
            {
                if (simulatedTurn > 1)
                    return false;
                continue;
            }
            if (!listener.ShouldPlayerResetEnergy(player))
                return false;
        }
        return true;
    }

    public static bool ShouldFlush(SimulatedCombatState combat, Player player)
    {
        int simulatedTurn = combat.GetPlayerTurnNumber(player);
        foreach (AbstractModel listener in combat.IterateHookListeners())
        {
            if (listener is RingingTriangle triangle && ReferenceEquals(triangle.Owner, player))
            {
                if (simulatedTurn <= 1)
                    return false;
                continue;
            }
            if (!listener.ShouldFlush(player))
                return false;
        }
        return true;
    }

    public static int ModifyPowerAmountGiven(
        SimulatedCombatState combat,
        PowerModel power,
        Creature giver,
        Creature target,
        int amount,
        CardModel? cardSource)
    {
        if (power is not PoisonPower)
            return amount;

        decimal modified = amount;
        foreach (SneckoSkull skull in combat.IterateHookListeners().OfType<SneckoSkull>())
            modified += skull.ModifyPowerAmountGivenAdditive(power, giver, modified, target, cardSource);
        return (int)modified;
    }

    public static void TriggerAfterPreventingBlockClear(
        CombatPredictionSimulator simulator,
        AbstractModel? preventer,
        Creature creature)
    {
        SimCreatureState state = simulator.State.GetCreature(creature);
        int retained = BlockAfterPreventingClear(preventer, creature, state.Block);
        if (retained < state.Block)
            state.DamageBlock(state.Block - retained, ValueProp.Unpowered);
    }
    // Shared pure projection of the existing clear-prevention settlement, including capped retention.
    public static int BlockAfterPreventingClear(AbstractModel? preventer, Creature creature, int block)
        => preventer is SturdyClamp clamp && ReferenceEquals(clamp.Owner.Creature, creature)
            ? Math.Min(block, clamp.DynamicVars.Block.IntValue)
            : block;

}
