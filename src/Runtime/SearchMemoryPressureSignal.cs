namespace CombatSolver;

internal readonly record struct SearchMemoryPressureUsage(
    long AllocatedBytes,
    long AllocationLimitBytes,
    bool Reclaiming)
{
    public static SearchMemoryPressureUsage Disabled { get; } = new(0, long.MaxValue, false);
}

internal sealed class SearchMemoryPressureSignal
{
    private long _allocatedBytesAtStart;
    private long _allocationLimitBytes = long.MaxValue;
    private Action<CancellationToken>? _reclaimAndContinue;
    private Func<bool>? _unexpectedNoGcLossProbe;
    private int _reclaiming;

    public int ReclaimCount { get; private set; }

    public long AllocatedBytes
        => Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - Volatile.Read(ref _allocatedBytesAtStart));

    public long AllocationLimitBytes => Volatile.Read(ref _allocationLimitBytes);

    public bool IsEnabled => AllocationLimitBytes != long.MaxValue;

    public long RemainingBytes
    {
        get
        {
            long limit = AllocationLimitBytes;
            return limit == long.MaxValue
                ? long.MaxValue
                : Math.Max(0, limit - AllocatedBytes);
        }
    }

    public SearchMemoryPressureUsage CaptureUsage()
    {
        long limit = AllocationLimitBytes;
        return limit == long.MaxValue
            ? SearchMemoryPressureUsage.Disabled
            : new SearchMemoryPressureUsage(
                AllocatedBytes,
                limit,
                Volatile.Read(ref _reclaiming) != 0);
    }

    public void Configure(
        long allocatedBytesAtStart,
        long allocationLimitBytes,
        Action<CancellationToken> reclaimAndContinue,
        Func<bool>? unexpectedNoGcLossProbe = null)
    {
        if (allocationLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(allocationLimitBytes));
        ArgumentNullException.ThrowIfNull(reclaimAndContinue);
        Volatile.Write(ref _allocatedBytesAtStart, allocatedBytesAtStart);
        Volatile.Write(ref _reclaimAndContinue, reclaimAndContinue);
        Volatile.Write(ref _unexpectedNoGcLossProbe, unexpectedNoGcLossProbe);
        Volatile.Write(ref _allocationLimitBytes, allocationLimitBytes);
    }

    public bool IsLimitReached()
        => AllocatedBytes >= AllocationLimitBytes;

    public bool CanReachCommit(long reservedBytes)
    {
        if (reservedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(reservedBytes));
        long remaining = RemainingBytes;
        return remaining == long.MaxValue || reservedBytes <= remaining;
    }

    public bool HasUnexpectedNoGcLoss()
        => Volatile.Read(ref _unexpectedNoGcLossProbe)?.Invoke() == true;

    public void ReclaimAndContinue(CancellationToken cancellationToken)
    {
        Action<CancellationToken> reclaim = Volatile.Read(ref _reclaimAndContinue)
            ?? throw new InvalidOperationException("搜索内存回收信号尚未配置。");
        Volatile.Write(ref _reclaiming, 1);
        try
        {
            reclaim(cancellationToken);
            ReclaimCount++;
        }
        finally
        {
            Volatile.Write(ref _reclaiming, 0);
        }
    }

    public void Disable()
    {
        Volatile.Write(ref _allocationLimitBytes, long.MaxValue);
        Volatile.Write(ref _reclaimAndContinue, null);
        Volatile.Write(ref _unexpectedNoGcLossProbe, null);
    }
}
