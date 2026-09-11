namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertStandPatProbeBatchesAsync(
        CombatRootSnapshot rootSnapshot,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot capturedPolicy)
    {
        // A bounded Deep profile reaches the retention probes that the ordinary Short
        // DOP contract cannot exercise. Production profile/budgets are not modified.
        SolverSearchProfile profile = capturedPolicy.Profile with
        {
            BeamWidth = 24,
            MaxExpandedNodes = 1_000,
        };
        foreach (bool injectError in new[] { false, true })
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
            using CountdownEvent bothProbes = new(2);
            InvalidOperationException injectedError = new("unattended stand-pat failure probe");
            int probes = 0;
            int callbacksActive = 0;
            int triggered = 0;
            SearchRequestWorkTotals totals = new();
            SearchPathObserver observer = new(_ => true, observation =>
            {
                if (observation.Stage != SearchPathObservationStage.StandPatProbe
                    || Thread.CurrentThread.Name?.StartsWith(
                        "CombatSolver expansion ", StringComparison.Ordinal) != true)
                {
                    return;
                }
                Interlocked.Increment(ref callbacksActive);
                try
                {
                    int ordinal = Interlocked.Increment(ref probes);
                    if (ordinal > 2)
                        return;
                    bothProbes.Signal();
                    if (!bothProbes.Wait(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("待命评估未在两个 lane 上同时执行。");
                    if (ordinal != 2)
                        return;
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
                    searchProfile: profile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
                throw new InvalidOperationException("待命评估没有传播注入的失败。");
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
                || work.WorkerAllocatedBytes <= 0)
            {
                throw new InvalidOperationException(
                    $"待命评估失败后未排空或未记录部分工作：error={injectError} " +
                    $"triggered={triggered} callbacks={callbacksActive} " +
                    $"records={totals.RecordedSolverCountForTesting} " +
                    $"transitions={work.TransitionCount} allocated={work.WorkerAllocatedBytes}。");
            }
        }

        // Reuse the captured root after both failure paths, then compare the complete
        // result, work counters and action/choice sequence through the normal oracle.
        SolverResult parallel = await Solve(2);
        SolverResult serial = await Solve(1);
        if (parallel.StandPatProbes < 2 || serial.StandPatProbes < 2)
            throw new InvalidOperationException("Deep 固定预算测试未覆盖待命评估。");
        AssertEquivalentSearchResults(serial, parallel, "Deep stand-pat DOP1/DOP2");
        capturedPolicy.Diagnostics.Info(
            $"[CombatSolver/Test] STAND_PAT_BATCH_CONTRACT probes={parallel.StandPatProbes} " +
            $"expanded={parallel.ExpandedNodes} cancellation=True failure=True reuse=True");

        Task<SolverResult> Solve(int parallelism) => Task.Run(() => new CombatBeamSolver(
            rootSnapshot, displayNames, battleDamage,
            capturedPolicy with { MaxDegreeOfParallelism = parallelism },
            CancellationToken.None,
            searchProfile: profile,
            potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
    }
}
