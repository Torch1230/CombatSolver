using CombatSolver;

static void Equal(long expected, long actual)
{
    if (expected != actual)
        throw new InvalidOperationException($"Expected {expected}, observed {actual}.");
}

foreach (int parents in new[] { 8, 4, 2, 1, 0 })
    Equal(parents, SearchWaveMemoryPolicy.Capacity(8, 100, parents * 100));
Equal(3, SearchWaveMemoryPolicy.Capacity(8, 100, 399));
Equal(0, SearchWaveMemoryPolicy.Capacity(0, 100, 1000));
Equal(1, SearchWaveMemoryPolicy.Capacity(8, long.MaxValue, long.MaxValue));
Equal(150, SearchWaveMemoryPolicy.Reserve(100));
Equal(151, SearchWaveMemoryPolicy.Reserve(101));
Equal(1200, SearchWaveMemoryPolicy.Reserve(100, 8));
Equal(long.MaxValue, SearchWaveMemoryPolicy.Reserve(long.MaxValue));
Equal(long.MaxValue, SearchWaveMemoryPolicy.Reserve(long.MaxValue / 2, 4));
Equal(0, SearchWaveMemoryPolicy.Reserve(long.MaxValue, 0));

Random random = new(36);
for (int sample = 0; sample < 10_000; sample++)
{
    long reserve = random.NextInt64(1, long.MaxValue);
    long remaining = random.NextInt64(long.MaxValue);
    int desired = random.Next(1, 17);
    int accepted = SearchWaveMemoryPolicy.Capacity(desired, reserve, remaining);
    if (accepted < 0 || accepted > desired || (decimal)accepted * reserve > remaining)
        throw new InvalidOperationException("Admission exceeded its remaining allocation budget.");
    if (accepted < desired && (decimal)(accepted + 1) * reserve <= remaining)
        throw new InvalidOperationException("Admission failed to use a safe smaller wave.");
}
Console.WriteLine("PASS: remaining-budget admission, partial waves, zero capacity, overflow, 10000 bounded cases.");

Equal(16, SearchWaveMemoryPolicy.MaximumQueuedParents(8));
Equal(2, SearchWaveMemoryPolicy.MaximumQueuedParents(1));
Equal(0, SearchWaveMemoryPolicy.GrowCapacity(0, 1));
Equal(6, SearchWaveMemoryPolicy.GrowCapacity(3, 7));
Equal(7, SearchWaveMemoryPolicy.GrowCapacity(4, 7));
Equal(64, SearchWaveMemoryPolicy.GrowCapacity(48, 64));
Equal(int.MaxValue, SearchWaveMemoryPolicy.GrowCapacity(int.MaxValue, int.MaxValue));
Equal(1, SearchWaveMemoryPolicy.GrowCapacity(1, 1));
for (int sample = 0; sample < 10_000; sample++)
{
    int maximum = random.Next(1, int.MaxValue);
    int current = random.Next(0, int.MaxValue);
    Equal(Math.Min(maximum, 2L * current), SearchWaveMemoryPolicy.GrowCapacity(current, maximum));
}
Console.WriteLine("PASS: parent reservation cap, exact saturating growth, odd caps, zero and overflow.");

// A cheap observation must not multiply the cold estimate by every queued parent.
// Keep that estimate as extra wave headroom, and retain buffered measured costs.
const long mib = 1024 * 1024;
Equal(96 * mib, SearchWaveMemoryPolicy.SingleParentReserve(0));
Equal(96 * mib, SearchWaveMemoryPolicy.SingleParentReserve(mib));
Equal(300 * mib, SearchWaveMemoryPolicy.SingleParentReserve(200 * mib));
Equal(6, SearchWaveMemoryPolicy.ParentWaveCapacity(32, 0, 666_666_664));
Equal(32, SearchWaveMemoryPolicy.ParentWaveCapacity(32, mib, 666_666_664));
Equal(144 * mib, SearchWaveMemoryPolicy.ParentWaveReserve(mib, 32));
Equal(1, SearchWaveMemoryPolicy.ParentWaveCapacity(32, 200 * mib, 666_666_664));
Equal(396 * mib, SearchWaveMemoryPolicy.ParentWaveReserve(200 * mib));
Equal(0, SearchWaveMemoryPolicy.ParentWaveCapacity(32, mib, 96 * mib));
Equal(0, SearchWaveMemoryPolicy.ParentWaveReserve(long.MaxValue, 0));
Equal(long.MaxValue, SearchWaveMemoryPolicy.ParentWaveReserve(long.MaxValue));
Equal(0, SearchWaveMemoryPolicy.ParentWaveCapacity(32, long.MaxValue, long.MaxValue));
for (int sample = 0; sample < 10_000; sample++)
{
    long observed = sample % 3 == 0 ? 0 : random.NextInt64(1, 512 * mib);
    long remaining = random.NextInt64(1, 4_000 * mib);
    int desired = random.Next(0, 33);
    int accepted = SearchWaveMemoryPolicy.ParentWaveCapacity(desired, observed, remaining);
    decimal perParent = observed == 0 ? 96 * mib : observed + observed / 2;
    decimal headroom = observed == 0 ? 0 : 96 * mib;
    if (accepted < 0 || accepted > desired
        || (accepted > 0 && headroom + accepted * perParent > remaining))
        throw new InvalidOperationException("Parent admission consumed its burst headroom.");
    if (accepted < desired && headroom + (accepted + 1) * perParent <= remaining)
        throw new InvalidOperationException("Parent admission discarded a fitting parent.");
    if (accepted > 0)
        Equal((long)(headroom + accepted * perParent),
            SearchWaveMemoryPolicy.ParentWaveReserve(observed, accepted));
}
Console.WriteLine("PASS: cold admission, learned waves, 200 MiB spike, burst headroom, saturation, 10000 independent arithmetic cases.");
