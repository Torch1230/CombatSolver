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
    HotWaveStats Stats,
    TimeSpan Elapsed,
    int Gen0,
    int Gen1,
    int Gen2,
    TimeSpan GcPause);

/// <summary>
/// Unified Hot-Kernel search coordinator built on ONE coherent economic objective
/// (<c>V(route) = NetPersistentHpDelta - NetPotionOpportunityCost</c>, zero post-hoc Smart/Ambergris vetoes):
/// - Supports depth-0 <see cref="HotActionKind.TurnSetup"/> root expansion when <c>policy.IncludeTurnSetup</c> is active;
/// - Stage 1 allows 0-cost potions directly (Token / PetrifiedToad Rock / combat-generated / Forced) and reserves ~40%
///   of node and time budget for Stage 2 whenever optional paid potions exist;
/// - Stage 2 triggers via an exact sound upper bound and shares the tie-break-safe <see cref="HotIncumbentBound"/>;
/// - Enforces hard potion constraints explicitly (throwing <see cref="PotionPolicyUnsatisfiedException"/> when impossible)
///   and contains <see cref="HotCapacityExceededException"/> gracefully without crashing combat.
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
        using PackedStateArena mainStateArena = new();
        using HotTranspositionTable tt = new(hotPolicy.TranspositionEntryLimit);
        using HotBeamRetention retention = new();
        using HotParallelExpander expander = new(dop);

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
            mainStateArena,
            tt,
            retention,
            expander,
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
            reviewedOffset: 0);

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
        short[] rootPotionCosts = HotPotionPolicy.BuildRootSlotCosts(
            root, in rootState, rootBundle.Rules, policy.PotionStrategy.Thresholds);
        HotSearchPolicy hotPolicy = new(root, battleDamage, policy, rootBundle.Rules, in rootState, rootPotionCosts);
        HotEvalLookup evalLookup = new(rootBundle.Rules);

        long softBudgetMs = Math.Max(1L, policy.BudgetOverrideMilliseconds ?? policy.Profile.SoftTimeBudgetMilliseconds);
        int totalNodeLimit = Math.Max(1, policy.Profile.MaxExpandedNodes);
        HotSearchBudget fullBudget = new(requestClock, totalNodeLimit, softBudgetMs, cancellationToken);
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
        bool isDisabledMode = hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Disabled;

        // Stage 1 allows 0-cost potions directly (Token / PetrifiedToad Rock / combat-generated / Forced)!
        bool stage1AllowPaidOptional = !smartMode && !isDisabledMode;
        bool stage1AllowZeroCostOptional = !isDisabledMode;
        bool stage1AllowForced = hotPolicy.HasForcedDirectives;

        // Reserve ~40% of node and time budgets for Stage 2 whenever optional paid potions exist in Smart mode.
        bool reserveForStage2 = smartMode && hotPolicy.PaidOptionalPotionCount > 0;
        int stage1ScaledNodes = (int)(((long)totalNodeLimit * 60L) / 100L);
        int stage1NodeCap = reserveForStage2
            ? Math.Min(totalNodeLimit, Math.Max(HotSearchConstants.MinimumTurnLayerExpandedNodes, stage1ScaledNodes))
            : totalNodeLimit;
        long stage1TimeCapMs = reserveForStage2
            ? Math.Min(softBudgetMs, Math.Max(50L, (softBudgetMs * 60L) / 100L))
            : softBudgetMs;
        HotSearchBudget stage1Budget = reserveForStage2
            ? new HotSearchBudget(requestClock, stage1NodeCap, stage1TimeCapMs, cancellationToken)
            : fullBudget;

        long totalExpanded = 0;
        long totalTransitions = 0;
        long totalChoices = 0;
        long totalAllocated = 0;
        int totalGen0 = 0, totalGen1 = 0, totalGen2 = 0;
        TimeSpan totalGcPause = TimeSpan.Zero;
        int passCount = 0;

        // =========================================================================
        // STAGE 1: ZERO-PAID-POTION BASELINE (allows 0-cost & Forced potions directly!)
        // =========================================================================

        // ---- PASS 1A: Primary Beam (Balanced) ----
        HotPassOutcome pass1A = RunBeamPass(
            rootBundle,
            in hotPolicy,
            in evalLookup,
            rootPotionCosts,
            policy.Profile,
            stage1Budget,
            nodeArena,
            mainStateArena,
            tt,
            retention,
            expander,
            beamWidth: baseBeamWidth,
            maxNodesForPass: stage1NodeCap,
            allowPaidOptionalPotions: stage1AllowPaidOptional,
            allowZeroCostOptionalPotions: stage1AllowZeroCostOptional,
            allowForcedPotions: stage1AllowForced,
            maxPotionUses: -1,
            disableTt: disableTt,
            evalMode: HotEvalMode.Balanced,
            preferPotionBuckets: stage1AllowPaidOptional && hotPolicy.PaidOptionalPotionCount > 0,
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

        // ---- PASS 1B: Wider Setup & Survival Refinement ----
        bool runPass1B = !bestPass.StopTargetReached
            && !stage1Budget.IsCanceled
            && !stage1Budget.IsTimeExpired
            && totalExpanded < stage1NodeCap
            && (!bestPass.HasVictory
                || (!HotRouteQuality.MeetsVictoryStopTarget(in bestPassKey, in hotPolicy)
                    && requestClock.ElapsedMilliseconds < (stage1TimeCapMs * 55) / 100));

        if (runPass1B)
        {
            materializedBest = MaterializePassResult(
                root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                nodeArena, in bestPass, tt, expander, totalExpanded, totalTransitions, totalChoices,
                totalAllocated, requestClock.Elapsed, potionHpSaved: 0, potionBranchesRejected: 0);

            ResetSearchStateForNextPass(nodeArena, mainStateArena, tt, expander);
            int remainingNodes = (int)Math.Max(1, stage1NodeCap - totalExpanded);
            int setupBeamWidth = !bestPass.HasVictory
                ? ResolveSecondaryBeamWidth(baseBeamWidth, scaleNumerator: 2, scaleDenominator: 1, floorWidth: 120, defaultCap: 160)
                : ResolveSecondaryBeamWidth(baseBeamWidth, scaleNumerator: 2, scaleDenominator: 1, floorWidth: 100, defaultCap: 140);

            HotPassOutcome pass1B = RunBeamPass(
                rootBundle,
                in hotPolicy,
                in evalLookup,
                rootPotionCosts,
                policy.Profile,
                stage1Budget,
                nodeArena,
                mainStateArena,
                tt,
                retention,
                expander,
                beamWidth: setupBeamWidth,
                maxNodesForPass: remainingNodes,
                allowPaidOptionalPotions: stage1AllowPaidOptional,
                allowZeroCostOptionalPotions: stage1AllowZeroCostOptional,
                allowForcedPotions: stage1AllowForced,
                maxPotionUses: -1,
                disableTt: disableTt,
                evalMode: HotEvalMode.SetupAndSurvival,
                preferPotionBuckets: stage1AllowPaidOptional && hotPolicy.PaidOptionalPotionCount > 0,
                ref sharedIncumbent,
                progressCallback: progressCallback,
                phaseLabel: "正在精炼主路线",
                reviewedOffset: totalExpanded);

            AccumulatePassTotals(pass1B,
                ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

            HotRouteKey pass1BKey = pass1B.BestKey;
            if (IsBetterPassOutcome(in pass1B, in bestPass, in hotPolicy))
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
            && !stage1Budget.IsCanceled
            && !stage1Budget.IsTimeExpired
            && totalExpanded < stage1NodeCap
            && requestClock.ElapsedMilliseconds < (stage1TimeCapMs * 55) / 100;

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
            int remainingNodes = (int)Math.Max(1, stage1NodeCap - totalExpanded);
            int offenseBeamWidth = ResolveSecondaryBeamWidth(
                baseBeamWidth, scaleNumerator: 2, scaleDenominator: 1, floorWidth: 120, defaultCap: 160);

            HotPassOutcome pass1C = RunBeamPass(
                rootBundle,
                in hotPolicy,
                in evalLookup,
                rootPotionCosts,
                policy.Profile,
                stage1Budget,
                nodeArena,
                mainStateArena,
                tt,
                retention,
                expander,
                beamWidth: offenseBeamWidth,
                maxNodesForPass: remainingNodes,
                allowPaidOptionalPotions: stage1AllowPaidOptional,
                allowZeroCostOptionalPotions: stage1AllowZeroCostOptional,
                allowForcedPotions: stage1AllowForced,
                maxPotionUses: -1,
                disableTt: disableTt,
                evalMode: HotEvalMode.Offense,
                preferPotionBuckets: stage1AllowPaidOptional && hotPolicy.PaidOptionalPotionCount > 0,
                ref sharedIncumbent,
                progressCallback: progressCallback,
                phaseLabel: "正在强化攻势搜索",
                reviewedOffset: totalExpanded);

            AccumulatePassTotals(pass1C,
                ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

            HotRouteKey pass1CKey = pass1C.BestKey;
            if (IsBetterPassOutcome(in pass1C, in bestPass, in hotPolicy))
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
        // STAGE 2: SMART PAID-POTION PASSES (sound upper-bound gate, reserved 40% budget!)
        // NOTE: Do NOT blindly skip on bestPass.StopTargetReached — BloodPotion / FruitJuice
        // can strictly increase V even after a 0-damage Stage 1 win!
        // =========================================================================
        bool runStage2 = smartMode
            && !fullBudget.IsTimeExpired
            && !fullBudget.IsCanceled
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

            // ---- PASS 2A: Smart Paid-Potion Beam (Balanced, potion-bucket & slot-diversity focused!) ----
            ResetSearchStateForNextPass(nodeArena, mainStateArena, tt, expander);
            int remainingNodes = (int)Math.Max(1, totalNodeLimit - totalExpanded);
            int potionBeamWidth = ResolveSecondaryBeamWidth(
                baseBeamWidth, scaleNumerator: 3, scaleDenominator: 2, floorWidth: baseBeamWidth, defaultCap: 120);

            HotPassOutcome pass2A = RunBeamPass(
                rootBundle,
                in hotPolicy,
                in evalLookup,
                rootPotionCosts,
                policy.Profile,
                fullBudget,
                nodeArena,
                mainStateArena,
                tt,
                retention,
                expander,
                beamWidth: potionBeamWidth,
                maxNodesForPass: remainingNodes,
                allowPaidOptionalPotions: true,
                allowZeroCostOptionalPotions: true,
                allowForcedPotions: stage1AllowForced,
                maxPotionUses: -1,
                disableTt: disableTt,
                evalMode: HotEvalMode.Balanced,
                preferPotionBuckets: true,
                ref sharedIncumbent,
                progressCallback: progressCallback,
                phaseLabel: "正在评估药水路线",
                reviewedOffset: totalExpanded);

            AccumulatePassTotals(pass2A,
                ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

            HotRouteKey pass2AKey = pass2A.BestKey;
            if (IsBetterPassOutcome(in pass2A, in bestPass, in hotPolicy))
            {
                potionHpSaved = pass2AKey.OptionalPaidPotionCount > 0
                    ? HotPotionPolicy.HpSavedWholeHp(in baselineKey, in pass2AKey)
                    : 0;
                bestPass = pass2A;
                bestPassKey = pass2AKey;
                materializedBest = MaterializePassResult(
                    root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                    nodeArena, in pass2A, tt, expander, totalExpanded, totalTransitions, totalChoices,
                    totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);
            }
            else if (pass2A.HasBest && pass2AKey.OptionalPaidPotionCount > 0)
            {
                potionBranchesRejected++;
            }

            // ---- PASS 2B: Smart Potion + Setup/Survival Refinement ----
            bool runPass2B = !pass2A.StopTargetReached
                && !fullBudget.IsCanceled
                && !fullBudget.IsTimeExpired
                && totalExpanded < totalNodeLimit
                && (!bestPass.HasVictory
                    || (HotPotionPolicy.CanAnyPaidPotionImproveOn(in bestPassKey, in hotPolicy)
                        && requestClock.ElapsedMilliseconds < (softBudgetMs * 80) / 100));

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
                    fullBudget,
                    nodeArena,
                    mainStateArena,
                    tt,
                    retention,
                    expander,
                    beamWidth: potionBeamWidth,
                    maxNodesForPass: remaining2B,
                    allowPaidOptionalPotions: true,
                    allowZeroCostOptionalPotions: true,
                    allowForcedPotions: stage1AllowForced,
                    maxPotionUses: -1,
                    disableTt: disableTt,
                    evalMode: HotEvalMode.SetupAndSurvival,
                    preferPotionBuckets: true,
                    ref sharedIncumbent,
                    progressCallback: progressCallback,
                    phaseLabel: "正在精炼药水路线",
                    reviewedOffset: totalExpanded);

                AccumulatePassTotals(pass2B,
                    ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                    ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

                HotRouteKey pass2BKey = pass2B.BestKey;
                if (IsBetterPassOutcome(in pass2B, in bestPass, in hotPolicy))
                {
                    potionHpSaved = pass2BKey.OptionalPaidPotionCount > 0
                        ? HotPotionPolicy.HpSavedWholeHp(in baselineKey, in pass2BKey)
                        : 0;
                    bestPass = pass2B;
                    bestPassKey = pass2BKey;
                    materializedBest = MaterializePassResult(
                        root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                        nodeArena, in pass2B, tt, expander, totalExpanded, totalTransitions, totalChoices,
                        totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);
                }
                else if (pass2B.HasBest && pass2BKey.OptionalPaidPotionCount > 0)
                {
                    potionBranchesRejected++;
                }
            }
        }

        ValidateHardPotionConstraints(in bestPassKey, bestPass.HasBest, in hotPolicy, policy.PotionStrategy);

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
        PackedStateArena mainStateArena,
        HotTranspositionTable tt,
        HotBeamRetention retention,
        HotParallelExpander expander,
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
            netPotionCostCenti: 0,
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
            PlayerMaxHpDelta = (short)Math.Clamp(hotPolicy.InitialPlayerMaxHp - rootSummary.PlayerMaxHp, short.MinValue, short.MaxValue),
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
            OptionalPaidPotionCount = 0,
            AmbergrisCount = 0,
            AlchemizeCount = rootSummary.AlchemizeCount,
            EmptyPotionSlots = rootSummary.EmptyPotionSlots,
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
            allowPaidOptionalPotions: allowPaidOptionalPotions,
            allowZeroCostOptionalPotions: allowZeroCostOptionalPotions,
            allowForcedPotions: allowForcedPotions,
            maxPotionUses: maxPotionUses,
            maxCardBranchesPerNode: profile.MaxCardBranchesPerNode,
            maxHandChoiceBranches: profile.MaxHandChoiceBranchesPerAction,
            maxPileChoiceBranches: profile.MaxPileChoiceBranchesPerAction,
            evalMode: evalMode);

        List<int> currentTurnFrontier = new(beamWidth) { rootNodeId };
        List<int> activeFrontier = new(beamWidth);
        List<int> nextStepCandidates = new(beamWidth * 8);
        List<int> nextTurnCandidates = new(beamWidth * 4);
        List<int> turnSetupEndedTurnNodes = [];
        List<int> victories = new(64);
        int[] pruneOutBuf = ArrayPool<int>.Shared.Rent(Math.Max(beamWidth * 2, 128));
        int[] standPatBuf = ArrayPool<int>.Shared.Rent(Math.Max(StandPatTopK, 32));

        HotWaveStats stats = default;
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
        int bestSeenSetup = rootSetup;
        int turnsWithoutProgress = 0;

        int midTurnBeamWidth = beamWidth;

        try
        {
            // ---- DEPTH-0 TURN SETUP EXPANSION (GamblingChip / ToolsOfTheTrade / HistoryCourse / Dread) ----
            if (hotPolicy.IncludeTurnSetup && HotCombatEngine.HasPendingTurnSetup(in rootState))
            {
                HotWaveExpansionConfig setupConfig = new(
                    allowPaidOptionalPotions: false,
                    allowZeroCostOptionalPotions: false,
                    allowForcedPotions: false,
                    maxPotionUses: -1,
                    maxCardBranchesPerNode: profile.MaxCardBranchesPerNode,
                    maxHandChoiceBranches: profile.MaxHandChoiceBranchesPerAction,
                    maxPileChoiceBranches: profile.MaxPileChoiceBranchesPerAction,
                    evalMode: evalMode,
                    allowEndTurn: false);

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
                    int setupKept = retention.PruneLayer(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nextStepCandidates),
                        nodeArena,
                        beamWidth,
                        pruneOutBuf.AsSpan(0, beamWidth),
                        isTurnBoundary: false,
                        preferPotionBuckets: false);
                    for (int i = 0; i < setupKept; i++)
                        currentTurnFrontier.Add(pruneOutBuf[i]);
                }
            }

            for (int turnLayer = 0; turnLayer < MaxSearchTurns; turnLayer++)
            {
                if ((currentTurnFrontier.Count == 0 && (turnLayer > 0 || turnSetupEndedTurnNodes.Count == 0)) || stopTargetReached)
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
                bool emergencyTurn0StandPat = !hasVictory
                    && !hasCompletedTurnOngoing
                    && turnLayer == 0
                    && activeFrontier.Count > 0
                    && !budget.IsCanceled;
                if (!stopTargetReached
                    && activeFrontier.Count > 0
                    && !budget.IsCanceled
                    && ((nextTurnCandidates.Count < beamWidth && !budget.IsTimeExpired) || emergencyTurn0StandPat))
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
                    if (!hasVictory)
                        boundaryReason = SearchBoundaryReason.TurnLimit;
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pruneOutBuf, clearArray: false);
            ArrayPool<int>.Shared.Return(standPatBuf, clearArray: false);
        }

        bool capacityBounded = stats.CapacityExceededDrops > 0;
        if (capacityBounded && !hasVictory && boundaryReason == SearchBoundaryReason.None)
            boundaryReason = SearchBoundaryReason.DynamicResolution;

        int chosenId;
        HotRouteKey chosenKey;
        bool onlyDeathRoutes;
        bool isMidTurnFallback;

        if (hasVictory)
        {
            chosenId = bestVictoryId;
            chosenKey = bestVictoryKey;
            onlyDeathRoutes = false;
            isMidTurnFallback = false;
        }
        else if (hasCompletedTurnOngoing)
        {
            chosenId = bestCompletedTurnOngoingId;
            chosenKey = bestCompletedTurnOngoingKey;
            onlyDeathRoutes = false;
            isMidTurnFallback = false;
        }
        else if (hasBestFallback)
        {
            // No surviving EndTurn route exists on this turn (e.g. every legal EndTurn is lethal).
            // Return the best terminal/EndTurn fallback with accurate OnlyDeathRoutes rather than a
            // truncated mid-turn node that omits EndTurn and triggers repeated PlanExhaustedWithoutEndTurn loops.
            chosenId = bestFallbackId;
            chosenKey = bestFallbackKey;
            onlyDeathRoutes = !bestFallbackKey.Survives;
            isMidTurnFallback = false;
        }
        else if (hasMidTurnOngoing)
        {
            // Last-resort fallback only when zero EndTurn/terminal nodes were materialized (e.g. capacity drop).
            chosenId = bestMidTurnOngoingId;
            chosenKey = bestMidTurnOngoingKey;
            onlyDeathRoutes = false;
            isMidTurnFallback = true;
        }
        else
        {
            chosenId = bestFallbackId;
            chosenKey = bestFallbackKey;
            onlyDeathRoutes = false;
            isMidTurnFallback = true;
        }

        passClock.Stop();
        return new HotPassOutcome(
            BestNodeId: chosenId,
            BestKey: chosenKey,
            HasBest: chosenId >= 0,
            HasVictory: hasVictory,
            HasCompletedTurnOngoing: hasCompletedTurnOngoing,
            CompletedOngoingTurn: hasCompletedTurnOngoing ? bestCompletedTurnOngoingTurn : -1,
            IsMidTurnFallback: isMidTurnFallback,
            OnlyDeathRoutes: onlyDeathRoutes,
            StopTargetReached: stopTargetReached,
            CapacityBounded: capacityBounded,
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
                    && !hotPolicy.HasGrowthTargets
                    && !hotPolicy.HasForcedDirectives
                    && hotPolicy.TheftPolicy != SolverTheftPolicy.PreserveResources
                    && key.DeathSaveUseCount == 0
                    && key.HasCentiPrecision)
                {
                    bool zHas = incumbent.HasCertifiedZeroPotionBound;
                    int zHpDelta = incumbent.BestZeroPotionHpDeltaCenti;
                    int zTurn = incumbent.BestZeroPotionEndTurn;
                    bool oHas = incumbent.HasCertifiedOverallBound;
                    long oValue = incumbent.BestOverallValueCenti;
                    int oHpDelta = incumbent.BestOverallHpDeltaCenti;
                    int oTurn = incumbent.BestOverallEndTurn;
                    bool changed = false;

                    if (key.OptionalPaidPotionCount == 0 && key.ExplicitPotionCount == 0)
                    {
                        if (!zHas
                            || key.NetPersistentHpDeltaCenti > zHpDelta
                            || (key.NetPersistentHpDeltaCenti == zHpDelta && key.CombatEndedTurn < zTurn))
                        {
                            zHas = true;
                            zHpDelta = key.NetPersistentHpDeltaCenti;
                            zTurn = key.CombatEndedTurn;
                            changed = true;
                        }
                    }

                    long vCenti = (long)key.NetPersistentHpDeltaCenti - key.NetPotionOpportunityCostCenti;
                    if (!oHas
                        || vCenti > oValue
                        || (vCenti == oValue && key.NetPersistentHpDeltaCenti > oHpDelta)
                        || (vCenti == oValue && key.NetPersistentHpDeltaCenti == oHpDelta && key.CombatEndedTurn < oTurn))
                    {
                        oHas = true;
                        oValue = vCenti;
                        oHpDelta = key.NetPersistentHpDeltaCenti;
                        oTurn = key.CombatEndedTurn;
                        changed = true;
                    }

                    if (changed)
                    {
                        incumbent = new HotIncumbentBound(zHas, zHpDelta, zTurn, oHas, oValue, oHpDelta, oTurn);
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
        int width = Math.Clamp(configuredBeamWidth, 4, HotSearchConstants.MaxBeamWidth);
        if (policy.IsActEndingBoss && width < 45)
            width = 45;
        return width;
    }

    private static int ResolveSecondaryBeamWidth(
        int baseBeamWidth,
        int scaleNumerator,
        int scaleDenominator,
        int floorWidth,
        int defaultCap)
    {
        int safeBase = Math.Clamp(baseBeamWidth, 4, HotSearchConstants.MaxBeamWidth);
        int scaled = (int)Math.Min(
            (long)HotSearchConstants.MaxBeamWidth,
            ((long)safeBase * scaleNumerator) / Math.Max(1, scaleDenominator));
        int lo = Math.Max(safeBase, Math.Min(floorWidth, HotSearchConstants.MaxBeamWidth));
        int hi = Math.Max(lo, Math.Min(defaultCap, HotSearchConstants.MaxBeamWidth));
        return Math.Clamp(scaled, lo, hi);
    }

    private static int ResolveEffectiveDop(SearchPolicySnapshot policy)
    {
        if (policy.DetailedDiagnostics)
            return 1;
        int maxDop = Math.Max(1, Math.Min(HotSearchConstants.MaximumSearchMaxDegreeOfParallelism, Environment.ProcessorCount));
        return Math.Clamp(policy.MaxDegreeOfParallelism, 1, maxDop);
    }
}
