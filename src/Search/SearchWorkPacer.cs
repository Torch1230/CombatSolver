using System.Diagnostics;

namespace CombatSolver;

/// <summary>限制后台求解连续占用 CPU 的时间片，给游戏渲染线程留出调度空间。</summary>
internal sealed class SearchWorkPacer(SearchFramePressureSignal framePressureSignal)
{
    private readonly Stopwatch _slice = Stopwatch.StartNew();
    private int _checks;
    private int _gen0 = GC.CollectionCount(0);
    private int _gen1 = GC.CollectionCount(1);
    private int _gen2 = GC.CollectionCount(2);
    private int _observedFramePressureEpoch;
    private long _frameRecoveryWaitTicks;

    public int YieldCount { get; private set; }
    public TimeSpan MaxObservedGcPause { get; private set; }
    public int FrameRecoveryWaitCount { get; private set; }
    public TimeSpan FrameRecoveryWaitDuration
        => Stopwatch.GetElapsedTime(0, _frameRecoveryWaitTicks);

    public void YieldIfNeeded()
    {
        if ((++_checks & (SolverWeights.BackgroundYieldCheckInterval - 1)) != 0)
            return;
        ObserveGcPause();
        long recoveryStarted = Stopwatch.GetTimestamp();
        if (framePressureSignal.WaitForRecovery(ref _observedFramePressureEpoch))
        {
            _frameRecoveryWaitTicks += Stopwatch.GetTimestamp() - recoveryStarted;
            FrameRecoveryWaitCount++;
            _slice.Restart();
            return;
        }
        if (_slice.ElapsedMilliseconds < SolverWeights.BackgroundWorkSliceMilliseconds)
            return;
        Thread.Yield();
        YieldCount++;
        _slice.Restart();
    }

    /// <summary>合并并清空一个已经越过完成 barrier 的持久 worker 节流指标。</summary>
    public void DrainFrom(SearchWorkPacer worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        YieldCount += worker.YieldCount;
        if (worker.MaxObservedGcPause > MaxObservedGcPause)
            MaxObservedGcPause = worker.MaxObservedGcPause;
        FrameRecoveryWaitCount += worker.FrameRecoveryWaitCount;
        _frameRecoveryWaitTicks += worker._frameRecoveryWaitTicks;
        worker.YieldCount = 0;
        worker.MaxObservedGcPause = TimeSpan.Zero;
        worker.FrameRecoveryWaitCount = 0;
        worker._frameRecoveryWaitTicks = 0;
    }

    internal void ObserveGcPause()
    {
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        if (gen0 == _gen0 && gen1 == _gen1 && gen2 == _gen2)
            return;
        _gen0 = gen0;
        _gen1 = gen1;
        _gen2 = gen2;
        foreach (TimeSpan pause in GC.GetGCMemoryInfo().PauseDurations)
        {
            if (pause > MaxObservedGcPause)
                MaxObservedGcPause = pause;
        }
    }

    internal void ObserveGcPause(TimeSpan pause)
    {
        if (pause > MaxObservedGcPause)
            MaxObservedGcPause = pause;
    }
}
