using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class TurnStartRelicSupport
{
    public static bool TriggerBeforeSideTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Creature> participants)
        => combat.PrepareRelicsBeforeSideTurnStart(simulator, participants);

    public static void TriggerAfterEnergyReset(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player)
        => combat.TriggerRelicsAfterEnergyReset(simulator, player);

    public static bool TriggerAfterPlayerTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        TurnStartChoiceCursor choices)
        => combat.TriggerRelicsAfterPlayerTurnStart(simulator, player, choices);

    public static bool TriggerAfterSideTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CombatSide side,
        IReadOnlyList<Creature> participants)
        => combat.TriggerRelicsAfterSideTurnStart(simulator, side, participants);

    public static bool TriggerAfterSideTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Creature> participants,
        int etherealExhaustCount)
        => combat.CompleteRelicsAfterSideTurnEnd(simulator, participants, etherealExhaustCount);

    public static void TriggerBeforeSideTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Creature> participants)
        => combat.PrepareRelicsBeforeSideTurnEnd(simulator, participants);


    public static void TriggerAfterEnergyResetLate(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player)
    {
        foreach (BoundPhylactery relic in combat.RelicsOf(player)
                     .OfType<BoundPhylactery>()
                     .Where(static relic => !relic.IsMelted))
        {
            if (combat.GetPlayerTurnNumber(player) != 1)
                combat.SummonOsty(simulator, player, relic.DynamicVars.Summon.IntValue);
        }
    }
}
