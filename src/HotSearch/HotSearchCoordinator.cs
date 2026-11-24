using System.Buffers;
using System.Diagnostics;
using CombatSolver.HotKernel;

namespace CombatSolver.HotSearch;

internal sealed class HotSearchBudget
{
    private readonly Stopwatch _clock;
    public readonly int MaxExpandedNodes;
    public readonly long SoftTimeBudgetMilliseconds;
    public readonly CancellationToken CancellationToken;

    public HotSearchBudget(
        Stopwatch clock,
        int maxExpandedNodes,
        long softTimeBudgetMilliseconds,
        CancellationToken cancellationToken)
    {
        _clock = clock;
        MaxExpandedNodes = Math.Max(1, maxExpandedNodes);
        SoftTimeBudgetMilliseconds = Math.Max(1L, softTimeBudgetMilliseconds);
        CancellationToken = cancellationToken;
    }

    public long ElapsedMilliseconds => _clock.ElapsedMilliseconds;
    public TimeSpan Elapsed => _clock.Elapsed;
    public long RemainingMilliseconds => SoftTimeBudgetMilliseconds - _clock.ElapsedMilliseconds;
    public bool IsTimeExpired => _clock.ElapsedMilliseconds >= SoftTimeBudgetMilliseconds;
    public bool IsCanceled => CancellationToken.IsCancellationRequested;
}

internal readonly record struct HotPassOutcome(
    int BestNodeId,
    HotRouteKey BestKey,
    bool HasBest,
    bool HasVictory,
    bool HasCompletedTurnOngoing,
    int CompletedOngoingTurn,
    bool IsMidTurnFallback,
    bool OnlyDeathRoutes,
    bool StopTargetReached,
    bool CapacityBounded,
    SearchBoundaryReason BoundaryReason,
    int SearchedTurns,
    int PrimaryIncumbentUpdates,
    int PeakTranspositionCount,
    int PeakStandPatCacheCount,
    HotWaveStats Stats,
    HotRolloutStats RolloutStats,
    TimeSpan Elapsed,
    int Gen0,
    int Gen1,
    int Gen2,
    TimeSpan GcPause);

/// <summary>
/// Unified Hot-Kernel search coordinator built on ONE coherent economic objective
/// (<c>V(route) = NetPersistentHpDelta - NetPotionOpportunityCost</c>, zero post-hoc Smart/Ambergris vetoes)
/// with 3-stage outcome-driven Anytime Adaptive Escalation (Stage 1 W=96/150k Balanced -> Stage 2 W=240/320k
/// SetupAndSurvival -> Stage 3 Balanced refinement when winning or sequential Stage 3a W=320 Offense / Stage 3b
/// W=384 DesperateRescue defeat-rescue portfolio sharing <see cref="HotIncumbentBound"/>),
/// turn-boundary packed-state compaction (exact 1 MiB power-of-2 slabs), and Anytime Progressive Preview via
/// <c>onPreliminaryResult</c>.
/// </summary>
internal static class HotSearchCoordinator
{
    private const int DefaultMaxSearchTurns = 28;
    private const int BossExtendedMaxSearchTurns = 40;
    private const int BossExtensionHpStallLimit = 2;
    private const int StandPatTopK = 16;

    // Exact 1 MiB power-of-2 ArrayPool slab (blockPayloadBytes + MaxPackedStateBytes == 1,048,576)
    // eliminating 2x LOH rounding waste from default 2 MiB + 17 KiB -> 4 MiB rents.
    private const int MainStateArenaPayloadBytes = (1024 * 1024) - HotLimits.MaxPackedStateBytes;

    // 3-Stage Outcome-Driven Adaptive Escalation Schedule (#283 Plan A + #301/#314 RSS & W=240 tuning + Wave 3 rescue portfolio)
    private const int Stage1AdaptiveWidth = 96;
    private const int Stage1AdaptiveNodes = 150_000;
    private const int Stage1RolloutHorizon = 4;

    private const int Stage2AdaptiveWidth = 240;
    private const int Stage2AdaptiveNodes = 320_000;
    private const int Stage2RolloutHorizon = 6;

    private const int Stage3AdaptiveWidth = 320;
    private const int Stage3AdaptiveNodes = 500_000;
    private const int Stage3RolloutHorizon = 6;

    private const int Stage3bRescueWidth = 384;
    private const int Stage3bRescueNodes = 650_000;

