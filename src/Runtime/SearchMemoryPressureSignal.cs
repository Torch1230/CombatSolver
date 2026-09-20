namespace CombatSolver;

internal enum SearchMemoryCommitStatus
{
    Ready,
    Reclaim,
    CapacityInsufficient,
}

// A value decision at an already drained boundary, not a reservation or a GC lease.
// Runtime alone decides whether a collection loss needs recovery before rechecking.
internal readonly record struct SearchMemoryCommitDecision(
    SearchMemoryCommitStatus Status,
    long ReservedBytes,
    string? ReclaimReason = null);

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
    private Action<CancellationToken, string>? _reclaimAndContinue;
    private Action<CancellationToken>? _useDefaultGcAndContinue;
    private Func<bool>? _unexpectedNoGcLossProbe;
    private Func<long>? _systemMemoryLoadProbe;
    private long _reusableHeapBytesAtStart;
    private Func<SearchGcLifecycleSnapshot>? _gcLifecycleProbe;
    private long _lastReclaimMaxObservedGcPauseTicks;
    private int _reclaiming;
    private int _conservativeParallelismRequired;
    private Action<long, CancellationToken>? _noGcRecoveryProbe;
    private Action<long>? _noGcFallbackObserver;
    private int _noGcRecoveryAllowed;

    public int ReclaimCount { get; private set; }

    // Coordinator-owned observation at drained boundaries; Runtime uses it when
    // deciding whether a replacement region can absorb useful work after its reserve.
    public long NextCommitReserveBytes { get; private set; }

    public void ObserveCommitReserve(long reservedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reservedBytes);
        NextCommitReserveBytes = reservedBytes;
    }

    /// <summary>连续多少次搜索内回收没有腾出余量；有进展的那次回收把它清零。</summary>
    public int ConsecutiveNoProgressReclaims { get; private set; }

    /// <summary>最近一次搜索内回收腾出的字节数（非压缩 Gen2 前后的活数据差）。</summary>
    public long LastReclaimRegainedBytes { get; private set; }

    /// <summary>
    /// 一次回收算不算有进展的门槛：1 MiB。实测的空转回收中位增益为 0 字节，而正常回收腾出的是
    /// 整段候选图（远大于 1 MiB），所以这个量级只区分“腾出了空间”和“什么都没腾出”。
    /// </summary>
    internal const long NoProgressReclaimThresholdBytes = 1024L * 1024;

    public TimeSpan LastReclaimMaxObservedGcPause
        => TimeSpan.FromTicks(Volatile.Read(ref _lastReclaimMaxObservedGcPauseTicks));

    public SearchGcLifecycleSnapshot CaptureGcLifecycle()
        => Volatile.Read(ref _gcLifecycleProbe)?.Invoke() ?? default;

    internal void SetGcLifecycleProbe(Func<SearchGcLifecycleSnapshot> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        Volatile.Write(ref _gcLifecycleProbe, probe);
    }

    internal void ObserveReclaimGcPause(TimeSpan pause)
    {
        if (pause < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pause));
        long previous = Volatile.Read(ref _lastReclaimMaxObservedGcPauseTicks);
        if (pause.Ticks > previous)
            Volatile.Write(ref _lastReclaimMaxObservedGcPauseTicks, pause.Ticks);
    }

    /// <summary>
    /// 搜索内回收完成后的记账入口。只有真正重建 No-GC 区域的那条回收路径调用它；
    /// 回退到常规 GC 不算一次回收，因为它不再重建区域。
    /// </summary>
    public void ObserveReclaimGain(long regainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(regainedBytes);
        LastReclaimRegainedBytes = regainedBytes;
        ConsecutiveNoProgressReclaims = regainedBytes < NoProgressReclaimThresholdBytes
            ? ConsecutiveNoProgressReclaims + 1
            : 0;
    }

    /// <summary>连续无进展回收达到上限时该停止本搜索；上限 0 表示关闭这条规则。</summary>
    public bool ShouldStopForNoProgressReclaims(int limit)
        => limit > 0 && ConsecutiveNoProgressReclaims >= limit;

    /// <summary>
    /// 每次搜索（组合成员、补充审计、抬上限重搜）各自拿一份连续无进展额度，
    /// 免得上一份搜索用剩的计数把下一份搜索提前截断。
    /// </summary>
    public void ResetNoProgressReclaimTracking()
    {
        ConsecutiveNoProgressReclaims = 0;
        LastReclaimRegainedBytes = 0;
    }

    public long AllocatedBytes
        => Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - Volatile.Read(ref _allocatedBytesAtStart));

    public long AllocationLimitBytes => Volatile.Read(ref _allocationLimitBytes);

    public long ProjectedMemoryLoadBytes
    {
        get
        {
            Func<long>? probe = Volatile.Read(ref _systemMemoryLoadProbe);
            if (probe != null) return Math.Max(0, probe());
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
            if (systemRemaining > 0 && systemRemaining != long.MaxValue)
            {
                long reusableRemaining = Math.Max(0, Volatile.Read(ref _reusableHeapBytesAtStart) - AllocatedBytes);
                systemRemaining = systemRemaining > long.MaxValue - reusableRemaining
                    ? long.MaxValue : systemRemaining + reusableRemaining;
            }
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
        Func<bool>? unexpectedNoGcLossProbe = null,
        Func<long>? systemMemoryLoadProbe = null,
        long reusableHeapBytesAtStart = 0)
    {
        ArgumentNullException.ThrowIfNull(reclaimAndContinue);
        Configure(
            allocatedBytesAtStart,
            allocationLimitBytes,
            memoryLoadBytesAtStart,
            systemMemoryLimitBytes,
            (token, _) => reclaimAndContinue(token),
            useDefaultGcAndContinue,
            unexpectedNoGcLossProbe,
            systemMemoryLoadProbe,
            reusableHeapBytesAtStart);
    }

    public void Configure(
        long allocatedBytesAtStart,
        long allocationLimitBytes,
        long memoryLoadBytesAtStart,
        long systemMemoryLimitBytes,
        Action<CancellationToken, string> reclaimAndContinue,
        Action<CancellationToken> useDefaultGcAndContinue,
        Func<bool>? unexpectedNoGcLossProbe = null,
        Func<long>? systemMemoryLoadProbe = null,
        long reusableHeapBytesAtStart = 0)
    {
        if (allocationLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(allocationLimitBytes));
        if (memoryLoadBytesAtStart < 0)
            throw new ArgumentOutOfRangeException(nameof(memoryLoadBytesAtStart));
        if (systemMemoryLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(systemMemoryLimitBytes));
        if (reusableHeapBytesAtStart < 0 || (reusableHeapBytesAtStart > 0 && systemMemoryLoadProbe == null))
            throw new ArgumentOutOfRangeException(nameof(reusableHeapBytesAtStart));
        ArgumentNullException.ThrowIfNull(reclaimAndContinue);
        ArgumentNullException.ThrowIfNull(useDefaultGcAndContinue);
        Volatile.Write(ref _allocatedBytesAtStart, allocatedBytesAtStart);
        Volatile.Write(ref _memoryLoadBytesAtStart, memoryLoadBytesAtStart);
        Volatile.Write(ref _systemMemoryLimitBytes, systemMemoryLimitBytes);
        Volatile.Write(ref _reclaimAndContinue, reclaimAndContinue);
        Volatile.Write(ref _useDefaultGcAndContinue, useDefaultGcAndContinue);
        Volatile.Write(ref _unexpectedNoGcLossProbe, unexpectedNoGcLossProbe);
        Volatile.Write(ref _systemMemoryLoadProbe, systemMemoryLoadProbe);
        Volatile.Write(ref _reusableHeapBytesAtStart, reusableHeapBytesAtStart);
        Volatile.Write(ref _conservativeParallelismRequired, 0);
        Volatile.Write(ref _noGcRecoveryAllowed, 0);
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

    public SearchMemoryCommitDecision PrepareCommit(long reservedBytes, CancellationToken cancellationToken)
    {
        TryRecoverNoGc(reservedBytes, cancellationToken);
        return HasUnexpectedNoGcLoss()
            ? new(SearchMemoryCommitStatus.Reclaim, reservedBytes, "unexpected_no_gc_loss")
            : new(InspectCommit(reservedBytes, reclaimAttempted: false), reservedBytes);
    }

    public SearchMemoryCommitDecision RecheckCommitAfterReclaim(
        SearchMemoryCommitDecision decision, CancellationToken cancellationToken)
    {
        if (decision.Status != SearchMemoryCommitStatus.Reclaim)
            throw new InvalidOperationException("Only a requested memory reclaim can be rechecked.");
        if (decision.ReclaimReason != null)
            TryRecoverNoGc(decision.ReservedBytes, cancellationToken);
        return new(InspectCommit(decision.ReservedBytes, reclaimAttempted: true), decision.ReservedBytes);
    }

    private SearchMemoryCommitStatus InspectCommit(long reservedBytes, bool reclaimAttempted)
        => ResolveCommitStatus(IsEnabled, reservedBytes, AllocationLimitBytes,
            RemainingBytes, AllocatedBytes, reclaimAttempted);

    internal static SearchMemoryCommitStatus ResolveCommitStatus(
        bool signalEnabled, long reservedBytes, long allocationLimitBytes,
        long remainingBytes, long allocatedBytes, bool reclaimAttempted)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reservedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allocationLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(allocatedBytes);
        if (!signalEnabled || reservedBytes <= remainingBytes)
            return SearchMemoryCommitStatus.Ready;
        if (reservedBytes > allocationLimitBytes || reclaimAttempted || allocatedBytes == 0)
            return SearchMemoryCommitStatus.CapacityInsufficient;
        return SearchMemoryCommitStatus.Reclaim;
    }

    public bool HasUnexpectedNoGcLoss()
        => Volatile.Read(ref _unexpectedNoGcLossProbe)?.Invoke() == true;

    public bool NeedsReclaimAtBoundary()
        => HasUnexpectedNoGcLoss() || IsLimitReached();

    public string? ReclaimReasonAfterCommit(long reservedBytes, string pressureReason)
    {
        if (HasUnexpectedNoGcLoss()) return "unexpected_no_gc_loss";
        if (!IsEnabled) return null;
        ObserveCommitReserve(reservedBytes);
        bool reserveCanEverFit = reservedBytes <= AllocationLimitBytes;
        return IsLimitReached() || (reserveCanEverFit && !CanReachCommit(reservedBytes))
            ? pressureReason : null;
    }

    public SmartLayerMemoryDecision DecideLayerReclaim(SmartLayerMemoryForecast forecast)
    {
        bool enabled = IsEnabled;
        bool unexpectedLoss = HasUnexpectedNoGcLoss();
        long allocated = AllocatedBytes;
        long remaining = RemainingBytes;
        long limit = AllocationLimitBytes;
        if (!enabled) return new(false, "no_active_no_gc_region", 0, remaining);
        if (unexpectedLoss) return new(true, "unexpected_no_gc_loss", 0, remaining);
        return forecast.Decide(allocated, remaining, limit);
    }

    public void ReclaimAndContinue(CancellationToken cancellationToken, string reason = "unspecified")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Action<CancellationToken, string> reclaim = Volatile.Read(ref _reclaimAndContinue)
            ?? throw new InvalidOperationException("搜索内存回收信号尚未配置。");
        RunCheckpoint(token => reclaim(token, reason), cancellationToken);
    }

    public void ReleaseAllocationLimitAndContinue(CancellationToken cancellationToken)
        => RunCheckpoint(
            Volatile.Read(ref _useDefaultGcAndContinue)
                ?? throw new InvalidOperationException("搜索默认 GC 回退信号尚未配置。"),
            cancellationToken);

    private void RunCheckpoint(
        Action<CancellationToken> checkpoint,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastReclaimMaxObservedGcPauseTicks, 0);
        cancellationToken.ThrowIfCancellationRequested();
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

    // Called only by the coordinator at a drained commit boundary; a probe may establish
    // a new region, but cannot collect or wait for search/deferred work.
    public void TryRecoverNoGc(long reservedBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reservedBytes);
        cancellationToken.ThrowIfCancellationRequested();
        ObserveCommitReserve(reservedBytes);
        if (!IsEnabled && Volatile.Read(ref _noGcRecoveryAllowed) != 0)
            Volatile.Read(ref _noGcRecoveryProbe)?.Invoke(reservedBytes, cancellationToken);
    }

    internal void SetNoGcRecoveryProbe(Action<long, CancellationToken> probe, Action<long>? fallbackObserver = null)
    {
        Volatile.Write(ref _noGcRecoveryProbe, probe);
        Volatile.Write(ref _noGcFallbackObserver, fallbackObserver);
    }

    public void Disable()
    {
        DisableLimits();
        NextCommitReserveBytes = 0;
        Volatile.Write(ref _noGcRecoveryProbe, null);
        Volatile.Write(ref _noGcFallbackObserver, null);
    }

    private void DisableLimits()
    {
        Volatile.Write(ref _allocationLimitBytes, long.MaxValue);
        Volatile.Write(ref _memoryLoadBytesAtStart, 0);
        Volatile.Write(ref _systemMemoryLimitBytes, long.MaxValue);
        Volatile.Write(ref _reclaimAndContinue, null);
        Volatile.Write(ref _useDefaultGcAndContinue, null);
        Volatile.Write(ref _unexpectedNoGcLossProbe, null);
        Volatile.Write(ref _systemMemoryLoadProbe, null);
        Volatile.Write(ref _reusableHeapBytesAtStart, 0);
        Volatile.Write(ref _conservativeParallelismRequired, 0);
        Volatile.Write(ref _noGcRecoveryAllowed, 0);
    }

    public void UseDefaultGcFallback(bool systemHeadroomConstrained, bool allowNoGcRecovery = false,
        long completedRecoveryGen2Index = 0)
    {
        DisableLimits();
        Volatile.Write(ref _noGcRecoveryAllowed, allowNoGcRecovery ? 1 : 0);
        Volatile.Write(ref _conservativeParallelismRequired, systemHeadroomConstrained ? 1 : 0);
        if (allowNoGcRecovery)
            Volatile.Read(ref _noGcFallbackObserver)?.Invoke(completedRecoveryGen2Index);
    }
}
