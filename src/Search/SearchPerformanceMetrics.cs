using System.Diagnostics;

namespace CombatSolver;

internal enum SearchMetricPhase
{
    Fork,
    Action,
    CardExecution,
    ExecutionChoiceResume,
    CardPostProcessing,
    PotionExecution,
    RoundAdvance,
    RoundPlayerEnd,
    RoundEndSimulation,
    RoundFlush,
    RoundPlayerEndPowers,
    RoundEnemyTurn,
    RoundEnemyStart,
    RoundEnemyMoves,
    RoundEnemyEndPowers,
    RoundPlayerStart,
    RoundDraw,
    Snapshot,
    ThreatProjection,
    Fingerprint,
    ProjectedShuffle,
    PileFingerprint,
    PileFingerprintMiss,
    CardFingerprintMiss,
    CombatFingerprint,
    Prune,
    FinalSelection,
}

internal readonly record struct SearchMeasurement(long Timestamp, long AllocatedBytes, int FrameId)
{
    public static SearchMeasurement Disabled => new(0, 0, 0);
}

internal sealed class SearchPerformanceMetrics(bool enabled)
{
    private readonly bool _enabled = enabled;
    private readonly long[] _ticks = new long[Enum.GetValues<SearchMetricPhase>().Length];
    private readonly long[] _allocatedBytes = new long[Enum.GetValues<SearchMetricPhase>().Length];
    private readonly List<ActiveFrame> _activeFrames = [];
    private int _nextFrameId;

    /// <summary>
    /// 每个阶段只记"排他"增量：从父测量里扣掉所有已结束子测量的时间与分配，
    /// 这样各阶段相加才有归属意义。测量范围必须与 Begin/End 调用严格后进先出；
    /// 否则说明某个调用方在子范围里就结束了父范围，诊断直接失败而不是输出错账。
    /// </summary>
    private struct ActiveFrame(int id, long timestamp, long allocatedBytes)
    {
        public int Id = id;
        public long Timestamp = timestamp;
        public long AllocatedBytes = allocatedBytes;
        public long ChildTicks;
        public long ChildAllocatedBytes;
    }

    public SearchMeasurement Begin()
    {
        if (!_enabled)
            return SearchMeasurement.Disabled;
        long timestamp = Stopwatch.GetTimestamp();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        int frameId = ++_nextFrameId;
        _activeFrames.Add(new ActiveFrame(frameId, timestamp, allocatedBytes));
        return new SearchMeasurement(timestamp, allocatedBytes, frameId);
    }

    public SearchMeasurementScope Measure(SearchMetricPhase phase)
        => new(this, phase, Begin());

    public void End(SearchMetricPhase phase, SearchMeasurement measurement)
    {
        if (!_enabled)
            return;
        if (_activeFrames.Count == 0
            || _activeFrames[^1].Id != measurement.FrameId)
        {
            throw new InvalidOperationException(
                $"搜索阶段测量不是后进先出：phase={phase} frame={measurement.FrameId} " +
                $"active={(_activeFrames.Count == 0 ? "-" : _activeFrames[^1].Id.ToString())}。");
        }
        ActiveFrame frame = _activeFrames[^1];
        _activeFrames.RemoveAt(_activeFrames.Count - 1);
        long ticks = Math.Max(0, Stopwatch.GetTimestamp() - frame.Timestamp);
        long allocatedBytes = Math.Max(
            0, GC.GetAllocatedBytesForCurrentThread() - frame.AllocatedBytes);
        int index = (int)phase;
        _ticks[index] += Math.Max(0, ticks - frame.ChildTicks);
        _allocatedBytes[index] += Math.Max(0, allocatedBytes - frame.ChildAllocatedBytes);
        if (_activeFrames.Count > 0)
        {
            ActiveFrame parent = _activeFrames[^1];
            parent.ChildTicks += ticks;
            parent.ChildAllocatedBytes += allocatedBytes;
            _activeFrames[^1] = parent;
        }
    }

    /// <summary>合并并清空一个已经越过完成 barrier 的持久 worker 阶段指标。</summary>
    public void DrainFrom(SearchPerformanceMetrics worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        if (worker._activeFrames.Count != 0)
            throw new InvalidOperationException("不能合并仍有未结束阶段测量的 worker 指标。");
        for (int index = 0; index < _ticks.Length; index++)
        {
            if (_enabled)
            {
                _ticks[index] += worker._ticks[index];
                _allocatedBytes[index] += worker._allocatedBytes[index];
            }
            worker._ticks[index] = 0;
            worker._allocatedBytes[index] = 0;
        }
    }

    public SearchPhaseMetric Snapshot(SearchMetricPhase phase)
    {
        int index = (int)phase;
        return new SearchPhaseMetric(
            Stopwatch.GetElapsedTime(0, _ticks[index]),
            _allocatedBytes[index]);
    }
}

internal readonly struct SearchMeasurementScope(
    SearchPerformanceMetrics owner,
    SearchMetricPhase phase,
    SearchMeasurement measurement) : IDisposable
{
    public void Dispose() => owner.End(phase, measurement);
}

internal readonly record struct SearchPhaseMetric(TimeSpan Elapsed, long AllocatedBytes);
