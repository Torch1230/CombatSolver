using System.Diagnostics;
using System.Text.Json;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

/// <summary>Main-thread backend admission; one captured root selects one execution backend.</summary>
internal static class SearchBackendPolicy
{
    internal static SearchPolicySnapshot Capture(CombatRootSnapshot root, SearchPolicySnapshot policy)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("Search backend admission must run on the main thread.");
        if (policy.CompactRoot != null)
            throw new InvalidOperationException("Search policy already owns an admitted backend root.");
        var preparation = Stopwatch.StartNew();
        string rejection;
        CompactCombatRoot? compact = null;
        if (policy.IncludeTurnSetup)
            rejection = "initial turn setup requires the model selector";
        else if (policy.VerifyIncrementalSearch)
            rejection = "incremental diagnostic requires full model replay";
        else
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            CompactCombatRoot.TryCreate(root.ForkSimulator(), root.PlayerIdentity, out compact, out rejection);
        }
        preparation.Stop();
        policy.Diagnostics.Info($"[CombatSolver] SEARCH_BACKEND backend={(compact == null ? "model" : "compact")} " +
            $"preparation_ms={preparation.Elapsed.TotalMilliseconds:F3} rejection={JsonSerializer.Serialize(rejection)}");
        return policy with { CompactRoot = compact };
    }

    internal static void ReportCompleted(SearchPolicySnapshot policy)
    {
        if (policy.CompactRoot is not { } compact) return;
        var counts = compact.Counts;
        policy.Diagnostics.Info($"[CombatSolver] SEARCH_COMPACT_WORK completed={counts.CompletedReplays} " +
            $"pending={counts.PendingReplays} materializations={counts.Materializations}");
    }
}
