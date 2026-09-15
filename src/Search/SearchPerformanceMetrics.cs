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

internal readonly record struct SearchMeasurement(long Timestamp, long AllocatedBytes)
{
    public static SearchMeasurement Disabled => new(0, 0);
}

internal sealed class SearchPerformanceMetrics(bool enabled)
{
    private readonly bool _enabled = enabled;
    private readonly long[] _ticks = new long[Enum.GetValues<SearchMetricPhase>().Length];
    private readonly long[] _allocatedBytes = new long[Enum.GetValues<SearchMetricPhase>().Length];

    public SearchMeasurement Begin()
        => _enabled
            ? new SearchMeasurement(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread())
            : SearchMeasurement.Disabled;

    public SearchMeasurementScope Measure(SearchMetricPhase phase)
        => new(this, phase, Begin());

    public void End(SearchMetricPhase phase, SearchMeasurement measurement)
    {
        if (!_enabled)
            return;
        int index = (int)phase;
        _ticks[index] += Stopwatch.GetTimestamp() - measurement.Timestamp;
        _allocatedBytes[index] += GC.GetAllocatedBytesForCurrentThread() - measurement.AllocatedBytes;
    }

    /// <summary>合并并清空一个已经越过完成 barrier 的持久 worker 阶段指标。</summary>
    public void DrainFrom(SearchPerformanceMetrics worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
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
