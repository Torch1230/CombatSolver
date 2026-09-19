namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    internal static void RunBackgroundTailChecks()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        IsActive = true;
        if (Environment.GetEnvironmentVariable("GC_POLICY_TRACE") == "1")
            Entry.Logger.InfoSink = Console.WriteLine;
        try
        {
            Check("manual absorption then platform scope exit", () => AssertManualGcAtInSearchCheckpointAsync(1_000_000_000, deadline.Token));
            Check("faulted checkpoint preserves independent deferred cleanup", () => AssertDeferredReclaimSurvivesFaultedCheckpointAsync(1_000_000_000, deadline.Token));
            Check("reference release before coverage capture", () => AssertExhaustionReclaimReferenceCoverageTimingAsync(false, 1, deadline.Token));
            Check("reference release after coverage capture", () => AssertExhaustionReclaimReferenceCoverageTimingAsync(true, 2, deadline.Token));
        }
        finally
        {
            IsActive = false;
            Entry.Logger.InfoSink = null;
        }
        static void Check(string name, Func<Task> action)
        {
            Console.WriteLine("RUN " + name);
            PolicyCheck.Run(name, () => action().GetAwaiter().GetResult());
        }
    }
}
