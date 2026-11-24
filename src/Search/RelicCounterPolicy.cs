namespace CombatSolver;

internal enum RelicCounterId
{
    HappyFlower, FakeHappyFlower, Pendulum, PollinousCore, PenNib,
    Nunchaku, TuningFork, JossPaper, IronClub, GalacticDust, MeatOnTheBone,
}

internal enum RelicCounterEventStream
{
    None,
    TurnTick,
    AttackPlayed,
    SkillPlayed,
    CardPlayed,
    CardExhausted,
    StarSpent,
}

internal sealed record RelicCounterRule(RelicCounterId Id, bool Enabled, int Minimum, int Maximum, int HpAllowance, int Priority = 1);
internal readonly record struct RelicCounterTarget(RelicCounterId Id, int Minimum, int Maximum, int HpAllowance, int Period, int Priority = 1);
internal readonly record struct RelicCounterCandidate(RelicCounterTarget Target, int CurrentValue);
internal readonly record struct RelicCounterEvaluation(ulong SatisfiedMask, ulong TargetMask, int HpCredit, int Distance)
{
    // Ten counters and the half-HP flag fit in four-bit slots; these preserve each
    // route's exact ending value without allocating a collection per search node.
    public ulong CounterValues { get; init; }
    public int SatisfiedPriority { get; init; }
    // Actual healing reduces net HP cost, while goal allowances only rank policy rewards.
    public int HealingHpCredit { get; init; }
    public ulong TargetMinimums { get; init; }
    public ulong TargetMaximums { get; init; }
    public int Value(RelicCounterId id) => (int)((CounterValues >> ((int)id * 4)) & 15);
    public int Minimum(RelicCounterId id) => (int)((TargetMinimums >> ((int)id * 4)) & 15);
    public int Maximum(RelicCounterId id) => (int)((TargetMaximums >> ((int)id * 4)) & 15);
    public bool Satisfied => SatisfiedMask == TargetMask;
    public int SatisfiedCount => System.Numerics.BitOperations.PopCount(SatisfiedMask);
}

internal static class RelicCounterPolicy
{
    internal const int MaxTurnTickHorizon = 24;
    private const int MaxActionTickHorizon = 1000;
    private const int EventStreamCount = (int)RelicCounterEventStream.StarSpent + 1;

    public static RelicCounterEventStream GetEventStream(RelicCounterId id) => id switch
    {
        RelicCounterId.HappyFlower => RelicCounterEventStream.TurnTick,
        RelicCounterId.FakeHappyFlower => RelicCounterEventStream.TurnTick,
        RelicCounterId.Pendulum => RelicCounterEventStream.TurnTick,
        RelicCounterId.PollinousCore => RelicCounterEventStream.TurnTick,
        RelicCounterId.PenNib => RelicCounterEventStream.AttackPlayed,
        RelicCounterId.Nunchaku => RelicCounterEventStream.AttackPlayed,
        RelicCounterId.TuningFork => RelicCounterEventStream.SkillPlayed,
        RelicCounterId.JossPaper => RelicCounterEventStream.CardExhausted,
        RelicCounterId.IronClub => RelicCounterEventStream.CardPlayed,
        RelicCounterId.GalacticDust => RelicCounterEventStream.StarSpent,
        RelicCounterId.MeatOnTheBone => RelicCounterEventStream.None,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown relic counter id."),
    };

