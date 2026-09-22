using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertLoopReplayBudgetAsync(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        SearchRequestWorkTotals totals = new();
        // Model work already consumed by earlier request members; only 96 actions remain.
        for (int i = 0; i < 4000; i++)
            if (!totals.TryConsumeCycleReplayAction()) throw new InvalidOperationException("Budget setup failed.");
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            RequestWorkTotals = totals, MaxDegreeOfParallelism = 1,
            FixedBudget = true, VerifyIncrementalSearch = true, StopAtAcceptableBattleHpLoss = true,
        };
        var profile = policy.Profile with { MaxExpandedNodes = 2000, SoftTimeBudgetMilliseconds = 20000 };
        async Task<SolverResult> Solve() => await Task.Run(() => new CombatBeamSolver(
            root, names, damage, policy, CancellationToken.None, null, profile,
            potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
        SolverResult first = await Solve();
        SolverResult second = await Solve();
        if (first.CycleReplayActions != 96 || first.CycleReplayContinuations != 1
            || second.CycleReplayActions != 0 || totals.Snapshot().CycleReplayActions != 4096
            || totals.RecordedSolverCountForTesting != 2
            || first.ProjectedBattleHpLost != 0 || second.ProjectedBattleHpLost != 0
            || first.CombatEndedTurn != 1 || second.CombatEndedTurn != 1
            || liveBefore != ContinuationStamp.CaptureLive(combat).StateText)
            throw new InvalidOperationException($"Shared replay work/continuation contract failed: " +
                $"first={first.CycleReplayActions}/{first.CycleReplayContinuations}/{first.CombatEndedTurn} " +
                $"second={second.CycleReplayActions}/{second.CombatEndedTurn} total={totals.Snapshot().CycleReplayActions}.");
        _completedChecks.Add("LoopReplayBudget:TwoSolvers:96Then0:Request4096:PartialFrontier:ZeroLossT1:Incremental:LiveUnchanged");
    }
}
