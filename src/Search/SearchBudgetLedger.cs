using System.Diagnostics;

namespace CombatSolver;

// Request-level reporting. Individual solvers retain their own stop reasons.
internal sealed record SearchRequestBudgetSnapshot(
    long ExpandedNodes, long NodeLimit, long ElapsedMilliseconds, long TimeLimitMilliseconds)
{
    internal SearchBoundaryReason LimitReason => ExpandedNodes >= NodeLimit
        ? SearchBoundaryReason.NodeLimit
        : ElapsedMilliseconds >= TimeLimitMilliseconds
            ? SearchBoundaryReason.TimeLimit
            : SearchBoundaryReason.None;
}

internal readonly record struct SearchBudgetWindow(int RemainingMilliseconds, long RemainingNodes)
{
    internal bool CanStart(int minimumMilliseconds)
        => RemainingMilliseconds > minimumMilliseconds && RemainingNodes > 0;

    internal SolverSearchProfile Limit(
        SolverSearchProfile profile,
        int maximumNodes,
        int maximumMilliseconds,
        int reserveMilliseconds)
        => profile with
        {
            MaxExpandedNodes = (int)Math.Min(maximumNodes, RemainingNodes),
            SoftTimeBudgetMilliseconds = Math.Min(
                maximumMilliseconds, RemainingMilliseconds - reserveMilliseconds),
        };
}

internal sealed class SearchBudgetLedger
{
    private readonly Stopwatch _requestClock;
    private readonly int _requestLimitMilliseconds;

    internal SearchBudgetLedger(Stopwatch requestClock, SearchPolicySnapshot policy)
    {
        _requestClock = requestClock;
        _requestLimitMilliseconds = policy.BudgetOverrideMilliseconds
            ?? policy.Profile.SoftTimeBudgetMilliseconds;
    }

    internal SearchRequestWorkTotals WorkTotals { get; } = new();

    internal SearchRequestBudgetSnapshot Capture(SolverSearchProfile profile)
        => new(WorkTotals.Snapshot().ExpandedNodes, profile.MaxExpandedNodes,
            _requestClock.ElapsedMilliseconds, _requestLimitMilliseconds);

    internal int RemainingRequestMilliseconds
        => _requestLimitMilliseconds - (int)_requestClock.ElapsedMilliseconds;

    internal long RemainingRequestMillisecondsLong
        => _requestLimitMilliseconds - _requestClock.ElapsedMilliseconds;

    internal long RemainingNodes(SolverSearchProfile profile)
        => profile.MaxExpandedNodes - WorkTotals.Snapshot().ExpandedNodes;

    internal SearchBudgetWindow RequestWindow(SolverSearchProfile profile)
    {
        int remainingMilliseconds = RemainingRequestMilliseconds;
        long remainingNodes = RemainingNodes(profile);
        return new(remainingMilliseconds, remainingNodes);
    }

    internal SearchBudgetWindow PassWindow(SolverSearchProfile profile, Stopwatch clock)
    {
        int remainingMilliseconds = RemainingPassMillisecondsForSlice(profile, clock);
        long remainingNodes = RemainingNodes(profile);
        return new(remainingMilliseconds, remainingNodes);
    }

    internal SearchBudgetWindow ProfileWindow(SolverSearchProfile profile)
        => new(profile.SoftTimeBudgetMilliseconds, RemainingNodes(profile));

    internal long RemainingMilliseconds(SolverSearchProfile profile, Stopwatch clock)
        => profile.SoftTimeBudgetMilliseconds - clock.ElapsedMilliseconds;

    internal int RemainingPassMillisecondsForSlice(SolverSearchProfile profile, Stopwatch clock)
        => profile.SoftTimeBudgetMilliseconds - (int)clock.ElapsedMilliseconds;
}
