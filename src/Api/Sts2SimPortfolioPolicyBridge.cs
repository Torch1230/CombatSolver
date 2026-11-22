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
    int MemoryNoProgressRecoveryLimit = 0,
    bool HasBuiltInOnlyProfileTuning = false);

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

internal readonly record struct Sts2SimAttemptWorkSnapshot(
    long ExpandedParents,
    long TransitionCount,
    long ChoiceReplayAttempts,
    int ObservedPeakParallelism,
    TimeSpan Elapsed,
    bool HasMeasuredPeakParallelism = false)
{
    public bool HasAnyWork =>
        ExpandedParents > 0
        || TransitionCount > 0
        || ChoiceReplayAttempts > 0
        || Elapsed > TimeSpan.Zero;
}

internal readonly record struct Sts2SimPartitionedActionChoices(
    int PrimaryIndex,
    IReadOnlyList<InCombatChoiceTraceDto> ActionChoices,
    IReadOnlyList<InCombatChoiceTraceDto> NonActionChoices,
    int NestedChoicesBeforePrimary);

internal sealed class Sts2SimAttemptWorkTracker
{
    private readonly object _sync = new();
    private long _expandedParents;
    private long _transitionCount;
    private long _choiceReplayAttempts;
    private int _measuredPeakParallelism;
    private int _authoritativeFallbackParallelism;
    private int _provisionalFallbackParallelism;
    private TimeSpan _elapsed;

    public void RecordProgress(InCombatSts2SimProgressDto progress, int fallbackParallelism)
    {
        ArgumentNullException.ThrowIfNull(progress);
        long expandedParents = progress.ExpandedParents is >= 0
            ? progress.ExpandedParents.Value
            : Math.Max(0L, progress.ExpandedNodes);
        long transitionCount = progress.TransitionCount is >= 0
            ? progress.TransitionCount.Value
            : 0L;
        long choiceReplayAttempts = progress.ChoiceReplayAttempts is >= 0
            ? progress.ChoiceReplayAttempts.Value
            : 0L;
        int measuredPeakParallelism = progress.ObservedPeakParallelism is >= 1
            ? progress.ObservedPeakParallelism.Value
            : 0;
        int sanitizedFallback = Math.Max(0, fallbackParallelism);
        TimeSpan elapsed = double.IsFinite(progress.ElapsedMilliseconds) && progress.ElapsedMilliseconds > 0d
            ? TimeSpan.FromMilliseconds(progress.ElapsedMilliseconds)
            : TimeSpan.Zero;

        if (progress.CurrentBest is { } best)
        {
            Sts2SimMaterializedResultMetrics bestMetrics =
                Sts2SimPortfolioPolicyBridge.ResolveResultMetrics(best, Math.Max(1, fallbackParallelism));
            expandedParents = Math.Max(expandedParents, bestMetrics.TotalExpandedNodes);
            transitionCount = Math.Max(transitionCount, bestMetrics.TotalTransitionCount);
            choiceReplayAttempts = Math.Max(choiceReplayAttempts, bestMetrics.TotalChoiceBranchesEvaluated);
            if (best.ObservedPeakParallelism is >= 1)
                measuredPeakParallelism = Math.Max(measuredPeakParallelism, best.ObservedPeakParallelism.Value);
        }

        lock (_sync)
        {
            if (expandedParents > _expandedParents)
                _expandedParents = expandedParents;
            if (transitionCount > _transitionCount)
                _transitionCount = transitionCount;
            if (choiceReplayAttempts > _choiceReplayAttempts)
                _choiceReplayAttempts = choiceReplayAttempts;
            if (measuredPeakParallelism > _measuredPeakParallelism)
                _measuredPeakParallelism = measuredPeakParallelism;
            if (sanitizedFallback > _provisionalFallbackParallelism)
                _provisionalFallbackParallelism = sanitizedFallback;
            if (elapsed > _elapsed)
                _elapsed = elapsed;
        }
    }

