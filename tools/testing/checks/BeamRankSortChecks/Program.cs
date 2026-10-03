using CombatSolver;
using System.Text.Json;

Random random = new(20260912);
int cases = 0, entries = 0;
int[] sizes = [0, 1, 2, 3, 15, 16, 17, 31, 32, 33, 63, 64, 65, 255, 256, 257, 1024, 2048];
double[] extremes = [double.NaN, double.NegativeInfinity, double.PositiveInfinity, -0d, 0d, double.MinValue, double.MaxValue];
foreach (bool boss in new[] { false, true })
foreach (int enemies in new[] { 1, 2 })
foreach (bool negative in new[] { false, true })
{
    Scorer scorer = new(boss, enemies, new Run
    {
        InitialPersistentBuffValue = negative ? -5 : 12,
        InitialEnemyStrengthSuppression = negative ? -1 : 4,
        InitialEnemyWeakTurns = negative ? -9 : 2,
        InitialRetainedAttackValue = negative ? -20 : 6
    });
    foreach (int n in sizes)
    foreach (int pattern in Enumerable.Range(0, 5))
    {
        List<SearchNode> input = [];
        for (int i = 0; i < n; i++)
        {
            if (pattern == 3 && i > 0 && i % 3 == 0)
            {
                input.Add(input[i / 3]); // Same object appears more than once.
                continue;
            }
            int Value() => pattern == 0 ? 0 : random.Next(negative ? -100 : 0, 101);
            input.Add(new SearchNode
            {
                Score = pattern == 4 ? extremes[i % extremes.Length] : pattern == 0 ? 0 : random.Next(-20, 21) * 30000d,
                ActionCount = pattern == 0 ? 0 : random.Next(0, 12),
                Snapshot = new()
                {
                    Energy = Value(), PersistentBuffValue = Value(), LatentSetupValue = Value(),
                    FutureResourceValue = Value(), ReplayPotentialValue = Value(),
                    RetainedAttackValue = Value(), DelayedDamageValue = Value(),
                    SandpitRemaining = Value(), EnemyStrengthSuppression = Value(),
                    EnemyWeakTurns = Value(), OffensiveProgressValue = Value()
                }
            });
        }
        Comparison<SearchNode> original = (a, b) => Scorer.CompareBeamRankOrder(
            scorer.BeamRankScore(a), a.Snapshot.OffensiveProgressValue, a.ActionCount,
            scorer.BeamRankScore(b), b.Snapshot.OffensiveProgressValue, b.ActionCount);
        if (pattern == 1) input.Sort(original);
        if (pattern == 2) { input.Sort(original); input.Reverse(); }
        List<SearchNode> expected = [.. input], actual = [.. input];
        expected.Sort(original);
        scorer.SortByBeamRank(actual);
        if (actual.Count != expected.Count || actual.Where((node, index) => !ReferenceEquals(node, expected[index])).Any())
            throw new InvalidOperationException($"Order mismatch: boss={boss}, enemies={enemies}, n={n}, pattern={pattern}");
        cases++;
        entries += n;
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { status = "Passed", cases, entries, runtime = Environment.Version.ToString(), scope = "Extracted production score/sort/comparison; minimal immutable snapshot inputs; exact reference order vs original List.Sort" }));