    /// <summary>
    /// Captures the canonical comparison quality of an already-materialized <see cref="SolverResult"/>
    /// against <paramref name="root"/> and <paramref name="policy"/>.
    /// </summary>
    public static SolverInterimResult CaptureQuality(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
    {
        if (result.ComparisonQuality is { } existing)
            return existing;

        HotRootBundle rootBundle = root.HotRoot;
        CombatHotState rootState = rootBundle.RootState;
        short[] rootPotionCosts = HotPotionPolicy.BuildRootSlotCosts(
            root, in rootState, rootBundle.Rules, policy.PotionStrategy.Thresholds);
        BattleDamageSnapshot battleDamage = new(
            HpLostSoFar: result.BattleHpLostSoFar,
            SoldHpCommitted: result.SoldHp,
            PotionsUsedSoFar: result.BattlePotionsUsedSoFar,
            PotionIdsUsedSoFar: result.BattlePotionIdsUsedSoFar,
            HpRecoveredOrGainedSoFar: result.BattleHpRecoveredOrGainedSoFar);
        HotSearchPolicy hotPolicy = new(root, battleDamage, policy, rootBundle.Rules, in rootState, rootPotionCosts);
        HotRouteKey key = HotRouteQuality.FromSolverResult(result, in hotPolicy);
        return HotRouteQuality.ToInterimResult(in key, in hotPolicy);
    }

    public static SolverResult SolveSingleBeam(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        Stopwatch requestClock = Stopwatch.StartNew();
        HotRootBundle rootBundle = root.HotRoot;
        CombatHotState rootState = rootBundle.RootState;
        short[] rootPotionCosts = HotPotionPolicy.BuildRootSlotCosts(
            root, in rootState, rootBundle.Rules, policy.PotionStrategy.Thresholds);
        HotSearchPolicy hotPolicy = new(root, battleDamage, policy, rootBundle.Rules, in rootState, rootPotionCosts);
        HotEvalLookup evalLookup = new(rootBundle.Rules);

        long softBudgetMs = Math.Max(1L, policy.BudgetOverrideMilliseconds ?? policy.Profile.SoftTimeBudgetMilliseconds);
        int maxNodes = Math.Max(1, policy.Profile.MaxExpandedNodes);
        HotSearchBudget budget = new(requestClock, maxNodes, softBudgetMs, cancellationToken);
        int dop = ResolveEffectiveDop(policy);

        using HotNodeArena nodeArena = new();
        using HotTranspositionTable tt = new(hotPolicy.TranspositionEntryLimit);
        using HotBeamRetention retention = new();
        using HotParallelExpander expander = new(dop);
        using HotRolloutEvaluator? rolloutEvaluator = policy.EnableTurnBoundaryRollouts
            ? new HotRolloutEvaluator(dop)
            : null;
        PackedStateArena mainStateArena = new(MainStateArenaPayloadBytes);
        PackedStateArena compactStateArena = new(MainStateArenaPayloadBytes);

        try
        {
            bool isDisabledMode = hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Disabled;
            bool allowPaidOptional = !isDisabledMode;
            bool allowZeroCostOptional = !isDisabledMode;
            bool allowForced = hotPolicy.HasForcedDirectives;

            int beamWidth = ResolveInitialBeamWidth(policy.Profile.BeamWidth, in hotPolicy);
            bool disableTt = (policy.TranspositionPruningDisabledMask & 1) != 0;

            HotIncumbentBound incumbent = HotIncumbentBound.None;

            HotPassOutcome pass = RunBeamPass(
                rootBundle,
                in hotPolicy,
                in evalLookup,
                rootPotionCosts,
                policy.Profile,
                budget,
                nodeArena,
                ref mainStateArena,
                ref compactStateArena,
                tt,
                retention,
                expander,
                rolloutEvaluator,
                beamWidth,
                maxNodesForPass: maxNodes,
                allowPaidOptionalPotions: allowPaidOptional,
                allowZeroCostOptionalPotions: allowZeroCostOptional,
                allowForcedPotions: allowForced,
                maxPotionUses: -1,
                disableTt: disableTt,
                evalMode: HotEvalMode.Balanced,
                preferPotionBuckets: allowPaidOptional && hotPolicy.PaidOptionalPotionCount > 0,
                ref incumbent,
                progressCallback: null,
                phaseLabel: "正在搜索路线",
                reviewedOffset: 0,
                enableTurnBoundaryRollouts: policy.EnableTurnBoundaryRollouts,
                rolloutHorizonTurns: Stage1RolloutHorizon);

            HotRolloutStats passRolloutStats = pass.RolloutStats;
            EmitRolloutDiagnostics(in passRolloutStats, policy);

            HotRouteKey passBestKey = pass.BestKey;
            ValidateHardPotionConstraints(in passBestKey, pass.HasBest, in hotPolicy, policy.PotionStrategy);

            int potionHpRequired = passBestKey.OptionalPotionStrategicCost;
            HotMaterializeMetrics metrics = new(
                ExpandedNodes: pass.Stats.ExpandedParents,
                TotalExpandedNodes: pass.Stats.ExpandedParents,
                TransitionCount: pass.Stats.Transitions,
                TotalTransitionCount: pass.Stats.Transitions,
                ChoiceBranchesEvaluated: pass.Stats.ChoiceBranches,
                TotalChoiceBranchesEvaluated: pass.Stats.ChoiceBranches,
                DuplicateCardBranchesPruned: pass.Stats.DuplicateCardsPruned,
                TranspositionBranchesPruned: pass.Stats.TranspositionPrunes,
                PrimaryIncumbentBranchesPruned: pass.Stats.IncumbentPrunes,
                PrimaryIncumbentUpdates: pass.PrimaryIncumbentUpdates,
                StandPatProbes: pass.Stats.StandPatProbes,
                EndTurnPrefixCaptures: pass.Stats.EndTurnPrefixCaptures,
                EndTurnPrefixReuses: pass.Stats.EndTurnPrefixReuses,
                ParallelExpansionWaves: pass.Stats.ParallelWaves,
                ParallelExpansionWorkItems: pass.Stats.ParallelWorkItems,
                MaxParallelExpansionConcurrency: pass.Stats.MaxConcurrency,
                TranspositionCount: pass.PeakTranspositionCount,
                TranspositionLimitBypasses: tt.LimitBypasses,
                StandPatCacheCount: pass.PeakStandPatCacheCount,
                SearchedTurns: pass.SearchedTurns,
                BoundaryReason: ResolveEffectiveBoundaryReason(in pass),
                OnlyDeathRoutesFound: pass.OnlyDeathRoutes,
                WorkerAllocatedBytes: pass.Stats.WorkerAllocatedBytes,
                TotalWorkerAllocatedBytes: pass.Stats.WorkerAllocatedBytes,
                Gen0Collections: pass.Gen0,
                Gen1Collections: pass.Gen1,
                Gen2Collections: pass.Gen2,
                GcPauseDuration: pass.GcPause,
                Elapsed: pass.Elapsed,
                TotalSearchElapsed: requestClock.Elapsed,
                PotionHpSaved: 0,
                PotionHpRequired: potionHpRequired,
                PotionBranchesRejected: 0);

            SolverResult result = HotPlanMaterializer.Materialize(
                root,
                displayNames,
                battleDamage,
                policy,
                rootBundle,
                in hotPolicy,
                rootPotionCosts,
                nodeArena,
                pass.BestNodeId,
                in passBestKey,
                in metrics);
            result.SingleSessionSearch = true;
            result.IsPreliminary = false;
            result.EscalationStage = 1;
            return result;
        }
        finally
        {
            mainStateArena.Dispose();
            compactStateArena.Dispose();
        }
    }

    public static SolverResult Solve(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback = null,
        Action<SolverResult>? onPreliminaryResult = null)
    {
        Stopwatch requestClock = Stopwatch.StartNew();

        HotRootBundle rootBundle = root.HotRoot;
        CombatHotState rootState = rootBundle.RootState;
        short[] rootPotionCosts = HotPotionPolicy.BuildRootSlotCosts(
            root, in rootState, rootBundle.Rules, policy.PotionStrategy.Thresholds);
        HotSearchPolicy hotPolicy = new(root, battleDamage, policy, rootBundle.Rules, in rootState, rootPotionCosts);
        HotEvalLookup evalLookup = new(rootBundle.Rules);

        long liveSolveCeilingMs = (long)Math.Round(
            Math.Clamp(
                policy.MaxCombatSolveSeconds > 0d
                    ? policy.MaxCombatSolveSeconds
                    : SearchPolicySnapshot.DefaultMaxCombatSolveSeconds,
                SearchPolicySnapshot.MinMaxCombatSolveSeconds,
                SearchPolicySnapshot.MaxMaxCombatSolveSeconds) * 1000d);

        long softBudgetMs = policy.BudgetOverrideMilliseconds is { } overrideMs
            ? Math.Max(1L, overrideMs)
            : (!policy.FixedBudget && policy.AdaptiveEscalationEnabled
                ? liveSolveCeilingMs
                : Math.Max(1L, policy.Profile.SoftTimeBudgetMilliseconds));

        bool isDefaultAdaptiveProfile = policy.AdaptiveEscalationEnabled
            && policy.Profile.BeamWidth == SolverSearchProfile.Default.BeamWidth
            && policy.Profile.MaxExpandedNodes == SolverSearchProfile.Default.MaxExpandedNodes;

        int configuredBeam = ResolveInitialBeamWidth(policy.Profile.BeamWidth, in hotPolicy);
        int configuredNodes = Math.Max(1, policy.Profile.MaxExpandedNodes);

        int stage1Width = isDefaultAdaptiveProfile ? Stage1AdaptiveWidth : configuredBeam;
        int stage1NodeCeiling = isDefaultAdaptiveProfile ? Stage1AdaptiveNodes : configuredNodes;
        int stage2Width = isDefaultAdaptiveProfile ? Stage2AdaptiveWidth : ResolveWidenedBeamWidth(configuredBeam);
        int stage2NodeCeiling = isDefaultAdaptiveProfile ? Stage2AdaptiveNodes : configuredNodes;
        int stage3Width = Stage3AdaptiveWidth;
        int stage3NodeCeiling = Stage3AdaptiveNodes;
        int maxTotalNodes = isDefaultAdaptiveProfile
            ? Math.Max(stage3NodeCeiling, Stage3bRescueNodes)
            : configuredNodes;

        HotSearchBudget fullBudget = new(requestClock, maxTotalNodes, softBudgetMs, cancellationToken);
        int dop = ResolveEffectiveDop(policy);
        bool disableTt = (policy.TranspositionPruningDisabledMask & 1) != 0;

        using HotNodeArena nodeArena = new();
        using HotTranspositionTable tt = new(hotPolicy.TranspositionEntryLimit);
        using HotBeamRetention retention = new();
        using HotParallelExpander expander = new(dop);
        using HotRolloutEvaluator? rolloutEvaluator = policy.EnableTurnBoundaryRollouts
            ? new HotRolloutEvaluator(dop)
            : null;
        PackedStateArena mainStateArena = new(MainStateArenaPayloadBytes);
        PackedStateArena compactStateArena = new(MainStateArenaPayloadBytes);

        try
        {
            HotIncumbentBound sharedIncumbent = HotIncumbentBound.None;

            bool smartMode = hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Smart;
            bool isDisabledMode = hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Disabled;
            bool phaseAAllowPaidOptional = !smartMode && !isDisabledMode;
            bool phaseAAllowZeroCostOptional = !isDisabledMode;
            bool allowForcedPotions = hotPolicy.HasForcedDirectives;
            bool reserveForPaidPotions = smartMode && hotPolicy.PaidOptionalPotionCount > 0;
            bool isEliteOrBoss = hotPolicy.IsEliteEncounter || hotPolicy.IsBossEncounter;

            long totalExpanded = 0;
            long totalTransitions = 0;
            long totalChoices = 0;
            long totalAllocated = 0;
            int totalGen0 = 0, totalGen1 = 0, totalGen2 = 0;
            TimeSpan totalGcPause = TimeSpan.Zero;
            int passCount = 0;
            HotRolloutStats totalRolloutStats = default;

            HotPassOutcome bestPass = default;
            HotRouteKey bestPassKey = default;
            bool hasBestPass = false;
            HotRouteKey baselineKey = default;
            bool hasBaselineKey = false;
            int potionHpSaved = 0;
            int potionBranchesRejected = 0;
            SolverResult? materializedBest = null;
            bool bestPassInArena = false;
            int completedEscalationStage = 1;

            void EnsureBestMaterializedBeforeReset()
            {
                if (bestPassInArena && hasBestPass)
                {
                    materializedBest = MaterializePassResult(
                        root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                        nodeArena, in bestPass, tt, totalExpanded, totalTransitions, totalChoices,
                        totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);
                    bestPassInArena = false;
                }
            }

            void RunAdaptiveStage(
                int stageNumber,
                int stageWidth,
                int stageNodeCeiling,
                int rolloutHorizonTurns,
                HotEvalMode stageEvalMode,
                string mainPhaseLabel,
                string potionPhaseLabel,
                HotSearchBudget stageBudget,
                bool allowDeathSaveRescue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stageNumber > 1 && (stageBudget.IsTimeExpired || totalExpanded >= stageNodeCeiling))
                    return;

                // Phase A: 0-paid-potion single-tree MAP-Elites pass (or all-allowed when not Smart).
                if (passCount > 0)
                {
                    EnsureBestMaterializedBeforeReset();
                    ResetSearchStateForNextPass(nodeArena, mainStateArena, compactStateArena, tt, expander);
                }

                int stageAvailableNodes = (int)Math.Max(1, stageNodeCeiling - totalExpanded);
                long phaseAPercent = (isEliteOrBoss || (long)hotPolicy.InitialPlayerHp * 100L <= (long)hotPolicy.InitialPlayerMaxHp * 60L)
                    ? 50L
                    : 60L;
                int phaseANodes = reserveForPaidPotions
                    ? Math.Min(
                        stageAvailableNodes,
                        Math.Max(HotSearchConstants.MinimumTurnLayerExpandedNodes, (int)(((long)stageAvailableNodes * phaseAPercent) / 100L)))
                    : stageAvailableNodes;

                HotSearchBudget phaseABudget = stageBudget;
                if (reserveForPaidPotions && !policy.FixedBudget)
                {
                    long remMs = Math.Max(50L, stageBudget.RemainingMilliseconds);
                    long phaseATimeCapMs = Math.Min(
                        stageBudget.SoftTimeBudgetMilliseconds,
                        requestClock.ElapsedMilliseconds + Math.Max(50L, (remMs * (phaseAPercent + 5L)) / 100L));
                    phaseABudget = new HotSearchBudget(requestClock, phaseANodes, phaseATimeCapMs, cancellationToken);
                }

                retention.RelaxZeroPotionReserve = false;
                retention.AllowDeathSaveRescue = allowDeathSaveRescue;

                HotPassOutcome phaseA = RunBeamPass(
                    rootBundle,
                    in hotPolicy,
                    in evalLookup,
                    rootPotionCosts,
                    policy.Profile,
                    phaseABudget,
                    nodeArena,
                    ref mainStateArena,
                    ref compactStateArena,
                    tt,
                    retention,
                    expander,
                    rolloutEvaluator,
                    beamWidth: stageWidth,
                    maxNodesForPass: phaseANodes,
                    allowPaidOptionalPotions: phaseAAllowPaidOptional,
                    allowZeroCostOptionalPotions: phaseAAllowZeroCostOptional,
                    allowForcedPotions: allowForcedPotions,
                    maxPotionUses: -1,
                    disableTt: disableTt,
                    evalMode: stageEvalMode,
                    preferPotionBuckets: phaseAAllowPaidOptional && hotPolicy.PaidOptionalPotionCount > 0,
                    ref sharedIncumbent,
                    progressCallback: progressCallback,
                    phaseLabel: mainPhaseLabel,
                    reviewedOffset: totalExpanded,
                    enableTurnBoundaryRollouts: policy.EnableTurnBoundaryRollouts,
                    rolloutHorizonTurns: rolloutHorizonTurns,
                    allowDeathSaveRescue: allowDeathSaveRescue);

                AccumulatePassTotals(phaseA,
                    ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                    ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount, ref totalRolloutStats);

                HotRouteKey phaseAKey = phaseA.BestKey;
                if (!hasBaselineKey
                    || (phaseA.HasBest && HotRouteQuality.IsBetter(in phaseAKey, in baselineKey, in hotPolicy, includeOptionalPotionCost: true)))
                {
                    hasBaselineKey = phaseA.HasBest;
                    baselineKey = phaseAKey;
                }

                if (!hasBestPass || IsBetterPassOutcome(in phaseA, in bestPass, in hotPolicy))
                {
                    hasBestPass = true;
                    bestPass = phaseA;
                    bestPassKey = phaseAKey;
                    potionHpSaved = 0;
                    materializedBest = null;
                    bestPassInArena = true;
                }

                completedEscalationStage = stageNumber;

                // Phase B: Smart Paid-Potion pass (exact sound upper-bound gate, reusing remaining stage nodes).
                bool runPaidPotionPhase = smartMode
                    && !stageBudget.IsCanceled
                    && !stageBudget.IsTimeExpired
                    && totalExpanded < stageNodeCeiling
                    && HotPotionPolicy.ShouldRunSmartPotionPass(in baselineKey, in hotPolicy);

                if (!runPaidPotionPhase)
                {
                    retention.AllowDeathSaveRescue = false;
                    return;
                }

                EnsureBestMaterializedBeforeReset();
                ResetSearchStateForNextPass(nodeArena, mainStateArena, compactStateArena, tt, expander);

                int phaseBNodes = (int)Math.Max(1, stageNodeCeiling - totalExpanded);
                int potionWidth = ResolveStagePotionBeamWidth(stageWidth);

                retention.RelaxZeroPotionReserve = !(hasBaselineKey
                    && baselineKey.Won
                    && baselineKey.Survives
                    && baselineKey.DeathSaveUseCount == 0);
                bool phaseBAllowDeathSaveRescue = allowDeathSaveRescue
                    && !(bestPass.HasVictory && bestPassKey.Survives && bestPassKey.DeathSaveUseCount == 0);
                retention.AllowDeathSaveRescue = phaseBAllowDeathSaveRescue;

                HotPassOutcome phaseB = RunBeamPass(
                    rootBundle,
                    in hotPolicy,
                    in evalLookup,
                    rootPotionCosts,
                    policy.Profile,
                    stageBudget,
                    nodeArena,
                    ref mainStateArena,
                    ref compactStateArena,
                    tt,
                    retention,
                    expander,
                    rolloutEvaluator,
                    beamWidth: potionWidth,
                    maxNodesForPass: phaseBNodes,
                    allowPaidOptionalPotions: true,
                    allowZeroCostOptionalPotions: true,
                    allowForcedPotions: allowForcedPotions,
                    maxPotionUses: -1,
                    disableTt: disableTt,
                    evalMode: stageEvalMode,
                    preferPotionBuckets: true,
                    ref sharedIncumbent,
                    progressCallback: progressCallback,
                    phaseLabel: potionPhaseLabel,
                    reviewedOffset: totalExpanded,
                    enableTurnBoundaryRollouts: policy.EnableTurnBoundaryRollouts,
                    rolloutHorizonTurns: rolloutHorizonTurns,
                    allowDeathSaveRescue: phaseBAllowDeathSaveRescue);

                retention.AllowDeathSaveRescue = false;

                AccumulatePassTotals(phaseB,
                    ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                    ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount, ref totalRolloutStats);

                HotRouteKey phaseBKey = phaseB.BestKey;
                if (!hasBestPass || IsBetterPassOutcome(in phaseB, in bestPass, in hotPolicy))
                {
                    potionHpSaved = phaseBKey.OptionalPaidPotionCount > 0
                        ? HotPotionPolicy.HpSavedWholeHp(in baselineKey, in phaseBKey)
                        : 0;
                    hasBestPass = true;
                    bestPass = phaseB;
                    bestPassKey = phaseBKey;
                    materializedBest = null;
                    bestPassInArena = true;
                }
                else if (phaseB.HasBest && phaseBKey.OptionalPaidPotionCount > 0)
                {
                    potionBranchesRejected++;
                }
            }

            // =========================================================================
            // STAGE 1: FAST SINGLE-TREE MAP-ELITES (W=96, 150k nodes, Balanced, rolloutHorizon=4 normal / 6 elite+boss)
            // =========================================================================
            RunAdaptiveStage(
                stageNumber: 1,
                stageWidth: stage1Width,
                stageNodeCeiling: stage1NodeCeiling,
                rolloutHorizonTurns: isEliteOrBoss ? Stage2RolloutHorizon : Stage1RolloutHorizon,
                stageEvalMode: HotEvalMode.Balanced,
                mainPhaseLabel: "正在搜索路线",
                potionPhaseLabel: "正在评估药水路线",
                stageBudget: fullBudget,
                allowDeathSaveRescue: false);

            // =========================================================================
            // STAGE 1 -> STAGE 2 OUTCOME-DRIVEN ESCALATION GATE (#283 Plan A + #301)
            // =========================================================================
            bool stage1CleanSurvival = bestPass.HasVictory
                && bestPassKey.Survives
                && bestPassKey.DeathSaveUseCount == 0
                && (hotPolicy.TheftPolicy != SolverTheftPolicy.PreserveResources
                    || (bestPassKey.OutstandingStolen == 0 && !bestPassKey.ForfeitsGold));
            int stage1PaidPotions = bestPassKey.OptionalPaidPotionCount;
            bool stage1MeetsStopTarget = HotRouteQuality.MeetsVictoryStopTarget(in bestPassKey, in hotPolicy)
                && (!smartMode || !HotPotionPolicy.CanAnyPaidPotionImproveOn(in bestPassKey, in hotPolicy));

            bool stage1EarlyExit = stage1MeetsStopTarget
                || (stage1CleanSurvival
                    && bestPassKey.AllStrategyTargetsSatisfied
                    && HotPotionPolicy.SatisfiesHardConstraints(in bestPassKey, in hotPolicy)
                    && bestPassKey.ProjectedBattleHpLost <= 0
                    && stage1PaidPotions == 0
                    && (!smartMode || !HotPotionPolicy.CanAnyPaidPotionImproveOn(in bestPassKey, in hotPolicy)));

            bool canAttemptStage2 = policy.AdaptiveEscalationEnabled
                && (isDefaultAdaptiveProfile || configuredNodes >= 30_000)
                && !fullBudget.IsCanceled
                && !fullBudget.IsTimeExpired
                && totalExpanded < stage2NodeCeiling;

            bool shouldEscalateToStage2 = canAttemptStage2 && !stage1EarlyExit;

            if (shouldEscalateToStage2)
            {
                // Anytime Progressive Preview: emit Stage-1 playable route (IsPreliminary=true) before deepening!
                if (onPreliminaryResult != null && hasBestPass && bestPass.HasBest && !cancellationToken.IsCancellationRequested)
                {
                    EnsureBestMaterializedBeforeReset();
                    if (materializedBest != null)
                    {
                        SolverResult preliminary = materializedBest.CloneAsPreliminary(escalationStage: 1);
                        StampAggregateTelemetry(
                            preliminary,
                            totalExpanded,
                            totalTransitions,
                            totalChoices,
                            totalAllocated,
                            totalGen0,
                            totalGen1,
                            totalGen2,
                            totalGcPause,
                            requestClock.Elapsed,
                            passCount,
                            escalationStage: 1,
                            isPreliminary: true);
                        onPreliminaryResult(preliminary);
                    }
                }

                // =========================================================================
                // STAGE 2: DEEPENING MAP-ELITES (W=240, 320k cumulative nodes, SetupAndSurvival, rolloutHorizon=6)
                // =========================================================================
                RunAdaptiveStage(
                    stageNumber: 2,
                    stageWidth: stage2Width,
                    stageNodeCeiling: stage2NodeCeiling,
                    rolloutHorizonTurns: Stage2RolloutHorizon,
                    stageEvalMode: HotEvalMode.SetupAndSurvival,
                    mainPhaseLabel: "正在深化搜索路线",
                    potionPhaseLabel: "正在深化评估药水路线",
                    stageBudget: fullBudget,
                    allowDeathSaveRescue: false);

                // =========================================================================
                // STAGE 2 -> STAGE 3 DEEPENING ESCALATION GATE (#283 Plan A)
                // =========================================================================
                bool stage2CleanSurvival = bestPass.HasVictory
                    && bestPassKey.Survives
                    && bestPassKey.DeathSaveUseCount == 0
                    && (hotPolicy.TheftPolicy != SolverTheftPolicy.PreserveResources
                        || (bestPassKey.OutstandingStolen == 0 && !bestPassKey.ForfeitsGold));
                int stage2EffHpLoss = Math.Max(0, bestPassKey.StrategicHpDeficit);
                int stage2TriggerHpLoss = Math.Max(stage2EffHpLoss, bestPassKey.ProjectedBattleHpLost);
                int stage2PaidPotions = bestPassKey.OptionalPaidPotionCount;
                int rootPlayerHp = Math.Max(1, hotPolicy.InitialPlayerHp);
                bool stage2MeetsStopTarget = HotRouteQuality.MeetsVictoryStopTarget(in bestPassKey, in hotPolicy)
                    && (!smartMode || !HotPotionPolicy.CanAnyPaidPotionImproveOn(in bestPassKey, in hotPolicy));

                bool shouldEscalateToStage3 = isDefaultAdaptiveProfile
                    && !stage2MeetsStopTarget
                    && !fullBudget.IsCanceled
                    && !fullBudget.IsTimeExpired
                    && totalExpanded < stage3NodeCeiling
                    && (!bestPass.HasVictory
                        || !stage2CleanSurvival
                        || (hotPolicy.HasAnyStrategyTargets && !bestPassKey.AllStrategyTargetsSatisfied)
                        || stage2TriggerHpLoss >= 18
                        || stage2TriggerHpLoss * 4 >= rootPlayerHp
                        || (isEliteOrBoss && (stage2TriggerHpLoss >= 8 || stage2PaidPotions >= 1)));

                if (shouldEscalateToStage3)
                {
                    if (bestPass.HasVictory)
                    {
                        // =========================================================================
                        // STAGE 3: WINNING DEEP REFINEMENT MAP-ELITES (W=320, 500k cumulative nodes, Balanced)
                        // =========================================================================
                        RunAdaptiveStage(
                            stageNumber: 3,
                            stageWidth: stage3Width,
                            stageNodeCeiling: stage3NodeCeiling,
                            rolloutHorizonTurns: Stage3RolloutHorizon,
                            stageEvalMode: HotEvalMode.Balanced,
                            mainPhaseLabel: "正在深度搜索路线",
                            potionPhaseLabel: "正在深度评估药水路线",
                            stageBudget: fullBudget,
                            allowDeathSaveRescue: false);
                    }
                    else
                    {
                        // =========================================================================
                        // STAGE 3a/3b: SEQUENTIAL DEFEAT-RESCUE PORTFOLIO (arena reset between passes -> RSS <= 650 MB):
                        //   Stage 3a: HotEvalMode.Offense (W=320, allowDeathSaveRescue=true, 55% remaining Stage 3 budget)
                        //   Stage 3b: HotEvalMode.DesperateRescue (W=384, allowDeathSaveRescue=true, up to 650k nodes)
                        //             runs only if Stage 3a did not find a clean 0-death-save survival victory.
                        // =========================================================================
                        long stage3RemainingNodes = Math.Max(1L, stage3NodeCeiling - totalExpanded);
                        int stage3aAllocNodes = Math.Max(
                            HotSearchConstants.MinimumTurnLayerExpandedNodes,
                            (int)((stage3RemainingNodes * 55L) / 100L));
                        int stage3aNodeCeiling = (int)Math.Min(stage3NodeCeiling, totalExpanded + stage3aAllocNodes);

                        HotSearchBudget stage3aBudget = fullBudget;
                        long stage3RemMs = fullBudget.RemainingMilliseconds;
                        if (!policy.FixedBudget && stage3RemMs >= 1600L)
                        {
                            long stage3aAllocMs = Math.Max(50L, (stage3RemMs * 55L) / 100L);
                            long stage3aTimeCapMs = Math.Min(softBudgetMs, requestClock.ElapsedMilliseconds + stage3aAllocMs);
                            stage3aBudget = new HotSearchBudget(requestClock, stage3aNodeCeiling, stage3aTimeCapMs, cancellationToken);
                        }

                        RunAdaptiveStage(
                            stageNumber: 3,
                            stageWidth: stage3Width,
                            stageNodeCeiling: stage3aNodeCeiling,
                            rolloutHorizonTurns: Stage3RolloutHorizon,
                            stageEvalMode: HotEvalMode.Offense,
                            mainPhaseLabel: "正在深度搜索路线",
                            potionPhaseLabel: "正在深度评估药水路线",
                            stageBudget: stage3aBudget,
                            allowDeathSaveRescue: true);

                        bool stage3aCleanSurvivalVictory = bestPass.HasVictory
                            && bestPassKey.Survives
                            && bestPassKey.DeathSaveUseCount == 0;

                        if (!stage3aCleanSurvivalVictory
                            && (!bestPass.HasVictory || bestPassKey.DeathSaveUseCount > 0)
                            && !fullBudget.IsCanceled
                            && fullBudget.RemainingMilliseconds >= 700L)
                        {
                            int stage3bNodeCeiling = !bestPass.HasVictory
                                ? Math.Min(fullBudget.MaxExpandedNodes, Math.Max(stage3NodeCeiling, Stage3bRescueNodes))
                                : stage3NodeCeiling;

                            if (totalExpanded < stage3bNodeCeiling)
                            {
                                RunAdaptiveStage(
                                    stageNumber: 3,
                                    stageWidth: Stage3bRescueWidth,
                                    stageNodeCeiling: stage3bNodeCeiling,
                                    rolloutHorizonTurns: Stage3RolloutHorizon,
                                    stageEvalMode: HotEvalMode.DesperateRescue,
                                    mainPhaseLabel: "正在深度搜索路线",
                                    potionPhaseLabel: "正在深度评估药水路线",
                                    stageBudget: fullBudget,
                                    allowDeathSaveRescue: true);
                            }
                        }
                    }
                }
            }

            ValidateHardPotionConstraints(in bestPassKey, bestPass.HasBest, in hotPolicy, policy.PotionStrategy);

            EnsureBestMaterializedBeforeReset();
            SolverResult finalResult = materializedBest ?? MaterializePassResult(
                root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                nodeArena, in bestPass, tt, totalExpanded, totalTransitions, totalChoices,
                totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);

            StampAggregateTelemetry(
                finalResult,
                totalExpanded,
                totalTransitions,
                totalChoices,
                totalAllocated,
                totalGen0,
                totalGen1,
                totalGen2,
                totalGcPause,
                requestClock.Elapsed,
                passCount,
                escalationStage: completedEscalationStage,
                isPreliminary: false);

            EmitRolloutDiagnostics(in totalRolloutStats, policy);
            return finalResult;
        }
        finally
        {
            mainStateArena.Dispose();
            compactStateArena.Dispose();
        }
    }

