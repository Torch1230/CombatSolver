namespace CombatSolver;

internal sealed partial class SearchOutcomeValueModel
{
    // A learned additive foundation can extrapolate beyond histogram thresholds.
    // Each coordinate uses exact pair differences, so root-constant identities
    // cannot acquire spurious marginal utility. Trees then fit its residuals.
    private static (double[] Weights, double[] Scores) FitLinearTerms(
        IReadOnlyList<RankingObservation> rows, IReadOnlyList<Pair> pairs, string[] names)
    {
        var columns = names.Select((name, i) => (name, i))
            .ToDictionary(p => p.name, p => p.i, StringComparer.Ordinal);
        var differences = new List<(int Pair, double Value)>?[names.Length];
        for (int p = 0; p < pairs.Count; p++)
        {
            var left = rows[pairs[p].Preferred].Features;
            var right = rows[pairs[p].Other].Features;
            void Add(string name, double difference)
            {
                if (difference == 0 || !columns.TryGetValue(name, out int column)) return;
                (differences[column] ??= []).Add((p, difference));
            }
            foreach (var (name, value) in left)
                Add(name, (double)(float)value - (float)right.GetValueOrDefault(name));
            foreach (var (name, value) in right)
                if (!left.ContainsKey(name)) Add(name, -(double)(float)value);
        }
        double[] scales = new double[names.Length];
        for (int column = 0; column < differences.Length; column++)
        {
            if (differences[column] is not { } entries) continue;
            scales[column] = Math.Sqrt(entries.Sum(e => pairs[e.Pair].Weight * e.Value * e.Value));
            if (!(scales[column] > 0) || !double.IsFinite(scales[column]))
                throw new InvalidDataException("Invalid pairwise feature scale.");
            for (int i = 0; i < entries.Count; i++)
                entries[i] = (entries[i].Pair, entries[i].Value / scales[column]);
        }
        double[] margins = new double[pairs.Count], gradients = new double[pairs.Count], hessians = new double[pairs.Count];
        double[] coefficients = new double[names.Length];
        // Fixed before validation: stagewise coordinate Newton steps with shrinkage,
        // not a hand-assigned card/HP conversion. At most 128 nonzero terms.
        for (int round = 0; round < 128; round++)
        {
            for (int p = 0; p < pairs.Count; p++)
            {
                double probability = 1 / (1 + Math.Exp(Math.Clamp(margins[p], -40, 40)));
                gradients[p] = pairs[p].Weight * probability;
                hessians[p] = pairs[p].Weight * probability * (1 - probability);
            }
            int best = -1;
            double bestGain = 0, step = 0;
            for (int column = 0; column < differences.Length; column++)
            {
                if (differences[column] is not { } entries) continue;
                double g = 0, h = 0;
                foreach (var entry in entries)
                {
                    g += gradients[entry.Pair] * entry.Value;
                    h += hessians[entry.Pair] * entry.Value * entry.Value;
                }
                const double regularization = 0.001;
                double gain = g * g / (h + regularization);
                if (gain <= bestGain + 1e-12) continue;
                best = column; bestGain = gain;
                step = LearningRate * g / (h + regularization);
            }
            if (best < 0) break;
            coefficients[best] += step / scales[best];
            foreach (var entry in differences[best]!) margins[entry.Pair] += step * entry.Value;
        }
        // Match inference's float observations and sorted-column accumulation.
        int[] used = Enumerable.Range(0, names.Length).Where(i => coefficients[i] != 0).ToArray();
        double[] scores = rows.Select(row => used.Sum(i => coefficients[i]
            * (float)row.Features.GetValueOrDefault(names[i]))).ToArray();
        return (coefficients, scores);
    }
}
