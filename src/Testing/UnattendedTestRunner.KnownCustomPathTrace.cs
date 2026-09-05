using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private Task<int> RunKnownCustomPathTraceAsync(CombatState combat, Player player)
    {
        List<KnownRoutePrefix> prefixes = [];
        RunKnownCustomRouteReplay(combat, player, frozenPrefixes: prefixes);
        if (prefixes.Count != 19)
            throw new InvalidOperationException("Custom 路径诊断缺少全部19个冻结前缀。");
        // The observed state-10 alias must prove the entire frozen suffix before it can
        // anchor this pool. No claim is made about its cycle/ordered scheduling history.
        return RunKnownRoutePathTraceAsync(combat, player, prefixes,
            "KnownCustom", "known_custom_path_search", requiredRetentionStep: 10,
            requirePotionFirstStep: true, proveRetentionAliases: true);
    }
}
