using CombatSolver;

internal static class GcMemoryBudgetChecks
{
    public static void Run()
    {
        const long limit = 21846584370;
        const long physical = 19080511488;
        const long heap = 5885806336;
        const long fragmented = 4364757576;
        const long live = 1712933896;
        string kind = SearchGcPolicy.CollectAutomaticReclaimForTesting().GetAwaiter().GetResult();
        Require(kind is "background" or "full_blocking", "Automatic reclaim requests the confirmed background path; compaction remains manual.");
        long reusable = SearchGcPolicy.CalculateReusableHeapBytes(heap, fragmented, live);
        long capacity = SearchGcPolicy.CalculateAllocationCapacity(16000000000, limit, physical, reusable);
        Require(reusable == 4172872440 && capacity == 6938945322, "Player trace must retain reclaimed heap allocation capacity.");
        Require(SearchGcPolicy.CalculateReusableHeapBytes(heap, fragmented, heap + 1) == 0, "New allocations consume old holes.");
        Require(SearchGcPolicy.CalculateAllocationCapacity(1000000000, limit, physical, reusable) == 1000000000, "Configured budget remains a hard cap.");
        Require(SearchGcPolicy.CalculateAllocationCapacity(16000000000, limit, limit, reusable) == 0, "Physical pressure still prevents admission.");

        long load = physical;
        int reclaimed = 0;
        SearchMemoryPressureSignal signal = new();
        long allocatedAtStart = GC.GetTotalAllocatedBytes(false) - 1858812152;
        signal.Configure(allocatedAtStart, capacity, physical, limit,
            _ => reclaimed++, _ => {}, systemMemoryLoadProbe: () => load, reusableHeapBytesAtStart: reusable);
        Require(signal.ProjectedMemoryLoadBytes == physical, "Reused allocation must not be added to physical usage.");
        Require(!signal.IsLimitReached() && signal.CanReachCommit(3000000000), "Player trace must allow another wave using reclaimed holes plus physical headroom.");
        load = limit;
        Require(signal.IsLimitReached() && !signal.CanReachCommit(1), "External memory growth immediately stops admission.");
        signal.ReclaimAndContinue(CancellationToken.None);
        Require(reclaimed == 1, "A real pressure checkpoint still calls reclamation.");
        signal.Disable();
        Require(signal.RemainingBytes == long.MaxValue && !signal.IsLimitReached(), "Disabling releases the physical probe.");
        signal.Configure(GC.GetTotalAllocatedBytes(false), capacity, physical, limit, _ => {}, _ => {});
        Require(signal.ProjectedMemoryLoadBytes >= physical && signal.ProjectedMemoryLoadBytes < limit,
            "Platforms without a live physical probe retain allocation projection.");
        Console.WriteLine($"MEMORY_ACCOUNTING_OK reusable={reusable} capacity={capacity}; physical pressure, configured cap, allocation reuse and probe lifetime passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
