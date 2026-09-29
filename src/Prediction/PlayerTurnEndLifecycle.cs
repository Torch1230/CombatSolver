using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Combat;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PlayerTurnEndLifecycle
{
    public static bool RunPhaseTwo(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Creature> participants,
        int etherealExhaustCount = 0)
    {
        if (!CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(
                simulator, combat, participants, etherealExhaustCount)
            || !TurnStartRelicSupport.TriggerAfterSideTurnEnd(
                simulator, combat, participants, etherealExhaustCount)
            || !HookMirrors.AfterSideTurnEndLate(simulator, CombatSide.Player, participants))
        {
            return false;
        }
        combat.NormalizeCardAfflictions(simulator);
        foreach (Creature participant in participants)
            if (participant.Player is { } player)
                simulator.State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.None;
        return true;
    }

    public static bool RunPhaseOne(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        IReadOnlyList<Creature> participants)
    {
        simulator.State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.End;
        EndTurnPowerSupport.TriggerVeryEarly(combat, participants);
        if (combat.HasPendingChoice)
            return false;
        TurnStartRelicSupport.TriggerBeforeSideTurnEnd(simulator, combat, participants);
        if (combat.HasPendingChoice)
            return false;
        if (!simulator.SimulateEndPlayerTurnBeforeOrbPassives(
                combat.GetPlayerTurnNumber(player), combat.CurrentTurnPlayers))
            return false;
        if (simulator.IsOverOrEnding)
            return true;
        foreach (Creature participant in participants)
        {
            if (participant.Player is { } endingPlayer
                && (!OrbLifecycleSupport.TriggerBeforeTurnEnd(simulator, combat, endingPlayer)
                    || combat.HasPendingChoice))
                return false;
        }
        if (!simulator.SimulateEndPlayerTurnAfterOrbPassives(
                combat.GetPlayerTurnNumber(player), combat.CurrentTurnPlayers))
            return false;
        CorePowerSupport.CompletePlayerEarlySideTurnEndEffects(combat, participants);
        return !combat.HasPendingChoice;
    }
}
