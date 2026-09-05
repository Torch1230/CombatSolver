namespace CombatSolver;

/// <summary>One completed wave, measured over the same wall-clock interval on the coordinator.</summary>
internal readonly record struct SearchParallelismSample(
    TimeSpan Elapsed,
    long Transitions,
    long AllocatedBytes,
    TimeSpan GcPause,
    int UsedParallelism,
    double MemoryPressureRatio,
    bool Cancelled = false);

internal enum SearchParallelismDecisionKind
{
    None,
    ProbeLower,
    ProbeHigher,
    ProbeAccepted,
    ProbeRejected,
    WorkloadChanged,
    MemoryPressureLimited,
    Cancelled,
}

internal readonly record struct SearchParallelismDecision(
    SearchParallelismDecisionKind Kind,
    int PreviousCapacity,
    int Capacity,
    double TransitionsPerSecond = 0,
    double BytesPerTransition = 0,
    double GcPauseDuty = 0,
    double ThroughputRatio = 0)
{
    public bool CapacityChanged => Capacity != PreviousCapacity;
}

/// <summary>
/// Experimental ordinary-GC controller. It requests bounded capacity probes and accepts them
/// only against a comparable measured workload. It never changes ordering, pruning or budgets,
/// and consumes no CLR APIs itself. The caller must disable it for deterministic diagnostics,
/// replay and NoGC operation. Enablement is explicit until visible-game A/B evidence exists.
/// </summary>
internal sealed class SearchParallelismController
{
    private const long MinimumWindowTicks = TimeSpan.TicksPerMillisecond * 100;
    private const long MinimumWindowTransitions = 128;
    private const int RequiredPressureWindows = 2;
    private const int RequiredLowPressureWindows = 3;
    private const int RequiredProbeWindows = 2;
    private const double HighGcDuty = 0.25;
    private const double LowGcDuty = 0.08;
    private const double HighMemoryPressure = 0.90;
    private const double CriticalMemoryPressure = 0.97;
    private const double LowMemoryPressure = 0.75;
    private const double MaximumComparableAllocationRatio = 1.25;
    private readonly int _maximumCapacity;
    private readonly bool _enabled;
    private Window _pending;
    private Window _baseline;
    private bool _hasBaseline;
    private int _pressureWindows;
    private int _lowPressureWindows;
    private int _lowerCooldown;
    private int _higherCooldown;
    private int _failedHigherProbes;
    private Probe? _probe;
    private bool _cancelled;

