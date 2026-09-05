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