    private static void StampAggregateTelemetry(
        SolverResult result,
        long totalExpanded,
        long totalTransitions,
        long totalChoices,
        long totalAllocated,
        int totalGen0,
        int totalGen1,
        int totalGen2,
        TimeSpan totalGcPause,
        TimeSpan totalElapsed,
        int passCount,
        int escalationStage,
        bool isPreliminary)
    {
        result.TotalExpandedNodes = totalExpanded;
        result.TotalTransitionCount = totalTransitions;
        result.TotalChoiceBranchesEvaluated = totalChoices;
        result.TotalWorkerAllocatedBytes = totalAllocated;
        result.TotalGen0Collections = totalGen0;
        result.TotalGen1Collections = totalGen1;
        result.TotalGen2Collections = totalGen2;
        result.TotalGcPauseDuration = totalGcPause;
        result.TotalSearchElapsed = totalElapsed;
        result.SingleSessionSearch = passCount <= 1;
        result.EscalationStage = escalationStage;
        result.IsPreliminary = isPreliminary;
    }

    private static void ValidateHardPotionConstraints(
        in HotRouteKey bestKey,
        bool hasBest,
        in HotSearchPolicy hotPolicy,
        PotionStrategySnapshot strategy)
    {
        if (!hotPolicy.HasHardPotionConstraint)
            return;
        if (hasBest && HotPotionPolicy.SatisfiesHardConstraints(in bestKey, in hotPolicy))
            return;

        if (hotPolicy.HasForcedDirectives)
        {
            string forcedDesc = strategy.DescribeForcedUses();
            throw new PotionPolicyUnsatisfiedException(
                string.IsNullOrEmpty(forcedDesc)
                    ? "未能找到满足全部强制用药指令的合法路线。"
                    : $"未能找到满足强制用药指令（{forcedDesc}）的合法路线。");
        }

        throw new PotionPolicyUnsatisfiedException(
            $"未能找到满足至少使用 {hotPolicy.MinimumRequiredPotionUses} 瓶药水要求的合法路线。");
    }

