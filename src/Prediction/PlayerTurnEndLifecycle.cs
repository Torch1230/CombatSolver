using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PlayerTurnEndLifecycle
{
    public static bool RunPhaseOne(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        IReadOnlyList<Creature> participants)
    {
        EndTurnPowerSupport.TriggerVeryEarly(combat, participants);
        if (combat.HasPendingChoice)
            return false;
        TurnStartRelicSupport.TriggerBeforeSideTurnEnd(simulator, combat, participants);
        if (combat.HasPendingChoice)
            return false;
        if (!OrbLifecycleSupport.TriggerBeforeTurnEnd(simulator, combat, player)
            || combat.HasPendingChoice
            || !simulator.SimulateEndPlayerTurnAfterOrbPassives(combat.GetPlayerTurnNumber(player)))
        {
            return false;
        }
        CorePowerSupport.CompletePlayerEarlySideTurnEndEffects(combat, participants);
        return !combat.HasPendingChoice;
    }
}
