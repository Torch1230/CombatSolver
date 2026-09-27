namespace CombatSolver;

internal sealed partial class SearchOutcomeValueModel
{
    // Offline-learned second-order factors; no per-card or per-relic utility table.
    // Each row is aligned with the same compact feature order as linear/tree terms.
    private double[][]? _factorWeights;

    private static void ValidateFactors(double[][]? factors, int columns)
    {
        if (factors == null) return;
        if (factors.Length != columns || factors.Length == 0
            || factors[0] is not { Length: >= 1 and <= 16 })
            throw new InvalidDataException("Invalid ranking factor dimensions.");
        int rank = factors[0].Length;
        if (factors.Any(row => row == null || row.Length != rank || row.Any(value => !double.IsFinite(value))))
            throw new InvalidDataException("Invalid ranking factors.");
    }

    private double AddInteractions(float[] values, double score)
    {
        if (_factorWeights == null) return score;
        int rank = _factorWeights[0].Length;
        Span<double> sums = stackalloc double[rank], squares = stackalloc double[rank];
        sums.Clear(); squares.Clear();
        for (int i = 0; i < values.Length; i++)
        {
            double value = values[i];
            if (value == 0) continue;
            double[] factors = _factorWeights[i];
            for (int k = 0; k < rank; k++)
            {
                double product = value * factors[k];
                sums[k] += product;
                squares[k] += product * product;
            }
        }
        // Removes self-interactions: only pairs of distinct observed features remain.
        // Factor order is also the canonical offline export/parity accumulation order.
        for (int k = 0; k < rank; k++) score += 0.5 * (sums[k] * sums[k] - squares[k]);
        return score;
    }
}