    public void RecordResult(InCombatSts2SimResultDto result, int effectiveDegreeOfParallelism, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(result);
        Sts2SimMaterializedResultMetrics metrics =
            Sts2SimPortfolioPolicyBridge.ResolveResultMetrics(result, effectiveDegreeOfParallelism);
        int measuredPeakParallelism = result.ObservedPeakParallelism is >= 1
            ? result.ObservedPeakParallelism.Value
            : 0;
        int authoritativeFallback = Math.Max(1, effectiveDegreeOfParallelism);
        TimeSpan dtoElapsed = double.IsFinite(result.SearchElapsedMilliseconds) && result.SearchElapsedMilliseconds > 0d
            ? TimeSpan.FromMilliseconds(result.SearchElapsedMilliseconds)
            : TimeSpan.Zero;
        TimeSpan effectiveElapsed = elapsed > dtoElapsed ? elapsed : dtoElapsed;

        lock (_sync)
        {
            if (metrics.TotalExpandedNodes > _expandedParents)
                _expandedParents = metrics.TotalExpandedNodes;
            if (metrics.TotalTransitionCount > _transitionCount)
                _transitionCount = metrics.TotalTransitionCount;
            if (metrics.TotalChoiceBranchesEvaluated > _choiceReplayAttempts)
                _choiceReplayAttempts = metrics.TotalChoiceBranchesEvaluated;
            if (measuredPeakParallelism > _measuredPeakParallelism)
                _measuredPeakParallelism = measuredPeakParallelism;
            if (authoritativeFallback > _authoritativeFallbackParallelism)
                _authoritativeFallbackParallelism = authoritativeFallback;
            if (effectiveElapsed > _elapsed)
                _elapsed = effectiveElapsed;
        }
    }

    public void RecordElapsed(
        TimeSpan elapsed,
        int fallbackParallelism = 0,
        bool isAuthoritativeFallback = true)
    {
        TimeSpan sanitizedElapsed = elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
        int sanitizedParallelism = Math.Max(0, fallbackParallelism);
        lock (_sync)
        {
            if (sanitizedElapsed > _elapsed)
                _elapsed = sanitizedElapsed;
            if (sanitizedParallelism > 0)
            {
                if (isAuthoritativeFallback)
                {
                    if (sanitizedParallelism > _authoritativeFallbackParallelism)
                        _authoritativeFallbackParallelism = sanitizedParallelism;
                }
                else if (sanitizedParallelism > _provisionalFallbackParallelism)
                {
                    _provisionalFallbackParallelism = sanitizedParallelism;
                }
            }
        }
    }

    public Sts2SimAttemptWorkSnapshot Snapshot()
    {
        lock (_sync)
        {
            int peak = _measuredPeakParallelism >= 1
                ? _measuredPeakParallelism
                : (_authoritativeFallbackParallelism >= 1
                    ? _authoritativeFallbackParallelism
                    : _provisionalFallbackParallelism);
            return new Sts2SimAttemptWorkSnapshot(
                _expandedParents,
                _transitionCount,
                _choiceReplayAttempts,
                peak,
                _elapsed,
                _measuredPeakParallelism >= 1);
        }
    }
}

internal static class Sts2SimPortfolioPolicyBridge
{
    public const string InCombatProgressPhaseBase = "正在搜索·Sts2Sim";
    public const string InCombatProgressPhasePrefix = "正在搜索·Sts2Sim·";

