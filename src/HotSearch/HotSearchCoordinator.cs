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
/// Clean, unified Phase 3 search coordinator (~450 LOC replacing 42 legacy entry points &amp; 10 PostSearch passes):
/// - Pass 1: Primary Beam (0-potion baseline under Smart policy, or configured policy) with VictoryStopTarget early exit;
/// - Pass 2: Smart Allowed-Potion Beam sharing certified Incumbent bound (only when potions available and justified);
/// - Pass 3: Optional fast wider refinement pass when budget remains and victory/HP target is not yet satisfied.
/// Reuses a single <see cref="HotNodeArena"/>, <see cref="PackedStateArena"/>, <see cref="HotTranspositionTable"/>
/// (O(1) generation epoch reset!), and <see cref="HotParallelExpander"/> across passes.
/// </summary>
internal static class HotSearchCoordinator
{
    private const int MaxSearchTurns = 28;
    private const int StandPatTopK = 16;

    public static SolverResult SolveSingleBeam(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        Stopwatch requestClock = Stopwatch.StartNew();
        HotRootBundle rootBundle = HotRootBuilder.BuildFromRootSnapshot(root);
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
            baselineForEligibility: null,
            ref incumbent,
            progressCallback: null,
            phaseLabel: "正在搜索路线",
            reviewedOffset: 0);

        policy.RequestWorkTotals?.Record(
            new SearchSolverWorkContribution(
                pass.Stats.ExpandedParents,
                pass.Stats.Transitions,
                pass.Stats.ChoiceBranches,
                pass.Elapsed,
                pass.Stats.WorkerAllocatedBytes,
                pass.Gen0,
                pass.Gen1,
                pass.Gen2,
                pass.GcPause,
                pass.GcPause),
            directSearchPurpose: DirectSearchPurpose.PrimaryBeam);

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

    public static SolverResult SolveCoordinator(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback)
    {
        Stopwatch requestClock = Stopwatch.StartNew();
        SearchRequestWorkTotals workTotals = policy.RequestWorkTotals ?? new SearchRequestWorkTotals();
        BeamWidthPortfolioTelemetry portfolioTelemetry = policy.PortfolioTelemetry ?? new BeamWidthPortfolioTelemetry();

        HotRootBundle rootBundle = HotRootBuilder.BuildFromRootSnapshot(root);
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

        // Seed certified incumbent from shared PrimaryIncumbentTable witness if available
        HotIncumbentBound sharedIncumbent = HotIncumbentBound.None;
        SolverResult? reusedWitness = null;
        if (!policy.DisableRefinementIncumbentForTesting
            && !policy.DisableSharedPrimaryIncumbentsForTesting
            && PrimaryIncumbentTable.CanShareRoot(root)
            && policy.PrimaryIncumbents?.PotionFreeWitness is { } witness
            && witness.ExplicitPotionCount == 0
            && !witness.OnlyDeathRoutesFound
            && witness.Snapshot.AllEnemiesDead
            && !witness.Snapshot.PlayerDead
            && witness.CombatEndedTurn is int witnessEndTurn
            && hotPolicy.EffectivePotionPolicy != SolverPotionPolicy.RequireAtLeastOne
            && !hotPolicy.HasForcedDirectives)
        {
            reusedWitness = witness;
            int witnessDeficit = ActEndingBossPolicy.StrategicHpDeficit(
                witness.Snapshot.CumulativePlayerHpLost,
                Math.Max(0, root.InitialPlayerMaxHp - witness.Snapshot.PlayerMaxHp),
                witness.Snapshot.RecoveredPlayerHp
                    + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                        hotPolicy.PostCombatRelicHeal, true, witness.Snapshot.PlayerHp, witness.Snapshot.PlayerMaxHp),
                hotPolicy.StrategicBossHpRelief,
                witness.Snapshot.DeathSaveHpRestored) - witness.Snapshot.StrategicHpCredit;
            if (!hotPolicy.HasAnyHealOrUnknownGeneration && witness.Snapshot.ProjectedDeathSaveUseCount == 0)
            {
                sharedIncumbent = new HotIncumbentBound(
                    hasCertifiedZeroPotionBound: true,
                    bestZeroPotionHpLost: witnessDeficit,
                    bestZeroPotionEndTurn: witnessEndTurn,
                    hasCertifiedOverallBound: true,
                    bestOverallCost: witnessDeficit,
                    bestOverallEndTurn: witnessEndTurn);
            }
        }

