using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal enum SolverTheftPolicy
{
    PreserveResources,
    LetEscape,
}

internal static class TheftEncounterStrategy
{
    public static int CompareRecovery(SolverTheftPolicy? policy,
        bool candidateWon, int candidateOutstanding, bool currentWon, int currentOutstanding,
        bool candidateForfeitsGold, bool currentForfeitsGold)
    {
        int victory = currentWon.CompareTo(candidateWon);
        return victory != 0 ? victory : policy == SolverTheftPolicy.PreserveResources
            ? CompareOutstanding(candidateOutstanding, candidateForfeitsGold, currentOutstanding, currentForfeitsGold) : 0;
    }

    public static int CompareOutstanding(int candidateOutstanding, bool candidateForfeitsGold,
        int currentOutstanding, bool currentForfeitsGold)
    {
        int stolen = candidateOutstanding.CompareTo(currentOutstanding);
        return stolen != 0 ? stolen : candidateForfeitsGold.CompareTo(currentForfeitsGold);
    }

    public static bool RecoverySatisfied(SolverTheftPolicy? policy, int outstanding, bool forfeitsGold)
        => policy != SolverTheftPolicy.PreserveResources || outstanding == 0 && !forfeitsGold;

    public static bool ForfeitsEncounterGold(CombatState combat)
        => combat.RunState.GameMode != GameMode.Blitz
            && combat.Encounter is GremlinMercNormal
            && combat.EscapedCreatures.Any(static creature => creature.Monster is FatGremlin);

    public static bool IsApplicable(CombatState state)
        => state.Encounter?.Id.Entry is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK"
            || state.Creatures.Any(creature => creature.Monster is GremlinMerc or ThievingHopper or FatGremlin);
}
