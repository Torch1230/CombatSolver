using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal enum SolverTheftPolicy
{
    PreserveResources,
    LetEscape,
}

internal static class TheftEncounterStrategy
{
    public static int CompareRecovery(SolverTheftPolicy? policy,
        bool candidateWon, int candidateOutstanding, bool currentWon, int currentOutstanding)
    {
        int victory = currentWon.CompareTo(candidateWon);
        return victory != 0 ? victory : policy == SolverTheftPolicy.PreserveResources
            ? candidateOutstanding.CompareTo(currentOutstanding) : 0;
    }

    public static bool RecoverySatisfied(SolverTheftPolicy? policy, int outstanding)
        => policy != SolverTheftPolicy.PreserveResources || outstanding == 0;

    public static bool IsApplicable(CombatState state)
        => state.Encounter?.Id.Entry is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK"
            || state.Creatures.Any(creature => creature.Monster is GremlinMerc or ThievingHopper or FatGremlin);

    public static int OutstandingStolenResource(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
        => combat.OutstandingStolenResource(simulator);
}
