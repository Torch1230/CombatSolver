using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private int RunKnownCustomDeferredFrontier(CombatState combat, Player player)
        => RunKnownCustomRouteReplay(combat, player, verifyDeferredFrontier: true);

    private void AssertKnownCustomDeferredFrontierReplay(
        CombatBeamSolver driver,
        SimulationSnapshot initial,
        IReadOnlyList<(PlanAction Action, SimulationSnapshot Snapshot)> prefixes,
        ContinuationStamp leafStamp)
    {
        if (prefixes.Count != 19
            || prefixes.Take(18).Count(item => item.Action.Choice != null) != 3
            || prefixes.Take(18).Count(item => item.Action.Kind == PlanActionKind.UsePotion) != 1
            || prefixes[17].Snapshot.AllEnemiesDead
            || !prefixes[18].Snapshot.AllEnemiesDead)
            throw new InvalidOperationException("恢复合同要求已严格回放的18步非终局前缀与最后胜利动作。");

        CombatBeamSolver.VerifyDeferredTurnFrontierPolicyForTesting(initial);
        _completedChecks.Add("DeferredFrontier:QueueCapacity:PathCost:RoundRobin:ConsumeOnce:Clear");
        _completedChecks.Add("OrderedMutationLineageBuilder:ValueContractOnly");
        int attempts = driver.VerifyDeferredFrontierReplayForTesting(
            initial, prefixes.Take(18).ToArray(), leafStamp,
            prefixes[18].Action, prefixes[18].Snapshot, EnsureWithinDeadline);
        _completedChecks.Add("DeferredFrontier:RealReplay:18PrefixActions:3PrimaryChoices:1Potion:VictorySuffix");
        _completedChecks.Add("DeferredFrontier:RootAndEdgeBudget:Preflight:Stop:Exception:AllTemporarySnapshotsReleased");
        _completedChecks.Add("DeferredFrontier:OriginalParentAndPolicyPreserved:TranspositionEntriesAndCheckedLedgerFieldsUnchanged");
        _completedChecks.Add("DeferredFrontier:MetadataContractOnly:NotFrontierQualityOrPerformance");
        Entry.Logger.Info($"[CombatSolver/Test] DEFERRED_FRONTIER_CONTRACT completed " +
            $"attempts={attempts} prefix_actions=18 suffix_actions=1 " +
            "scope=metadata_and_formal_replay_not_frontier_quality_or_performance");
    }
}
