using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertSurvivableBoundaryAsync(CombatState combat)
    {
        string before = ContinuationStamp.CaptureLive(combat).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        {
            FixedBudget = true, MaxDegreeOfParallelism = 16,
            VerifyIncrementalSearch = false, DetailedDiagnostics = false,
            MeasurePhasePerformance = false, BudgetOverrideMilliseconds = null,
        };
        SolverSearchProfile profile = policy.Profile with
        {
            BeamWidth = 135, MaxExpandedNodes = 6000,
            MaxCardBranchesPerNode = 72, MaxPileChoiceBranchesPerAction = 42,
            MaxHandChoiceBranchesPerAction = 54, SoftTimeBudgetMilliseconds = 90000,
        };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(100));
        SolverResult legacy = await Task.Run(() => new CombatBeamSolver(root, names,
            damage, policy, deadline.Token, searchProfile: profile)
            { DisableSurvivableBoundaryFallbackForTesting = true }.Solve());
        SolverResult recovered = await Task.Run(() => new CombatBeamSolver(root, names,
            damage, policy, deadline.Token, searchProfile: profile).Solve());
        if (legacy.BoundaryReason != SearchBoundaryReason.NodeLimit
            || recovered.BoundaryReason != SearchBoundaryReason.NodeLimit
            || legacy.ExpandedNodes != 6000 || recovered.ExpandedNodes != 6000
            || legacy.TransitionCount != recovered.TransitionCount
            || !legacy.OnlyDeathRoutesFound || recovered.OnlyDeathRoutesFound
            || !legacy.Snapshot.PlayerDead || recovered.Snapshot.PlayerDead)
            throw new InvalidOperationException(
                $"Survivable boundary fixture did not restore a safe prefix at the same work boundary. " +
                $"legacy={legacy.BoundaryReason}/{legacy.ExpandedNodes}/{legacy.TransitionCount}" +
                $"/death={legacy.OnlyDeathRoutesFound}/{legacy.Snapshot.PlayerDead}" +
                $"/hp={legacy.Snapshot.PlayerHp}/enemy={legacy.Snapshot.EnemyHp}" +
                $"/released={legacy.NodeLimitSnapshotsReleased}/layer_stops={legacy.TurnLayerBudgetStops}; " +
                $"new={recovered.BoundaryReason}/{recovered.ExpandedNodes}/{recovered.TransitionCount}" +
                $"/death={recovered.OnlyDeathRoutesFound}/{recovered.Snapshot.PlayerDead}" +
                $"/hp={recovered.Snapshot.PlayerHp}/enemy={recovered.Snapshot.EnemyHp}" +
                $"/released={recovered.NodeLimitSnapshotsReleased}/layer_stops={recovered.TurnLayerBudgetStops}; " +
                $"policy={policy.PotionPolicy}");
        // Independently rebuild the selected action prefix, both from its root and
        // one action at a time. This exercises the released checkpoint's real choices,
        // ordered piles, AI, history and RNG without advancing the live game.
        CombatBeamSolver replay = new(root, names, damage, policy, deadline.Token,
            searchProfile: profile);
        List<SimulationSnapshot> owned = [];
        try
        {
            SimulationSnapshot incremental = ReplayKnownCustom(replay, [], null, 0, 0, owned);
            List<PlanAction> prefix = [];
            foreach (PlanAction action in recovered.BestNode.Actions)
            {
                prefix.Add(action);
                incremental = ReplayKnownCustom(replay, [action], incremental,
                    incremental.Turn, prefix.Count - 1, owned);
                SimulationSnapshot full = ReplayKnownCustom(replay, prefix, null, 0, 0, owned);
                if (replay.CaptureDiagnosticContinuation(incremental).StateText
                    != replay.CaptureDiagnosticContinuation(full).StateText)
                    throw new InvalidOperationException($"Survival prefix replay mismatch after {prefix.Count} actions.");
            }
            if (incremental.PlayerDead || incremental.PlayerHp != recovered.Snapshot.PlayerHp
                || incremental.EnemyHp != recovered.Snapshot.EnemyHp
                || incremental.CumulativePlayerHpLost != recovered.Snapshot.CumulativePlayerHpLost)
                throw new InvalidOperationException("Rebuilt survival prefix changed its saved health or enemies.");
        }
        finally
        {
            foreach (SimulationSnapshot snapshot in owned)
                snapshot.ReleaseSimulator();
        }
        if (ContinuationStamp.CaptureLive(combat).StateText != before)
            throw new InvalidOperationException("Survival checkpoint contract changed the live root.");
        _completedChecks.Add("SurvivableBoundary:old-death:new-safe-prefix:same-6000-nodes:released-history:full-incremental-state:root-unchanged");
    }
}