    public static bool IsBuiltInOnlyPolicy(
        in Sts2SimPortfolioPolicyInput input,
        out string? unsupportedReason)
    {
        if (TryRejectBuiltInOnlyOptions(in input, out unsupportedReason))
            return true;
        if (input.OffensiveRefinementPortfolio && input.BoundedOffensiveRefinementPortfolio)
        {
            unsupportedReason = "OffensiveRefinementPortfolio and BoundedOffensiveRefinementPortfolio cannot both be enabled.";
            return true;
        }
        if (input.EarlyTurnExplorationDepth is < 0 or > 8)
        {
            unsupportedReason = $"EarlyTurnExplorationDepth must be between 0 and 8 (got {input.EarlyTurnExplorationDepth}).";
            return true;
        }
        if (input.EarlyTurnExplorationDepth > 0 && input.EarlyTurnExplorationBudgetMilliseconds < 1)
        {
            unsupportedReason = $"EarlyTurnExplorationBudgetMilliseconds must be at least 1 when depth > 0 (got {input.EarlyTurnExplorationBudgetMilliseconds}).";
            return true;
        }
        if (input.BeamWidthPortfolioWidths is { } widths)
        {
            if (widths.Count > 16)
            {
                unsupportedReason = $"BeamWidthPortfolioWidths count {widths.Count} exceeds maximum 16.";
                return true;
            }
            foreach (int width in widths)
            {
                if (width < 1)
                {
                    unsupportedReason = $"BeamWidthPortfolioWidths contains non-positive width {width}.";
                    return true;
                }
            }
        }
        if (input.NoveltySearch is { } noveltySearch
            && (noveltySearch.Width is not (1 or 2)
                || noveltySearch.FamiliarAllowance < 0
                || noveltySearch.MaxOpen < 1
                || noveltySearch.MaxNoveltyEntries < 1
                || noveltySearch.MaxActions < 1
                || noveltySearch.MaxTurns < 1))
        {
            unsupportedReason = "NoveltySearch options contain out-of-range values.";
            return true;
        }
        if (input.NoveltyBudget is { } noveltyBudget
            && (noveltyBudget.TimeDivisor < 2
                || noveltyBudget.MaximumMilliseconds < 1
                || noveltyBudget.MaximumExpandedNodes < 1))
        {
            unsupportedReason = "NoveltyBudget options contain out-of-range values.";
            return true;
        }

        unsupportedReason = null;
        return false;
    }

    public static bool TryResolvePortfolioPolicy(
        in Sts2SimPortfolioPolicyInput input,
        Sts2SimBridgeCapabilitiesDto capabilities,
        out Sts2SimPortfolioPolicyFields resolved,
        out string? unsupportedReason)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (IsBuiltInOnlyPolicy(in input, out unsupportedReason))
        {
            resolved = default;
            return false;
        }

        bool hasV2 = capabilities.PortfolioOptionsVersion >= 2;
        bool supportsBeamFull = hasV2 && capabilities.SupportsBeamWidthPortfolioFull;

        if (input.BeamWidthPortfolioWidths is not null && !supportsBeamFull)
        {
            resolved = default;
            unsupportedReason = "Custom BeamWidthPortfolioWidths require a v2 Sts2Sim bridge with SupportsBeamWidthPortfolioFull.";
            return false;
        }
        if (!input.BeamWidthPortfolioPlainBaselineMember && !supportsBeamFull)
        {
            resolved = default;
            unsupportedReason = "BeamWidthPortfolioPlainBaselineMember=false requires a v2 Sts2Sim bridge with SupportsBeamWidthPortfolioFull.";
            return false;
        }
        if (!input.ReallocatedRefinementPortfolio && !supportsBeamFull)
        {
            resolved = default;
            unsupportedReason = "ReallocatedRefinementPortfolio=false requires a v2 Sts2Sim bridge with SupportsBeamWidthPortfolioFull.";
            return false;
        }
        if ((input.OffensiveRefinementPortfolio || input.BoundedOffensiveRefinementPortfolio) && !supportsBeamFull)
        {
            resolved = default;
            unsupportedReason = "Offensive refinement portfolio modes require a v2 Sts2Sim bridge with SupportsBeamWidthPortfolioFull.";
            return false;
        }
        if (!input.StopPortfolioAtHpTarget && !supportsBeamFull)
        {
            resolved = default;
            unsupportedReason = "StopPortfolioAtHpTarget=false requires a v2 Sts2Sim bridge with SupportsBeamWidthPortfolioFull.";
            return false;
        }

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

        if (input.NoveltySearch is not null)
        {
            bool supportsNoveltySearch = hasV2
                && (input.UseNoveltyPortfolio || input.AdaptiveNoveltyRefinement
                    ? (capabilities.SupportsNoveltyPortfolio
                        || (input.AdaptiveNoveltyRefinement && capabilities.SupportsAdaptiveNoveltyRefinement))
                    : capabilities.SupportsNoveltyPortfolio);
            if (!supportsNoveltySearch)
            {
                resolved = default;
                unsupportedReason = "NoveltySearch options require a v2 Sts2Sim bridge with SupportsNoveltyPortfolio.";
                return false;
            }
        }

