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
    bool OnlyDeathRoutes,
    bool StopTargetReached,
    SearchBoundaryReason BoundaryReason,
    int SearchedTurns,
    int PrimaryIncumbentUpdates,
    HotWaveStats Stats,
    TimeSpan Elapsed,
    int Gen0,
    int Gen1,
    int Gen2,
    TimeSpan GcPause);

/// <summary>
/// Standalone Phase 4 Hot-Kernel search coordinator:
/// - Stage 1: Zero-potion baseline (Pass 1A Balanced Primary -> Pass 1B Wider Setup/Survival Refinement -> Pass 1C No-Victory Offense fallback);
/// - Stage 2: Smart Allowed-Potion stage (Pass 2A Balanced potion-bucket-focused -> Pass 2B Setup/Survival potion refinement) sharing Stage 1's tightened Incumbent bound.
/// Reuses a single <see cref="HotNodeArena"/>, <see cref="PackedStateArena"/>, <see cref="HotTranspositionTable"/>
/// (O(1) generation epoch reset), and <see cref="HotParallelExpander"/> across passes.
/// Standalone Hot-Kernel decision layer with zero legacy simulator or backend dependencies.
/// </summary>
internal static class HotSearchCoordinator
{
    private const int MaxSearchTurns = 28;
    private const int StandPatTopK = 16;

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
        short[] rootPotionCosts = HotPotionPolicy.BuildRootSlotCosts(root, in rootState, rootBundle.Rules);
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
        short[] rootPotionCosts = HotPotionPolicy.BuildRootSlotCosts(root, in rootState, rootBundle.Rules);
        HotSearchPolicy hotPolicy = new(root, battleDamage, policy, rootBundle.Rules, in rootState, rootPotionCosts);
        HotEvalLookup evalLookup = new(rootBundle.Rules);

        long softBudgetMs = policy.BudgetOverrideMilliseconds ?? policy.Profile.SoftTimeBudgetMilliseconds;
        HotSearchBudget budget = new(requestClock, policy.Profile.MaxExpandedNodes, softBudgetMs, cancellationToken);
        int dop = ResolveEffectiveDop(policy);

        using HotNodeArena nodeArena = new();
        using PackedStateArena mainStateArena = new();
        using HotTranspositionTable tt = new(hotPolicy.TranspositionEntryLimit);
        using HotBeamRetention retention = new();
        using HotParallelExpander expander = new(dop);

