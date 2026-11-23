namespace CombatSolver;

internal readonly record struct BeamWidthPortfolioMemberSpec(
    int BeamWidth,
    bool SecondRankBand = false,
    bool BaseScoreOnly = false,
    bool AggressivePowerCommitment = false,
    bool OffensiveRefinement = false,
    bool BoundedRefinement = false)
{
    public override string ToString()
        => BeamWidth
            + (AggressivePowerCommitment ? "+power" : string.Empty)
            + (OffensiveRefinement ? "+offense" : string.Empty)
            + (BoundedRefinement ? "+bounded" : string.Empty)
            + (SecondRankBand ? "+band" : string.Empty)
            + (BaseScoreOnly ? "+base" : string.Empty);
}

internal readonly record struct BeamWidthPortfolioRun<TResult>(
    TResult Result,
    long ExpandedNodes,
    long TransitionCount,
    string Termination,
    bool Terminal,
    bool Won,
    int? BattleHpLost,
    int PotionCount)
{
    public bool StopPortfolio { get; init; }
    public bool MemoryTruncated { get; init; }
}

internal sealed record BeamWidthPortfolioMember(
    int BeamWidth,
    bool SecondRankBand,
    bool BaseScoreOnly,
    bool AggressivePowerCommitment,
    int NodeBudget,
    bool Ran,
    long ExpandedNodes,
    long TransitionCount,
    string? Termination,
    bool? Terminal,
    bool? Won,
    int? BattleHpLost,
    int? PotionCount,
    bool Compared,
    string? SkippedReason)
{
    public bool OffensiveRefinement { get; init; }
    public bool BoundedRefinement { get; init; }
}

internal sealed record BeamWidthPortfolioOutcome<TResult>(
    TResult Selected,
    int SelectedIndex,
    string SelectionReason,
    IReadOnlyList<BeamWidthPortfolioMember> Members,
    long TotalExpandedNodes,
    long TotalTransitionCount);

internal static class BeamWidthPortfolio
{
    internal const string SkippedBudgetExhausted = "BudgetExhausted";
    internal const string SkippedNodeLimitNotTerminal = "NodeLimitNotTerminal";
    internal const string SkippedPowerMemberNotTerminal = "PowerMemberNotTerminal";
    internal const string NodeLimitTermination = "NodeLimit";
    internal const string SkippedMemoryTruncated = "MemoryTruncated";
    internal const string SelectionBaselineFallback = "BaselineFallback";
    internal const string SelectionBest = "Best";
    internal const string SelectionStopped = "StopPortfolio";

    internal const double NarrowRefinementRatio = 2d / 3d;
    internal const double WideRefinementRatio = 3d / 2d;
    internal const double OffensiveRefinementRatio = 2d / 5d;
    internal const int BoundedRefinementWorkDivisor = 8;

