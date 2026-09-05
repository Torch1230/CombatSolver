namespace CombatSolver;

internal readonly record struct SearchMemoryPressureUsage(
    long AllocatedBytes,
    long AllocationLimitBytes,
    long ProjectedMemoryLoadBytes,
    long SystemMemoryLimitBytes,
    bool Reclaiming)
{
    public static SearchMemoryPressureUsage Disabled { get; } = new(
        0,
        long.MaxValue,
        0,
        long.MaxValue,
        false);

    public double AllocationPressureRatio
        => AllocationLimitBytes == long.MaxValue
            ? 0d
            : Math.Clamp(AllocatedBytes / (double)Math.Max(1, AllocationLimitBytes), 0d, 1d);

    public double SystemPressureRatio
        => SystemMemoryLimitBytes == long.MaxValue
            ? 0d
            : Math.Clamp(ProjectedMemoryLoadBytes / (double)Math.Max(1, SystemMemoryLimitBytes), 0d, 1d);

    public double EffectivePressureRatio => Math.Max(AllocationPressureRatio, SystemPressureRatio);

    public bool SystemPressureDominates => SystemPressureRatio > AllocationPressureRatio;
}

internal sealed class SearchMemoryPressureSignal
{
    private long _allocatedBytesAtStart;
    private long _allocationLimitBytes = long.MaxValue;
    private long _memoryLoadBytesAtStart;
    private long _systemMemoryLimitBytes = long.MaxValue;
    private Action<CancellationToken>? _reclaimAndContinue;
    private Action<CancellationToken>? _useDefaultGcAndContinue;
    private Func<bool>? _unexpectedNoGcLossProbe;
    private int _reclaiming;
    private int _conservativeParallelismRequired;

    public int ReclaimCount { get; private set; }

    public long AllocatedBytes
        => Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - Volatile.Read(ref _allocatedBytesAtStart));

    public long AllocationLimitBytes => Volatile.Read(ref _allocationLimitBytes);

    public long ProjectedMemoryLoadBytes
    {
        get
        {
            long baseline = Volatile.Read(ref _memoryLoadBytesAtStart);
            long allocated = AllocatedBytes;
            return baseline > long.MaxValue - allocated
                ? long.MaxValue
                : baseline + allocated;
        }
    }

    public long SystemMemoryLimitBytes => Volatile.Read(ref _systemMemoryLimitBytes);

    public bool IsSystemLimitReached
        => ProjectedMemoryLoadBytes >= SystemMemoryLimitBytes;

    public bool SystemPressureDominates
    {
        get
        {
            SearchMemoryPressureUsage usage = CaptureUsage();
            return usage.SystemPressureDominates;
        }
    }

    public bool IsEnabled => AllocationLimitBytes != long.MaxValue;

    /// <summary>
    /// Keeps allocation-heavy waves narrow only when the runtime reported that system
    /// headroom itself is constrained. An active NoGC region already sizes waves against its
    /// remaining allocation budget, and a region that merely failed for runtime size limits
    /// says nothing about system memory, so neither case caps the user's requested parallelism.
    /// </summary>
    public bool ConservativeParallelismRequired
        => Volatile.Read(ref _conservativeParallelismRequired) != 0;

    public long RemainingBytes
    {
        get
        {
            long limit = AllocationLimitBytes;
            if (limit == long.MaxValue)
                return long.MaxValue;
            long allocationRemaining = Math.Max(0, limit - AllocatedBytes);
            long systemLimit = SystemMemoryLimitBytes;
            long systemRemaining = systemLimit == long.MaxValue
                ? long.MaxValue
                : Math.Max(0, systemLimit - ProjectedMemoryLoadBytes);
            return Math.Min(allocationRemaining, systemRemaining);
        }
    }

    public SearchMemoryPressureUsage CaptureUsage()
    {
        long limit = AllocationLimitBytes;
        long systemLimit = SystemMemoryLimitBytes;
        return limit == long.MaxValue
            ? SearchMemoryPressureUsage.Disabled
            : new SearchMemoryPressureUsage(
                AllocatedBytes,
                limit,
                ProjectedMemoryLoadBytes,
                systemLimit,
                Volatile.Read(ref _reclaiming) != 0);
    }

    public void Configure(
        long allocatedBytesAtStart,
        long allocationLimitBytes,
        long memoryLoadBytesAtStart,
        long systemMemoryLimitBytes,
        Action<CancellationToken> reclaimAndContinue,
        Action<CancellationToken> useDefaultGcAndContinue,
        Func<bool>? unexpectedNoGcLossProbe = null)
    {
        if (allocationLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(allocationLimitBytes));
        if (memoryLoadBytesAtStart < 0)
            throw new ArgumentOutOfRangeException(nameof(memoryLoadBytesAtStart));
        if (systemMemoryLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(systemMemoryLimitBytes));
        ArgumentNullException.ThrowIfNull(reclaimAndContinue);
        ArgumentNullException.ThrowIfNull(useDefaultGcAndContinue);
        Volatile.Write(ref _allocatedBytesAtStart, allocatedBytesAtStart);
        Volatile.Write(ref _memoryLoadBytesAtStart, memoryLoadBytesAtStart);
        Volatile.Write(ref _systemMemoryLimitBytes, systemMemoryLimitBytes);
        Volatile.Write(ref _reclaimAndContinue, reclaimAndContinue);
        Volatile.Write(ref _useDefaultGcAndContinue, useDefaultGcAndContinue);
        Volatile.Write(ref _unexpectedNoGcLossProbe, unexpectedNoGcLossProbe);
        Volatile.Write(ref _conservativeParallelismRequired, 0);
        Volatile.Write(ref _allocationLimitBytes, allocationLimitBytes);
    }

    public bool IsLimitReached()
        => AllocatedBytes >= AllocationLimitBytes
            || IsSystemLimitReached;

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
        => RunCheckpoint(
            Volatile.Read(ref _reclaimAndContinue)
                ?? throw new InvalidOperationException("搜索内存回收信号尚未配置。"),
            cancellationToken);

    public void UseDefaultGcAndContinue(CancellationToken cancellationToken)
        => RunCheckpoint(
            Volatile.Read(ref _useDefaultGcAndContinue)
                ?? throw new InvalidOperationException("搜索默认 GC 回退信号尚未配置。"),
            cancellationToken);

    private void RunCheckpoint(
        Action<CancellationToken> checkpoint,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _reclaiming, 1);
        try
        {
            checkpoint(cancellationToken);
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
        Volatile.Write(ref _memoryLoadBytesAtStart, 0);
        Volatile.Write(ref _systemMemoryLimitBytes, long.MaxValue);
        Volatile.Write(ref _reclaimAndContinue, null);
        Volatile.Write(ref _useDefaultGcAndContinue, null);
        Volatile.Write(ref _unexpectedNoGcLossProbe, null);
        Volatile.Write(ref _conservativeParallelismRequired, 0);
    }

    public void UseDefaultGcFallback(bool systemHeadroomConstrained)
    {
        Disable();
        Volatile.Write(ref _conservativeParallelismRequired, systemHeadroomConstrained ? 1 : 0);
    }
}