    private static HotPassOutcome RunBeamPass(
        HotRootBundle rootBundle,
        in HotSearchPolicy hotPolicy,
        in HotEvalLookup evalLookup,
        short[] rootPotionCosts,
        SolverSearchProfile profile,
        HotSearchBudget budget,
        HotNodeArena nodeArena,
        ref PackedStateArena mainStateArena,
        ref PackedStateArena compactStateArena,
        HotTranspositionTable tt,
        HotBeamRetention retention,
        HotParallelExpander expander,
        HotRolloutEvaluator? rolloutEvaluator,
        int beamWidth,
        int maxNodesForPass,
        bool allowPaidOptionalPotions,
        bool allowZeroCostOptionalPotions,
        bool allowForcedPotions,
        int maxPotionUses,
        bool disableTt,
        HotEvalMode evalMode,
        bool preferPotionBuckets,
        ref HotIncumbentBound incumbent,
        Action<SolverProgress>? progressCallback,
        string phaseLabel,
        long reviewedOffset,
        bool enableTurnBoundaryRollouts = true,
        int rolloutHorizonTurns = 4,
        bool allowDeathSaveRescue = false)
    {
        Stopwatch passClock = Stopwatch.StartNew();
        int gen0Start = GC.CollectionCount(0);
        int gen1Start = GC.CollectionCount(1);
        int gen2Start = GC.CollectionCount(2);
        TimeSpan gcPauseStart = GC.GetTotalPauseDuration();
        int peakTranspositionCount = 0;
        int peakStandPatCacheCount = 0;

        CombatHotState rootState = rootBundle.RootState;
        HotColdSidecar? rootCold = rootBundle.RootCold;
        int rootPackedSize = HotStatePacker.ComputePackedSize(in rootState);
        Span<byte> rootBuf = mainStateArena.Reserve(rootPackedSize);
        int rootWritten = HotStatePacker.Pack(ref rootState, ref rootCold, rootBuf, canonicalize: true);
        HotStateHandle rootHandle = mainStateArena.CommitReserved(rootWritten);
        HotHash128 rootHash = HotHash.ComputeHash(mainStateArena.GetSpan(rootHandle), rootCold);

        HotFastSummary rootSummary = HotEvaluator.ExtractStage1(in rootState, in hotPolicy);
        HotEvaluator.EvaluateState(
            in rootState,
            rootCold,
            rootBundle.Rules,
            in hotPolicy,
            in evalLookup,
            in rootSummary,
            netPotionCostCenti: 0,
            actionCount: 0,
            evalMode: evalMode,
            out HotEvalFeatures rootEval);

        HotNodeFlags rootFlags = HotNodeFlags.IsRoot;
        if (rootSummary.AllGrowthTargetsSatisfied)
            rootFlags |= HotNodeFlags.AllGrowthTargetsSatisfied;
        if (rootSummary.AllRelicTargetsSatisfied)
            rootFlags |= HotNodeFlags.AllRelicTargetsSatisfied;

        HotSearchNode rootNode = new()
        {
            Hash = rootHash,
            Score = rootEval.Score,
            RolloutScore = 0,
            Cold = rootCold,
            StateHandle = rootHandle,
            ParentId = -1,
            ChoiceOffset = -1,
            HpLost = rootSummary.HpLost,
            HpRecovered = rootSummary.HpRecovered,
            PlayerHp = rootSummary.PlayerHp,
            EnemyHp = rootSummary.EnemyHpSum,
            SetupScore = rootEval.SetupScore,
            BoardInvestment = rootEval.BoardInvestment,
            MaxDarkEvoke = rootEval.MaxDarkEvoke,
            TotalPoisonAndDoom = rootEval.TotalPoisonAndDoom,
            PlayerMaxHpDelta = (short)Math.Clamp(hotPolicy.InitialPlayerMaxHp - rootSummary.PlayerMaxHp, short.MinValue, short.MaxValue),
            PlayerBlock = (short)Math.Clamp(rootSummary.PlayerBlock, 0, short.MaxValue),
            EffectiveDefense = rootEval.EffectiveDefense,
            OptionalityScore = rootEval.OptionalityScore,
            OutstandingStolen = (short)Math.Clamp(rootSummary.OutstandingStolen, 0, short.MaxValue),
            GrowthHpCredit = (short)Math.Clamp(rootSummary.GrowthHpCredit, short.MinValue, short.MaxValue),
            RelicHpCredit = rootSummary.RelicHpCredit,
            RelicHealingHpCredit = rootSummary.RelicHealingHpCredit,
            RelicSatisfiedMask = rootSummary.RelicSatisfiedMask,
            RelicDistance = rootSummary.RelicDistance,
            RelicSatisfiedPriority = rootSummary.RelicSatisfiedPriority,
            FeedCount = rootSummary.FeedCount,
            PotionStrategicCost = 0,
            DeathSaveHpRestored = (short)Math.Clamp(rootSummary.DeathSaveHpRestored, 0, short.MaxValue),
            PotionMask = 0,
            ActionCount = 0,
            GrowthRewardCount = (ushort)Math.Clamp(rootSummary.GrowthRewardCount, 0, ushort.MaxValue),
            Flags = rootFlags,
            Action = default,
            ChoiceCount = 0,
            PotionCount = 0,
            ExplicitPotionCount = 0,
            OptionalPaidPotionCount = 0,
            AmbergrisCount = 0,
            AlchemizeCount = rootSummary.AlchemizeCount,
            EmptyPotionSlots = rootSummary.EmptyPotionSlots,
            Energy = (byte)Math.Clamp(rootSummary.Energy, 0, byte.MaxValue),
            Turn = (byte)Math.Clamp(rootSummary.Turn, 0, byte.MaxValue),
            ActionsThisTurn = 0,
            AliveEnemyCount = rootSummary.AliveEnemyCount,
            RevivingEnemyCount = rootSummary.RevivingEnemyCount,
            PlayableHandCount = rootEval.PlayableHandCount,
            DeathSaveUseCount = rootSummary.DeathSaveUseCount,
            Outcome = rootSummary.Outcome,
        };

        int rootNodeId = nodeArena.AddNode(in rootNode);
        HotRouteKey rootKey = HotRouteQuality.Capture(in rootNode, in hotPolicy);

        int effectiveMaxCardBranches = ResolveEffectiveMaxCardBranches(profile.MaxCardBranchesPerNode);
        bool turnBoundaryRolloutsActive = enableTurnBoundaryRollouts
            && rolloutEvaluator != null
            && rolloutHorizonTurns > 0;

        HotWaveExpansionConfig waveConfig = new(
            allowPaidOptionalPotions: allowPaidOptionalPotions,
            allowZeroCostOptionalPotions: allowZeroCostOptionalPotions,
            allowForcedPotions: allowForcedPotions,
            maxPotionUses: maxPotionUses,
            maxCardBranchesPerNode: effectiveMaxCardBranches,
            maxHandChoiceBranches: profile.MaxHandChoiceBranchesPerAction,
            maxPileChoiceBranches: profile.MaxPileChoiceBranchesPerAction,
            evalMode: evalMode,
            enableTurnBoundaryRollouts: turnBoundaryRolloutsActive);

        int earlyTurnBoundaryWidth = Math.Min(HotSearchConstants.MaxBeamWidth, (beamWidth * 3) / 2);
        int earlyMidTurnWidth = Math.Min(HotSearchConstants.MaxBeamWidth, (beamWidth * 5) / 4);
        int maxFrontierWidth = Math.Max(beamWidth, earlyTurnBoundaryWidth);
        int maxMidTurnWidth = Math.Max(beamWidth, earlyMidTurnWidth);

        List<int> currentTurnFrontier = new(maxFrontierWidth) { rootNodeId };
        List<int> activeFrontier = new(maxFrontierWidth);
        List<int> nextStepCandidates = new(maxMidTurnWidth * 8);
        List<int> nextTurnCandidates = new(maxFrontierWidth * 4);
        List<int> turnSetupEndedTurnNodes = [];
        List<int> victories = new(64);
        int[] pruneOutBuf = ArrayPool<int>.Shared.Rent(Math.Max(maxFrontierWidth * 2, 128));
        int[] shortlistBuf = ArrayPool<int>.Shared.Rent(Math.Max(maxFrontierWidth * 2, 128));
        int[] standPatBuf = ArrayPool<int>.Shared.Rent(Math.Max(StandPatTopK, 32));

        HotWaveStats stats = default;
        HotRolloutStats rolloutStats = default;
        int bestVictoryId = -1;
        HotRouteKey bestVictoryKey = default;
        bool hasVictory = false;

        int bestCompletedTurnOngoingId = -1;
        HotRouteKey bestCompletedTurnOngoingKey = default;
        int bestCompletedTurnOngoingTurn = -1;
        bool hasCompletedTurnOngoing = false;

        int bestMidTurnOngoingId = -1;
        HotRouteKey bestMidTurnOngoingKey = default;
        bool hasMidTurnOngoing = false;

        int bestFallbackId = rootNodeId;
        HotRouteKey bestFallbackKey = rootKey;
        bool hasBestFallback = false;

        int incumbentUpdates = 0;
        bool stopTargetReached = false;
        SearchBoundaryReason boundaryReason = SearchBoundaryReason.None;
        int searchedTurns = 0;
        long lastProgressReportMs = -HotSearchConstants.ProgressUiIntervalMilliseconds;

        int bestSeenEnemyHp = rootSummary.EnemyHpSum;
        int bestSeenAliveEnemies = rootSummary.AliveEnemyCount + rootSummary.RevivingEnemyCount;
        int bestSeenMaxDarkEvoke = rootEval.MaxDarkEvoke;
        int bestSeenPoisonAndDoom = rootEval.TotalPoisonAndDoom;
        int bestSeenBoardInvestment = rootEval.BoardInvestment;
        int bestSeenStrategyGoalHpCredit = hotPolicy.HasAnyStrategyTargets ? rootSummary.StrategyGoalHpCredit : 0;
        int bestSeenStrategyGoalCount = hotPolicy.HasAnyStrategyTargets ? rootSummary.StrategyGoalCount : 0;
        ushort seenRelicSatisfiedMask = hotPolicy.HasRelicTargets ? rootSummary.RelicSatisfiedMask : (ushort)0;
        int turnsWithoutProgress = 0;
        int turnsSinceEnemyHpDecreased = 0;
        int maxTurnsForPass = hotPolicy.IsBossEncounter
            ? BossExtendedMaxSearchTurns
            : DefaultMaxSearchTurns;
        bool hitMaxSearchTurns = false;
        bool lastTurnHitPerTurnNodeCap = false;

        bool prevAllowDeathSaveRescue = retention.AllowDeathSaveRescue;
        retention.AllowDeathSaveRescue = allowDeathSaveRescue;

        try
        {
            // ---- DEPTH-0 TURN SETUP EXPANSION (GamblingChip / ToolsOfTheTrade / HistoryCourse / Dread / Entropy / Toolbox) ----
            if (hotPolicy.IncludeTurnSetup && HotCombatEngine.HasPendingTurnSetup(in rootState))
            {
                // Full Depth-0 expansion (#294 Pillar 2): never starve TurnSetup options before Turn-1 plays.
                int setupBranchCap = Math.Max(beamWidth, 64);
                HotWaveExpansionConfig setupConfig = new(
                    allowPaidOptionalPotions: false,
                    allowZeroCostOptionalPotions: false,
                    allowForcedPotions: false,
                    maxPotionUses: -1,
                    maxCardBranchesPerNode: effectiveMaxCardBranches,
                    maxHandChoiceBranches: setupBranchCap,
                    maxPileChoiceBranches: setupBranchCap,
                    evalMode: evalMode,
                    allowEndTurn: false,
                    enableTurnBoundaryRollouts: turnBoundaryRolloutsActive);

                HotIncumbentBound frozenIncumbent = incumbent;
                Span<int> rootSlice = stackalloc int[1];
                rootSlice[0] = rootNodeId;

                nextStepCandidates.Clear();
                turnSetupEndedTurnNodes.Clear();
                victories.Clear();

                expander.ExpandAndCommitWave(
                    rootSlice,
                    nodeArena,
                    mainStateArena,
                    tt,
                    rootBundle.Rules,
                    in hotPolicy,
                    in evalLookup,
                    rootPotionCosts,
                    in setupConfig,
                    in frozenIncumbent,
                    disableTt,
                    nextStepCandidates,
                    turnSetupEndedTurnNodes,
                    victories,
                    ref bestFallbackId,
                    ref bestFallbackKey,
                    ref hasBestFallback,
                    ref stats,
                    budget.CancellationToken);

                ProcessVictories(
                    victories,
                    nodeArena,
                    in hotPolicy,
                    allowPaidOptionalPotions,
                    ref bestVictoryId,
                    ref bestVictoryKey,
                    ref hasVictory,
                    ref incumbent,
                    ref incumbentUpdates,
                    ref stopTargetReached);

                UpdateBestOngoing(nextStepCandidates, nodeArena, in hotPolicy, ref bestMidTurnOngoingId, ref bestMidTurnOngoingKey, ref hasMidTurnOngoing);
                UpdateBestCompletedTurnOngoing(
                    turnSetupEndedTurnNodes,
                    nodeArena,
                    in hotPolicy,
                    ref bestCompletedTurnOngoingId,
                    ref bestCompletedTurnOngoingKey,
                    ref bestCompletedTurnOngoingTurn,
                    ref hasCompletedTurnOngoing);

                currentTurnFrontier.Clear();
                if (!stopTargetReached && nextStepCandidates.Count > 0)
                {
                    ReadOnlySpan<int> setupInput = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nextStepCandidates);
                    bool useSetupRollout = false;

                    // Keep 100% of setup nodes when Count <= beamWidth; only roll out & prune when Count > beamWidth
                    // (e.g. GamblingChip on 7-10 cards or stacked turn-1 setups).
                    if (setupInput.Length > beamWidth
                        && enableTurnBoundaryRollouts
                        && rolloutEvaluator != null
                        && !budget.IsTimeExpired)
                    {
                        int shortlistCap = Math.Min(setupInput.Length, beamWidth * 2);
                        if (shortlistBuf.Length < shortlistCap)
                        {
                            ArrayPool<int>.Shared.Return(shortlistBuf, clearArray: false);
                            shortlistBuf = ArrayPool<int>.Shared.Rent(shortlistCap);
                        }

                        int shortlistCount;
                        if (setupInput.Length > shortlistCap)
                        {
                            shortlistCount = retention.PruneLayer(
                                setupInput,
                                nodeArena,
                                shortlistCap,
                                shortlistBuf.AsSpan(0, shortlistCap),
                                in hotPolicy,
                                isTurnBoundary: false,
                                preferPotionBuckets: false,
                                useRolloutScores: false,
                                turnLayer: 0,
                                allowDeathSaveRescue: allowDeathSaveRescue);
                        }
                        else
                        {
                            setupInput.CopyTo(shortlistBuf);
                            shortlistCount = setupInput.Length;
                        }

                        victories.Clear();
                        rolloutEvaluator.EvaluateTurnBoundary(
                            shortlistBuf.AsSpan(0, shortlistCount),
                            nodeArena,
                            mainStateArena,
                            rootBundle.Rules,
                            in hotPolicy,
                            in evalLookup,
                            evalMode,
                            horizonTurns: 2,
                            victories,
                            ref rolloutStats,
                            budget.CancellationToken);

                        if (victories.Count > 0)
                        {
                            ProcessVictories(
                                victories,
                                nodeArena,
                                in hotPolicy,
                                allowPaidOptionalPotions,
                                ref bestVictoryId,
                                ref bestVictoryKey,
                                ref hasVictory,
                                ref incumbent,
                                ref incumbentUpdates,
                                ref stopTargetReached);
                        }

                        setupInput = shortlistBuf.AsSpan(0, shortlistCount);
                        useSetupRollout = true;
                    }

                    if (!stopTargetReached)
                    {
                        if (pruneOutBuf.Length < beamWidth)
                        {
                            ArrayPool<int>.Shared.Return(pruneOutBuf, clearArray: false);
                            pruneOutBuf = ArrayPool<int>.Shared.Rent(beamWidth);
                        }

                        int setupKept = retention.PruneLayer(
                            setupInput,
                            nodeArena,
                            beamWidth,
                            pruneOutBuf.AsSpan(0, beamWidth),
                            in hotPolicy,
                            isTurnBoundary: false,
                            preferPotionBuckets: false,
                            useRolloutScores: useSetupRollout,
                            turnLayer: 0,
                            allowDeathSaveRescue: allowDeathSaveRescue);
                        for (int i = 0; i < setupKept; i++)
                            currentTurnFrontier.Add(pruneOutBuf[i]);
                    }
                }
            }

            for (int turnLayer = 0; turnLayer < maxTurnsForPass; turnLayer++)
            {
                if ((currentTurnFrontier.Count == 0 && (turnLayer > 0 || turnSetupEndedTurnNodes.Count == 0)) || stopTargetReached)
                    break;
                if (HasExceededPostVictoryTurnCap(
                    hasVictory,
                    in bestVictoryKey,
                    hotPolicy.StartTurnNumber + turnLayer,
                    in hotPolicy,
                    allowPaidOptionalPotions))
                {
                    break;
                }
                if (budget.IsCanceled)
                    budget.CancellationToken.ThrowIfCancellationRequested();
                if (budget.IsTimeExpired)
                {
                    boundaryReason = SearchBoundaryReason.TimeLimit;
                    break;
                }
                if (stats.ExpandedParents >= maxNodesForPass)
                {
                    boundaryReason = SearchBoundaryReason.NodeLimit;
                    break;
                }

                searchedTurns = turnLayer + 1;
                lastTurnHitPerTurnNodeCap = false;
                activeFrontier.Clear();
                activeFrontier.AddRange(currentTurnFrontier);
                nextTurnCandidates.Clear();
                if (turnLayer == 0 && turnSetupEndedTurnNodes.Count > 0)
                {
                    nextTurnCandidates.AddRange(turnSetupEndedTurnNodes);
                    turnSetupEndedTurnNodes.Clear();
                }

                int remainingPassNodes = maxNodesForPass - stats.ExpandedParents;
                int perTurnNodeCap = hasVictory
                    ? remainingPassNodes
                    : Math.Max(
                        HotSearchConstants.MinimumTurnLayerExpandedNodes,
                        Math.Min(remainingPassNodes, Math.Max(beamWidth * 40, maxNodesForPass / 4)));
                int expandedThisTurn = 0;

                for (int actionDepth = 0; actionDepth <= HotBeamRetention.MaxActionsPerTurn; actionDepth++)
                {
                    if (activeFrontier.Count == 0 || stopTargetReached)
                        break;
                    if (budget.IsCanceled)
                        budget.CancellationToken.ThrowIfCancellationRequested();
                    if (budget.IsTimeExpired)
                    {
                        boundaryReason = SearchBoundaryReason.TimeLimit;
                        break;
                    }

                    int allowedNodes = Math.Min(
                        maxNodesForPass - stats.ExpandedParents,
                        perTurnNodeCap - expandedThisTurn);
                    if (allowedNodes <= 0)
                    {
                        if (stats.ExpandedParents >= maxNodesForPass)
                            boundaryReason = SearchBoundaryReason.NodeLimit;
                        else
                            lastTurnHitPerTurnNodeCap = true;
                        break;
                    }

                    int waveCount = Math.Min(activeFrontier.Count, allowedNodes);
                    if (waveCount <= 0)
                        break;

                    nextStepCandidates.Clear();
                    victories.Clear();

                    HotIncumbentBound frozenIncumbent = incumbent;

                    ReadOnlySpan<int> parentSlice = System.Runtime.InteropServices.CollectionsMarshal
                        .AsSpan(activeFrontier)[..waveCount];

                    expander.ExpandAndCommitWave(
                        parentSlice,
                        nodeArena,
                        mainStateArena,
                        tt,
                        rootBundle.Rules,
                        in hotPolicy,
                        in evalLookup,
                        rootPotionCosts,
                        in waveConfig,
                        in frozenIncumbent,
                        disableTt,
                        nextStepCandidates,
                        nextTurnCandidates,
                        victories,
                        ref bestFallbackId,
                        ref bestFallbackKey,
                        ref hasBestFallback,
                        ref stats,
                        budget.CancellationToken);

                    expandedThisTurn += waveCount;

                    ProcessVictories(
                        victories,
                        nodeArena,
                        in hotPolicy,
                        allowPaidOptionalPotions,
                        ref bestVictoryId,
                        ref bestVictoryKey,
                        ref hasVictory,
                        ref incumbent,
                        ref incumbentUpdates,
                        ref stopTargetReached);

                    if (stopTargetReached)
                        break;

                    UpdateBestOngoing(nextStepCandidates, nodeArena, in hotPolicy, ref bestMidTurnOngoingId, ref bestMidTurnOngoingKey, ref hasMidTurnOngoing);

                    if (nextStepCandidates.Count == 0)
                    {
                        activeFrontier.Clear();
                        break;
                    }

                    bool keepEarlyMidTurnWidth = turnLayer <= 2
                        && (!hasVictory || (hotPolicy.HasAnyStrategyTargets && !bestVictoryKey.AllStrategyTargetsSatisfied));
                    int effectiveMidTurnWidth = keepEarlyMidTurnWidth
                        ? earlyMidTurnWidth
                        : beamWidth;

                    if (pruneOutBuf.Length < effectiveMidTurnWidth)
                    {
                        ArrayPool<int>.Shared.Return(pruneOutBuf, clearArray: false);
                        pruneOutBuf = ArrayPool<int>.Shared.Rent(effectiveMidTurnWidth);
                    }

                    int kept = retention.PruneLayer(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nextStepCandidates),
                        nodeArena,
                        effectiveMidTurnWidth,
                        pruneOutBuf.AsSpan(0, effectiveMidTurnWidth),
                        in hotPolicy,
                        isTurnBoundary: false,
                        preferPotionBuckets: preferPotionBuckets,
                        useRolloutScores: false,
                        turnLayer: turnLayer,
                        allowDeathSaveRescue: allowDeathSaveRescue);

                    activeFrontier.Clear();
                    for (int i = 0; i < kept; i++)
                        activeFrontier.Add(pruneOutBuf[i]);

                    if (progressCallback != null
                        && budget.ElapsedMilliseconds - lastProgressReportMs >= HotSearchConstants.ProgressUiIntervalMilliseconds)
                    {
                        lastProgressReportMs = budget.ElapsedMilliseconds;
                        SolverInterimResult? interim = hasVictory
                            ? HotRouteQuality.ToInterimResult(in bestVictoryKey, in hotPolicy)
                            : (hasCompletedTurnOngoing
                                ? HotRouteQuality.ToInterimResult(in bestCompletedTurnOngoingKey, in hotPolicy)
                                : (hasBestFallback
                                    ? HotRouteQuality.ToInterimResult(in bestFallbackKey, in hotPolicy)
                                    : (hasMidTurnOngoing
                                        ? HotRouteQuality.ToInterimResult(in bestMidTurnOngoingKey, in hotPolicy)
                                        : null)));
                        progressCallback(new SolverProgress(
                            StartTurnNumber: hotPolicy.StartTurnNumber,
                            CurrentTurnNumber: hotPolicy.StartTurnNumber + turnLayer,
                            CompletedTurnLayers: turnLayer,
                            PlayDepth: actionDepth + 1,
                            ExpandedNodes: stats.ExpandedParents,
                            ReviewedWorldlines: reviewedOffset + stats.ExpandedParents,
                            MaxNodes: maxNodesForPass,
                            FrontierNodes: activeFrontier.Count,
                            EndedNodes: nextTurnCandidates.Count,
                            ElapsedMilliseconds: budget.ElapsedMilliseconds,
                            Phase: phaseLabel,
                            CurrentBestResult: interim,
                            RequestBudgetMilliseconds: (int)Math.Min(int.MaxValue, budget.SoftTimeBudgetMilliseconds)));
                    }
                }

                // Per-potion-bucket StandPat probing (prevents unpenalized 0-potion nodes from starving potion buckets!).
                // Also run a compact emergency Turn-0 StandPat probe if TimeLimit cut Turn 0 short before any
                // completed-turn surviving route was found, so time-bounded turns still evaluate EndTurn cleanly.
                int effectiveTurnBoundaryWidth = (turnLayer <= 2
                    && (!hasVictory || (hotPolicy.HasAnyStrategyTargets && !bestVictoryKey.AllStrategyTargetsSatisfied)))
                    ? earlyTurnBoundaryWidth
                    : beamWidth;
                bool emergencyTurn0StandPat = !hasVictory
                    && !hasCompletedTurnOngoing
                    && turnLayer == 0
                    && activeFrontier.Count > 0
                    && !budget.IsCanceled;
                if (!stopTargetReached
                    && activeFrontier.Count > 0
                    && !budget.IsCanceled
                    && ((nextTurnCandidates.Count < effectiveTurnBoundaryWidth && !budget.IsTimeExpired) || emergencyTurn0StandPat))
                {
                    int standPatLimit = budget.IsTimeExpired ? Math.Min(4, StandPatTopK) : StandPatTopK;
                    int standPatCount = SelectStandPatCandidatesPerBucket(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(activeFrontier),
                        nodeArena,
                        standPatLimit,
                        preferPotionBuckets,
                        standPatBuf.AsSpan(0, standPatLimit));

                    if (standPatCount > 0)
                    {
                        victories.Clear();
                        expander.RunStandPatBatch(
                            standPatBuf.AsSpan(0, standPatCount),
                            nodeArena,
                            mainStateArena,
                            rootBundle.Rules,
                            in hotPolicy,
                            in evalLookup,
                            evalMode,
                            nextTurnCandidates,
                            victories,
                            ref bestFallbackId,
                            ref bestFallbackKey,
                            ref hasBestFallback,
                            ref stats,
                            budget.CancellationToken);

                        ProcessVictories(
                            victories,
                            nodeArena,
                            in hotPolicy,
                            allowPaidOptionalPotions,
                            ref bestVictoryId,
                            ref bestVictoryKey,
                            ref hasVictory,
                            ref incumbent,
                            ref incumbentUpdates,
                            ref stopTargetReached);
                    }
                }

                // Commit completed-turn ongoing nodes AFTER both actionDepth and StandPat finish for this turn layer.
                // Keeping completed-turn nodes strictly separate from mid-turn nextStepCandidates ensures non-victory
                // fallback routes always end cleanly with EndTurn (never truncating mid-turn before enemy attacks).
                // If a deeper turn layer was cut short mid-turn by TimeLimit/NodeLimit while an earlier turn layer
                // already completed cleanly, retain that earlier fully-explored completed-turn plan.
                bool turnLayerCompleted = !stopTargetReached
                    && boundaryReason is not (SearchBoundaryReason.TimeLimit or SearchBoundaryReason.NodeLimit);
                if ((turnLayerCompleted || !hasCompletedTurnOngoing) && nextTurnCandidates.Count > 0)
                {
                    UpdateBestCompletedTurnOngoing(
                        nextTurnCandidates,
                        nodeArena,
                        in hotPolicy,
                        ref bestCompletedTurnOngoingId,
                        ref bestCompletedTurnOngoingKey,
                        ref bestCompletedTurnOngoingTurn,
                        ref hasCompletedTurnOngoing);
                }

                if (stopTargetReached || boundaryReason is SearchBoundaryReason.TimeLimit or SearchBoundaryReason.NodeLimit)
                    break;

                if (nextTurnCandidates.Count == 0)
                {
                    if (lastTurnHitPerTurnNodeCap && !hasVictory && boundaryReason == SearchBoundaryReason.None)
                        boundaryReason = SearchBoundaryReason.NodeLimit;
                    break;
                }

                if (HasExceededPostVictoryTurnCap(
                    hasVictory,
                    in bestVictoryKey,
                    hotPolicy.StartTurnNumber + turnLayer + 1,
                    in hotPolicy,
                    allowPaidOptionalPotions))
                {
                    break;
                }

                effectiveTurnBoundaryWidth = (turnLayer <= 2
                    && (!hasVictory || (hotPolicy.HasAnyStrategyTargets && !bestVictoryKey.AllStrategyTargetsSatisfied)))
                    ? earlyTurnBoundaryWidth
                    : beamWidth;

                if (pruneOutBuf.Length < effectiveTurnBoundaryWidth)
                {
                    ArrayPool<int>.Shared.Return(pruneOutBuf, clearArray: false);
                    pruneOutBuf = ArrayPool<int>.Shared.Rent(effectiveTurnBoundaryWidth);
                }

                // Stage 8C (Lever P1) Turn-Boundary 2-Pilot Fast Rollouts + Exact Victory Promotion:
                // 1. Pre-filter diverse MAP-Elites shortlist M = min(Count, effectiveTurnBoundaryWidth * 2) with useRolloutScores: false
                //    so 100% of final turn-boundary prune inputs have HasRolloutScore == true (zero rolled-vs-unrolled skew).
                // 2. Run parallel 0-alloc rollouts & strict 0..M-1 deterministic Victory promotion -> ProcessVictories.
                // 3. Final PruneLayer(..., isTurnBoundary: true, preferPotionBuckets, useRolloutScores: ranTurnBoundaryRollout).
                ReadOnlySpan<int> turnBoundaryInput = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nextTurnCandidates);
                bool ranTurnBoundaryRollout = false;

