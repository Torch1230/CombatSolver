using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private Task<int> RunKnownExoskeletonsPathTraceAsync(
        CombatState combat, Player player, int requiredRetentionStep = 4)
    {
        List<KnownExoskeletonsPrefix> frozen = [];
        RunKnownExoskeletonsRouteReplay(combat, player, freeze: frozen);
        if (frozen.Count != 24 || frozen.Any(prefix => prefix.Enemies.Count != 4))
            throw new InvalidOperationException("外骨骼虫路径诊断缺少全部24个四敌冻结前缀。");
        // The original entry watches the generation boundary at step 4; the follow-up
        // entry watches step 5, first lost after that generation survived in v75.
        // Both capture a real outer pool without forcing any path into Solve.
        return RunKnownRoutePathTraceAsync(combat, player,
            frozen.Select(prefix => prefix.Prefix).ToArray(),
            "KnownExoskeletons", "known_exoskeletons_path_search", requiredRetentionStep);
    }
}
