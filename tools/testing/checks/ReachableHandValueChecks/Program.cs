using CombatSolver;
using System.Diagnostics;

int cases = 0;
Compare([], 0, 0);
Compare([(0, 0, 7), (0, 0, 11)], 0, 0);
Compare([(0, 0, int.MaxValue), (0, 0, int.MaxValue)], 0, 0);
Compare([(1, 0, int.MaxValue), (1, 0, int.MaxValue)], 2, 0);
Compare([(3, 0, 7), (0, 5, 13), (3, 5, 23)], 3, 5);
Compare([(30, 0, 7), (0, 30, 13), (30, 30, 23)], 30, 30); // heap fallback
Compare(Enumerable.Repeat((1, 1, 3), 80).ToArray(), 32, 32);
Random random = new(7123);
for (int sample = 0; sample < 20_000; sample++)
{
    var cards = new (int Energy, int Stars, int Value)[random.Next(0, 25)];
    for (int index = 0; index < cards.Length; index++)
        cards[index] = (random.Next(0, 6), random.Next(0, 9), random.Next(1, 40));
    Compare(cards, random.Next(-2, 35), random.Next(-2, 40));
}
for (int firstEnergy = 0; firstEnergy <= 3; firstEnergy++)
for (int firstStars = 0; firstStars <= 3; firstStars++)
for (int secondEnergy = 0; secondEnergy <= 3; secondEnergy++)
for (int secondStars = 0; secondStars <= 3; secondStars++)
for (int energy = 0; energy <= 6; energy++)
for (int stars = 0; stars <= 6; stars++)
    Compare([(firstEnergy, firstStars, 3), (secondEnergy, secondStars, 5)], energy, stars);
Console.WriteLine($"PASS {cases} exact comparisons with original 2D algorithm.");
Measure("energy-constrained", [(1, 0, 6), (2, 0, 13), (1, 0, 8), (0, 0, 4), (3, 0, 20)], 3, 0);
Measure("dual-resource", [(1, 2, 6), (2, 0, 13), (1, 4, 8), (0, 0, 4), (3, 1, 20)], 3, 5);
Measure("all-affordable", [(1, 2, 6), (2, 0, 13), (1, 4, 8), (0, 0, 4), (3, 1, 20)], 9, 10);

void Compare((int Energy, int Stars, int Value)[] cards, int energy, int stars)
{
    int expected = Original(cards, energy, stars);
    int actual = ReachableHandValue.Calculate(cards, energy, stars);
    if (expected != actual)
        throw new Exception($"Mismatch: {expected} != {actual}, resources {energy}/{stars}, cards {string.Join(';', cards)}");
    cases++;
}
static int Original((int Energy, int Stars, int Value)[] cards, int energy, int stars)
{
    int totalEnergy = 0, totalStars = 0;
    foreach (var card in cards) { totalEnergy += card.Energy; totalStars += card.Stars; }
    int energyCapacity = Math.Min(Math.Max(0, energy), totalEnergy);
    int starCapacity = Math.Min(Math.Max(0, stars), totalStars);
    int[,] best = new int[energyCapacity + 1, starCapacity + 1];
    foreach (var card in cards)
        for (int e = energyCapacity; e >= card.Energy; e--)
        for (int s = starCapacity; s >= card.Stars; s--)
            best[e, s] = Math.Max(best[e, s], best[e - card.Energy, s - card.Stars] + card.Value);
    return best[energyCapacity, starCapacity];
}
static void Measure(string name, (int Energy, int Stars, int Value)[] cards, int energy, int stars)
{
    const int iterations = 100_000;
    for (int i = 0; i < 10_000; i++)
    {
        Original(cards, energy, stars);
        ReachableHandValue.Calculate(cards, energy, stars);
    }
    long before = GC.GetAllocatedBytesForCurrentThread();
    long start = Stopwatch.GetTimestamp();
    long reference = 0;
    for (int i = 0; i < iterations; i++) reference += Original(cards, energy, stars);
    double oldMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    long oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
    before = GC.GetAllocatedBytesForCurrentThread();
    start = Stopwatch.GetTimestamp();
    long optimized = 0;
    for (int i = 0; i < iterations; i++) optimized += ReachableHandValue.Calculate(cards, energy, stars);
    double newMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    long newBytes = GC.GetAllocatedBytesForCurrentThread() - before;
    if (reference != optimized) throw new Exception("Benchmark result mismatch");
    if (newBytes != 0) throw new Exception("Common-sized hand DP allocated managed memory");
    Console.WriteLine($"{name}: calls={iterations} old_bytes={oldBytes} new_bytes={newBytes} old_ms={oldMs:F2} new_ms={newMs:F2}; microbenchmark only");
}