        bool disableAllPotions = hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Disabled && !hotPolicy.HasForcedDirectives;
        bool onlyForced = hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Disabled && hotPolicy.HasForcedDirectives;
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
            mainStateArena,
            tt,
            retention,
            expander,
            beamWidth,
            maxNodesForPass: policy.Profile.MaxExpandedNodes,
            disableAllPotions: disableAllPotions,
            onlyForcedPotions: onlyForced,
            maxPotionUses: -1,
            disableTt: disableTt,
            evalMode: HotEvalMode.Balanced,
            preferPotionBuckets: false,
            baselineForEligibility: null,
            ref incumbent,
            progressCallback: null,
            phaseLabel: "正在搜索路线",
            reviewedOffset: 0);

        int potionHpRequired = pass.BestKey.OptionalPotionStrategicCost;
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
            TranspositionCount: tt.ActiveEntryCount,
            TranspositionLimitBypasses: tt.LimitBypasses,
            StandPatCacheCount: expander.StandPatCacheCount,
            SearchedTurns: pass.SearchedTurns,
            BoundaryReason: pass.BoundaryReason,
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

        HotRouteKey passBestKey = pass.BestKey;
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
        return result;
    }

    public static SolverResult Solve(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback = null)
    {
        Stopwatch requestClock = Stopwatch.StartNew();

        HotRootBundle rootBundle = root.HotRoot;
        CombatHotState rootState = rootBundle.RootState;
        short[] rootPotionCosts = HotPotionPolicy.BuildRootSlotCosts(root, in rootState, rootBundle.Rules);
        HotSearchPolicy hotPolicy = new(root, battleDamage, policy, rootBundle.Rules, in rootState, rootPotionCosts);
        HotEvalLookup evalLookup = new(rootBundle.Rules);

        long softBudgetMs = policy.BudgetOverrideMilliseconds ?? policy.Profile.SoftTimeBudgetMilliseconds;
        int totalNodeLimit = policy.Profile.MaxExpandedNodes;
        HotSearchBudget budget = new(requestClock, totalNodeLimit, softBudgetMs, cancellationToken);
        int dop = ResolveEffectiveDop(policy);
        bool disableTt = (policy.TranspositionPruningDisabledMask & 1) != 0;

        using HotNodeArena nodeArena = new();
        using PackedStateArena mainStateArena = new();
        using HotTranspositionTable tt = new(hotPolicy.TranspositionEntryLimit);
        using HotBeamRetention retention = new();
        using HotParallelExpander expander = new(dop);

        HotIncumbentBound sharedIncumbent = HotIncumbentBound.None;

        int baseBeamWidth = ResolveInitialBeamWidth(policy.Profile.BeamWidth, in hotPolicy);
        bool smartMode = hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Smart;
        bool stage1DisablePotions = smartMode && !hotPolicy.HasForcedDirectives
            || (hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Disabled && !hotPolicy.HasForcedDirectives);
        bool stage1OnlyForced = hotPolicy.HasForcedDirectives && (smartMode || hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Disabled);

        long totalExpanded = 0;
        long totalTransitions = 0;
        long totalChoices = 0;
        long totalAllocated = 0;
        int totalGen0 = 0, totalGen1 = 0, totalGen2 = 0;
        TimeSpan totalGcPause = TimeSpan.Zero;
        int passCount = 0;

        // =========================================================================
        // STAGE 1: ZERO-POTION BASELINE (or configured Disabled/Allow/Require policy)
        // =========================================================================

        // ---- PASS 1A: Primary Beam (Balanced) ----
        HotPassOutcome pass1A = RunBeamPass(
            rootBundle,
            in hotPolicy,
            in evalLookup,
            rootPotionCosts,
            policy.Profile,
            budget,
            nodeArena,
            mainStateArena,
            tt,
            retention,
            expander,
            beamWidth: baseBeamWidth,
            maxNodesForPass: totalNodeLimit,
            disableAllPotions: stage1DisablePotions,
            onlyForcedPotions: stage1OnlyForced,
            maxPotionUses: -1,
            disableTt: disableTt,
            evalMode: HotEvalMode.Balanced,
            preferPotionBuckets: false,
            baselineForEligibility: null,
            ref sharedIncumbent,
            progressCallback: progressCallback,
            phaseLabel: "正在搜索主路线",
            reviewedOffset: 0);

        AccumulatePassTotals(pass1A,
            ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
            ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

        HotPassOutcome bestPass = pass1A;
        HotRouteKey bestPassKey = pass1A.BestKey;
        HotRouteKey baselineKey = pass1A.BestKey;
        int potionHpSaved = 0;
        int potionBranchesRejected = 0;
        SolverResult? materializedBest = null;

        // ---- PASS 1B: Wider Setup & Survival 0-Potion Refinement ----
        bool runPass1B = !bestPass.StopTargetReached
            && !budget.IsCanceled
            && !budget.IsTimeExpired
            && totalExpanded < totalNodeLimit
            && (!bestPass.HasVictory
                || (!HotRouteQuality.MeetsVictoryStopTarget(in bestPassKey, in hotPolicy)
                    && requestClock.ElapsedMilliseconds < (softBudgetMs * 35) / 100));

        if (runPass1B)
        {
            materializedBest = MaterializePassResult(
                root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                nodeArena, in bestPass, tt, expander, totalExpanded, totalTransitions, totalChoices,
                totalAllocated, requestClock.Elapsed, potionHpSaved: 0, potionBranchesRejected: 0);

            ResetSearchStateForNextPass(nodeArena, mainStateArena, tt, expander);
            int remainingNodes = (int)Math.Max(1, totalNodeLimit - totalExpanded);
            int setupBeamWidth = !bestPass.HasVictory
                ? Math.Clamp(baseBeamWidth * 2, 120, 160)
                : Math.Clamp(baseBeamWidth * 2, 100, 140);

            HotPassOutcome pass1B = RunBeamPass(
                rootBundle,
                in hotPolicy,
                in evalLookup,
                rootPotionCosts,
                policy.Profile,
                budget,
                nodeArena,
                mainStateArena,
                tt,
                retention,
                expander,
                beamWidth: setupBeamWidth,
                maxNodesForPass: remainingNodes,
                disableAllPotions: stage1DisablePotions,
                onlyForcedPotions: stage1OnlyForced,
                maxPotionUses: -1,
                disableTt: disableTt,
                evalMode: HotEvalMode.SetupAndSurvival,
                preferPotionBuckets: false,
                baselineForEligibility: null,
                ref sharedIncumbent,
                progressCallback: progressCallback,
                phaseLabel: "正在精炼主路线",
                reviewedOffset: totalExpanded);

            AccumulatePassTotals(pass1B,
                ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

            HotRouteKey pass1BKey = pass1B.BestKey;
            if (pass1B.HasBest
                && HotRouteQuality.IsBetter(in pass1BKey, in bestPassKey, in hotPolicy, includeOptionalPotionCost: true))
            {
                bestPass = pass1B;
                bestPassKey = pass1BKey;
                baselineKey = pass1BKey;
                materializedBest = MaterializePassResult(
                    root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                    nodeArena, in pass1B, tt, expander, totalExpanded, totalTransitions, totalChoices,
                    totalAllocated, requestClock.Elapsed, potionHpSaved: 0, potionBranchesRejected: 0);
            }
        }

        // ---- PASS 1C: No-Victory Offense Escalation (ONLY if Stage 1 still has NO victory) ----
        bool runPass1C = !bestPass.HasVictory
            && !budget.IsCanceled
            && !budget.IsTimeExpired
            && totalExpanded < totalNodeLimit
            && requestClock.ElapsedMilliseconds < (softBudgetMs * 35) / 100;

        if (runPass1C)
        {
            if (materializedBest == null)
            {
                materializedBest = MaterializePassResult(
                    root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                    nodeArena, in bestPass, tt, expander, totalExpanded, totalTransitions, totalChoices,
                    totalAllocated, requestClock.Elapsed, potionHpSaved: 0, potionBranchesRejected: 0);
            }

            ResetSearchStateForNextPass(nodeArena, mainStateArena, tt, expander);
            int remainingNodes = (int)Math.Max(1, totalNodeLimit - totalExpanded);
            int offenseBeamWidth = Math.Clamp(baseBeamWidth * 2, 120, 160);

            HotPassOutcome pass1C = RunBeamPass(
                rootBundle,
                in hotPolicy,
                in evalLookup,
                rootPotionCosts,
                policy.Profile,
                budget,
                nodeArena,
                mainStateArena,
                tt,
                retention,
                expander,
                beamWidth: offenseBeamWidth,
                maxNodesForPass: remainingNodes,
                disableAllPotions: stage1DisablePotions,
                onlyForcedPotions: stage1OnlyForced,
                maxPotionUses: -1,
                disableTt: disableTt,
                evalMode: HotEvalMode.Offense,
                preferPotionBuckets: false,
                baselineForEligibility: null,
                ref sharedIncumbent,
                progressCallback: progressCallback,
                phaseLabel: "正在强化攻势搜索",
                reviewedOffset: totalExpanded);

            AccumulatePassTotals(pass1C,
                ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

            HotRouteKey pass1CKey = pass1C.BestKey;
            if (pass1C.HasBest
                && HotRouteQuality.IsBetter(in pass1CKey, in bestPassKey, in hotPolicy, includeOptionalPotionCost: true))
            {
                bestPass = pass1C;
                bestPassKey = pass1CKey;
                baselineKey = pass1CKey;
                materializedBest = MaterializePassResult(
                    root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                    nodeArena, in pass1C, tt, expander, totalExpanded, totalTransitions, totalChoices,
                    totalAllocated, requestClock.Elapsed, potionHpSaved: 0, potionBranchesRejected: 0);
            }
        }

        // =========================================================================
        // STAGE 2: SMART ALLOWED-POTION PASSES (only when potions can beat Stage 1!)
        // =========================================================================
        bool runStage2 = smartMode
            && !bestPass.StopTargetReached
            && !budget.IsTimeExpired
            && !budget.IsCanceled
            && totalExpanded < totalNodeLimit
            && HotPotionPolicy.ShouldRunSmartPotionPass(in baselineKey, in hotPolicy);

        if (runStage2)
        {
            if (materializedBest == null)
            {
                materializedBest = MaterializePassResult(
                    root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                    nodeArena, in bestPass, tt, expander, totalExpanded, totalTransitions, totalChoices,
                    totalAllocated, requestClock.Elapsed, potionHpSaved: 0, potionBranchesRejected: 0);
            }

            // ---- PASS 2A: Smart Allowed-Potion Beam (Balanced, potion-bucket-focused!) ----
            ResetSearchStateForNextPass(nodeArena, mainStateArena, tt, expander);
            int remainingNodes = (int)Math.Max(1, totalNodeLimit - totalExpanded);
            int potionBeamWidth = Math.Clamp((baseBeamWidth * 3) / 2, baseBeamWidth, 100);

            HotPassOutcome pass2A = RunBeamPass(
                rootBundle,
                in hotPolicy,
                in evalLookup,
                rootPotionCosts,
                policy.Profile,
                budget,
                nodeArena,
                mainStateArena,
                tt,
                retention,
                expander,
                beamWidth: potionBeamWidth,
                maxNodesForPass: remainingNodes,
                disableAllPotions: false,
                onlyForcedPotions: false,
                maxPotionUses: -1,
                disableTt: disableTt,
                evalMode: HotEvalMode.Balanced,
                preferPotionBuckets: true,
                baselineForEligibility: baselineKey,
                ref sharedIncumbent,
                progressCallback: progressCallback,
                phaseLabel: "正在评估药水路线",
                reviewedOffset: totalExpanded);

            AccumulatePassTotals(pass2A,
                ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

            HotRouteKey pass2AKey = pass2A.BestKey;
            bool pass2AEligible = pass2A.HasBest
                && HotPotionPolicy.IsEligibleFinalRoute(
                    in pass2AKey,
                    baselineKey,
                    anyRouteWon: bestPass.HasVictory || pass2A.HasVictory,
                    in hotPolicy);

            if (pass2AEligible
                && HotRouteQuality.IsBetter(in pass2AKey, in bestPassKey, in hotPolicy, includeOptionalPotionCost: true))
            {
                potionHpSaved = pass2AKey.ExplicitPotionCount > hotPolicy.ForcedDirectiveCount
                    ? HotPotionPolicy.HpSaved(baselineKey.StrategicHpDeficit, pass2AKey.StrategicHpDeficit)
                    : 0;
                bestPass = pass2A;
                bestPassKey = pass2AKey;
                materializedBest = MaterializePassResult(
                    root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                    nodeArena, in pass2A, tt, expander, totalExpanded, totalTransitions, totalChoices,
                    totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);
            }
            else if (pass2A.HasBest && pass2AKey.ExplicitPotionCount > hotPolicy.ForcedDirectiveCount)
            {
                potionBranchesRejected++;
            }

            // ---- PASS 2B: Smart Potion + Setup/Survival Refinement ----
            bool runPass2B = !bestPass.StopTargetReached
                && !budget.IsCanceled
                && !budget.IsTimeExpired
                && totalExpanded < totalNodeLimit
                && (!bestPass.HasVictory
                    || (bestPassKey.StrategicHpDeficit > 12
                        && !HotRouteQuality.MeetsVictoryStopTarget(in bestPassKey, in hotPolicy)
                        && requestClock.ElapsedMilliseconds < (softBudgetMs * 40) / 100));

            if (runPass2B)
            {
                ResetSearchStateForNextPass(nodeArena, mainStateArena, tt, expander);
                int remaining2B = (int)Math.Max(1, totalNodeLimit - totalExpanded);

                HotPassOutcome pass2B = RunBeamPass(
                    rootBundle,
                    in hotPolicy,
                    in evalLookup,
                    rootPotionCosts,
                    policy.Profile,
                    budget,
                    nodeArena,
                    mainStateArena,
                    tt,
                    retention,
                    expander,
                    beamWidth: potionBeamWidth,
                    maxNodesForPass: remaining2B,
                    disableAllPotions: false,
                    onlyForcedPotions: false,
                    maxPotionUses: -1,
                    disableTt: disableTt,
                    evalMode: HotEvalMode.SetupAndSurvival,
                    preferPotionBuckets: true,
                    baselineForEligibility: baselineKey,
                    ref sharedIncumbent,
                    progressCallback: progressCallback,
                    phaseLabel: "正在精炼药水路线",
                    reviewedOffset: totalExpanded);

                AccumulatePassTotals(pass2B,
                    ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                    ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

                HotRouteKey pass2BKey = pass2B.BestKey;
                bool pass2BEligible = pass2B.HasBest
                    && HotPotionPolicy.IsEligibleFinalRoute(
                        in pass2BKey,
                        baselineKey,
                        anyRouteWon: bestPass.HasVictory || pass2B.HasVictory,
                        in hotPolicy);

                if (pass2BEligible
                    && HotRouteQuality.IsBetter(in pass2BKey, in bestPassKey, in hotPolicy, includeOptionalPotionCost: true))
                {
                    potionHpSaved = pass2BKey.ExplicitPotionCount > hotPolicy.ForcedDirectiveCount
                        ? HotPotionPolicy.HpSaved(baselineKey.StrategicHpDeficit, pass2BKey.StrategicHpDeficit)
                        : 0;
                    bestPass = pass2B;
                    bestPassKey = pass2BKey;
                    materializedBest = MaterializePassResult(
                        root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                        nodeArena, in pass2B, tt, expander, totalExpanded, totalTransitions, totalChoices,
                        totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);
                }
                else if (pass2B.HasBest && pass2BKey.ExplicitPotionCount > hotPolicy.ForcedDirectiveCount)
                {
                    potionBranchesRejected++;
                }
            }
        }

        SolverResult finalResult = materializedBest ?? MaterializePassResult(
            root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
            nodeArena, in bestPass, tt, expander, totalExpanded, totalTransitions, totalChoices,
            totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);

        // Stamp aggregate request-level telemetry cleanly
        finalResult.TotalExpandedNodes = totalExpanded;
        finalResult.TotalTransitionCount = totalTransitions;
        finalResult.TotalChoiceBranchesEvaluated = totalChoices;
        finalResult.TotalWorkerAllocatedBytes = totalAllocated;
        finalResult.TotalGen0Collections = totalGen0;
        finalResult.TotalGen1Collections = totalGen1;
        finalResult.TotalGen2Collections = totalGen2;
        finalResult.TotalGcPauseDuration = totalGcPause;
        finalResult.TotalSearchElapsed = requestClock.Elapsed;
        finalResult.SingleSessionSearch = passCount <= 1;

        return finalResult;
    }

    private static HotPassOutcome RunBeamPass(
        HotRootBundle rootBundle,
        in HotSearchPolicy hotPolicy,
        in HotEvalLookup evalLookup,
        short[] rootPotionCosts,
        SolverSearchProfile profile,
        HotSearchBudget budget,
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotTranspositionTable tt,
        HotBeamRetention retention,
        HotParallelExpander expander,
        int beamWidth,
        int maxNodesForPass,
        bool disableAllPotions,
        bool onlyForcedPotions,
        int maxPotionUses,
        bool disableTt,
        HotEvalMode evalMode,
        bool preferPotionBuckets,
        HotRouteKey? baselineForEligibility,
        ref HotIncumbentBound incumbent,
        Action<SolverProgress>? progressCallback,
        string phaseLabel,
        long reviewedOffset)
    {
        Stopwatch passClock = Stopwatch.StartNew();
        int gen0Start = GC.CollectionCount(0);
        int gen1Start = GC.CollectionCount(1);
        int gen2Start = GC.CollectionCount(2);
        TimeSpan gcPauseStart = GC.GetTotalPauseDuration();

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
            potionStrategicCost: 0,
            actionCount: 0,
            evalMode: evalMode,
            out long rootScore,
            out int rootSetup,
            out byte rootPlayableHand);

        HotSearchNode rootNode = new()
        {
            Hash = rootHash,
            Score = rootScore,
            Cold = rootCold,
            StateHandle = rootHandle,
            ParentId = -1,
            ChoiceOffset = -1,
            HpLost = rootSummary.HpLost,
            HpRecovered = rootSummary.HpRecovered,
            PlayerHp = rootSummary.PlayerHp,
            EnemyHp = rootSummary.EnemyHpSum,
            SetupScore = rootSetup,
            PlayerMaxHpDelta = (short)Math.Clamp(hotPolicy.InitialPlayerMaxHp - rootSummary.PlayerMaxHp, 0, short.MaxValue),
            PlayerBlock = (short)Math.Clamp(rootSummary.PlayerBlock, 0, short.MaxValue),
            OutstandingStolen = (short)Math.Clamp(rootSummary.OutstandingStolen, 0, short.MaxValue),
            GrowthHpCredit = (short)Math.Clamp(rootSummary.GrowthHpCredit, short.MinValue, short.MaxValue),
            PotionStrategicCost = 0,
            DeathSaveHpRestored = (short)Math.Clamp(rootSummary.DeathSaveHpRestored, 0, short.MaxValue),
            PotionMask = 0,
            ActionCount = 0,
            GrowthRewardCount = (ushort)Math.Clamp(rootSummary.GrowthRewardCount, 0, ushort.MaxValue),
            Action = default,
            ChoiceCount = 0,
            PotionCount = 0,
            ExplicitPotionCount = 0,
            AmbergrisCount = 0,
            Energy = (byte)Math.Clamp(rootSummary.Energy, 0, byte.MaxValue),
            Turn = (byte)Math.Clamp(rootSummary.Turn, 0, byte.MaxValue),
            ActionsThisTurn = 0,
            AliveEnemyCount = rootSummary.AliveEnemyCount,
            RevivingEnemyCount = rootSummary.RevivingEnemyCount,
            PlayableHandCount = rootPlayableHand,
            DeathSaveUseCount = rootSummary.DeathSaveUseCount,
            Outcome = rootSummary.Outcome,
            Flags = HotNodeFlags.IsRoot,
        };

        int rootNodeId = nodeArena.AddNode(in rootNode);
        HotRouteKey rootKey = HotRouteQuality.Capture(in rootNode, in hotPolicy);

        HotWaveExpansionConfig waveConfig = new(
            disableAllPotions: disableAllPotions,
            onlyForcedPotions: onlyForcedPotions,
            maxPotionUses: maxPotionUses,
            maxCardBranchesPerNode: profile.MaxCardBranchesPerNode,
            maxHandChoiceBranches: profile.MaxHandChoiceBranchesPerAction,
            maxPileChoiceBranches: profile.MaxPileChoiceBranchesPerAction,
            evalMode: evalMode);

        List<int> currentTurnFrontier = new(beamWidth) { rootNodeId };
        List<int> activeFrontier = new(beamWidth);
        List<int> nextStepCandidates = new(beamWidth * 8);
        List<int> nextTurnCandidates = new(beamWidth * 4);
        List<int> victories = new(64);
        int[] pruneOutBuf = ArrayPool<int>.Shared.Rent(Math.Max(beamWidth * 2, 128));

        HotWaveStats stats = default;
        int bestVictoryId = -1;
        HotRouteKey bestVictoryKey = default;
        bool hasVictory = false;

        int bestOngoingId = -1;
        HotRouteKey bestOngoingKey = default;
        bool hasOngoing = false;

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
        int bestSeenSetup = rootSetup;
        int turnsWithoutProgress = 0;

        int midTurnBeamWidth = beamWidth;

        try
        {
            for (int turnLayer = 0; turnLayer < MaxSearchTurns; turnLayer++)
            {
                if (currentTurnFrontier.Count == 0 || stopTargetReached)
                    break;
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
                activeFrontier.Clear();
                activeFrontier.AddRange(currentTurnFrontier);
                nextTurnCandidates.Clear();

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
                        baselineForEligibility,
                        ref bestVictoryId,
                        ref bestVictoryKey,
                        ref hasVictory,
                        ref incumbent,
                        ref incumbentUpdates,
                        ref stopTargetReached);

                    if (stopTargetReached)
                        break;

                    UpdateBestOngoing(nextStepCandidates, nodeArena, in hotPolicy, ref bestOngoingId, ref bestOngoingKey, ref hasOngoing);
                    UpdateBestOngoing(nextTurnCandidates, nodeArena, in hotPolicy, ref bestOngoingId, ref bestOngoingKey, ref hasOngoing);

                    if (nextStepCandidates.Count == 0)
                    {
                        activeFrontier.Clear();
                        break;
                    }

                    if (pruneOutBuf.Length < midTurnBeamWidth)
                    {
                        ArrayPool<int>.Shared.Return(pruneOutBuf, clearArray: false);
                        pruneOutBuf = ArrayPool<int>.Shared.Rent(midTurnBeamWidth);
                    }

                    int kept = retention.PruneLayer(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nextStepCandidates),
                        nodeArena,
                        midTurnBeamWidth,
                        pruneOutBuf.AsSpan(0, midTurnBeamWidth),
                        isTurnBoundary: false,
                        preferPotionBuckets: preferPotionBuckets);

                    activeFrontier.Clear();
                    for (int i = 0; i < kept; i++)
                        activeFrontier.Add(pruneOutBuf[i]);

                    if (progressCallback != null
                        && budget.ElapsedMilliseconds - lastProgressReportMs >= HotSearchConstants.ProgressUiIntervalMilliseconds)
                    {
                        lastProgressReportMs = budget.ElapsedMilliseconds;
                        SolverInterimResult? interim = hasVictory
                            ? HotRouteQuality.ToInterimResult(in bestVictoryKey, in hotPolicy)
                            : (hasOngoing ? HotRouteQuality.ToInterimResult(in bestOngoingKey, in hotPolicy) : null);
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

                if (!stopTargetReached
                    && activeFrontier.Count > 0
                    && nextTurnCandidates.Count < beamWidth
                    && !budget.IsTimeExpired
                    && !budget.IsCanceled)
                {
                    int standPatCount = Math.Min(StandPatTopK, activeFrontier.Count);
                    victories.Clear();
                    expander.RunStandPatBatch(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(activeFrontier)[..standPatCount],
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
                        baselineForEligibility,
                        ref bestVictoryId,
                        ref bestVictoryKey,
                        ref hasVictory,
                        ref incumbent,
                        ref incumbentUpdates,
                        ref stopTargetReached);
                }

                if (stopTargetReached || boundaryReason is SearchBoundaryReason.TimeLimit or SearchBoundaryReason.NodeLimit)
                    break;

                if (nextTurnCandidates.Count == 0)
                    break;

                if (pruneOutBuf.Length < beamWidth)
                {
                    ArrayPool<int>.Shared.Return(pruneOutBuf, clearArray: false);
                    pruneOutBuf = ArrayPool<int>.Shared.Rent(beamWidth);
                }

                int nextKept = retention.PruneLayer(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nextTurnCandidates),
                    nodeArena,
                    beamWidth,
                    pruneOutBuf.AsSpan(0, beamWidth),
                    isTurnBoundary: true,
                    preferPotionBuckets: preferPotionBuckets);

                currentTurnFrontier.Clear();
                bool layerProgressed = false;
                for (int i = 0; i < nextKept; i++)
                {
                    int nid = pruneOutBuf[i];
                    currentTurnFrontier.Add(nid);
                    ref readonly HotSearchNode n = ref nodeArena.GetRef(nid);
                    int remEnemies = n.AliveEnemyCount + n.RevivingEnemyCount;
                    if (n.EnemyHp < bestSeenEnemyHp) { bestSeenEnemyHp = n.EnemyHp; layerProgressed = true; }
                    if (remEnemies < bestSeenAliveEnemies) { bestSeenAliveEnemies = remEnemies; layerProgressed = true; }
                    if (n.SetupScore > bestSeenSetup) { bestSeenSetup = n.SetupScore; layerProgressed = true; }
                }

                turnsWithoutProgress = layerProgressed ? 0 : turnsWithoutProgress + 1;
                if (turnsWithoutProgress >= HotSearchConstants.SetupValueHorizonTurns)
                {
                    boundaryReason = SearchBoundaryReason.TurnLimit;
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pruneOutBuf, clearArray: false);
        }

        int chosenId;
        HotRouteKey chosenKey;
        bool onlyDeathRoutes;

        if (hasVictory)
        {
            chosenId = bestVictoryId;
            chosenKey = bestVictoryKey;
            onlyDeathRoutes = false;
        }
        else if (hasOngoing)
        {
            chosenId = bestOngoingId;
            chosenKey = bestOngoingKey;
            onlyDeathRoutes = false;
        }
        else
        {
            chosenId = bestFallbackId;
            chosenKey = bestFallbackKey;
            onlyDeathRoutes = hasBestFallback && !bestFallbackKey.Survives;
        }

        passClock.Stop();
        return new HotPassOutcome(
            BestNodeId: chosenId,
            BestKey: chosenKey,
            HasBest: chosenId >= 0,
            HasVictory: hasVictory,
            OnlyDeathRoutes: onlyDeathRoutes,
            StopTargetReached: stopTargetReached,
            BoundaryReason: boundaryReason,
            SearchedTurns: Math.Max(1, searchedTurns),
            PrimaryIncumbentUpdates: incumbentUpdates,
            Stats: stats,
            Elapsed: passClock.Elapsed,
            Gen0: Math.Max(0, GC.CollectionCount(0) - gen0Start),
            Gen1: Math.Max(0, GC.CollectionCount(1) - gen1Start),
            Gen2: Math.Max(0, GC.CollectionCount(2) - gen2Start),
            GcPause: GC.GetTotalPauseDuration() - gcPauseStart);
    }

    private static void ProcessVictories(
        List<int> victories,
        HotNodeArena nodeArena,
        in HotSearchPolicy hotPolicy,
        HotRouteKey? baselineForEligibility,
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

            if (!HotPotionPolicy.IsEligibleFinalRoute(in key, baselineForEligibility, anyRouteWon: true, in hotPolicy))
                continue;

            if (!hasVictory || HotRouteQuality.IsBetter(in key, in bestVictoryKey, in hotPolicy, includeOptionalPotionCost: true))
            {
                hasVictory = true;
                bestVictoryId = nid;
                bestVictoryKey = key;

                if (!hotPolicy.HasAnyHealOrUnknownGeneration
                    && !hotPolicy.HasGrowthTargets
                    && !hotPolicy.HasForcedDirectives
                    && hotPolicy.TheftPolicy != SolverTheftPolicy.PreserveResources
                    && key.DeathSaveUseCount == 0)
                {
                    bool zHas = incumbent.HasCertifiedZeroPotionBound;
                    int zHp = incumbent.BestZeroPotionHpLost;
                    int zTurn = incumbent.BestZeroPotionEndTurn;
                    bool oHas = incumbent.HasCertifiedOverallBound;
                    int oCost = incumbent.BestOverallCost;
                    int oTurn = incumbent.BestOverallEndTurn;
                    bool changed = false;

                    if (key.ExplicitPotionCount == 0)
                    {
                        if (!zHas || key.StrategicHpDeficit < zHp || (key.StrategicHpDeficit == zHp && key.CombatEndedTurn < zTurn))
                        {
                            zHas = true;
                            zHp = key.StrategicHpDeficit;
                            zTurn = key.CombatEndedTurn;
                            changed = true;
                        }
                    }

                    int totalCost = key.StrategicHpDeficit + key.OptionalPotionStrategicCost;
                    if (!oHas || totalCost < oCost || (totalCost == oCost && key.CombatEndedTurn < oTurn))
                    {
                        oHas = true;
                        oCost = totalCost;
                        oTurn = key.CombatEndedTurn;
                        changed = true;
                    }

                    if (changed)
                    {
                        incumbent = new HotIncumbentBound(zHas, zHp, zTurn, oHas, oCost, oTurn);
                        incumbentUpdates++;
                    }
                }

                if (HotRouteQuality.MeetsVictoryStopTarget(in key, in hotPolicy))
                {
                    stopTargetReached = true;
                    break;
                }
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
        HotParallelExpander expander,
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
            TranspositionCount: tt.ActiveEntryCount,
            TranspositionLimitBypasses: tt.LimitBypasses,
            StandPatCacheCount: expander.StandPatCacheCount,
            SearchedTurns: pass.SearchedTurns,
            BoundaryReason: pass.BoundaryReason,
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
        ref int passCount)
    {
        totalExpanded += pass.Stats.ExpandedParents;
        totalTransitions += pass.Stats.Transitions;
        totalChoices += pass.Stats.ChoiceBranches;
        totalAllocated += pass.Stats.WorkerAllocatedBytes;
        totalGen0 += pass.Gen0;
        totalGen1 += pass.Gen1;
        totalGen2 += pass.Gen2;
        totalGcPause += pass.GcPause;
        passCount++;
    }

    private static void ResetSearchStateForNextPass(
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotTranspositionTable tt,
        HotParallelExpander expander)
    {
        nodeArena.Reset();
        mainStateArena.Reset();
        tt.ResetForNextPass();
        expander.ClearStandPatCache();
    }

    private static int ResolveInitialBeamWidth(int configuredBeamWidth, in HotSearchPolicy policy)
    {
        int width = Math.Max(4, configuredBeamWidth);
        if (policy.IsActEndingBoss && width < 45)
            width = 45;
        return width;
    }

    private static int ResolveEffectiveDop(SearchPolicySnapshot policy)
    {
        if (policy.DetailedDiagnostics)
            return 1;
        int dop = Math.Clamp(policy.MaxDegreeOfParallelism, 1, Math.Min(HotSearchConstants.MaximumSearchMaxDegreeOfParallelism, Environment.ProcessorCount));
        return Math.Max(1, dop);
    }
}
