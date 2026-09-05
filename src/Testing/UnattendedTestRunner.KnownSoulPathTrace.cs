using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private Task<int> RunKnownSoulPathTraceAsync(CombatState combat, Player player)
    {
        List<KnownRoutePrefix> prefixes = [];
        RunKnownSoulRouteReplay(combat, player, prefixes);
        return RunKnownRoutePathTraceAsync(combat, player, prefixes,
            "KnownSoul", "known_soul_path_search", requiredRetentionStep: 11);
    }
}
