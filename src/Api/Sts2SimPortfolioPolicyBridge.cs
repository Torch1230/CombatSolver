namespace CombatSolver.Api;

internal readonly record struct Sts2SimPortfolioPolicyInput(
    bool UseBeamWidthPortfolio,
    bool UseNoveltyPortfolio,
    int EarlyTurnExplorationDepth,
    int EarlyTurnExplorationBudgetMilliseconds,
    IReadOnlyList<int>? BeamWidthPortfolioWidths,
    bool BeamWidthPortfolioPlainBaselineMember,
    bool ReallocatedRefinementPortfolio,
    bool OffensiveRefinementPortfolio,
    bool BoundedOffensiveRefinementPortfolio,
    bool StopPortfolioAtHpTarget,
    bool AdaptiveNoveltyRefinement,
    Sts2SimNoveltySearchOptionsDto? NoveltySearch,
    Sts2SimNoveltyPortfolioBudgetDto? NoveltyBudget,
    int MaxDegreeOfParallelism,
    bool VerifyIncrementalSearch = false,
    bool MeasurePhasePerformance = false,
    bool IncludeTurnSetup = false,
    bool HasBossTempoSearch = false,
    bool HasContextualRanking = false,
    bool ContinuousThreatRanking = false,
    bool BaseScoreTacticalTies = false,
    bool HasBeamWeightPerturbation = false,
    bool HasDevelopmentStrategy = false,
    bool HasPortfolioExperiment = false,
    int PileOrderInvariantMask = 0,
    int TranspositionPruningDisabledMask = 0,
    int StateKeySalt = 0,
    int MemoryNoProgressRecoveryLimit = 0);

internal readonly record struct Sts2SimPortfolioPolicyFields(
    int? PortfolioOptionsVersion,
    IReadOnlyList<int>? BeamWidthPortfolioWidths,
    bool? BeamWidthPortfolioPlainBaselineMember,
    bool? ReallocatedRefinementPortfolio,
    bool? OffensiveRefinementPortfolio,
    bool? BoundedOffensiveRefinementPortfolio,
    bool? StopPortfolioAtHpTarget,
    bool? AdaptiveNoveltyRefinement,
    Sts2SimNoveltySearchOptionsDto? NoveltySearch,
    Sts2SimNoveltyPortfolioBudgetDto? NoveltyBudget,
    int? EarlyTurnExplorationDepth,
    int? EarlyTurnExplorationBudgetMilliseconds,
    string? PortfolioSchedulingMode)
{
    public static Sts2SimPortfolioPolicyFields V1Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null);
}

internal readonly record struct Sts2SimMaterializedResultMetrics(
    int ExpandedNodes,
    long TotalExpandedNodes,
    int TransitionCount,
    long TotalTransitionCount,
    int ChoiceBranchesEvaluated,
    long TotalChoiceBranchesEvaluated,
    int MaxParallelExpansionConcurrency);

internal readonly record struct Sts2SimMaterializedProgressMetrics(
    int ExpandedNodes,
    long ReviewedWorldlines,
    string Phase);

internal static class Sts2SimPortfolioPolicyBridge
{
    public static bool TryResolvePortfolioPolicy(
        in Sts2SimPortfolioPolicyInput input,
        Sts2SimBridgeCapabilitiesDto capabilities,
        out Sts2SimPortfolioPolicyFields resolved,
        out string? unsupportedReason)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (TryRejectBuiltInOnlyOptions(in input, out unsupportedReason))
        {
            resolved = default;
            return false;
        }

        if (input.OffensiveRefinementPortfolio && input.BoundedOffensiveRefinementPortfolio)
        {
            resolved = default;
            unsupportedReason = "OffensiveRefinementPortfolio and BoundedOffensiveRefinementPortfolio cannot both be enabled.";
            return false;
        }

        if (input.EarlyTurnExplorationDepth < 0)
        {
            resolved = default;
            unsupportedReason = $"EarlyTurnExplorationDepth must be non-negative (got {input.EarlyTurnExplorationDepth}).";
            return false;
        }

        bool hasV2 = capabilities.PortfolioOptionsVersion >= 2;

        if (input.UseNoveltyPortfolio && (!hasV2 || !capabilities.SupportsNoveltyPortfolio))
        {
            resolved = default;
            unsupportedReason = "UseNoveltyPortfolio requires a v2 Sts2Sim bridge with SupportsNoveltyPortfolio.";
            return false;
        }

        if (input.AdaptiveNoveltyRefinement && (!hasV2 || !capabilities.SupportsAdaptiveNoveltyRefinement))
        {
            resolved = default;
            unsupportedReason = "AdaptiveNoveltyRefinement requires a v2 Sts2Sim bridge with SupportsAdaptiveNoveltyRefinement.";
            return false;
        }