        if (input.NoveltyBudget is { } nb && !IsDefaultNoveltyBudget(nb)
            && (!hasV2 || (!capabilities.SupportsNoveltyPortfolio && !capabilities.SupportsAdaptiveNoveltyRefinement)))
        {
            resolved = default;
            unsupportedReason = "Non-default NoveltyBudget requires a v2 Sts2Sim bridge with novelty support.";
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
            if (input.EarlyTurnExplorationDepth > capabilities.MaxEarlyTurnExplorationDepth)
            {
                resolved = default;
                unsupportedReason = $"EarlyTurnExplorationDepth {input.EarlyTurnExplorationDepth} exceeds negotiated maximum {capabilities.MaxEarlyTurnExplorationDepth}.";
                return false;
            }
        }

        if (!hasV2)
        {
            resolved = Sts2SimPortfolioPolicyFields.V1Empty;
            unsupportedReason = null;
            return true;
        }

        bool forwardBeamFull = capabilities.SupportsBeamWidthPortfolioFull;
        bool forwardAdaptiveNovelty = capabilities.SupportsAdaptiveNoveltyRefinement;
        bool forwardNoveltySearch = input.NoveltySearch is not null
            && (capabilities.SupportsNoveltyPortfolio
                || (input.AdaptiveNoveltyRefinement && capabilities.SupportsAdaptiveNoveltyRefinement));
        bool forwardNoveltyBudget = input.NoveltyBudget is not null
            && (capabilities.SupportsNoveltyPortfolio || capabilities.SupportsAdaptiveNoveltyRefinement)
            && (input.UseNoveltyPortfolio
                || input.AdaptiveNoveltyRefinement
                || input.NoveltySearch is not null
                || !IsDefaultNoveltyBudget(input.NoveltyBudget));
        bool forwardEarlyTurn = capabilities.SupportsEarlyTurnExploration;
        int? boundedEarlyBudget = forwardEarlyTurn && input.EarlyTurnExplorationDepth > 0
            ? Math.Min(capabilities.MaxEarlyTurnExplorationBudgetMilliseconds, input.EarlyTurnExplorationBudgetMilliseconds)
            : null;
        string? schedulingMode = capabilities.SupportsParallelPortfolioScheduling
            ? (input.MaxDegreeOfParallelism > 1 ? "Parallel" : "Sequential")
            : null;

        resolved = new Sts2SimPortfolioPolicyFields(
            PortfolioOptionsVersion: 2,
            BeamWidthPortfolioWidths: forwardBeamFull ? input.BeamWidthPortfolioWidths?.ToArray() : null,
            BeamWidthPortfolioPlainBaselineMember: forwardBeamFull ? input.BeamWidthPortfolioPlainBaselineMember : null,
            ReallocatedRefinementPortfolio: forwardBeamFull ? input.ReallocatedRefinementPortfolio : null,
            OffensiveRefinementPortfolio: forwardBeamFull ? input.OffensiveRefinementPortfolio : null,
            BoundedOffensiveRefinementPortfolio: forwardBeamFull ? input.BoundedOffensiveRefinementPortfolio : null,
            StopPortfolioAtHpTarget: forwardBeamFull ? input.StopPortfolioAtHpTarget : null,
            AdaptiveNoveltyRefinement: forwardAdaptiveNovelty ? input.AdaptiveNoveltyRefinement : null,
            NoveltySearch: forwardNoveltySearch ? input.NoveltySearch : null,
            NoveltyBudget: forwardNoveltyBudget ? input.NoveltyBudget : null,
            EarlyTurnExplorationDepth: forwardEarlyTurn ? input.EarlyTurnExplorationDepth : null,
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
        long fallbackNodes = Math.Max(0L, dto.ExpandedNodes);
        long expandedParents = dto.ExpandedParents is >= 0
            ? dto.ExpandedParents.Value
            : fallbackNodes;
        long transitionCount = dto.TransitionCount is >= 0
            ? dto.TransitionCount.Value
            : 0L;
        long choiceReplayAttempts = dto.ChoiceReplayAttempts is >= 0
            ? dto.ChoiceReplayAttempts.Value
            : 0L;
        int peakParallelism = dto.ObservedPeakParallelism is >= 1
            ? dto.ObservedPeakParallelism.Value
            : Math.Max(1, effectiveDegreeOfParallelism);

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
        long fallbackNodes = Math.Max(0L, dto.ExpandedNodes);
        long reviewedParents = dto.ExpandedParents is >= 0
            ? dto.ExpandedParents.Value
            : fallbackNodes;
        string phase = !string.IsNullOrWhiteSpace(dto.ActivePhaseLabel)
            ? $"{InCombatProgressPhasePrefix}{dto.ActivePhaseLabel.Trim()}"
            : InCombatProgressPhaseBase;

        return new Sts2SimMaterializedProgressMetrics(
            ExpandedNodes: (int)Math.Clamp(reviewedParents, 0L, int.MaxValue),
            ReviewedWorldlines: reviewedParents,
            Phase: phase);
    }

