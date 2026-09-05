namespace CombatSolver;

internal readonly record struct SmartLayerMemoryDecision(
    bool ShouldReclaim,
    string Reason,
    long ForecastBytes,
    long RemainingBytes);

/// <summary>
/// A request-local estimate of the next finite Smart layer. Every observation covers the same
/// complete layer interval for process allocation and request transition totals. A prediction
/// only permits skipping an optional layer reset; the usual per-parent checkpoints still apply.
/// </summary>
internal sealed class SmartLayerMemoryForecast
{
    private const long MinimumReserveBytes = 64L * 1024 * 1024;
    private const double InitialTransitionGrowth = 2d;
    private const double AllocationSafetyFactor = 1.5d;
    private long _lastTransitions;
    private long _lastAllocatedBytes;
    private double _bytesPerTransitionHighWater;
    private double _transitionGrowthHighWater = InitialTransitionGrowth;
    private double _underpredictionHighWater = 1d;
    private long _previousForecastBytes;
    private bool _hasCompleteObservation;

    public int ObservationCount { get; private set; }
    public double BytesPerTransitionHighWater => _bytesPerTransitionHighWater;
    public double UnderpredictionHighWater => _underpredictionHighWater;

    public void Observe(long processAllocatedBytes, long transitions, bool usableWorkSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(processAllocatedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(transitions);
        if (processAllocatedBytes == 0 || transitions == 0 || !usableWorkSample)
        {
            // An early stop is not evidence that a later, wider layer will allocate this little.
            _hasCompleteObservation = false;
            _previousForecastBytes = 0;
            return;
        }

        if (_hasCompleteObservation && _lastTransitions > 0)
        {
            _transitionGrowthHighWater = Math.Max(
                _transitionGrowthHighWater,
                transitions / (double)_lastTransitions);
        }
        if (_previousForecastBytes > 0 && _previousForecastBytes != long.MaxValue)
        {
            _underpredictionHighWater = Math.Max(
                _underpredictionHighWater,
                processAllocatedBytes / (double)_previousForecastBytes);
        }
        _bytesPerTransitionHighWater = Math.Max(
            _bytesPerTransitionHighWater,
            processAllocatedBytes / (double)transitions);
        _lastTransitions = transitions;
        _lastAllocatedBytes = processAllocatedBytes;
        _hasCompleteObservation = true;
        ObservationCount++;
        _previousForecastBytes = 0;
    }

    public SmartLayerMemoryDecision Decide(
        bool enabled,
        bool unexpectedNoGcLoss,
        long allocatedBytes,
        long remainingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allocatedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingBytes);
        if (!enabled)
            return new(false, "no_active_no_gc_region", 0, remainingBytes);
        if (unexpectedNoGcLoss)
            return new(true, "unexpected_no_gc_loss", 0, remainingBytes);
        if (allocatedBytes == 0)
            return new(false, "fresh_region", 0, remainingBytes);
        if (!_hasCompleteObservation)
            return new(true, "incomplete_layer_sample", 0, remainingBytes);

        double expectedTransitions = _lastTransitions * _transitionGrowthHighWater;
        double expectedBytes = Math.Max(
            _lastAllocatedBytes,
            expectedTransitions * _bytesPerTransitionHighWater);
        long forecast = SaturatingCeiling(
            expectedBytes * AllocationSafetyFactor * _underpredictionHighWater);
        forecast = Math.Max(MinimumReserveBytes, forecast);
        _previousForecastBytes = forecast;
        bool fits = forecast != long.MaxValue && forecast <= remainingBytes;
        return new(
            !fits,
            fits ? "forecast_fits" : "forecast_exceeds_remaining",
            forecast,
            remainingBytes);
    }

    private static long SaturatingCeiling(double value)
        => !double.IsFinite(value) || value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(value);
}
