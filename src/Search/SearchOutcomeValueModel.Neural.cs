namespace CombatSolver;

internal sealed partial class SearchOutcomeValueModel
{
    // Optional offline-trained residual. All parameters are numeric observations'
    // coefficients; runtime owns neither training nor a machine-learning library.
    internal sealed record NeuralTerm(double[][] InputWeights, double[] HiddenBias, double[] OutputWeights);
    private NeuralTerm? _neural;

    private static void ValidateNeural(NeuralTerm? term, int columns)
    {
        if (term == null) return;
        if (term.HiddenBias is not { Length: >= 1 and <= 32 }
            || term.InputWeights == null || term.InputWeights.Length != columns
            || term.OutputWeights == null || term.OutputWeights.Length != term.HiddenBias.Length
            || term.HiddenBias.Any(value => !double.IsFinite(value))
            || term.OutputWeights.Any(value => !double.IsFinite(value))
            || term.InputWeights.Any(row => row == null || row.Length != term.HiddenBias.Length
                || row.Any(value => !double.IsFinite(value))))
            throw new InvalidDataException("Invalid ranking neural dimensions or coefficients.");
    }

    private static NeuralTerm? CopyNeural(NeuralTerm? term) => term == null ? null : new(
        term.InputWeights.Select(row => row.ToArray()).ToArray(),
        term.HiddenBias.ToArray(), term.OutputWeights.ToArray());

    private double AddNeural(float[] values, double score)
    {
        if (_neural == null) return score;
        int units = _neural.HiddenBias.Length;
        Span<double> hidden = stackalloc double[units];
        _neural.HiddenBias.AsSpan().CopyTo(hidden);
        for (int i = 0; i < values.Length; i++)
        {
            double value = values[i];
            if (value == 0) continue;
            double[] weights = _neural.InputWeights[i];
            for (int k = 0; k < units; k++) hidden[k] += value * weights[k];
        }
        for (int k = 0; k < units; k++) score += Math.Tanh(hidden[k]) * _neural.OutputWeights[k];
        return score;
    }
}
