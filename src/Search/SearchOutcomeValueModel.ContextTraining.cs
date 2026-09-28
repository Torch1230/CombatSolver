namespace CombatSolver;

internal sealed partial class SearchOutcomeValueModel
{
    private const int MaximumCrossTurnRowsPerRoot = 256;
    private const int MaximumCrossTurnPairsPerRoot = 1024;

    // An explicit offline experiment, sharing the original row objects. Source
    // balance and the per-root budget remain common with corrective supervision.
    internal static PreparedTraining PrepareContextCalibratedTraining(IReadOnlyList<JointObservation[]> roots)
    {
        Random sampler = new(1); // Independent of both original graph and battle RNG.
        return PrepareRanking(roots, ValidateJointRows, CompareJoint, kindCount: 4,
            correctionSource: row => row.IsCorrection,
            crossTurnPairs: root => CrossTurnComparisons(root, sampler));
    }

    private static IReadOnlyList<(int Preferred, int Other, int Kind)> CrossTurnComparisons(
        JointObservation[] root, Random sampler)
    {
        int[] indices = Enumerable.Range(0, root.Length)
            .Where(i => !root[i].IsCorrection && root[i].Source is TrainingRow).ToArray();
        // Validate every eligible row before this second sampler can omit one.
        int[] turns = new int[root.Length];
        foreach (int index in indices) turns[index] = TrainingTurn(root[index]);
        sampler.Shuffle(indices);
        indices = indices.Take(MaximumCrossTurnRowsPerRoot).Order().ToArray();
        var groups = indices.Select(i => root[i].Groups.ToHashSet()).ToArray();
        List<(int Preferred, int Other, int Kind)> sampled = [];
        int seen = 0;
        for (int a = 0; a < indices.Length; a++)
            for (int b = a + 1; b < indices.Length; b++)
            {
                int ia = indices[a], ib = indices[b];
                if (turns[ia] == turns[ib] || groups[a].Overlaps(groups[b])) continue;
                int order = CompareWitnesses((TrainingRow)root[ia].Source, (TrainingRow)root[ib].Source, out int kind);
                // Never compare local imitation labels or reward a descendant
                // solely because its remaining witnessed suffix is shorter.
                if (order == 0 || kind == 2) continue;
                var pair = (order < 0 ? ia : ib, order < 0 ? ib : ia, kind);
                seen++;
                if (sampled.Count < MaximumCrossTurnPairsPerRoot) sampled.Add(pair);
                else
                {
                    int slot = sampler.Next(seen);
                    if (slot < sampled.Count) sampled[slot] = pair;
                }
            }
        return sampled;
    }
}