        int baseBeamWidth = ResolveInitialBeamWidth(policy.Profile.BeamWidth, in hotPolicy);
        bool smartMode = hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Smart;
        bool pass1DisablePotions = smartMode && !hotPolicy.HasForcedDirectives
            || (hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Disabled && !hotPolicy.HasForcedDirectives);
        bool pass1OnlyForced = hotPolicy.HasForcedDirectives && (smartMode || hotPolicy.EffectivePotionPolicy == SolverPotionPolicy.Disabled);

        long totalExpanded = 0;
        long totalTransitions = 0;
        long totalChoices = 0;
        long totalAllocated = 0;
        int totalGen0 = 0, totalGen1 = 0, totalGen2 = 0;
        TimeSpan totalGcPause = TimeSpan.Zero;
        int passCount = 0;

        // ---- PASS 1: Primary Beam (0-potion baseline under Smart policy, or configured policy) ----
        HotPassOutcome pass1 = RunBeamPass(
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
            disableAllPotions: pass1DisablePotions,
            onlyForcedPotions: pass1OnlyForced,
            maxPotionUses: -1,
            disableTt: disableTt,
            baselineForEligibility: null,
            ref sharedIncumbent,
            progressCallback: progressCallback,
            phaseLabel: "正在搜索主路线",
            reviewedOffset: 0);