    public static int NormalizeCounter(int value, int period)
    {
        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period), period, "Relic counter period must be positive.");
        int remainder = value % period;
        return remainder < 0 ? remainder + period : remainder;
    }

    public static RelicCounterRule[] ValidateAndCopy(IEnumerable<RelicCounterRule> rules)
    {
        var result = rules.ToArray();
        HashSet<RelicCounterId> ids = [];
        foreach (var rule in result)
        {
            if (!Enum.IsDefined(rule.Id) || !ids.Add(rule.Id) || rule.Minimum < 0
                || rule.Maximum < rule.Minimum || rule.Maximum > 1000 || rule.HpAllowance is < 0 or > 1000 || rule.Priority is < 1 or > 3)
                throw new ArgumentException("Invalid relic counter policy.", nameof(rules));
        }
        return result;
    }

    public static RelicCounterTarget[] ResolveConflicts(IReadOnlyList<RelicCounterCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            return Array.Empty<RelicCounterTarget>();

        List<RelicCounterTarget> resolved = new(candidates.Count);
        List<RelicCounterCandidate>?[] streamGroups = new List<RelicCounterCandidate>?[EventStreamCount];
        ulong seenMask = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            RelicCounterCandidate candidate = candidates[i];
            RelicCounterTarget target = candidate.Target;
            if (!Enum.IsDefined(target.Id)
                || target.Period <= 0
                || target.Minimum < 0
                || target.Maximum < target.Minimum
                || target.Maximum >= target.Period)
            {
                throw new ArgumentException("Invalid relic counter target.", nameof(candidates));
            }

            ulong bit = 1UL << (int)target.Id;
            if ((seenMask & bit) != 0)
                throw new ArgumentException("Duplicate relic counter target.", nameof(candidates));
            seenMask |= bit;

            RelicCounterEventStream stream = GetEventStream(target.Id);
            if (stream == RelicCounterEventStream.None)
            {
                resolved.Add(target);
                continue;
            }

            int normalizedCurrent = NormalizeCounter(candidate.CurrentValue, target.Period);
            int streamIndex = (int)stream;
            List<RelicCounterCandidate> group = streamGroups[streamIndex] ??= [];
            group.Add(new RelicCounterCandidate(target, normalizedCurrent));
        }

        for (int streamIndex = 1; streamIndex < streamGroups.Length; streamIndex++)
        {
            List<RelicCounterCandidate>? group = streamGroups[streamIndex];
            if (group == null || group.Count == 0)
                continue;

            if (group.Count == 1)
            {
                resolved.Add(group[0].Target);
                continue;
            }

            // Rank by fight-invariant order (Priority DESC -> HpAllowance DESC -> Id ASC)
            // so mid-fight captures keep the same resolved subset across turns.
            group.Sort(CompareCandidatePriority);

            RelicCounterEventStream stream = (RelicCounterEventStream)streamIndex;
            int maxHorizon = stream == RelicCounterEventStream.TurnTick
                ? MaxTurnTickHorizon
                : MaxActionTickHorizon;

            List<RelicCounterCandidate> accepted = new(group.Count);
            int acceptedLcm = 1;

            for (int i = 0; i < group.Count; i++)
            {
                RelicCounterCandidate candidate = group[i];
                if (accepted.Count == 0)
                {
                    accepted.Add(candidate);
                    acceptedLcm = candidate.Target.Period;
                    resolved.Add(candidate.Target);
                    continue;
                }

                int horizon = BoundedLeastCommonMultiple(acceptedLcm, candidate.Target.Period, maxHorizon);
                if (IsSimultaneouslyFeasible(accepted, candidate, horizon))
                {
                    accepted.Add(candidate);
                    acceptedLcm = horizon;
                    resolved.Add(candidate.Target);
                }
            }
        }

        resolved.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return resolved.ToArray();
    }

    private static int CompareCandidatePriority(RelicCounterCandidate left, RelicCounterCandidate right)
    {
        int priority = right.Target.Priority.CompareTo(left.Target.Priority);
        if (priority != 0)
            return priority;
        int allowance = right.Target.HpAllowance.CompareTo(left.Target.HpAllowance);
        if (allowance != 0)
            return allowance;
        return left.Target.Id.CompareTo(right.Target.Id);
    }

    private static bool IsSimultaneouslyFeasible(
        List<RelicCounterCandidate> accepted,
        RelicCounterCandidate candidate,
        int horizon)
    {
        int candCurrent = candidate.CurrentValue;
        int candPeriod = candidate.Target.Period;
        int candMin = candidate.Target.Minimum;
        int candMax = candidate.Target.Maximum;

        for (int k = 0; k < horizon; k++)
        {
            int candValue = (candCurrent + k) % candPeriod;
            if (candValue < candMin || candValue > candMax)
                continue;

            bool allSatisfied = true;
            for (int i = 0; i < accepted.Count; i++)
            {
                RelicCounterCandidate existing = accepted[i];
                int existingValue = (existing.CurrentValue + k) % existing.Target.Period;
                if (existingValue < existing.Target.Minimum || existingValue > existing.Target.Maximum)
                {
                    allSatisfied = false;
                    break;
                }
            }

            if (allSatisfied)
                return true;
        }

        return false;
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            int remainder = a % b;
            a = b;
            b = remainder;
        }
        return a;
    }

    private static int BoundedLeastCommonMultiple(int a, int b, int maxHorizon)
    {
        int gcd = GreatestCommonDivisor(a, b);
        long lcm = (long)(a / gcd) * b;
        return lcm >= maxHorizon ? maxHorizon : (int)lcm;
    }

    public static RelicCounterEvaluation Add(RelicCounterEvaluation evaluation, RelicCounterTarget target, int value)
    {
        ulong bit = 1UL << (int)target.Id;
        bool satisfied = value >= target.Minimum && value <= target.Maximum;
        bool rewardGoal = satisfied && target.Id != RelicCounterId.MeatOnTheBone;
        int distance = satisfied ? 0 : value < target.Minimum ? target.Minimum - value : target.Period - value + target.Minimum;
        return new(evaluation.SatisfiedMask | (satisfied ? bit : 0), evaluation.TargetMask | bit,
            evaluation.HpCredit + (rewardGoal ? target.HpAllowance : 0), evaluation.Distance + distance)
        {
            SatisfiedPriority = evaluation.SatisfiedPriority + (rewardGoal ? 1 << ((target.Priority - 1) * 4) : 0),
            CounterValues = (evaluation.CounterValues & ~(15UL << ((int)target.Id * 4)))
                | ((ulong)value << ((int)target.Id * 4)),
            TargetMinimums = (evaluation.TargetMinimums & ~(15UL << ((int)target.Id * 4)))
                | ((ulong)target.Minimum << ((int)target.Id * 4)),
            TargetMaximums = (evaluation.TargetMaximums & ~(15UL << ((int)target.Id * 4)))
                | ((ulong)target.Maximum << ((int)target.Id * 4)),
        };
    }
}