                if (enableTurnBoundaryRollouts
                    && rolloutEvaluator != null
                    && rolloutHorizonTurns > 0
                    && !budget.IsTimeExpired)
                {
                    int shortlistCap = Math.Min(turnBoundaryInput.Length, effectiveTurnBoundaryWidth * 2);
                    if (shortlistBuf.Length < shortlistCap)
                    {
                        ArrayPool<int>.Shared.Return(shortlistBuf, clearArray: false);
                        shortlistBuf = ArrayPool<int>.Shared.Rent(shortlistCap);
                    }

                    int shortlistCount;
                    if (turnBoundaryInput.Length > shortlistCap)
                    {
                        shortlistCount = retention.PruneLayer(
                            turnBoundaryInput,
                            nodeArena,
                            shortlistCap,
                            shortlistBuf.AsSpan(0, shortlistCap),
                            in hotPolicy,
                            isTurnBoundary: true,
                            preferPotionBuckets: preferPotionBuckets,
                            useRolloutScores: false,
                            turnLayer: turnLayer,
                            allowDeathSaveRescue: allowDeathSaveRescue);
                    }
                    else
                    {
                        turnBoundaryInput.CopyTo(shortlistBuf);
                        shortlistCount = turnBoundaryInput.Length;
                    }

                    victories.Clear();
                    rolloutEvaluator.EvaluateTurnBoundary(
                        shortlistBuf.AsSpan(0, shortlistCount),
                        nodeArena,
                        mainStateArena,
                        rootBundle.Rules,
                        in hotPolicy,
                        in evalLookup,
                        evalMode,
                        rolloutHorizonTurns,
                        victories,
                        ref rolloutStats,
                        budget.CancellationToken);

                    if (victories.Count > 0)
                    {
                        ProcessVictories(
                            victories,
                            nodeArena,
                            in hotPolicy,
                            allowPaidOptionalPotions,
                            ref bestVictoryId,
                            ref bestVictoryKey,
                            ref hasVictory,
                            ref incumbent,
                            ref incumbentUpdates,
                            ref stopTargetReached);
                    }

                    if (stopTargetReached)
                        break;

                    if (HasExceededPostVictoryTurnCap(
                        hasVictory,
                        in bestVictoryKey,
                        hotPolicy.StartTurnNumber + turnLayer + 1,
                        in hotPolicy,
                        allowPaidOptionalPotions))
                    {
                        break;
                    }

                    turnBoundaryInput = shortlistBuf.AsSpan(0, shortlistCount);
                    ranTurnBoundaryRollout = true;
                }

