using System.Runtime.CompilerServices;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private static int _checks;

    public static void Main()
    {
        // Fixed seed; include partial-order antichains, duplicates, replacement and
        // IEEE edge values. Compare each decision to the frozen List implementation.
        Random random = new(64937);
        for (int sequence = 0; sequence < 8000; sequence++)
        {
            TranspositionLabel first = Next(random);
            TranspositionFrontier actual = new(first);
            Baseline expected = new(first);
            for (int step = 0; step < 64; step++)
            {
                TranspositionLabel next = step % 8 == 0 ? first : Next(random);
                Require(actual.TryAccept(next) == expected.TryAccept(next), "Decision differs from the old frontier.");
            }
        }
        TranspositionLabel middle = new(3, 3, 3, 3, 3, 3);
        TranspositionFrontier changing = new(middle);
        Require(changing.TryAccept(middle with { PotionCount = 2, Score = 2 }), "Tradeoff rejected.");
        Require(changing.TryAccept(new(0, 0, 0, 0, 0, 10)), "Dominating collapse rejected.");
        Require(!changing.TryAccept(middle), "Dominated label survived collapse.");
        Require(changing.TryAccept(new(1, 0, 0, 0, 0, 11)), "Expansion after collapse rejected.");

        for (int i = 0; i < 1000; i++) { _ = new Baseline(middle); _ = new TranspositionFrontier(middle); }
        const int count = 100_000;
        object[] retained = new object[count];
        long baselineBytes = Allocate(retained, middle, baseline: true);
        long candidateBytes = Allocate(retained, middle, baseline: false);
        GC.KeepAlive(retained);
        Require(candidateBytes < baselineBytes, "Single-label storage did not reduce allocation.");
        Console.WriteLine($"Passed {_checks} frontier checks; {count} retained singleton allocations: {baselineBytes} -> {candidateBytes} bytes.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Allocate(object[] retained, TranspositionLabel label, bool baseline)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < retained.Length; i++)
            retained[i] = baseline ? new Baseline(label) : new TranspositionFrontier(label);
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static TranspositionLabel Next(Random random)
    {
        double score = random.Next(0, 80) switch
        {
            0 => double.NaN,
            1 => double.PositiveInfinity,
            2 => double.NegativeInfinity,
            _ => random.Next(-8, 9),
        };
        return new(random.Next(-2, 7), random.Next(-2, 7), random.Next(-2, 7),
            random.Next(-2, 7), random.Next(-2, 7), score);
    }

    private static void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    // Literal pre-change algorithm, with its own comparator to avoid self-comparison.
    private sealed class Baseline(TranspositionLabel first)
    {
        private readonly List<TranspositionLabel> _labels = [first];
        public bool TryAccept(TranspositionLabel next)
        {
            foreach (TranspositionLabel current in _labels)
                if (Dominates(current, next)) return false;
            _labels.RemoveAll(current => Dominates(next, current));
            _labels.Add(next);
            return true;
        }
        private static bool Dominates(TranspositionLabel left, TranspositionLabel right)
            => left.PotionCount <= right.PotionCount
                && left.PotionStrategicCost <= right.PotionStrategicCost
                && left.FutureSoldHp <= right.FutureSoldHp
                && left.CumulativePlayerHpLost <= right.CumulativePlayerHpLost
                && left.ActionCount <= right.ActionCount
                && left.Score >= right.Score;
    }
}
