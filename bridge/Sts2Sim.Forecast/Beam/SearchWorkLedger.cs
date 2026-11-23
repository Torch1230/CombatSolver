using Sts2Sim.Core.Combat.StateDescription;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal readonly record struct SearchWorkSnapshot(
    long ExpandedParents,
    long TransitionCount,
    long ChoiceReplayAttempts,
    int ObservedPeakParallelism,
    string ActivePhaseLabel);

internal sealed class SearchWorkLedger(bool useParentBudget)
{
    private readonly object _phaseSync = new();
    private readonly Dictionary<string, int> _activePhaseCounts = new(StringComparer.Ordinal);
    private long _expandedParents;
    private long _transitionCount;
    private long _choiceReplayAttempts;
    private int _activeLanes;
    private int _observedPeakParallelism = 1;
    private string _activePhaseLabel = "Beam";

    public bool UseParentBudget { get; } = useParentBudget;

    public long ExpandedParents => Volatile.Read(ref _expandedParents);
    public long TransitionCount => Volatile.Read(ref _transitionCount);
    public long ChoiceReplayAttempts => Volatile.Read(ref _choiceReplayAttempts);
    public int ObservedPeakParallelism => Volatile.Read(ref _observedPeakParallelism);
    public string ActivePhaseLabel
    {
        get
        {
            lock (_phaseSync)
                return ResolveActivePhaseLocked();
        }
    }

    public long BudgetUnits => UseParentBudget ? ExpandedParents : ChoiceReplayAttempts;

    public void SetActivePhase(string phaseLabel)
    {
        if (!string.IsNullOrWhiteSpace(phaseLabel))
            Volatile.Write(ref _activePhaseLabel, phaseLabel);
    }

    public IDisposable EnterPhase(string phaseLabel)
    {
        if (string.IsNullOrWhiteSpace(phaseLabel))
            return PhaseScope.Noop;
        lock (_phaseSync)
        {
            _activePhaseCounts[phaseLabel] = _activePhaseCounts.GetValueOrDefault(phaseLabel) + 1;
            Volatile.Write(ref _activePhaseLabel, phaseLabel);
        }
        return new PhaseScope(this, phaseLabel);
    }

    private void ExitPhase(string phaseLabel)
    {
        lock (_phaseSync)
        {
            if (!_activePhaseCounts.TryGetValue(phaseLabel, out int count))
                return;
            if (count <= 1)
                _activePhaseCounts.Remove(phaseLabel);
            else
                _activePhaseCounts[phaseLabel] = count - 1;
        }
    }

    private string ResolveActivePhaseLocked()
    {
        bool hasHybridNovelty = _activePhaseCounts.GetValueOrDefault("HybridNovelty") > 0;
        bool hasNovelty = hasHybridNovelty
            || _activePhaseCounts.GetValueOrDefault("NoveltyScout") > 0
            || _activePhaseCounts.GetValueOrDefault("AdaptiveNovelty") > 0;
        bool hasBeam = _activePhaseCounts.GetValueOrDefault("Beam") > 0
            || _activePhaseCounts.GetValueOrDefault("BeamPortfolio") > 0
            || _activePhaseCounts.GetValueOrDefault("PortfolioPower") > 0
            || _activePhaseCounts.GetValueOrDefault("PortfolioRefinement") > 0
            || _activePhaseCounts.GetValueOrDefault("PortfolioOffense") > 0;
        if (hasHybridNovelty)
            return "HybridNovelty+Beam";
        if (hasNovelty && hasBeam)
            return "HybridNovelty+Beam";
        if (hasNovelty)
            return _activePhaseCounts.GetValueOrDefault("AdaptiveNovelty") > 0 ? "AdaptiveNovelty" : "NoveltyScout";
        if (_activePhaseCounts.GetValueOrDefault("EarlyTurnContinuation") > 0)
            return "EarlyTurnContinuation";
        if (_activePhaseCounts.GetValueOrDefault("EarlyTurnScout") > 0)
            return "EarlyTurnScout";
        if (_activePhaseCounts.GetValueOrDefault("PortfolioRefinement") > 0)
            return "PortfolioRefinement";
        if (_activePhaseCounts.GetValueOrDefault("PortfolioPower") > 0)
            return "PortfolioPower";
        if (_activePhaseCounts.GetValueOrDefault("PortfolioOffense") > 0)
            return "PortfolioOffense";
        if (_activePhaseCounts.GetValueOrDefault("BeamPortfolio") > 0)
            return "BeamPortfolio";
        if (_activePhaseCounts.GetValueOrDefault("Beam") > 0)
            return "Beam";
        return Volatile.Read(ref _activePhaseLabel);
    }