                effectiveTurnBoundaryWidth = (turnLayer <= 2
                    && (!hasVictory || (hotPolicy.HasAnyStrategyTargets && !bestVictoryKey.AllStrategyTargetsSatisfied)))
                    ? earlyTurnBoundaryWidth
                    : beamWidth;
                if (pruneOutBuf.Length < effectiveTurnBoundaryWidth)
                {
                    ArrayPool<int>.Shared.Return(pruneOutBuf, clearArray: false);
                    pruneOutBuf = ArrayPool<int>.Shared.Rent(effectiveTurnBoundaryWidth);
                }

                int nextKept = retention.PruneLayer(
                    turnBoundaryInput,
                    nodeArena,
                    effectiveTurnBoundaryWidth,
                    pruneOutBuf.AsSpan(0, effectiveTurnBoundaryWidth),
                    in hotPolicy,
                    isTurnBoundary: true,
                    preferPotionBuckets: preferPotionBuckets,
                    useRolloutScores: ranTurnBoundaryRollout,
                    turnLayer: turnLayer,
                    allowDeathSaveRescue: allowDeathSaveRescue);

                currentTurnFrontier.Clear();
                bool layerProgressed = false;
                bool enemyHpDecreased = false;
                for (int i = 0; i < nextKept; i++)
                {
                    int nid = pruneOutBuf[i];
                    currentTurnFrontier.Add(nid);
                    ref readonly HotSearchNode n = ref nodeArena.GetRef(nid);
                    int remEnemies = n.AliveEnemyCount + n.RevivingEnemyCount;
                    if (n.EnemyHp < bestSeenEnemyHp)
                    {
                        bestSeenEnemyHp = n.EnemyHp;
                        layerProgressed = true;
                        enemyHpDecreased = true;
                    }
                    if (remEnemies < bestSeenAliveEnemies) { bestSeenAliveEnemies = remEnemies; layerProgressed = true; }
                    if (n.MaxDarkEvoke > bestSeenMaxDarkEvoke) { bestSeenMaxDarkEvoke = n.MaxDarkEvoke; layerProgressed = true; }
                    if (n.TotalPoisonAndDoom > bestSeenPoisonAndDoom) { bestSeenPoisonAndDoom = n.TotalPoisonAndDoom; layerProgressed = true; }
                    if (n.BoardInvestment > bestSeenBoardInvestment) { bestSeenBoardInvestment = n.BoardInvestment; layerProgressed = true; }
                    if (hotPolicy.HasAnyStrategyTargets)
                    {
                        int goalCredit = n.StrategyGoalHpCredit;
                        if (goalCredit > bestSeenStrategyGoalHpCredit)
                        {
                            bestSeenStrategyGoalHpCredit = goalCredit;
                            layerProgressed = true;
                        }
                        int goalCount = n.StrategyGoalCount;
                        if (goalCount > bestSeenStrategyGoalCount)
                        {
                            bestSeenStrategyGoalCount = goalCount;
                            layerProgressed = true;
                        }
                        if (hotPolicy.HasRelicTargets && (n.RelicSatisfiedMask & ~seenRelicSatisfiedMask) != 0)
                        {
                            seenRelicSatisfiedMask |= n.RelicSatisfiedMask;
                            layerProgressed = true;
                        }
                    }
                }

                turnsWithoutProgress = layerProgressed ? 0 : turnsWithoutProgress + 1;
                turnsSinceEnemyHpDecreased = enemyHpDecreased ? 0 : turnsSinceEnemyHpDecreased + 1;
                if (turnsWithoutProgress >= HotSearchConstants.SetupValueHorizonTurns)
                {
                    if (!hasVictory)
                        boundaryReason = SearchBoundaryReason.TurnLimit;
                    break;
                }

                bool bossExtendedTurnStalled = hotPolicy.IsBossEncounter
                    && turnLayer + 1 >= DefaultMaxSearchTurns
                    && turnsSinceEnemyHpDecreased > BossExtensionHpStallLimit;
                if (bossExtendedTurnStalled || (turnLayer == maxTurnsForPass - 1 && currentTurnFrontier.Count > 0))
                {
                    hitMaxSearchTurns = true;
                    if (!hasVictory && boundaryReason == SearchBoundaryReason.None)
                        boundaryReason = SearchBoundaryReason.TurnLimit;
                    break;
                }