        AccumulatePassTotals(workTotals, pass1, DirectSearchPurpose.PrimaryBeam,
            ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
            ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

        HotPassOutcome bestPass = pass1;
        HotRouteKey baselineKey = pass1.BestKey;
        int potionHpSaved = 0;
        int potionBranchesRejected = 0;

        // Materialize Pass 1 winner now ONLY if we will reset arenas for Pass 2/3, so we never lose its path!
        SolverResult? materializedBest = null;

        bool willRunPass2 = smartMode
            && !budget.IsTimeExpired
            && !budget.IsCanceled
            && totalExpanded < totalNodeLimit
            && HotPotionPolicy.ShouldRunSmartPotionPass(in baselineKey, in hotPolicy);

        HotRouteKey bestPassKey = bestPass.BestKey;
        bool canRunRefinement = !pass1.StopTargetReached
            && !budget.IsCanceled
            && budget.RemainingMilliseconds > Math.Max(500L, softBudgetMs / 4)
            && totalExpanded < totalNodeLimit
            && (!bestPass.HasVictory
                || (policy.UseBeamWidthPortfolio
                    && !HotRouteQuality.MeetsVictoryStopTarget(in bestPassKey, in hotPolicy)
                    && requestClock.ElapsedMilliseconds < (softBudgetMs * 45) / 100));

        if (willRunPass2 || canRunRefinement)
        {
            materializedBest = MaterializePassResult(
                root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                nodeArena, in pass1, tt, expander, totalExpanded, totalTransitions, totalChoices,
                totalAllocated, requestClock.Elapsed, potionHpSaved: 0, potionBranchesRejected: 0);
            portfolioTelemetry.RecordFirstRoutePublished(requestClock.Elapsed.TotalMilliseconds);
        }

        // ---- PASS 2: Smart Allowed-Potion Pass (shares Incumbent bound; skips redundant <=1 middle stage!) ----
        if (willRunPass2)
        {
            ResetSearchStateForNextPass(nodeArena, mainStateArena, tt);
            int remainingNodes = (int)Math.Max(1, totalNodeLimit - totalExpanded);

            HotPassOutcome pass2 = RunBeamPass(
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
                maxNodesForPass: remainingNodes,
                disableAllPotions: false,
                onlyForcedPotions: false,
                maxPotionUses: -1,
                disableTt: disableTt,
                baselineForEligibility: baselineKey,
                ref sharedIncumbent,
                progressCallback: progressCallback,
                phaseLabel: "正在评估药水路线",
                reviewedOffset: totalExpanded);

            AccumulatePassTotals(workTotals, pass2, DirectSearchPurpose.SmartPotionGradient,
                ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

            HotRouteKey pass2Key = pass2.BestKey;
            bool pass2Eligible = pass2.HasBest
                && HotPotionPolicy.IsEligibleFinalRoute(
                    in pass2Key,
                    baselineKey,
                    anyRouteWon: bestPass.HasVictory || pass2.HasVictory,
                    in hotPolicy);

            if (pass2Eligible
                && HotRouteQuality.IsBetter(in pass2Key, in bestPassKey, in hotPolicy, includeOptionalPotionCost: true))
            {
                potionHpSaved = pass2Key.ExplicitPotionCount > hotPolicy.ForcedDirectiveCount
                    ? PotionUsePolicy.HpSaved(baselineKey.StrategicHpDeficit, pass2Key.StrategicHpDeficit)
                    : 0;
                bestPass = pass2;
                bestPassKey = pass2Key;
                materializedBest = MaterializePassResult(
                    root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                    nodeArena, in pass2, tt, expander, totalExpanded, totalTransitions, totalChoices,
                    totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);
            }
            else if (pass2.HasBest && pass2Key.ExplicitPotionCount > hotPolicy.ForcedDirectiveCount)
            {
                potionBranchesRejected++;
            }
        }

        // ---- PASS 3: Optional Fast Wider Refinement Pass (only if budget remains & target not met) ----
        bool runPass3 = !bestPass.StopTargetReached
            && !budget.IsCanceled
            && budget.RemainingMilliseconds > Math.Max(500L, softBudgetMs / 4)
            && totalExpanded < totalNodeLimit
            && (!bestPass.HasVictory
                || (policy.UseBeamWidthPortfolio
                    && !HotRouteQuality.MeetsVictoryStopTarget(in bestPassKey, in hotPolicy)
                    && requestClock.ElapsedMilliseconds < (softBudgetMs * 45) / 100));

        if (runPass3)
        {
            if (materializedBest == null)
            {
                materializedBest = MaterializePassResult(
                    root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                    nodeArena, in bestPass, tt, expander, totalExpanded, totalTransitions, totalChoices,
                    totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);
            }

            ResetSearchStateForNextPass(nodeArena, mainStateArena, tt);

            int wideBeam = !bestPass.HasVictory
                ? Math.Clamp(baseBeamWidth * 2, baseBeamWidth + 20, 160)
                : Math.Clamp((baseBeamWidth * 3) / 2, baseBeamWidth + 16, 120);
            int remainingNodes = (int)Math.Max(1, totalNodeLimit - totalExpanded);

            bool pass3DisablePotions = pass1DisablePotions && !willRunPass2;
            bool pass3OnlyForced = pass1OnlyForced && !willRunPass2;
            HotRouteKey? pass3Baseline = smartMode && !pass3DisablePotions ? baselineKey : null;

            HotPassOutcome pass3 = RunBeamPass(
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
                beamWidth: wideBeam,
                maxNodesForPass: remainingNodes,
                disableAllPotions: pass3DisablePotions,
                onlyForcedPotions: pass3OnlyForced,
                maxPotionUses: -1,
                disableTt: disableTt,
                baselineForEligibility: pass3Baseline,
                ref sharedIncumbent,
                progressCallback: progressCallback,
                phaseLabel: "正在精炼路线",
                reviewedOffset: totalExpanded);

            AccumulatePassTotals(workTotals, pass3, DirectSearchPurpose.RefinementBeam,
                ref totalExpanded, ref totalTransitions, ref totalChoices, ref totalAllocated,
                ref totalGen0, ref totalGen1, ref totalGen2, ref totalGcPause, ref passCount);

            HotRouteKey pass3Key = pass3.BestKey;
            bool pass3Eligible = pass3.HasBest
                && HotPotionPolicy.IsEligibleFinalRoute(
                    in pass3Key,
                    pass3Baseline,
                    anyRouteWon: bestPass.HasVictory || pass3.HasVictory,
                    in hotPolicy);

            if (pass3Eligible
                && HotRouteQuality.IsBetter(in pass3Key, in bestPassKey, in hotPolicy, includeOptionalPotionCost: true))
            {
                if (smartMode && pass3Key.ExplicitPotionCount > hotPolicy.ForcedDirectiveCount)
                    potionHpSaved = PotionUsePolicy.HpSaved(baselineKey.StrategicHpDeficit, pass3Key.StrategicHpDeficit);
                else
                    potionHpSaved = 0;

                bestPass = pass3;
                bestPassKey = pass3Key;
                materializedBest = MaterializePassResult(
                    root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
                    nodeArena, in pass3, tt, expander, totalExpanded, totalTransitions, totalChoices,
                    totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);
            }
        }

        SolverResult finalResult = materializedBest ?? MaterializePassResult(
            root, displayNames, battleDamage, policy, rootBundle, in hotPolicy, rootPotionCosts,
            nodeArena, in bestPass, tt, expander, totalExpanded, totalTransitions, totalChoices,
            totalAllocated, requestClock.Elapsed, potionHpSaved, potionBranchesRejected);

        // Ensure reused witness never degrades when VerifySharedIncumbentReuse is active
        if (reusedWitness != null
            && RouteQualityPolicy.Compare(
                RouteQuality.FromInterim(CombatSearchCoordinator.CapturePortfolioQuality(root, policy, reusedWitness)),
                RouteQuality.FromInterim(CombatSearchCoordinator.CapturePortfolioQuality(root, policy, finalResult)),
                RouteQualityProjection.PotionPolicy,
                policy.TheftPolicy) < 0)
        {
            finalResult = reusedWitness;
        }

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
        finalResult.SearchWorkAttributions = workTotals.AttributionSnapshot();
        finalResult.PortfolioTelemetry = portfolioTelemetry;
        finalResult.RequestBudget = new SearchRequestBudgetSnapshot(
            totalExpanded,
            totalNodeLimit,
            requestClock.ElapsedMilliseconds,
            softBudgetMs);

        // Publish reusable potion-free victory witness to PrimaryIncumbentTable if enabled
        if (!policy.DisableRefinementIncumbentForTesting
            && !policy.DisableSharedPrimaryIncumbentsForTesting
            && PrimaryIncumbentTable.CanShareRoot(root)
            && policy.PrimaryIncumbents != null
            && finalResult.ExplicitPotionCount == 0
            && !finalResult.OnlyDeathRoutesFound
            && finalResult.Snapshot.AllEnemiesDead
            && !finalResult.Snapshot.PlayerDead
            && finalResult.CombatEndedTurn is int endTurn)
        {
            policy.PrimaryIncumbents.Tighten(
                ResourceIncumbentPolicy.CompletedBucket(finalResult.Snapshot, 0),
                new PrimarySearchIncumbent(bestPass.BestKey.StrategicHpDeficit, endTurn, 0));
            policy.PrimaryIncumbents.PotionFreeWitness = finalResult;
        }

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

        // Pack and insert root state into mainStateArena & nodeArena
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
            out long rootScore,
            out int rootSetup);

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
            maxPileChoiceBranches: profile.MaxPileChoiceBranchesPerAction);

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
        long lastProgressReportMs = -SolverWeights.ProgressUiIntervalMilliseconds;