    private sealed class PhaseScope(SearchWorkLedger? ledger, string? phaseLabel) : IDisposable
    {
        public static readonly PhaseScope Noop = new(null, null);
        private int _disposed;

        public void Dispose()
        {
            if (ledger is not null && phaseLabel is not null && Interlocked.Exchange(ref _disposed, 1) == 0)
                ledger.ExitPhase(phaseLabel);
        }
    }

    public void AddExpandedParents(long delta)
    {
        if (delta > 0)
            Interlocked.Add(ref _expandedParents, delta);
    }

    public void AddTransitions(long delta)
    {
        if (delta > 0)
            Interlocked.Add(ref _transitionCount, delta);
    }

    public void AddChoiceReplayAttempts(long delta)
    {
        if (delta > 0)
            Interlocked.Add(ref _choiceReplayAttempts, delta);
    }

    public void EnterActiveLane()
    {
        int active = Interlocked.Increment(ref _activeLanes);
        RecordObservedParallelism(active);
    }

    public void ExitActiveLane() => Interlocked.Decrement(ref _activeLanes);

    public void RecordObservedParallelism(int concurrency)
    {
        if (concurrency <= 1)
            return;
        while (true)
        {
            int current = Volatile.Read(ref _observedPeakParallelism);
            if (concurrency <= current)
                return;
            if (Interlocked.CompareExchange(ref _observedPeakParallelism, concurrency, current) == current)
                return;
        }
    }

    public SearchWorkSnapshot Snapshot() => new(
        ExpandedParents,
        TransitionCount,
        ChoiceReplayAttempts,
        ObservedPeakParallelism,
        ActivePhaseLabel);
}

internal sealed class SharedNodeBudget(long initialAllowance)
{
    private long _remaining = Math.Max(0L, initialAllowance);

    public long Remaining => Math.Max(0L, Volatile.Read(ref _remaining));

    public long TryAcquireUpTo(long requested)
    {
        if (requested <= 0)
            return 0;

        while (true)
        {
            long current = Volatile.Read(ref _remaining);
            if (current <= 0)
                return 0;
            long granted = Math.Min(current, requested);
            if (Interlocked.CompareExchange(ref _remaining, current - granted, current) == current)
                return granted;
        }
    }

    public bool TryAcquireOne()
    {
        while (true)
        {
            long current = Volatile.Read(ref _remaining);
            if (current <= 0)
                return false;
            if (Interlocked.CompareExchange(ref _remaining, current - 1, current) == current)
                return true;
        }
    }

    public void ReleaseUnspent(long count)
    {
        if (count > 0)
            Interlocked.Add(ref _remaining, count);
    }
}

