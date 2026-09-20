using CombatSolver;

// The runtime can only reserve a fraction of the configured no-GC budget when system headroom is
// exhausted. Entering such a region is worse than not entering one at all: the search exhausts it
// almost immediately, and every later memory checkpoint then pays a region teardown, a forced
// collection and a restart. These checks pin the threshold that decides when a headroom-capped
// region is worth keeping, so the admission gate cannot silently drift.
internal static class GcRegionAdmissionChecks
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    public static void Run()
    {
        PolicyCheck.Run("headroom-capped region far below the configured budget is declined", () =>
        {
            // Reported 0.40.2 boss-fight trace: 12 GiB configured, 2_967_362_558 bytes granted,
            // memory_load 26 523 082 752 of a 29 228 695 790 limit. Entering that region is what
            // produced 33 memory checkpoints and 362 in-search reclaims with a median gain of zero.
            PolicyCheck.Require(
                !SearchGcPolicy.IsNoGcRegionBudgetWorthEntering(12 * GiB, 2_967_362_558),
                "A region capped to a quarter of the configured budget must not be entered.");
        });

        PolicyCheck.Run("a partially capped region is still entered", () =>
        {
            PolicyCheck.Require(
                SearchGcPolicy.IsNoGcRegionBudgetWorthEntering(12 * GiB, 8 * GiB),
                "Losing a third of the configured budget must not disable the no-GC region.");
        });

        PolicyCheck.Run("the gate is scale free for small machines", () =>
        {
            PolicyCheck.Require(
                SearchGcPolicy.IsNoGcRegionBudgetWorthEntering(2 * GiB, 1_800 * MiB),
                "A small machine asking for a small budget keeps its region.");
        });

        PolicyCheck.Run("region below the startable minimum is never worth entering", () =>
        {
            PolicyCheck.Require(
                !SearchGcPolicy.IsNoGcRegionBudgetWorthEntering(4 * GiB, 300 * MiB),
                "A region smaller than the minimum startable budget cannot absorb search work.");
        });

        PolicyCheck.Run("exactly half the configured budget is entered", () =>
        {
            PolicyCheck.Require(
                SearchGcPolicy.IsNoGcRegionBudgetWorthEntering(8 * GiB, 4 * GiB),
                "The threshold is inclusive: half the configured budget is still worth keeping.");
        });

        PolicyCheck.Run("declining a region releases the in-search allocation limit", () =>
        {
            // The admission gate routes a declined region through the existing default-GC fallback.
            // That path must clear the allocation limit, otherwise a checkpoint would still fire
            // and pay a teardown for a region that no longer exists.
            SearchMemoryPressureSignal signal = new();
            signal.Configure(
                GC.GetTotalAllocatedBytes(precise: false),
                512 * MiB,
                20 * GiB,
                27 * GiB,
                (CancellationToken _) => { },
                (CancellationToken _) => { });
            signal.UseDefaultGcFallback(systemHeadroomConstrained: true);
            PolicyCheck.Require(
                !signal.IsLimitReached() && signal.RemainingBytes == long.MaxValue,
                "A declined region must leave no allocation limit for a checkpoint to trip on.");
        });

        Console.WriteLine(
            "GC_REGION_ADMISSION_OK declined the reported 12GiB-to-2.97GiB region; " +
            "partial caps, small-machine budgets and the inclusive threshold passed.");
    }
}