    internal static int DedicatedPowerNodeReserve(int sharedMaxExpandedNodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sharedMaxExpandedNodes);
        return Math.Max(1, sharedMaxExpandedNodes / 5);
    }

    internal static IReadOnlyList<BeamWidthPortfolioMemberSpec> ProductionMembers(
        int baselineBeamWidth,
        IReadOnlyList<int>? configuredWidths,
        bool includePlainBaseline = true,
        bool includePowerCommitmentMember = false,
        bool useOffensiveRefinement = false,
        bool appendBoundedOffensiveRefinement = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baselineBeamWidth);
        if (useOffensiveRefinement && appendBoundedOffensiveRefinement)
            throw new ArgumentException("替换与追加精炼实验不能同时启用。");
        List<BeamWidthPortfolioMemberSpec> members =
            includePlainBaseline ? [new(baselineBeamWidth)] : [];
        if (includePowerCommitmentMember)
        {
            members.Add(new BeamWidthPortfolioMemberSpec(
                baselineBeamWidth,
                AggressivePowerCommitment: true));
        }
        if (configuredWidths is { Count: > 0 })
        {
            foreach (int width in configuredWidths)
            {
                BeamWidthPortfolioMemberSpec member = new(width);
                if (width >= 1 && !members.Contains(member))
                    members.Add(member);
            }
            return members;
        }
        foreach (BeamWidthPortfolioMemberSpec member in new[]
                 {
                     new BeamWidthPortfolioMemberSpec(ScaledWidth(baselineBeamWidth, NarrowRefinementRatio)),
                     useOffensiveRefinement
                         ? new BeamWidthPortfolioMemberSpec(
                             ScaledWidth(baselineBeamWidth, OffensiveRefinementRatio), OffensiveRefinement: true)
                         : new BeamWidthPortfolioMemberSpec(ScaledWidth(baselineBeamWidth, WideRefinementRatio)),
                 })
        {
            if (!members.Contains(member))
                members.Add(member);
        }
        members.Add(new BeamWidthPortfolioMemberSpec(baselineBeamWidth, SecondRankBand: true));
        members.Add(new BeamWidthPortfolioMemberSpec(baselineBeamWidth, BaseScoreOnly: true));
        if (appendBoundedOffensiveRefinement)
            members.Add(new BeamWidthPortfolioMemberSpec(
                ScaledWidth(baselineBeamWidth, OffensiveRefinementRatio),
                OffensiveRefinement: true, BoundedRefinement: true));
        return members;
    }

    internal static int ScaledWidth(int baselineBeamWidth, double ratio)
    {
        double raw = Math.Round(baselineBeamWidth * ratio, MidpointRounding.AwayFromZero);
        if (raw >= int.MaxValue)
            return int.MaxValue;
        return Math.Max(1, (int)raw);
    }

    internal static void MoveLeadingBandToTail<T>(List<T> ranked, int bandWidth)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        if (bandWidth <= 0 || ranked.Count <= bandWidth)
            return;
        List<T> leading = ranked.GetRange(0, bandWidth);
        ranked.RemoveRange(0, bandWidth);
        ranked.AddRange(leading);
    }

    internal static BeamWidthPortfolioOutcome<TResult> Run<TResult>(
        IReadOnlyList<BeamWidthPortfolioMemberSpec> memberSpecs,
        int sharedMaxExpandedNodes,
        SolverSearchProfile baseProfile,
        Func<SolverSearchProfile, BeamWidthPortfolioRun<TResult>> solve,
        Func<TResult, TResult, bool> isBetter,
        Func<BeamWidthPortfolioMemberSpec, string?>? rejectMember = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(memberSpecs);
        ArgumentNullException.ThrowIfNull(baseProfile);
        ArgumentNullException.ThrowIfNull(solve);
        ArgumentNullException.ThrowIfNull(isBetter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sharedMaxExpandedNodes);
        if (memberSpecs.Count == 0)
            throw new ArgumentException("组合器至少需要一个成员。", nameof(memberSpecs));
        for (int index = 0; index < memberSpecs.Count; index++)
        {
            if (memberSpecs[index].BeamWidth <= 0)
                throw new ArgumentOutOfRangeException(nameof(memberSpecs), "成员 Beam 宽度必须为正。");
            if (memberSpecs[index] is { BoundedRefinement: true, OffensiveRefinement: false })
                throw new ArgumentException("有界精炼必须使用进攻成员。", nameof(memberSpecs));
            if (memberSpecs[index] is { OffensiveRefinement: true } invalid
                && (invalid.SecondRankBand || invalid.BaseScoreOnly || invalid.AggressivePowerCommitment))
                throw new ArgumentException("进攻精炼成员不能叠加其他成员策略。", nameof(memberSpecs));
        }

        List<BeamWidthPortfolioMember> members = new(memberSpecs.Count);
        long remainingNodes = sharedMaxExpandedNodes;
        long totalExpanded = 0;
        long totalTransitions = 0;
        TResult? selected = default;
        int selectedIndex = -1;
        TResult? firstRan = default;
        int firstRanIndex = -1;
        bool stopped = false;

        for (int index = 0; index < memberSpecs.Count; index++)
        {
            BeamWidthPortfolioMemberSpec spec = memberSpecs[index];
            if (rejectMember?.Invoke(spec) is { } rejection)
            {
                members.Add(Skipped(spec, rejection));
                continue;
            }
            long memberNodeBudget = spec.AggressivePowerCommitment
                ? Math.Max(remainingNodes, DedicatedPowerNodeReserve(sharedMaxExpandedNodes))
                : remainingNodes;
            if (spec.BoundedRefinement)
                memberNodeBudget = Math.Min(memberNodeBudget, totalExpanded / BoundedRefinementWorkDivisor);
            if (memberNodeBudget <= 0)
            {
                members.Add(Skipped(spec, SkippedBudgetExhausted));
                continue;
            }

            SolverSearchProfile memberProfile = baseProfile with
            {
                BeamWidth = spec.BeamWidth,
                SecondRankBand = spec.SecondRankBand,
                BaseScoreOnly = spec.BaseScoreOnly,
                AggressivePowerCommitment = spec.AggressivePowerCommitment,
                BeamWeightPerturbation = spec.OffensiveRefinement
                    ? new BeamWeightPerturbation(BeamWeightTerm.EnemyHp, 1.5d)
                    : baseProfile.BeamWeightPerturbation,
                MaxExpandedNodes = (int)Math.Min(int.MaxValue, memberNodeBudget),
            };
            BeamWidthPortfolioRun<TResult> run = solve(memberProfile);
            ArgumentOutOfRangeException.ThrowIfNegative(run.ExpandedNodes);
            ArgumentOutOfRangeException.ThrowIfNegative(run.TransitionCount);
            ArgumentNullException.ThrowIfNull(run.Termination);
            remainingNodes = Math.Max(0, remainingNodes - run.ExpandedNodes);
            totalExpanded += run.ExpandedNodes;
            totalTransitions += run.TransitionCount;
            if (firstRanIndex < 0)
            {
                firstRan = run.Result;
                firstRanIndex = index;
            }

            if (run.StopPortfolio)
            {
                members.Add(Ran(spec, memberProfile.MaxExpandedNodes, run, compared: true, skippedReason: null));
                selected = run.Result;
                selectedIndex = index;
                stopped = true;
                break;
            }

            bool comparable = spec.AggressivePowerCommitment
                ? run.Terminal
                : run.Terminal
                    || (!run.MemoryTruncated
                        && !string.Equals(run.Termination, NodeLimitTermination, StringComparison.Ordinal));
            if (comparable && (selectedIndex < 0 || isBetter(run.Result, selected!)))
            {
                selected = run.Result;
                selectedIndex = index;
            }
            members.Add(Ran(spec, memberProfile.MaxExpandedNodes, run,
                compared: comparable,
                skippedReason: comparable
                    ? null
                    : run.MemoryTruncated
                        ? SkippedMemoryTruncated
                        : spec.AggressivePowerCommitment
                            ? SkippedPowerMemberNotTerminal
                            : SkippedNodeLimitNotTerminal));
            run = default;
            if (selectedIndex >= 0)
                firstRan = default;
        }

        string selectionReason;
        if (stopped)
        {
            selectionReason = SelectionStopped;
        }
        else if (selectedIndex < 0)
        {
            if (firstRanIndex < 0)
                throw new InvalidOperationException("组合器没有任何成员运行；预算或成员合同拒绝了全部宽度。");
            selected = firstRan;
            selectedIndex = firstRanIndex;
            selectionReason = SelectionBaselineFallback;
        }
        else
        {
            selectionReason = SelectionBest;
        }

        log?.Invoke(
            $"[CombatSolver/Test] BEAM_WIDTH_PORTFOLIO result " +
            $"members={memberSpecs.Count} ran={members.Count(member => member.Ran)} " +
            $"compared={members.Count(member => member.Compared)} " +
            $"selected_index={selectedIndex} selected_beam={members[selectedIndex].BeamWidth} " +
            $"selected_second_rank_band={members[selectedIndex].SecondRankBand} " +
            $"selected_base_score_only={members[selectedIndex].BaseScoreOnly} " +
            $"selected_power_commitment={members[selectedIndex].AggressivePowerCommitment} " +
            $"selected_offensive_refinement={members[selectedIndex].OffensiveRefinement} " +
            $"selected_bounded_refinement={members[selectedIndex].BoundedRefinement} " +
            $"reason={selectionReason} shared_nodes={sharedMaxExpandedNodes} " +
            $"total_expanded={totalExpanded} total_transitions={totalTransitions}");
        return new BeamWidthPortfolioOutcome<TResult>(
            selected!,
            selectedIndex,
            selectionReason,
            members,
            totalExpanded,
            totalTransitions);

        static BeamWidthPortfolioMember Skipped(BeamWidthPortfolioMemberSpec spec, string reason)
            => new(spec.BeamWidth, spec.SecondRankBand, spec.BaseScoreOnly,
                spec.AggressivePowerCommitment, 0, false, 0, 0, null, null, null, null, null, false, reason)
            { OffensiveRefinement = spec.OffensiveRefinement, BoundedRefinement = spec.BoundedRefinement };

        static BeamWidthPortfolioMember Ran(
            BeamWidthPortfolioMemberSpec spec,
            int nodeBudget,
            BeamWidthPortfolioRun<TResult> run,
            bool compared,
            string? skippedReason)
            => new(spec.BeamWidth, spec.SecondRankBand, spec.BaseScoreOnly,
                spec.AggressivePowerCommitment, nodeBudget, true, run.ExpandedNodes, run.TransitionCount,
                run.Termination, run.Terminal, run.Won, run.BattleHpLost, run.PotionCount,
                compared, skippedReason)
            { OffensiveRefinement = spec.OffensiveRefinement, BoundedRefinement = spec.BoundedRefinement };
    }
}