internal sealed class MemberBudgetController(
    long memberCap,
    SharedNodeBudget? sharedBudget,
    long dedicatedPowerFloor = 0,
    SharedNodeBudget? dedicatedPowerReserve = null,
    long leaseBatchSize = 32)
{
    private readonly long _memberCap = Math.Max(0L, memberCap);
    private readonly long _leaseBatchSize = Math.Max(1L, leaseBatchSize);
    private readonly object _leaseSync = new();
    private long _spent;
    private long _sharedLeaseAvailable;
    private long _dedicatedLeaseAvailable;

    public long MemberCap => _memberCap;
    public long Spent => Volatile.Read(ref _spent);

    public bool TryReserveOne()
    {
        if (Volatile.Read(ref _spent) >= _memberCap)
            return false;

        lock (_leaseSync)
        {
            if (_spent >= _memberCap)
                return false;

            if (_sharedLeaseAvailable > 0)
            {
                _sharedLeaseAvailable--;
                Volatile.Write(ref _spent, _spent + 1);
                return true;
            }

            if (_dedicatedLeaseAvailable > 0)
            {
                _dedicatedLeaseAvailable--;
                Volatile.Write(ref _spent, _spent + 1);
                return true;
            }

            long headRoom = _memberCap - _spent;
            if (headRoom <= 0)
                return false;

            if (sharedBudget is not null)
            {
                long request = Math.Min(headRoom, _leaseBatchSize);
                long acquired = sharedBudget.TryAcquireUpTo(request);
                if (acquired > 0)
                {
                    _sharedLeaseAvailable = acquired - 1;
                    Volatile.Write(ref _spent, _spent + 1);
                    return true;
                }
            }
            else if (dedicatedPowerReserve is null)
            {
                // 无共享账本时仅受 memberCap 限制。
                Volatile.Write(ref _spent, _spent + 1);
                return true;
            }

            if (dedicatedPowerReserve is not null && _spent < dedicatedPowerFloor)
            {
                long powerShortfall = Math.Min(headRoom, dedicatedPowerFloor - _spent);
                long request = Math.Min(powerShortfall, _leaseBatchSize);
                long acquired = dedicatedPowerReserve.TryAcquireUpTo(request);
                if (acquired > 0)
                {
                    _dedicatedLeaseAvailable = acquired - 1;
                    Volatile.Write(ref _spent, _spent + 1);
                    return true;
                }
            }

            return false;
        }
    }

    public void ReclaimUnspent()
    {
        lock (_leaseSync)
        {
            if (_sharedLeaseAvailable > 0 && sharedBudget is not null)
            {
                sharedBudget.ReleaseUnspent(_sharedLeaseAvailable);
                _sharedLeaseAvailable = 0;
            }
            if (_dedicatedLeaseAvailable > 0 && dedicatedPowerReserve is not null)
            {
                dedicatedPowerReserve.ReleaseUnspent(_dedicatedLeaseAvailable);
                _dedicatedLeaseAvailable = 0;
            }
        }
    }
}

internal readonly record struct ParentAdmissionKey(
    CombatStateDescriptionDigest ExactFingerprint,
    int Turn,
    int PotionUses,
    int ForcedPotionUses,
    int DeathSaveUseCount,
    int EscapedTheft,
    int BrightestFlameMaxHpLost,
    GrowthCounts Growth,
    int EscapedCount)
{
    public static ParentAdmissionKey FromNode(SearchNode node) => new(
        node.Fingerprint,
        node.Turn,
        node.PotionUses,
        node.ForcedPotionUses,
        node.DeathSaveUseCount,
        node.EscapedTheft,
        node.BrightestFlameMaxHpLost,
        node.Growth,
        node.State.EscapedCreatures.Count);
}

internal sealed class ParentAdmissionTable(int capacity = 1_000_000)
{
    private readonly record struct Entry(int Depth, double Score);

    // Admission is coordinated per pass, before dispatching a wave. Keep lookup,
    // capacity and replacement atomic without taking every dictionary stripe to count.
    private readonly Dictionary<ParentAdmissionKey, Entry> _entries = new();
    private readonly int _capacity = Math.Max(0, capacity);

    public bool TryAdmit(SearchNode node)
    {
        if (_capacity == 0)
            return true;

        var key = ParentAdmissionKey.FromNode(node);
        var candidate = new Entry(node.Depth, node.Score);

        lock (_entries)
        {
            if (_entries.TryGetValue(key, out Entry existing))
            {
                if (candidate.Depth > existing.Depth
                    || (candidate.Depth == existing.Depth && candidate.Score <= existing.Score))
                {
                    return false;
                }
                _entries[key] = candidate;
            }
            else if (_entries.Count < _capacity)
            {
                _entries.Add(key, candidate);
            }
            return true;
        }
    }
}
