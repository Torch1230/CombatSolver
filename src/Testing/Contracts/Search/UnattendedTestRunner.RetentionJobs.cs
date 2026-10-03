namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertRetentionWorkerBatchesAsync(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy)
    {
        long chargedBytes = 0;
        foreach (bool injectError in new[] { true, false })
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
            InvalidOperationException injected = new("unattended retention failure probe");
            CombatBeamSolver solver = new(root, displayNames, battleDamage, policy, cancellation.Token);
            chargedBytes += await Task.Run(() => solver.VerifyRetentionJobsForTesting(evaluate =>
            {
                int[] visits = new int[257];
                byte[][] allocations = new byte[visits.Length][];
                void RunOrderedSlots()
                {
                    Array.Clear(visits);
                    evaluate(visits.Length, index =>
                    {
                        visits[index]++;
                        allocations[index] = new byte[256];
                    });
                    if (visits.Any(count => count != 1) || allocations.Any(value => value == null))
                        throw new InvalidOperationException("保路作业没有逐项写入自己的槽位。");
                }
                RunOrderedSlots();

                using CountdownEvent both = new(2);
                int started = 0;
                int active = 0;
                int firstThread = 0;
                int secondThread = 0;
                try
                {
                    evaluate(64, _ =>
                    {
                        Interlocked.Increment(ref active);
                        try
                        {
                            int ordinal = Interlocked.Increment(ref started);
                            if (ordinal > 2)
                                return;
                            if (Thread.CurrentThread.Name?.StartsWith(
                                    "CombatSolver expansion ", StringComparison.Ordinal) != true)
                                throw new InvalidOperationException("保路作业没有复用固定 lane。");
                            if (ordinal == 1)
                                firstThread = Environment.CurrentManagedThreadId;
                            else
                                secondThread = Environment.CurrentManagedThreadId;
                            GC.KeepAlive(new byte[4096]);
                            both.Signal();
                            if (!both.Wait(TimeSpan.FromSeconds(5)))
                                throw new InvalidOperationException("保路作业没有在两个 lane 上同时执行。");
                            if (ordinal == 2)
                            {
                                if (injectError)
                                    throw injected;
                                cancellation.Cancel();
                            }
                        }
                        finally { Interlocked.Decrement(ref active); }
                    });
                    throw new InvalidOperationException("保路作业没有传播注入的失败。");
                }
                catch (InvalidOperationException error) when (injectError && ReferenceEquals(error, injected)) { }
                catch (OperationCanceledException error) when (!injectError
                    && error.CancellationToken == cancellation.Token) { }
                if (Volatile.Read(ref active) != 0 || firstThread == 0
                    || secondThread == 0 || firstThread == secondThread)
                    throw new InvalidOperationException("保路作业失败后未排空所有固定 lane。");
                if (injectError)
                    RunOrderedSlots();
            }));
        }
        if (chargedBytes < 3L * 257 * 256 + 4L * 4096)
            throw new InvalidOperationException("保路作业的成功或失败分配没有完整计入请求。");
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] RETENTION_BATCH_CONTRACT dop=2 slots=257 " +
            $"failure=True cancellation=True drained=True reuse=True charged_bytes={chargedBytes}");
    }
}