        if (input.EarlyTurnExplorationDepth > 0)
        {
            if (!hasV2 || !capabilities.SupportsEarlyTurnExploration)
            {
                resolved = default;
                unsupportedReason = "EarlyTurnExploration requires a v2 Sts2Sim bridge with SupportsEarlyTurnExploration.";
                return false;
            }
            if (input.EarlyTurnExplorationDepth > 8 || input.EarlyTurnExplorationDepth > capabilities.MaxEarlyTurnExplorationDepth)
            {
                resolved = default;
                unsupportedReason = $"EarlyTurnExplorationDepth {input.EarlyTurnExplorationDepth} exceeds negotiated maximum {capabilities.MaxEarlyTurnExplorationDepth}.";
                return false;
            }
            if (input.EarlyTurnExplorationBudgetMilliseconds < 1)
            {
                resolved = default;
                unsupportedReason = $"EarlyTurnExplorationBudgetMilliseconds must be at least 1 when depth > 0 (got {input.EarlyTurnExplorationBudgetMilliseconds}).";
                return false;
            }
        }

        if (input.BeamWidthPortfolioWidths is not null)
        {
            if (!hasV2 || !capabilities.SupportsBeamWidthPortfolioFull)
            {
                resolved = default;
                unsupportedReason = "Custom BeamWidthPortfolioWidths require a v2 Sts2Sim bridge with SupportsBeamWidthPortfolioFull.";
                return false;
            }
            if (input.BeamWidthPortfolioWidths.Count > 16)
            {
                resolved = default;
                unsupportedReason = $"BeamWidthPortfolioWidths count {input.BeamWidthPortfolioWidths.Count} exceeds maximum 16.";
                return false;
            }
            foreach (int width in input.BeamWidthPortfolioWidths)
            {
                if (width < 1)
                {
                    resolved = default;
                    unsupportedReason = $"BeamWidthPortfolioWidths contains non-positive width {width}.";
                    return false;
                }
            }
        }

        if (!input.BeamWidthPortfolioPlainBaselineMember && (!hasV2 || !capabilities.SupportsBeamWidthPortfolioFull))
        {
            resolved = default;
            unsupportedReason = "BeamWidthPortfolioPlainBaselineMember=false requires a v2 Sts2Sim bridge with SupportsBeamWidthPortfolioFull.";
            return false;
        }

        if ((input.OffensiveRefinementPortfolio || input.BoundedOffensiveRefinementPortfolio)
            && (!hasV2 || !capabilities.SupportsBeamWidthPortfolioFull))
        {
            resolved = default;
            unsupportedReason = "Offensive refinement portfolio modes require a v2 Sts2Sim bridge with SupportsBeamWidthPortfolioFull.";
            return false;
        }

        if (!hasV2)
        {
            resolved = Sts2SimPortfolioPolicyFields.V1Empty;
            unsupportedReason = null;
            return true;
        }

        if (input.NoveltySearch is { } noveltySearch
            && (noveltySearch.Width is not (1 or 2)
                || noveltySearch.FamiliarAllowance < 0
                || noveltySearch.MaxOpen < 1
                || noveltySearch.MaxNoveltyEntries < 1
                || noveltySearch.MaxActions < 1
                || noveltySearch.MaxTurns < 1))
        {
            resolved = default;
            unsupportedReason = "NoveltySearch options contain out-of-range values.";
            return false;
        }

        if (input.NoveltyBudget is { } noveltyBudget
            && (noveltyBudget.TimeDivisor < 2
                || noveltyBudget.MaximumMilliseconds < 1
                || noveltyBudget.MaximumExpandedNodes < 1))
        {
            resolved = default;
            unsupportedReason = "NoveltyBudget options contain out-of-range values.";
            return false;
        }

        int? boundedEarlyBudget = input.EarlyTurnExplorationDepth > 0
            ? Math.Min(capabilities.MaxEarlyTurnExplorationBudgetMilliseconds, input.EarlyTurnExplorationBudgetMilliseconds)
            : null;
        string schedulingMode = capabilities.SupportsParallelPortfolioScheduling && input.MaxDegreeOfParallelism > 1
            ? "Parallel"
            : "Sequential";