                // Turn-boundary packed-state compaction: only surviving currentTurnFrontier (<= beamWidth states, ~0.8 MiB)
                // can ever be unpacked in turnLayer + 1. PlayerTurnNumber in HotHash128 is strictly monotonic across turns,
                // and HotPlanMaterializer replays from rootBundle.RootState without reading StateHandle.
                if (currentTurnFrontier.Count > 0)
                {
                    if (tt.ActiveEntryCount > peakTranspositionCount)
                        peakTranspositionCount = tt.ActiveEntryCount;
                    if (expander.StandPatCacheCount > peakStandPatCacheCount)
                        peakStandPatCacheCount = expander.StandPatCacheCount;

                    CompactFrontierStateArena(currentTurnFrontier, nodeArena, ref mainStateArena, ref compactStateArena);
                    tt.ResetForNextPass();
                    expander.ClearStandPatCache();
                }
            }
        }
        finally
        {
            retention.AllowDeathSaveRescue = prevAllowDeathSaveRescue;
            ArrayPool<int>.Shared.Return(pruneOutBuf, clearArray: false);
            ArrayPool<int>.Shared.Return(shortlistBuf, clearArray: false);
            ArrayPool<int>.Shared.Return(standPatBuf, clearArray: false);
        }

        bool capacityBounded = stats.CapacityExceededDrops > 0;
        if (capacityBounded && !hasVictory && boundaryReason == SearchBoundaryReason.None)
            boundaryReason = SearchBoundaryReason.DynamicResolution;

        bool frontierExhaustedInDefeat = !hasVictory
            && !capacityBounded
            && boundaryReason == SearchBoundaryReason.None
            && !lastTurnHitPerTurnNodeCap
            && !hitMaxSearchTurns
            && hasBestFallback
            && !bestFallbackKey.Survives;

        bool frontierExhaustedInEventDefeat = !hasVictory
            && !capacityBounded
            && boundaryReason == SearchBoundaryReason.None
            && !lastTurnHitPerTurnNodeCap
            && !hitMaxSearchTurns
            && hasBestFallback
            && bestFallbackKey.IsEventDefeat;

        int chosenId;
        HotRouteKey chosenKey;
        bool chosenHasCompletedTurnOngoing;
        int chosenCompletedOngoingTurn;
        bool onlyDeathRoutes;
        bool isMidTurnFallback;

        if (hasVictory)
        {
            chosenId = bestVictoryId;
            chosenKey = bestVictoryKey;
            chosenHasCompletedTurnOngoing = hasCompletedTurnOngoing;
            chosenCompletedOngoingTurn = hasCompletedTurnOngoing ? bestCompletedTurnOngoingTurn : -1;
            onlyDeathRoutes = false;
            isMidTurnFallback = false;
        }
        else if (frontierExhaustedInDefeat)
        {
            // Every explored branch in the search tree ended in defeat without hitting any time/node/turn/capacity
            // boundary (even if earlier turns survived before all branches died on a later turn).
            // Return the longest-surviving defeat route with OnlyDeathRoutes = true so UI displays "未找到生还路线"
            // and DeathTurn reflects the exact terminal defeat turn.
            chosenId = bestFallbackId;
            chosenKey = bestFallbackKey;
            chosenHasCompletedTurnOngoing = false;
            chosenCompletedOngoingTurn = -1;
            onlyDeathRoutes = true;
            isMidTurnFallback = false;
        }
        else if (frontierExhaustedInEventDefeat)
        {
            // Every explored branch ended in EventDefeat (e.g. BattlewornDummy timed out & escaped on Turn 3).
            // Return the full EventDefeat route (not an earlier truncated turn) with BoundaryReason = EventDefeat.
            boundaryReason = SearchBoundaryReason.EventDefeat;
            chosenId = bestFallbackId;
            chosenKey = bestFallbackKey;
            chosenHasCompletedTurnOngoing = false;
            chosenCompletedOngoingTurn = -1;
            onlyDeathRoutes = false;
            isMidTurnFallback = false;
        }
        else if (hasCompletedTurnOngoing)
        {
            chosenId = bestCompletedTurnOngoingId;
            chosenKey = bestCompletedTurnOngoingKey;
            chosenHasCompletedTurnOngoing = true;
            chosenCompletedOngoingTurn = bestCompletedTurnOngoingTurn;
            onlyDeathRoutes = false;
            isMidTurnFallback = false;
        }
        else if (hasBestFallback)
        {
            // No surviving EndTurn route exists on this turn (e.g. every legal EndTurn is lethal or times out an event).
            // Return the best terminal/EndTurn fallback with accurate OnlyDeathRoutes / EventDefeat rather than a
            // truncated mid-turn node that omits EndTurn and triggers repeated PlanExhaustedWithoutEndTurn loops.
            chosenId = bestFallbackId;
            chosenKey = bestFallbackKey;
            chosenHasCompletedTurnOngoing = false;
            chosenCompletedOngoingTurn = -1;
            onlyDeathRoutes = !bestFallbackKey.Survives;
            isMidTurnFallback = false;
            if (bestFallbackKey.IsEventDefeat && boundaryReason == SearchBoundaryReason.None)
                boundaryReason = SearchBoundaryReason.EventDefeat;
        }
        else if (hasMidTurnOngoing)
        {
            // Last-resort fallback only when zero EndTurn/terminal nodes were materialized (e.g. capacity drop).
            chosenId = bestMidTurnOngoingId;
            chosenKey = bestMidTurnOngoingKey;
            chosenHasCompletedTurnOngoing = false;
            chosenCompletedOngoingTurn = -1;
            onlyDeathRoutes = false;
            isMidTurnFallback = true;
        }
        else
        {
            chosenId = bestFallbackId;
            chosenKey = bestFallbackKey;
            chosenHasCompletedTurnOngoing = false;
            chosenCompletedOngoingTurn = -1;
            onlyDeathRoutes = false;
            isMidTurnFallback = true;
        }

        if (tt.ActiveEntryCount > peakTranspositionCount)
            peakTranspositionCount = tt.ActiveEntryCount;
        if (expander.StandPatCacheCount > peakStandPatCacheCount)
            peakStandPatCacheCount = expander.StandPatCacheCount;

        stats.WorkerAllocatedBytes += rolloutStats.RolloutAllocatedBytes;

        passClock.Stop();
        return new HotPassOutcome(
            BestNodeId: chosenId,
            BestKey: chosenKey,
            HasBest: chosenId >= 0,
            HasVictory: hasVictory,
            HasCompletedTurnOngoing: chosenHasCompletedTurnOngoing,
            CompletedOngoingTurn: chosenCompletedOngoingTurn,
            IsMidTurnFallback: isMidTurnFallback,
            OnlyDeathRoutes: onlyDeathRoutes,
            StopTargetReached: stopTargetReached,
            CapacityBounded: capacityBounded,
            BoundaryReason: boundaryReason,
            SearchedTurns: Math.Max(1, searchedTurns),
            PrimaryIncumbentUpdates: incumbentUpdates,
            PeakTranspositionCount: peakTranspositionCount,
            PeakStandPatCacheCount: peakStandPatCacheCount,
            Stats: stats,
            RolloutStats: rolloutStats,
            Elapsed: passClock.Elapsed,
            Gen0: Math.Max(0, GC.CollectionCount(0) - gen0Start),
            Gen1: Math.Max(0, GC.CollectionCount(1) - gen1Start),
            Gen2: Math.Max(0, GC.CollectionCount(2) - gen2Start),
            GcPause: GC.GetTotalPauseDuration() - gcPauseStart);
    }

    /// <summary>
    /// Selects up to <paramref name="maxTotal"/> Stand-Pat candidates balanced across active potion buckets
    /// (<c>0, 1, 2, 3+</c>) so 0-potion nodes do not starve potion buckets of Stand-Pat lookahead.
    /// Zero heap allocation, 100% deterministic.
    /// </summary>
    private static int SelectStandPatCandidatesPerBucket(
        ReadOnlySpan<int> activeFrontier,
        HotNodeArena arena,
        int maxTotal,
        bool preferPotionBuckets,
        Span<int> output)
    {
        int n = activeFrontier.Length;
        int cap = Math.Min(maxTotal, Math.Min(n, output.Length));
        if (cap <= 0)
            return 0;

        Span<int> counts = stackalloc int[HotBeamRetention.MaxPotionBuckets];
        counts.Clear();
        for (int i = 0; i < n; i++)
        {
            byte p = arena.GetRef(activeFrontier[i]).PotionCount;
            int b = p >= HotBeamRetention.MaxPotionBuckets ? HotBeamRetention.MaxPotionBuckets - 1 : p;
            counts[b]++;
        }

        int activeBuckets = 0;
        for (int b = 0; b < HotBeamRetention.MaxPotionBuckets; b++)
        {
            if (counts[b] > 0)
                activeBuckets++;
        }
        if (activeBuckets <= 1)
        {
            activeFrontier[..cap].CopyTo(output);
            return cap;
        }

        // Compute per-bucket quotas (round-robin starting from bucket 1 when preferring potions).
        Span<int> quotas = stackalloc int[HotBeamRetention.MaxPotionBuckets];
        quotas.Clear();
        int remaining = cap;
        int startBucket = preferPotionBuckets ? 1 : 0;
        while (remaining > 0)
        {
            bool progressed = false;
            for (int step = 0; step < HotBeamRetention.MaxPotionBuckets && remaining > 0; step++)
            {
                int b = (startBucket + step) % HotBeamRetention.MaxPotionBuckets;
                if (quotas[b] < counts[b])
                {
                    quotas[b]++;
                    remaining--;
                    progressed = true;
                }
            }
            if (!progressed)
                break;
        }

        // Single pass over Score-sorted activeFrontier collecting the top quotas[b] from each bucket.
        Span<int> taken = stackalloc int[HotBeamRetention.MaxPotionBuckets];
        taken.Clear();
        int written = 0;
        for (int i = 0; i < n && written < cap; i++)
        {
            int nid = activeFrontier[i];
            byte p = arena.GetRef(nid).PotionCount;
            int b = p >= HotBeamRetention.MaxPotionBuckets ? HotBeamRetention.MaxPotionBuckets - 1 : p;
            if (taken[b] >= quotas[b])
                continue;
            taken[b]++;
            output[written++] = nid;
        }
        return written;
    }

    private static void ProcessVictories(
        List<int> victories,
        HotNodeArena nodeArena,
        in HotSearchPolicy hotPolicy,
        bool allowPaidOptionalPotions,
        ref int bestVictoryId,
        ref HotRouteKey bestVictoryKey,
        ref bool hasVictory,
        ref HotIncumbentBound incumbent,
        ref int incumbentUpdates,
        ref bool stopTargetReached)
    {
        for (int i = 0; i < victories.Count; i++)
        {
            int nid = victories[i];
            ref readonly HotSearchNode node = ref nodeArena.GetRef(nid);
            HotRouteKey key = HotRouteQuality.Capture(in node, in hotPolicy);

            if (!HotPotionPolicy.SatisfiesHardConstraints(in key, in hotPolicy))
                continue;

            if (!hasVictory || HotRouteQuality.IsBetter(in key, in bestVictoryKey, in hotPolicy, includeOptionalPotionCost: true))
            {
                hasVictory = true;
                bestVictoryId = nid;
                bestVictoryKey = key;

                if (!hotPolicy.HasAnyHealOrUnknownGeneration
                    && !hotPolicy.HasAnyStrategyTargets
                    && !hotPolicy.HasForcedDirectives
                    && hotPolicy.TheftPolicy != SolverTheftPolicy.PreserveResources
                    && key.DeathSaveUseCount == 0
                    && key.HasCentiPrecision)
                {
                    bool zHas = incumbent.HasCertifiedZeroPotionBound;
                    int zHpDelta = incumbent.BestZeroPotionHpDeltaCenti;
                    int zBattleHpLost = zHas ? incumbent.BestZeroPotionBattleHpLost : int.MaxValue;
                    int zTurn = incumbent.BestZeroPotionEndTurn;
                    bool oHas = incumbent.HasCertifiedOverallBound;
                    long oValue = incumbent.BestOverallValueCenti;
                    int oHpDelta = incumbent.BestOverallHpDeltaCenti;
                    int oBattleHpLost = oHas ? incumbent.BestOverallBattleHpLost : int.MaxValue;
                    int oPotionCount = oHas ? incumbent.BestOverallPotionCount : int.MaxValue;
                    int oTurn = incumbent.BestOverallEndTurn;
                    bool changed = false;

                    if (key.OptionalPaidPotionCount == 0 && key.ExplicitPotionCount == 0)
                    {
                        if (!zHas
                            || key.NetPersistentHpDeltaCenti > zHpDelta
                            || (key.NetPersistentHpDeltaCenti == zHpDelta
                                && (key.ProjectedBattleHpLost < zBattleHpLost
                                    || (key.ProjectedBattleHpLost == zBattleHpLost && key.CombatEndedTurn < zTurn))))
                        {
                            zHas = true;
                            zHpDelta = key.NetPersistentHpDeltaCenti;
                            zBattleHpLost = key.ProjectedBattleHpLost;
                            zTurn = key.CombatEndedTurn;
                            changed = true;
                        }
                    }

                    long vCenti = (long)key.NetPersistentHpDeltaCenti - key.NetPotionOpportunityCostCenti;
                    if (!oHas
                        || vCenti > oValue
                        || (vCenti == oValue && key.NetPersistentHpDeltaCenti > oHpDelta)
                        || (vCenti == oValue
                            && key.NetPersistentHpDeltaCenti == oHpDelta
                            && (key.ProjectedBattleHpLost < oBattleHpLost
                                || (key.ProjectedBattleHpLost == oBattleHpLost
                                    && (key.ProjectedBattlePotionCount < oPotionCount
                                        || (key.ProjectedBattlePotionCount == oPotionCount && key.CombatEndedTurn < oTurn))))))
                    {
                        oHas = true;
                        oValue = vCenti;
                        oHpDelta = key.NetPersistentHpDeltaCenti;
                        oBattleHpLost = key.ProjectedBattleHpLost;
                        oPotionCount = key.ProjectedBattlePotionCount;
                        oTurn = key.CombatEndedTurn;
                        changed = true;
                    }

                    if (changed)
                    {
                        incumbent = new HotIncumbentBound(
                            zHas, zHpDelta, zTurn,
                            oHas, oValue, oHpDelta, oTurn,
                            zBattleHpLost, oBattleHpLost, oPotionCount);
                        incumbentUpdates++;
                    }
                }

                // In paid-potion passes (Stage 2 / SingleBeam), do NOT stop early on a 0-damage win if a
                // heal/FruitJuice/growth potion can still strictly improve V!
                if (HotRouteQuality.MeetsVictoryStopTarget(in key, in hotPolicy)
                    && (!allowPaidOptionalPotions || !HotPotionPolicy.CanAnyPaidPotionImproveOn(in key, in hotPolicy)))
                {
                    stopTargetReached = true;
                    break;
                }
            }
        }
    }

    private static bool IsBetterPassOutcome(
        in HotPassOutcome candidate,
        in HotPassOutcome current,
        in HotSearchPolicy hotPolicy)
    {
        HotRouteKey candidateKey = candidate.BestKey;
        if (!candidate.HasBest || !HotPotionPolicy.SatisfiesHardConstraints(in candidateKey, in hotPolicy))
            return false;

        HotRouteKey currentKey = current.BestKey;
        if (!current.HasBest || !HotPotionPolicy.SatisfiesHardConstraints(in currentKey, in hotPolicy))
            return true;

        if (candidate.HasVictory || current.HasVictory)
            return HotRouteQuality.IsBetter(in candidateKey, in currentKey, in hotPolicy, includeOptionalPotionCost: true);

        // Do not let a budget-truncated partial ongoing pass overwrite a complete EventDefeat pass.
        if (current.BoundaryReason == SearchBoundaryReason.EventDefeat
            && candidate.BoundaryReason != SearchBoundaryReason.None
            && candidate.BoundaryReason != SearchBoundaryReason.EventDefeat)
        {
            return false;
        }
        if (candidate.BoundaryReason == SearchBoundaryReason.EventDefeat
            && current.BoundaryReason != SearchBoundaryReason.None
            && current.BoundaryReason != SearchBoundaryReason.EventDefeat)
        {
            return true;
        }

        if (candidate.HasCompletedTurnOngoing != current.HasCompletedTurnOngoing)
            return candidate.HasCompletedTurnOngoing;

        if (candidate.IsMidTurnFallback != current.IsMidTurnFallback)
            return !candidate.IsMidTurnFallback;

        if (candidate.HasCompletedTurnOngoing
            && candidateKey.Survives
            && currentKey.Survives
            && candidate.CompletedOngoingTurn != current.CompletedOngoingTurn)
        {
            if (candidate.CompletedOngoingTurn > current.CompletedOngoingTurn
                && current.BoundaryReason == SearchBoundaryReason.None)
                return true;
            if (current.CompletedOngoingTurn > candidate.CompletedOngoingTurn
                && candidate.BoundaryReason == SearchBoundaryReason.None)
                return false;
        }

        return HotRouteQuality.IsBetter(in candidateKey, in currentKey, in hotPolicy, includeOptionalPotionCost: true);
    }

    private static void UpdateBestCompletedTurnOngoing(
        List<int> nodes,
        HotNodeArena nodeArena,
        in HotSearchPolicy hotPolicy,
        ref int bestId,
        ref HotRouteKey bestKey,
        ref int bestTurn,
        ref bool hasBest)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            int nid = nodes[i];
            ref readonly HotSearchNode node = ref nodeArena.GetRef(nid);
            HotRouteKey key = HotRouteQuality.Capture(in node, in hotPolicy);
            if (!key.Survives || !HotPotionPolicy.SatisfiesHardConstraints(in key, in hotPolicy))
                continue;

            int nodeTurn = node.Turn;
            if (!hasBest
                || nodeTurn > bestTurn
                || (nodeTurn == bestTurn
                    && HotRouteQuality.IsBetter(in key, in bestKey, in hotPolicy, includeOptionalPotionCost: true)))
            {
                hasBest = true;
                bestId = nid;
                bestKey = key;
                bestTurn = nodeTurn;
            }
        }
    }

    private static void UpdateBestOngoing(
        List<int> nodes,
        HotNodeArena nodeArena,
        in HotSearchPolicy hotPolicy,
        ref int bestId,
        ref HotRouteKey bestKey,
        ref bool hasBest)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            int nid = nodes[i];
            ref readonly HotSearchNode node = ref nodeArena.GetRef(nid);
            HotRouteKey key = HotRouteQuality.Capture(in node, in hotPolicy);
            if (!HotPotionPolicy.SatisfiesHardConstraints(in key, in hotPolicy))
                continue;
            if (!hasBest || HotRouteQuality.IsBetter(in key, in bestKey, in hotPolicy, includeOptionalPotionCost: true))
            {
                hasBest = true;
                bestId = nid;
                bestKey = key;
            }
        }
    }

    private static SolverResult MaterializePassResult(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        HotRootBundle rootBundle,
        in HotSearchPolicy hotPolicy,
        short[] rootPotionCosts,
        HotNodeArena nodeArena,
        in HotPassOutcome pass,
        HotTranspositionTable tt,
        long totalExpanded,
        long totalTransitions,
        long totalChoices,
        long totalAllocated,
        TimeSpan totalElapsed,
        int potionHpSaved,
        int potionBranchesRejected)
    {
        HotMaterializeMetrics metrics = new(
            ExpandedNodes: pass.Stats.ExpandedParents,
            TotalExpandedNodes: totalExpanded,
            TransitionCount: pass.Stats.Transitions,
            TotalTransitionCount: totalTransitions,
            ChoiceBranchesEvaluated: pass.Stats.ChoiceBranches,
            TotalChoiceBranchesEvaluated: totalChoices,
            DuplicateCardBranchesPruned: pass.Stats.DuplicateCardsPruned,
            TranspositionBranchesPruned: pass.Stats.TranspositionPrunes,
            PrimaryIncumbentBranchesPruned: pass.Stats.IncumbentPrunes,
            PrimaryIncumbentUpdates: pass.PrimaryIncumbentUpdates,
            StandPatProbes: pass.Stats.StandPatProbes,
            EndTurnPrefixCaptures: pass.Stats.EndTurnPrefixCaptures,
            EndTurnPrefixReuses: pass.Stats.EndTurnPrefixReuses,
            ParallelExpansionWaves: pass.Stats.ParallelWaves,
            ParallelExpansionWorkItems: pass.Stats.ParallelWorkItems,
            MaxParallelExpansionConcurrency: pass.Stats.MaxConcurrency,
            TranspositionCount: pass.PeakTranspositionCount,
            TranspositionLimitBypasses: tt.LimitBypasses,
            StandPatCacheCount: pass.PeakStandPatCacheCount,
            SearchedTurns: pass.SearchedTurns,
            BoundaryReason: ResolveEffectiveBoundaryReason(in pass),
            OnlyDeathRoutesFound: pass.OnlyDeathRoutes,
            WorkerAllocatedBytes: pass.Stats.WorkerAllocatedBytes,
            TotalWorkerAllocatedBytes: totalAllocated,
            Gen0Collections: pass.Gen0,
            Gen1Collections: pass.Gen1,
            Gen2Collections: pass.Gen2,
            GcPauseDuration: pass.GcPause,
            Elapsed: pass.Elapsed,
            TotalSearchElapsed: totalElapsed,
            PotionHpSaved: potionHpSaved,
            PotionHpRequired: pass.BestKey.OptionalPotionStrategicCost,
            PotionBranchesRejected: potionBranchesRejected);

        HotRouteKey passBestKey = pass.BestKey;
        return HotPlanMaterializer.Materialize(
            root,
            displayNames,
            battleDamage,
            policy,
            rootBundle,
            in hotPolicy,
            rootPotionCosts,
            nodeArena,
            pass.BestNodeId,
            in passBestKey,
            in metrics);
    }

    private static void AccumulatePassTotals(
        in HotPassOutcome pass,
        ref long totalExpanded,
        ref long totalTransitions,
        ref long totalChoices,
        ref long totalAllocated,
        ref int totalGen0,
        ref int totalGen1,
        ref int totalGen2,
        ref TimeSpan totalGcPause,
        ref int passCount,
        ref HotRolloutStats totalRolloutStats)
    {
        totalExpanded += pass.Stats.ExpandedParents;
        totalTransitions += pass.Stats.Transitions;
        totalChoices += pass.Stats.ChoiceBranches;
        totalAllocated += pass.Stats.WorkerAllocatedBytes;
        totalGen0 += pass.Gen0;
        totalGen1 += pass.Gen1;
        totalGen2 += pass.Gen2;
        totalGcPause += pass.GcPause;
        totalRolloutStats.RolloutCandidates += pass.RolloutStats.RolloutCandidates;
        totalRolloutStats.RolloutSteps += pass.RolloutStats.RolloutSteps;
        totalRolloutStats.RolloutVictoriesFound += pass.RolloutStats.RolloutVictoriesFound;
        totalRolloutStats.RolloutVictoriesPromoted += pass.RolloutStats.RolloutVictoriesPromoted;
        totalRolloutStats.RolloutCapacityDrops += pass.RolloutStats.RolloutCapacityDrops;
        totalRolloutStats.RolloutElapsedTicks += pass.RolloutStats.RolloutElapsedTicks;
        totalRolloutStats.RolloutAllocatedBytes += pass.RolloutStats.RolloutAllocatedBytes;
        passCount++;
    }

    private static void EmitRolloutDiagnostics(in HotRolloutStats stats, SearchPolicySnapshot policy)
    {
        if (stats.RolloutCandidates <= 0)
            return;

        string line = $"HOT_ROLLOUT candidates={stats.RolloutCandidates} steps={stats.RolloutSteps} " +
            $"wins_found={stats.RolloutVictoriesFound} wins_promoted={stats.RolloutVictoriesPromoted} " +
            $"cap_drops={stats.RolloutCapacityDrops} ms={stats.RolloutElapsedMilliseconds:F2} alloc_bytes={stats.RolloutAllocatedBytes}";
        if (policy.MeasurePhasePerformance)
            policy.Diagnostics.Info(line);
        else
            policy.Diagnostics.Debug(line);
    }

    private static void CompactFrontierStateArena(
        List<int> frontier,
        HotNodeArena nodeArena,
        ref PackedStateArena mainStateArena,
        ref PackedStateArena compactStateArena)
    {
        compactStateArena.Reset();
        for (int i = 0; i < frontier.Count; i++)
        {
            ref HotSearchNode node = ref nodeArena.GetRef(frontier[i]);
            if (node.StateHandle.Length <= 0)
                continue;
            ReadOnlySpan<byte> src = mainStateArena.GetSpan(node.StateHandle);
            Span<byte> dst = compactStateArena.Reserve(src.Length);
            src.CopyTo(dst);
            node.StateHandle = compactStateArena.CommitReserved(src.Length);
        }

        mainStateArena.Reset();
        (mainStateArena, compactStateArena) = (compactStateArena, mainStateArena);
    }

    private static void ResetSearchStateForNextPass(
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        PackedStateArena compactStateArena,
        HotTranspositionTable tt,
        HotParallelExpander expander)
    {
        nodeArena.Reset();
        mainStateArena.Reset();
        compactStateArena.Reset();
        tt.ResetForNextPass();
        expander.ClearStandPatCache();
    }

    private static int ResolveInitialBeamWidth(int configuredBeamWidth, in HotSearchPolicy policy)
    {
        int width = Math.Clamp(configuredBeamWidth, 4, HotSearchConstants.MaxBeamWidth);
        if (policy.IsActEndingBoss && width < 45)
            width = 45;
        return width;
    }

    private static int ResolveWidenedBeamWidth(int baseBeamWidth)
    {
        int safeBase = Math.Clamp(baseBeamWidth, 4, HotSearchConstants.MaxBeamWidth);
        long doubled = (long)safeBase * 2L;
        int widened = (int)Math.Min((long)HotSearchConstants.MaxBeamWidth, doubled);
        return Math.Max(safeBase, widened);
    }

    private static int ResolveStagePotionBeamWidth(int stageBeamWidth)
    {
        int safeBase = Math.Clamp(stageBeamWidth, 4, HotSearchConstants.MaxBeamWidth);
        long scaled = ((long)safeBase * 5L) / 4L;
        int width = (int)Math.Min((long)HotSearchConstants.MaxBeamWidth, scaled);
        return Math.Max(safeBase, width);
    }

    private static int ResolveEffectiveDop(SearchPolicySnapshot policy)
    {
        if (policy.DetailedDiagnostics)
            return 1;
        int maxDop = Math.Max(1, Math.Min(HotSearchConstants.MaximumSearchMaxDegreeOfParallelism, Environment.ProcessorCount));
        return Math.Clamp(policy.MaxDegreeOfParallelism, 1, maxDop);
    }

    private static SearchBoundaryReason ResolveEffectiveBoundaryReason(in HotPassOutcome pass)
        => pass.BestKey.IsEventDefeat
            ? SearchBoundaryReason.EventDefeat
            : (pass.BoundaryReason == SearchBoundaryReason.None && !pass.HasVictory && !pass.OnlyDeathRoutes
                ? SearchBoundaryReason.TurnLimit
                : pass.BoundaryReason);

    private static int ResolveEffectiveMaxCardBranches(int configuredMaxCardBranches)
        => configuredMaxCardBranches >= 32 && configuredMaxCardBranches < 64
            ? 64
            : configuredMaxCardBranches;

    private static bool HasExceededPostVictoryTurnCap(
        bool hasVictory,
        in HotRouteKey bestVictoryKey,
        int nextTurnNumber,
        in HotSearchPolicy hotPolicy,
        bool allowPaidOptionalPotions)
    {
        if (!hasVictory || bestVictoryKey.CombatEndedTurn == int.MaxValue)
            return false;

        bool unsatisfiedStrategyTargets = hotPolicy.HasAnyStrategyTargets
            && !bestVictoryKey.AllStrategyTargetsSatisfied;

        bool canHealOrGrow = hotPolicy.HasAnyHealOrUnknownGeneration
            || unsatisfiedStrategyTargets
            || (allowPaidOptionalPotions
                && (hotPolicy.HasPaidHealPotion || hotPolicy.HasPaidFruitJuice || hotPolicy.HasPaidEntropicBrew))
            || bestVictoryKey.DeathSaveUseCount > 0
            || (hotPolicy.TheftPolicy == SolverTheftPolicy.PreserveResources
                && (bestVictoryKey.OutstandingStolen > 0 || bestVictoryKey.ForfeitsGold));

        // Only apply the tight T*+1 cutoff when the best victory is completely lossless (0 persistent deficit,
        // 0 raw battle HP lost, 0 optional paid potion cost) and no heal/growth/recovery remains.
        // When raw battle HP was lost even if absorbed by BurningBlood / RunEnding (StrategicHpDeficit == 0),
        // allow T*+3 on Elite/Boss (T*+2 on normal monsters) so cleaner lower-raw-HP wins are explored;
        // when strategy targets remain unsatisfied, allow a tightly bounded post-victory stall window;
        // once AllStrategyTargetsSatisfied becomes true, stop widening immediately.
        bool tookPersistentDamage = bestVictoryKey.HasCentiPrecision
            ? bestVictoryKey.NetPersistentHpDeltaCenti < 0
            : bestVictoryKey.StrategicHpDeficit > 0;
        bool tookRawBattleHpLoss = bestVictoryKey.ProjectedBattleHpLost > 0;
        bool spentOptionalPaidPotion = bestVictoryKey.NetPotionOpportunityCostCenti > 0
            || bestVictoryKey.OptionalPaidPotionCount > 0;

        int maxPostVictoryTurn;
        if (unsatisfiedStrategyTargets)
        {
            int strategyMargin = hotPolicy.HasRelicTargets ? 5 : 4;
            int strategyFloor = hotPolicy.HasRelicTargets ? 10 : 9;
            maxPostVictoryTurn = Math.Max(bestVictoryKey.CombatEndedTurn + strategyMargin, strategyFloor);
        }
        else if (canHealOrGrow || tookPersistentDamage || spentOptionalPaidPotion)
        {
            maxPostVictoryTurn = Math.Max(bestVictoryKey.CombatEndedTurn + 4, 8);
        }
        else
        {
            int postVictoryMargin = tookRawBattleHpLoss
                ? ((hotPolicy.IsEliteEncounter || hotPolicy.IsBossEncounter) ? 3 : 2)
                : 1;
            maxPostVictoryTurn = bestVictoryKey.CombatEndedTurn + postVictoryMargin;
        }

        return nextTurnNumber > maxPostVictoryTurn;
    }
}
