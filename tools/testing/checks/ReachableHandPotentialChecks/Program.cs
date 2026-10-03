using CombatSolver;

Random random = new(20260908);
for (int trial = 0; trial < 10_000; trial++)
{
    var cards = new (int Energy, int Stars, int Value)[random.Next(11)];
    for (int index = 0; index < cards.Length; index++)
        cards[index] = (random.Next(6), random.Next(6), random.Next(1, 100));
    int energy = random.Next(-2, 16);
    int stars = random.Next(-2, 16);
    int expected = 0;
    for (int mask = 0; mask < 1 << cards.Length; mask++)
    {
        int usedEnergy = 0, usedStars = 0, value = 0;
        for (int index = 0; index < cards.Length; index++)
        {
            if ((mask & (1 << index)) == 0)
                continue;
            usedEnergy += cards[index].Energy;
            usedStars += cards[index].Stars;
            value += cards[index].Value;
        }
        if (usedEnergy <= Math.Max(0, energy) && usedStars <= Math.Max(0, stars))
            expected = Math.Max(expected, value);
    }
    Require(ReachableHandValue.Calculate(cards, energy, stars) == expected,
        $"Subset oracle differs in trial {trial}.");
}

Check([(0, 0, 5), (0, 0, 7), (1, 0, 20)], 0, 0);
Check([(1, 0, int.MaxValue), (1, 0, 1)], 2, 0);
Check([(0, 0, int.MaxValue), (0, 0, 1)], 0, 0);
Check(Enumerable.Repeat((Energy: 1, Stars: 1, Value: 3), 80).ToArray(), 32, 32);
Check([(200, 3, 7), (2, 200, 9), (4, 4, 11)], 100, 100);
Check([], 1000, 1000);

var smallHand = new[] { (0, 0, 7), (1, 2, 20), (2, 0, 15) };
for (int iteration = 0; iteration < 1000; iteration++)
    _ = ReachableHandValue.Calculate(smallHand, 2, 2);
long start = GC.GetAllocatedBytesForCurrentThread();
for (int iteration = 0; iteration < 10_000; iteration++)
    _ = ReachableHandValue.Calculate(smallHand, 2, 2);
long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
Require(allocated == 0, $"Small-hand evaluation allocated {allocated} bytes.");
Console.WriteLine("Passed 10,000 subset-oracle cases, overflow/large-buffer cases, and zero-allocation check.");

static void Check((int Energy, int Stars, int Value)[] cards, int energy, int stars)
{
    // Preserve the previous multidimensional recurrence even at integer overflow.
    int energyCapacity = Math.Min(Math.Max(0, energy), cards.Sum(card => card.Energy));
    int starCapacity = Math.Min(Math.Max(0, stars), cards.Sum(card => card.Stars));
    int[,] old = new int[energyCapacity + 1, starCapacity + 1];
    foreach (var card in cards)
        for (int e = energyCapacity; e >= card.Energy; e--)
        for (int s = starCapacity; s >= card.Stars; s--)
            old[e, s] = Math.Max(old[e, s], old[e - card.Energy, s - card.Stars] + card.Value);
    Require(ReachableHandValue.Calculate(cards, energy, stars) == old[energyCapacity, starCapacity],
        "Previous recurrence differs at a boundary case.");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