        resolved = new Sts2SimPortfolioPolicyFields(
            PortfolioOptionsVersion: 2,
            BeamWidthPortfolioWidths: input.BeamWidthPortfolioWidths?.ToArray(),
            BeamWidthPortfolioPlainBaselineMember: input.BeamWidthPortfolioPlainBaselineMember,
            ReallocatedRefinementPortfolio: input.ReallocatedRefinementPortfolio,
            OffensiveRefinementPortfolio: input.OffensiveRefinementPortfolio,
            BoundedOffensiveRefinementPortfolio: input.BoundedOffensiveRefinementPortfolio,
            StopPortfolioAtHpTarget: input.StopPortfolioAtHpTarget,
            AdaptiveNoveltyRefinement: input.AdaptiveNoveltyRefinement,
            NoveltySearch: input.NoveltySearch,
            NoveltyBudget: input.NoveltyBudget,
            EarlyTurnExplorationDepth: input.EarlyTurnExplorationDepth,
            EarlyTurnExplorationBudgetMilliseconds: boundedEarlyBudget,
            PortfolioSchedulingMode: schedulingMode);
        unsupportedReason = null;
        return true;
    }

    public static int ComputeInCombatOverallTimeoutMilliseconds(
        int searchBudgetMs,
        int earlyTurnExplorationDepth,
        int earlyTurnExplorationBudgetMilliseconds,
        Sts2SimBridgeCapabilitiesDto capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        int baseTimeoutMs = Math.Min(
            InCombatSts2SimAbi.MaxSoftTimeBudgetMilliseconds + 30_000,
            searchBudgetMs + 30_000);
        if (earlyTurnExplorationDepth > 0
            && capabilities.PortfolioOptionsVersion >= 2
            && capabilities.SupportsEarlyTurnExploration)
        {
            int cappedEarlyBudgetMs = Math.Min(
                capabilities.MaxEarlyTurnExplorationBudgetMilliseconds,
                Math.Max(searchBudgetMs, earlyTurnExplorationBudgetMilliseconds));
            return Math.Max(baseTimeoutMs, cappedEarlyBudgetMs + 30_000);
        }
        return baseTimeoutMs;
    }

    public static Sts2SimMaterializedResultMetrics ResolveResultMetrics(
        InCombatSts2SimResultDto dto,
        int effectiveDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(dto);
        long expandedParents = dto.ExpandedParents is >= 0
            ? dto.ExpandedParents.Value
            : dto.ExpandedNodes;
        long transitionCount = dto.TransitionCount is >= 0
            ? dto.TransitionCount.Value
            : 0L;
        long choiceReplayAttempts = dto.ChoiceReplayAttempts is >= 0
            ? dto.ChoiceReplayAttempts.Value
            : 0L;
        int peakParallelism = dto.ObservedPeakParallelism is >= 1
            ? dto.ObservedPeakParallelism.Value
            : effectiveDegreeOfParallelism;

        return new Sts2SimMaterializedResultMetrics(
            ExpandedNodes: (int)Math.Clamp(expandedParents, 0L, int.MaxValue),
            TotalExpandedNodes: expandedParents,
            TransitionCount: (int)Math.Clamp(transitionCount, 0L, int.MaxValue),
            TotalTransitionCount: transitionCount,
            ChoiceBranchesEvaluated: (int)Math.Clamp(choiceReplayAttempts, 0L, int.MaxValue),
            TotalChoiceBranchesEvaluated: choiceReplayAttempts,
            MaxParallelExpansionConcurrency: peakParallelism);
    }

    public static Sts2SimMaterializedProgressMetrics ResolveProgressMetrics(
        InCombatSts2SimProgressDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        long reviewedParents = dto.ExpandedParents is >= 0
            ? dto.ExpandedParents.Value
            : dto.ExpandedNodes;
        string phase = !string.IsNullOrWhiteSpace(dto.ActivePhaseLabel)
            ? $"正在搜索·Sts2Sim·{dto.ActivePhaseLabel.Trim()}"
            : "正在搜索·Sts2Sim";

        return new Sts2SimMaterializedProgressMetrics(
            ExpandedNodes: (int)Math.Clamp(reviewedParents, 0L, int.MaxValue),
            ReviewedWorldlines: reviewedParents,
            Phase: phase);
    }

    private static bool TryRejectBuiltInOnlyOptions(
        in Sts2SimPortfolioPolicyInput input,
        out string? reason)
    {
        if (input.VerifyIncrementalSearch)
        {
            reason = "VerifyIncrementalSearch requires the built-in incremental search verifier.";
            return true;
        }
        if (input.MeasurePhasePerformance)
        {
            reason = "MeasurePhasePerformance targets built-in solver metrics.";
            return true;
        }
        if (input.IncludeTurnSetup)
        {
            reason = "Turn-setup root exploration uses the built-in solver.";
            return true;
        }
        if (input.HasBossTempoSearch)
        {
            reason = "BossTempoSearch experiment flags are not mapped to Sts2Sim bridge.";
            return true;
        }
        if (input.HasContextualRanking
            || input.ContinuousThreatRanking
            || input.BaseScoreTacticalTies
            || input.HasBeamWeightPerturbation
            || input.HasDevelopmentStrategy
            || input.HasPortfolioExperiment
            || input.PileOrderInvariantMask != 0
            || input.TranspositionPruningDisabledMask != 0
            || input.StateKeySalt != 0
            || input.MemoryNoProgressRecoveryLimit != 0)
        {
            reason = "Built-in solver ablation or experimental ranking options are active.";
            return true;
        }

        reason = null;
        return false;
    }
}
