namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertAdmittedJobFailureAccountingAsync(
        MegaCrit.Sts2.Core.Combat.CombatState combat)
    {
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat));
        policy = policy with
        {
            Profile = policy.Profile with { MaxExpandedNodes = 250, SoftTimeBudgetMilliseconds = 10_000 },
            FixedBudget = true,
            VerifyIncrementalSearch = false,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        await AssertParallelExpansionFailureDrainAsync(root, names, damage, policy);
        await Task.Run(() => new CombatBeamSolver(root, names, damage,
            policy with { MaxDegreeOfParallelism = 2 }, CancellationToken.None,
            potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
    }

    private static async Task AssertParallelExpansionFailureDrainAsync(
        CombatRootSnapshot rootSnapshot,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot capturedPolicy)
    {
        foreach (bool injectError in new[] { false, true })
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
            InvalidOperationException injectedError = new("unattended admitted-job failure probe");
            int generated = 0;
            int callbacksActive = 0;
            int triggered = 0;
            long injectedAllocation = 0;
            SearchRequestWorkTotals totals = new();
            SearchPathObserver observer = new(_ => true, observation =>
            {
                if (observation.Stage != SearchPathObservationStage.Generated
                    || Thread.CurrentThread.Name?.StartsWith(
                        "CombatSolver expansion ", StringComparison.Ordinal) != true
                    || observation.Actions.LastOrDefault()?.Kind != PlanActionKind.PlayCard)
                {
                    return;
                }
                Interlocked.Increment(ref callbacksActive);
                try
                {
                    if (Interlocked.Increment(ref generated) != 1)
                        return;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    GC.KeepAlive(new byte[64 * 1024 * 1024]);
                    Interlocked.Add(ref injectedAllocation, GC.GetAllocatedBytesForCurrentThread() - before);
                    Volatile.Write(ref triggered, 1);
                    if (injectError)
                        throw injectedError;
                    cancellation.Cancel();
                }
                finally
                {
                    Interlocked.Decrement(ref callbacksActive);
                }
            });
            SearchPolicySnapshot policy = capturedPolicy with
            {
                MaxDegreeOfParallelism = 2,
                RequestWorkTotals = totals,
                Diagnostics = new SearchDiagnosticsSink(
                    capturedPolicy.Diagnostics.Info,
                    capturedPolicy.Diagnostics.Debug,
                    observer),
            };
            try
            {
                await Task.Run(() => new CombatBeamSolver(
                    rootSnapshot, displayNames, battleDamage, policy, cancellation.Token,
                    potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
                throw new InvalidOperationException("在途并行作业没有传播注入的失败。");
            }
            catch (OperationCanceledException ex) when (!injectError
                && ex.CancellationToken == cancellation.Token)
            {
            }
            catch (InvalidOperationException ex) when (injectError
                && ReferenceEquals(ex, injectedError))
            {
            }
            SearchRequestWorkSnapshot work = totals.Snapshot();
            if (Volatile.Read(ref triggered) != 1
                || Volatile.Read(ref callbacksActive) != 0
                || totals.RecordedSolverCountForTesting != 1
                || work.TransitionCount <= 0
                || work.WorkerAllocatedBytes < injectedAllocation)
            {
                throw new InvalidOperationException(
                    $"在途并行作业未排空或未精确记录部分工作：error={injectError} " +
                    $"triggered={triggered} callbacks={callbacksActive} " +
                    $"records={totals.RecordedSolverCountForTesting} " +
                    $"transitions={work.TransitionCount} allocated={work.WorkerAllocatedBytes} " +
                    $"injected_allocation={injectedAllocation}。");
            }
        }
        // The caller subsequently reuses this captured root for complete DOP2/DOP1 comparison.
    }
}
