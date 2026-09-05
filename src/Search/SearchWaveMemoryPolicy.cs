namespace CombatSolver;

/// <summary>Pure admission arithmetic; Runtime owns the source of the remaining budget.</summary>
internal static class SearchWaveMemoryPolicy
{
    public static int Capacity(int desiredCapacity, long parentReserveBytes, long remainingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(desiredCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentReserveBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingBytes);
        return (int)Math.Min(desiredCapacity, remainingBytes / parentReserveBytes);
    }

    public static long Reserve(long observedBytes, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(observedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        long margin = observedBytes / 2;
        long perParent = observedBytes > long.MaxValue - margin
            ? long.MaxValue
            : observedBytes + margin;
        return count == 0 ? 0
            : perParent > long.MaxValue / count ? long.MaxValue
            : perParent * count;
    }
}
