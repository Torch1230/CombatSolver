namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertEarlyEndTurnAsync(MegaCrit.Sts2.Core.Combat.CombatState combat)
    {
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        SearchPolicySnapshot capturedPolicy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        {
            FixedBudget = true, VerifyIncrementalSearch = false, DetailedDiagnostics = false,
            MeasurePhasePerformance = false, BudgetOverrideMilliseconds = null,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSearchProfile profile = capturedPolicy.Profile with { BeamWidth = 24, MaxExpandedNodes = 200 };
        SolverResult? parallelResult = null;
        foreach (int mode in new[] { 1, 2, 0 })
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
            using ManualResetEventSlim tailSeen = new();
            object observationsGate = new();
            HashSet<StateFingerprint?> endTurnParents = [];
            StateFingerprint? blockedParent = null;
            int claimed = 0, active = 0, overlaps = 0;
            InvalidOperationException injected = new("early EndTurn failure probe");
            SearchRequestWorkTotals totals = new();
            SearchPathObserver observer = new(_ => true, observation =>
            {
                if (observation.Stage != SearchPathObservationStage.Generated
                    || observation.ActionCount != 1
                    || Thread.CurrentThread.Name?.StartsWith("CombatSolver expansion ", StringComparison.Ordinal) != true)
                    return;
                PlanActionKind? kind = observation.Actions.LastOrDefault()?.Kind;
                if (kind == PlanActionKind.EndTurn)
                {
                    lock (observationsGate)
                    {
                        endTurnParents.Add(observation.ParentStateKey);
                        if (claimed != 0 && observation.ParentStateKey == blockedParent) tailSeen.Set();
                    }
                    return;
                }
                if (kind != PlanActionKind.PlayCard) return;
                lock (observationsGate)
                {
                    if (claimed != 0) return;
                    claimed = 1;
                    blockedParent = observation.ParentStateKey;
                    if (endTurnParents.Contains(blockedParent)) tailSeen.Set();
                }
                Interlocked.Increment(ref active);
                try
                {
                    // This action cannot finish until its own parent's EndTurn has produced
                    // a candidate. The old post-action tail scheduling cannot satisfy this.
                    if (!tailSeen.Wait(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("同父EndTurn没有在未完成动作旁独立计算。");
                    Interlocked.Increment(ref overlaps);
                    if (mode == 1) cancellation.Cancel();
                    if (mode == 2) throw injected;
                }
                finally { Interlocked.Decrement(ref active); }
            });
            SearchPolicySnapshot policy = capturedPolicy with
            {
                MaxDegreeOfParallelism = 2, RequestWorkTotals = totals,
                Diagnostics = new SearchDiagnosticsSink(capturedPolicy.Diagnostics.Info, capturedPolicy.Diagnostics.Debug, observer),
            };
            try
            {
                SolverResult result = await Task.Run(() => new CombatBeamSolver(root, names, damage,
                    policy, cancellation.Token, searchProfile: profile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
                if (mode != 0) throw new InvalidOperationException("提前EndTurn的在途失败未传播。");
                parallelResult = result;
            }
            catch (OperationCanceledException error) when (mode == 1 && error.CancellationToken == cancellation.Token) { }
            catch (InvalidOperationException error) when (mode == 2 && ReferenceEquals(error, injected)) { }
            SearchRequestWorkSnapshot work = totals.Snapshot();
            if (overlaps != 1 || active != 0 || totals.RecordedSolverCountForTesting != 1
                || work.TransitionCount <= 0 || work.WorkerAllocatedBytes <= 0)
                throw new InvalidOperationException("提前EndTurn没有证明并发、完整排空及部分工作记录。");
        }
        SolverResult serial = await Task.Run(() => new CombatBeamSolver(root, names, damage,
            capturedPolicy with { MaxDegreeOfParallelism = 1 }, CancellationToken.None,
            searchProfile: profile, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
        AssertEquivalentSearchResults(serial, parallelResult!, "early EndTurn DOP1/DOP2");
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("提前EndTurn合同改变了live战斗。");
    }
}