        // Cross-turn progress tracking (replaces legacy multi-thousand-LOC cross-turn cycle bloat)
        int bestSeenEnemyHp = rootSummary.EnemyHpSum;
        int bestSeenAliveEnemies = rootSummary.AliveEnemyCount;
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
                // Prevent Turn 1 draw/combo explosions from consuming the entire node budget before Turn 2+ is explored.
                int perTurnNodeCap = hasVictory
                    ? remainingPassNodes
                    : Math.Max(
                        SolverWeights.MinimumTurnLayerExpandedNodes,
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

                    // Freeze incumbent for this wave so DOP=1 and DOP=8/16 prune identically!
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

                    // Process discovered victories deterministically
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

                    // Track best ongoing route seen so far (for graceful NodeLimit/TimeLimit fallback)
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
                        pruneOutBuf.AsSpan(0, midTurnBeamWidth));

                    activeFrontier.Clear();
                    for (int i = 0; i < kept; i++)
                        activeFrontier.Add(pruneOutBuf[i]);

                    // Throttled progress notification
                    if (progressCallback != null
                        && budget.ElapsedMilliseconds - lastProgressReportMs >= SolverWeights.ProgressUiIntervalMilliseconds)
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

                // Lightweight Stand-Pat lookahead on Top-K active nodes if turn stopped early due to cap
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

                // Prune turn boundary candidates into next turn's frontier
                if (pruneOutBuf.Length < beamWidth)
                {
                    ArrayPool<int>.Shared.Return(pruneOutBuf, clearArray: false);
                    pruneOutBuf = ArrayPool<int>.Shared.Rent(beamWidth);
                }