    public SearchParallelismController(int maximumCapacity, bool enabled = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCapacity, 1);
        _maximumCapacity = maximumCapacity;
        _enabled = enabled && maximumCapacity > 1;
        Capacity = maximumCapacity;
    }

    public int Capacity { get; private set; }
    public bool IsEnabled => _enabled && !_cancelled;
    public bool IsProbing => _probe.HasValue;

    public SearchParallelismDecision Observe(SearchParallelismSample sample)
    {
        if (sample.Cancelled)
            return Cancel();
        if (!IsEnabled)
            return new(_cancelled ? SearchParallelismDecisionKind.Cancelled : SearchParallelismDecisionKind.None,
                Capacity, Capacity);
        Validate(sample);
        if (sample.Elapsed == TimeSpan.Zero || sample.Transitions == 0)
            return new(SearchParallelismDecisionKind.None, Capacity, Capacity);
        // A naturally narrow frontier does not measure the requested DOP. Never compare its
        // throughput against a fully utilized baseline, including during a capacity probe.
        if (sample.UsedParallelism != Capacity)
        {
            _pending = default;
            return new(SearchParallelismDecisionKind.None, Capacity, Capacity);
        }

        _pending = _pending.Add(sample);
        if (_pending.ElapsedTicks < MinimumWindowTicks || _pending.Transitions < MinimumWindowTransitions)
            return new(SearchParallelismDecisionKind.None, Capacity, Capacity);
        Window current = _pending;
        _pending = default;
        if (_probe is { } probe)
            return ObserveProbe(current, probe);

        if (_lowerCooldown > 0)
            _lowerCooldown--;
        if (_higherCooldown > 0)
            _higherCooldown--;
        if (_hasBaseline && !ComparableWork(_baseline, current))
        {
            EstablishBaseline(current);
            return Describe(SearchParallelismDecisionKind.WorkloadChanged, Capacity, current);
        }
        // Retain a bounded recent baseline instead of an average dominated by earlier layers.
        _baseline = _hasBaseline ? _baseline.DecayAndAdd(current) : current;
        _hasBaseline = true;
        bool pressured = current.GcDuty >= HighGcDuty || current.MemoryPressure >= HighMemoryPressure;
        bool relaxed = current.GcDuty <= LowGcDuty && current.MemoryPressure <= LowMemoryPressure;
        _pressureWindows = pressured ? Math.Min(RequiredPressureWindows, _pressureWindows + 1) : 0;
        _lowPressureWindows = relaxed ? Math.Min(RequiredLowPressureWindows, _lowPressureWindows + 1) : 0;

        if (Capacity > 1 && _lowerCooldown == 0 && _pressureWindows >= RequiredPressureWindows)
            return BeginProbe(Math.Max(1, Capacity / 2), _baseline);
        if (Capacity < _maximumCapacity && _higherCooldown == 0
            && _lowPressureWindows >= RequiredLowPressureWindows)
        {
            int higher = Capacity > _maximumCapacity / 2 ? _maximumCapacity : Capacity * 2;
            return BeginProbe(higher, _baseline);
        }
        return Describe(SearchParallelismDecisionKind.None, Capacity, current);
    }

    public SearchParallelismDecision Cancel()
    {
        _cancelled = true;
        _pending = default;
        _probe = null;
        return new(SearchParallelismDecisionKind.Cancelled, Capacity, Capacity);
    }

    private SearchParallelismDecision BeginProbe(int candidateCapacity, Window baseline)
    {
        int previous = Capacity;
        _probe = new Probe(previous, baseline, default, 0);
        Capacity = candidateCapacity;
        _pressureWindows = 0;
        _lowPressureWindows = 0;
        return Describe(candidateCapacity < previous
            ? SearchParallelismDecisionKind.ProbeLower : SearchParallelismDecisionKind.ProbeHigher,
            previous, baseline);
    }

    private SearchParallelismDecision ObserveProbe(Window current, Probe probe)
    {
        if (!ComparableWork(probe.Baseline, current))
        {
            int previous = Capacity;
            // A materially different allocation cost can explain a throughput jump without any
            // DOP benefit. Abandon the comparison. Preserve a lower safety cap only while the
            // newly observed system pressure remains critical; this is not a throughput win.
            bool safetyCap = Capacity < probe.PreviousCapacity
                && current.MemoryPressure >= CriticalMemoryPressure;
            if (!safetyCap)
                Capacity = probe.PreviousCapacity;
            _probe = null;
            _hasBaseline = false;
            _lowerCooldown = 4;
            _higherCooldown = 6;
            _pressureWindows = 0;
            _lowPressureWindows = 0;
            return Describe(safetyCap ? SearchParallelismDecisionKind.MemoryPressureLimited
                : SearchParallelismDecisionKind.WorkloadChanged, previous, current);
        }
        Window observations = probe.Observations.Add(current);
        int count = probe.WindowCount + 1;
        if (count < RequiredProbeWindows)
        {
            _probe = probe with { Observations = observations, WindowCount = count };
            return Describe(SearchParallelismDecisionKind.None, Capacity, current);
        }

        bool lower = Capacity < probe.PreviousCapacity;
        double throughputRatio = observations.Throughput / probe.Baseline.Throughput;
        bool critical = lower && observations.MemoryPressure >= CriticalMemoryPressure;
        bool pressureRelieved = (probe.Baseline.GcDuty > LowGcDuty
                && observations.GcDuty <= probe.Baseline.GcDuty * 0.75)
            || (probe.Baseline.MemoryPressure >= HighMemoryPressure
                && observations.MemoryPressure <= probe.Baseline.MemoryPressure - 0.05);
        bool accepted = lower
            ? throughputRatio >= 1.05
                || (throughputRatio >= 0.97 && pressureRelieved)
            : throughputRatio >= 1.08 && observations.GcDuty < 0.20
                && observations.MemoryPressure < HighMemoryPressure;
        accepted |= critical;
        int previousCapacity = Capacity;
        if (!accepted)
            Capacity = probe.PreviousCapacity;
        _probe = null;
        _pressureWindows = 0;
        _lowPressureWindows = 0;
        // Only observations at the capacity we keep can become its baseline.
        _baseline = accepted ? observations : probe.Baseline;
        _hasBaseline = true;
        if (lower)
        {
            _lowerCooldown = accepted ? 2 : 8;
            _higherCooldown = 6;
        }
        else
        {
            _lowerCooldown = 3;
            if (accepted)
                _failedHigherProbes = 0;
            else
                _failedHigherProbes = Math.Min(3, _failedHigherProbes + 1);
            _higherCooldown = accepted ? 3 : 4 << _failedHigherProbes;
        }
        return Describe(critical ? SearchParallelismDecisionKind.MemoryPressureLimited
            : accepted ? SearchParallelismDecisionKind.ProbeAccepted : SearchParallelismDecisionKind.ProbeRejected,
            previousCapacity, observations, throughputRatio);
    }

    private void EstablishBaseline(Window window)
    {
        _baseline = window;
        _hasBaseline = true;
        _pressureWindows = 0;
        _lowPressureWindows = 0;
    }

    private SearchParallelismDecision Describe(
        SearchParallelismDecisionKind kind,
        int previousCapacity,
        Window window,
        double throughputRatio = 0)
        => new(kind, previousCapacity, Capacity, window.Throughput, window.BytesPerTransition,
            window.GcDuty, throughputRatio);

    private static bool ComparableWork(Window baseline, Window current)
    {
        double first = baseline.BytesPerTransition;
        double second = current.BytesPerTransition;
        if (first == 0 || second == 0)
            return first == second;
        return Math.Max(first, second) / Math.Min(first, second) <= MaximumComparableAllocationRatio;
    }

    private void Validate(SearchParallelismSample sample)
    {
        if (sample.Elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sample), "Wave elapsed time must be nonnegative.");
        if (sample.GcPause < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sample), "GC pause delta must be nonnegative.");
        ArgumentOutOfRangeException.ThrowIfNegative(sample.Transitions);
        ArgumentOutOfRangeException.ThrowIfNegative(sample.AllocatedBytes);
        if (sample.UsedParallelism < 1 || sample.UsedParallelism > _maximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(sample), "Used DOP must respect the configured limit.");
        if (!double.IsFinite(sample.MemoryPressureRatio) || sample.MemoryPressureRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(sample), "Memory pressure must be a finite nonnegative ratio.");
    }

    private readonly record struct Probe(int PreviousCapacity, Window Baseline, Window Observations, int WindowCount);

    private readonly record struct Window(
        long ElapsedTicks,
        long Transitions,
        long AllocatedBytes,
        long GcPauseTicks,
        double MemoryPressure)
    {
        public double Throughput => Transitions * (double)TimeSpan.TicksPerSecond / Math.Max(1, ElapsedTicks);
        public double BytesPerTransition => AllocatedBytes / (double)Math.Max(1, Transitions);
        public double GcDuty => Math.Clamp(GcPauseTicks / (double)Math.Max(1, ElapsedTicks), 0, 1);

        public Window Add(SearchParallelismSample sample)
            => Add(new Window(sample.Elapsed.Ticks, sample.Transitions, sample.AllocatedBytes,
                Math.Min(sample.GcPause.Ticks, sample.Elapsed.Ticks), Math.Min(1, sample.MemoryPressureRatio)));

        public Window Add(Window other)
            => new(SaturatingAdd(ElapsedTicks, other.ElapsedTicks), SaturatingAdd(Transitions, other.Transitions),
                SaturatingAdd(AllocatedBytes, other.AllocatedBytes), SaturatingAdd(GcPauseTicks, other.GcPauseTicks),
                Math.Max(MemoryPressure, other.MemoryPressure));

        public Window DecayAndAdd(Window current)
            => new Window(ElapsedTicks / 2, Transitions / 2, AllocatedBytes / 2, GcPauseTicks / 2,
                current.MemoryPressure).Add(current);

        private static long SaturatingAdd(long first, long second)
            => first > long.MaxValue - second ? long.MaxValue : first + second;
    }
}