    public static bool TryExtractSts2SimActivePhaseLabel(
        string? phase,
        out string? activePhaseLabel)
    {
        if (string.IsNullOrEmpty(phase))
        {
            activePhaseLabel = null;
            return false;
        }
        if (phase.StartsWith(InCombatProgressPhasePrefix, StringComparison.Ordinal))
        {
            string suffix = phase[InCombatProgressPhasePrefix.Length..];
            if (!string.IsNullOrEmpty(suffix))
            {
                activePhaseLabel = suffix;
                return true;
            }
            activePhaseLabel = null;
            return true;
        }
        if (string.Equals(phase, InCombatProgressPhaseBase, StringComparison.Ordinal))
        {
            activePhaseLabel = null;
            return true;
        }

        activePhaseLabel = null;
        return false;
    }

    public static Sts2SimPartitionedActionChoices PartitionActionChoices(
        string actionKind,
        string? cardId,
        string? potionId,
        IReadOnlyList<InCombatChoiceTraceDto> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        List<InCombatChoiceTraceDto> actionChoices = [];
        List<InCombatChoiceTraceDto> nonActionChoices = [];
        for (int i = 0; i < choices.Count; i++)
        {
            InCombatChoiceTraceDto choice = choices[i];
            if (string.Equals(choice.Timing, "Action", StringComparison.Ordinal))
                actionChoices.Add(choice);
            else
                nonActionChoices.Add(choice);
        }

        int primaryIndex = -1;
        bool isPlayCard = string.Equals(actionKind, "PlayCard", StringComparison.Ordinal)
            && !string.IsNullOrEmpty(cardId);
        bool isUsePotion = string.Equals(actionKind, "UsePotion", StringComparison.Ordinal)
            && !string.IsNullOrEmpty(potionId);
        if (isPlayCard || isUsePotion)
        {
            string expectedSourceId = isPlayCard ? cardId! : potionId!;
            for (int i = 0; i < actionChoices.Count; i++)
            {
                if (string.Equals(actionChoices[i].SourceId, expectedSourceId, StringComparison.Ordinal))
                {
                    primaryIndex = i;
                    break;
                }
            }
        }

        int nestedBeforePrimary = primaryIndex >= 0 ? primaryIndex : actionChoices.Count;
        return new Sts2SimPartitionedActionChoices(
            primaryIndex,
            actionChoices,
            nonActionChoices,
            nestedBeforePrimary);
    }

    private static bool IsDefaultNoveltyBudget(Sts2SimNoveltyPortfolioBudgetDto budget) =>
        budget.TimeDivisor == 2
        && budget.MaximumMilliseconds == 5_000
        && budget.MaximumExpandedNodes == 2_500;

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
        if (input.HasBuiltInOnlyProfileTuning)
        {
            reason = "Built-in solver profile tuning options (SecondRankBand/BaseScoreOnly/BossTempoHpPricing/AggressivePowerCommitment) are active.";
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
