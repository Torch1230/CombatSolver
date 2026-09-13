namespace CombatSolver;

/// <summary>Pure admission arithmetic; Runtime owns the source of the remaining budget.</summary>
internal static class SearchWaveMemoryPolicy
{
    private const long ColdParentBytes = 64L * 1024 * 1024;

    // The serial fallback retains its original single-parent floor. It does not
    // need the extra allowance for several independently evolving parents.
    public static long SingleParentReserve(long observedParentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(observedParentBytes);
        return Reserve(Math.Max(ColdParentBytes, observedParentBytes));
    }

    // The cold estimate covers each unobserved parent. After a complete observation,
    // retain it once per drained wave as burst headroom, in addition to the buffered
    // whole-search parent high water. This is a predictor, not a hard allocation bound.
    public static long ParentWaveReserve(long observedParentBytes, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(observedParentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return 0;
        if (observedParentBytes == 0)
            return Reserve(ColdParentBytes, count);
        long headroom = Reserve(ColdParentBytes);
        long observed = Reserve(observedParentBytes, count);
        return observed > long.MaxValue - headroom ? long.MaxValue : observed + headroom;
    }

    public static int ParentWaveCapacity(int desiredCapacity, long observedParentBytes, long remainingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(observedParentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingBytes);
        if (observedParentBytes == 0)
            return Capacity(desiredCapacity, Reserve(ColdParentBytes), remainingBytes);
        long headroom = Reserve(ColdParentBytes);
        return Capacity(desiredCapacity, Math.Max(1, Reserve(observedParentBytes)),
            Math.Max(0, remainingBytes - headroom));
    }

    /// <summary>Parent reservation cap; the remaining allocation budget may reduce it.</summary>
    public static int MaximumQueuedParents(int degreeOfParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 1);
        return checked(degreeOfParallelism * 2);
    }

    /// <summary>Doubles the adaptive capacity without overflowing or overshooting its cap.</summary>
    public static int GrowCapacity(int current, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        return current >= maximum - current ? maximum : current * 2;
    }

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