                int nextKept = retention.PruneLayer(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nextTurnCandidates),
                    nodeArena,
                    beamWidth,
                    pruneOutBuf.AsSpan(0, beamWidth));

                currentTurnFrontier.Clear();
                bool layerProgressed = false;
                for (int i = 0; i < nextKept; i++)
                {
                    int nid = pruneOutBuf[i];
                    currentTurnFrontier.Add(nid);
                    ref readonly HotSearchNode n = ref nodeArena.GetRef(nid);
                    if (n.EnemyHp < bestSeenEnemyHp) { bestSeenEnemyHp = n.EnemyHp; layerProgressed = true; }
                    if (n.AliveEnemyCount < bestSeenAliveEnemies) { bestSeenAliveEnemies = n.AliveEnemyCount; layerProgressed = true; }
                    if (n.SetupScore > bestSeenSetup) { bestSeenSetup = n.SetupScore; layerProgressed = true; }
                }

                turnsWithoutProgress = layerProgressed ? 0 : turnsWithoutProgress + 1;
                if (turnsWithoutProgress >= SolverWeights.SetupValueHorizonTurns)
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

        // Select final winner for this pass: Victory > Best Surviving Ongoing > Best Fallback (death/root)
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
            }

            // Tighten certified 2-bit HP incumbent bound (sound when !HasAnyHealOrUnknownGeneration & no death save)
            if (!hotPolicy.HasAnyHealOrUnknownGeneration
                && !hotPolicy.HasGrowthTargets
                && !hotPolicy.HasForcedDirectives
                && hotPolicy.TheftPolicy != SolverTheftPolicy.PreserveResources
                && key.DeathSaveUseCount == 0)
            {
                bool updated = false;
                bool hasZero = incumbent.HasCertifiedZeroPotionBound;
                int zeroHp = incumbent.BestZeroPotionHpLost;
                int zeroTurn = incumbent.BestZeroPotionEndTurn;

                if (key.ExplicitPotionCount == 0
                    && (!hasZero
                        || key.StrategicHpDeficit < zeroHp
                        || (key.StrategicHpDeficit == zeroHp && key.CombatEndedTurn < zeroTurn)))
                {
                    hasZero = true;
                    zeroHp = key.StrategicHpDeficit;
                    zeroTurn = key.CombatEndedTurn;
                    updated = true;
                }

                int totalCost = key.StrategicHpDeficit + key.OptionalPotionStrategicCost;
                bool hasOverall = incumbent.HasCertifiedOverallBound;
                int overallCost = incumbent.BestOverallCost;
                int overallTurn = incumbent.BestOverallEndTurn;

                if (!hasOverall
                    || totalCost < overallCost
                    || (totalCost == overallCost && key.CombatEndedTurn < overallTurn))
                {
                    hasOverall = true;
                    overallCost = totalCost;
                    overallTurn = key.CombatEndedTurn;
                    updated = true;
                }

                if (updated)
                {
                    incumbent = new HotIncumbentBound(hasZero, zeroHp, zeroTurn, hasOverall, overallCost, overallTurn);
                    incumbentUpdates++;
                }
            }

            if (HotRouteQuality.MeetsVictoryStopTarget(in key, in hotPolicy))
                stopTargetReached = true;
        }
    }

    private static void UpdateBestOngoing(
        List<int> candidates,
        HotNodeArena nodeArena,
        in HotSearchPolicy hotPolicy,
        ref int bestOngoingId,
        ref HotRouteKey bestOngoingKey,
        ref bool hasOngoing)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            int nid = candidates[i];
            ref readonly HotSearchNode node = ref nodeArena.GetRef(nid);
            if (node.IsDefeat || node.ActionCount == 0)
                continue;

            HotRouteKey key = HotRouteQuality.Capture(in node, in hotPolicy);
            if (!hasOngoing || HotRouteQuality.IsBetter(in key, in bestOngoingKey, in hotPolicy, includeOptionalPotionCost: true))
            {
                hasOngoing = true;
                bestOngoingId = nid;
                bestOngoingKey = key;
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
        SearchRequestWorkTotals workTotals,
        in HotPassOutcome pass,
        DirectSearchPurpose purpose,
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

        workTotals.Record(
            new SearchSolverWorkContribution(
                pass.Stats.ExpandedParents,
                pass.Stats.Transitions,
                pass.Stats.ChoiceBranches,
                pass.Elapsed,
                pass.Stats.WorkerAllocatedBytes,
                pass.Gen0,
                pass.Gen1,
                pass.Gen2,
                pass.GcPause,
                pass.GcPause),
            directSearchPurpose: purpose);
    }

    private static void ResetSearchStateForNextPass(
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotTranspositionTable tt)
    {
        nodeArena.Reset();
        mainStateArena.Reset();
        tt.ResetForNextPass();
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
        if (policy.DetailedDiagnostics || policy.VerifyIncrementalSearch)
            return 1;
        int dop = Math.Clamp(policy.MaxDegreeOfParallelism, 1, Math.Min(SolverWeights.MaximumSearchMaxDegreeOfParallelism, Environment.ProcessorCount));
        if (!policy.MemoryPressureSignal.IsEnabled && policy.MemoryPressureSignal.ConservativeParallelismRequired)
            dop = Math.Min(2, dop);
        return Math.Max(1, dop);
    }
}
