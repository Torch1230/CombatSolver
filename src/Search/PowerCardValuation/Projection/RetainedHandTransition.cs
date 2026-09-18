namespace CombatSolver;

internal readonly record struct RetainedHandCardFact(int Value, int EnergyCost);

internal readonly record struct RetainedHandTransitionResult(
    int RetainedValue,
    int BlockedDrawValue,
    int PayableRetainedValue)
{
    public int NetValue => (int)Math.Clamp(
        (long)PayableRetainedValue - BlockedDrawValue,
        int.MinValue,
        int.MaxValue);
}

internal static class RetainedHandTransition
{
    internal static RetainedHandTransitionResult Evaluate(
        int nextTurnEnergy,
        int handLimit,
        int normalDrawCount,
        ReadOnlySpan<RetainedHandCardFact> retainedCards,
        ReadOnlySpan<int> nextDrawValues)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nextTurnEnergy);
        ArgumentOutOfRangeException.ThrowIfNegative(handLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(normalDrawCount);
        int retainedCount = Math.Min(handLimit, retainedCards.Length);
        int retainedValue = 0;
        int payableValue = 0;
        int spent = 0;
        foreach (RetainedHandCardFact card in retainedCards[..retainedCount]
                     .ToArray()
                     .OrderByDescending(card => card.Value))
        {
            retainedValue = SaturatingAdd(retainedValue, card.Value);
            if (spent + card.EnergyCost > nextTurnEnergy)
                continue;
            spent += card.EnergyCost;
            payableValue = SaturatingAdd(payableValue, card.Value);
        }

        int retainedDrawCapacity = Math.Max(0, handLimit - retainedCount);
        int blockedDraws = Math.Max(0, normalDrawCount - retainedDrawCapacity);
        int blockedDrawValue = nextDrawValues[..Math.Min(blockedDraws, nextDrawValues.Length)]
            .ToArray()
            .Sum();
        return new(retainedValue, blockedDrawValue, payableValue);
    }

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);
}
