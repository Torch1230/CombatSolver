namespace CombatSolver;

internal static class GcCommitAdmissionChecks
{
    private const long Budget = 100_000_000;
    private const long Reserve = 20_000_000;

    public static void Run()
    {
        PolicyCheck.Run("ordinary pressure asks the owner to release once, then resumes", () =>
        {
            SearchMemoryPressureSignal signal = new();
            int collections = 0;
            void Collect(CancellationToken token, string reason)
            {
                collections++;
                Configure(signal, 0, Collect);
            }
            Configure(signal, 90_000_000, Collect);
            SearchMemoryCommitDecision decision = signal.PrepareCommit(Reserve, default);
            PolicyCheck.Require(decision.Status == SearchMemoryCommitStatus.Reclaim
                && decision.ReclaimReason == null && collections == 0,
                "Preparation must not collect before Search releases its temporary graphs.");
            signal.ReclaimAndContinue(default, "ordinary_pressure");
            decision = signal.RecheckCommitAfterReclaim(decision, default);
            PolicyCheck.Require(decision.Status == SearchMemoryCommitStatus.Ready
                && collections == 1 && signal.ReclaimCount == 1,
                "One released boundary should regain capacity without another collection.");
        });

        PolicyCheck.Run("an unproductive reclaim returns insufficient capacity instead of looping", () =>
        {
            SearchMemoryPressureSignal signal = new();
            int collections = 0;
            Configure(signal, 90_000_000, (_, _) => collections++);
            SearchMemoryCommitDecision decision = signal.PrepareCommit(Reserve, default);
            signal.ReclaimAndContinue(default);
            decision = signal.RecheckCommitAfterReclaim(decision, default);
            PolicyCheck.Require(decision.Status == SearchMemoryCommitStatus.CapacityInsufficient
                && collections == 1,
                "The caller must be able to shrink the wave or leave the limit after one attempt.");
        });

        PolicyCheck.Run("an indivisible oversized commit does not trigger a pointless collection", () =>
        {
            SearchMemoryPressureSignal signal = new();
            Configure(signal, 0, (_, _) => throw new InvalidOperationException("Unexpected collection."));
            SearchMemoryCommitDecision decision = signal.PrepareCommit(Budget * 2, default);
            PolicyCheck.Require(decision.Status == SearchMemoryCommitStatus.CapacityInsufficient,
                "Collecting cannot make a commit larger than the whole region fit.");
            PolicyCheck.Throws<InvalidOperationException>(() =>
                signal.RecheckCommitAfterReclaim(decision, default));
        });

        PolicyCheck.Run("disabled memory control retains cancellation and records the next reserve", () =>
        {
            SearchMemoryPressureSignal signal = new();
            PolicyCheck.Throws<OperationCanceledException>(() =>
                signal.PrepareCommit(Reserve, new CancellationToken(true)));
            SearchMemoryCommitDecision decision = signal.PrepareCommit(long.MaxValue, default);
            PolicyCheck.Require(decision.Status == SearchMemoryCommitStatus.Ready
                && signal.NextCommitReserveBytes == long.MaxValue,
                "Ordinary GC has no allocation quota, but the observation remains available to Runtime.");
        });
    }

    private static void Configure(SearchMemoryPressureSignal signal, long simulatedSpent,
        Action<CancellationToken, string> collect)
        => signal.Configure(GC.GetTotalAllocatedBytes(false) - simulatedSpent,
            Budget, 0, long.MaxValue, collect,
            _ => signal.UseDefaultGcFallback(systemHeadroomConstrained: false));
}
