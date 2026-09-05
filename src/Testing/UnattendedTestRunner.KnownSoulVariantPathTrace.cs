using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private Task<int> RunKnownSoulVariantPathTraceAsync(
        CombatState combat, Player player, bool proveRetainedAlias = false)
    {
        Dictionary<string, IReadOnlyList<KnownRoutePrefix>> variants = [];
        RunKnownSoulGenerationContext(combat, player, fullKnownSuffix: true, frozenVariants: variants);
        if (variants.Count != 5 || !variants.TryGetValue("GENESIS", out IReadOnlyList<KnownRoutePrefix>? primary)
            || variants.Values.Any(prefixes => prefixes.Count != 26))
            throw new InvalidOperationException("Soul 替代路线观察缺少全部五条已严格证明的完整后缀。");
        if (proveRetainedAlias)
        {
            // v72's real surviving representative swaps the fourteenth/fifteenth actions,
            // reaches the same eighteenth state, then disappears at the completed prune.
            // The shared alias proof must replay that observed prefix plus the unchanged
            // final eight actions; matching a state key alone does not prove its suffix.
            if (!variants.TryGetValue("DEFEND_REGENT", out IReadOnlyList<KnownRoutePrefix>? retained))
                throw new InvalidOperationException("Soul 实际保留别名缺少已证明的防御置顶变体。");
            return RunKnownRoutePathTraceAsync(combat, player, retained,
                "KnownSoulRetained", "known_soul_retained_path_search",
                requiredRetentionStep: 18, proveRetentionAliases: true);
        }
        // A retained generation representative may have a different ordered hand. Prove
        // every needle's complete suffix first, then observe the original search policy.
        return RunKnownRoutePathTraceAsync(combat, player, primary,
            "KnownSoulVariants", "known_soul_variant_path_search",
            requiredRetentionStep: 12, frozenVariants: variants);
    }
}
