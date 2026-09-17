namespace CombatSolver;

internal readonly record struct DrawDiscardCardFact(
    int Value,
    int DiscardPayoff = 0);

internal readonly record struct DrawDiscardTransitionResult(
    int BaselineHandValue,
    int DrawDiscardHandValue,
    int DiscardPayoff)
{
    public int NetValue => (int)Math.Clamp(
        (long)DrawDiscardHandValue + DiscardPayoff - BaselineHandValue,
        int.MinValue,
        int.MaxValue);
}

internal static class DrawDiscardTransition
{
    internal static DrawDiscardTransitionResult Evaluate(
        int handLimit,
        int baseDrawCount,
        ReadOnlySpan<DrawDiscardCardFact> retainedCards,
        ReadOnlySpan<DrawDiscardCardFact> nextDrawCards)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(handLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(baseDrawCount);
        List<DrawDiscardCardFact> baseline = new(handLimit);
        foreach (DrawDiscardCardFact card in retainedCards[..Math.Min(
                     retainedCards.Length,
                     handLimit)])
        {
            baseline.Add(card);
        }
        int baselineDraws = Math.Min(
            Math.Min(baseDrawCount, nextDrawCards.Length),
            Math.Max(0, handLimit - baseline.Count));
        for (int index = 0; index < baselineDraws; index++)
            baseline.Add(nextDrawCards[index]);
        int baselineValue = SaturatingSum(baseline.Select(card => card.Value));

        List<DrawDiscardCardFact> withTool = [.. baseline];
        int extraDrawIndex = baselineDraws;
        if (withTool.Count < handLimit && extraDrawIndex < nextDrawCards.Length)
            withTool.Add(nextDrawCards[extraDrawIndex]);
        if (withTool.Count == 0)
            return new(baselineValue, baselineValue, 0);

        int discardIndex = 0;
        long bestTotal = long.MinValue;
        for (int index = 0; index < withTool.Count; index++)
        {
            DrawDiscardCardFact candidate = withTool[index];
            long total = (long)SaturatingSum(withTool.Select(card => card.Value))
                - candidate.Value
                + candidate.DiscardPayoff;
            if (total > bestTotal)
            {
                bestTotal = total;
                discardIndex = index;
            }
        }
        DrawDiscardCardFact discarded = withTool[discardIndex];
        withTool.RemoveAt(discardIndex);
        return new(
            baselineValue,
            SaturatingSum(withTool.Select(card => card.Value)),
            discarded.DiscardPayoff);
    }

    private static int SaturatingSum(IEnumerable<int> values)
    {
        long sum = 0;
        foreach (int value in values)
            sum += Math.Max(0, value);
        return (int)Math.Min(int.MaxValue, sum);
    }
}
