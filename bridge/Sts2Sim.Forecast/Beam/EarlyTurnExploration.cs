using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using CombatSolver.Beam.Novelty;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal sealed record EarlyTurnFrontierCandidate(
    int CompletedTurns,
    SearchNode FrontierNode,
    PlannedAction[] Actions,
    CombatStateDescriptionDigest StateKey,
    double Score);

internal static class EarlyTurnFrontierSelector
{
    private const int EarlyTurnCandidatesPerLayer = 24;

    private sealed record EarlyTurnEntry(
        SearchNode Node,
        EarlyTurnOpeningKey Opening,
        EarlyTurnStrategyKey Strategy);

    internal static IReadOnlyList<EarlyTurnFrontierCandidate> Select(
        IReadOnlyList<SearchNode> ended,
        int completedTurns,
        int rootTurnNumber,
        CombatPolicy policy,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var states = new Dictionary<CombatStateDescriptionDigest, SearchNode>();
        int targetTurn = rootTurnNumber + completedTurns;
        for (int i = 0; i < ended.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            SearchNode node = ended[i];
            if (node.Ended
                || !node.State.Players[0].Creature.IsAlive
                || node.NodeBoundary != ForecastSearchBoundary.None
                || node.Turn != targetTurn)
            {
                continue;
            }
            if (!states.TryGetValue(node.Fingerprint, out SearchNode? prior)
                || node.Score > prior.Score)
            {
                states[node.Fingerprint] = node;
            }
        }

        if (states.Count == 0)
            return [];

        var byStrategy = new Dictionary<EarlyTurnStrategyKey, (EarlyTurnEntry First, EarlyTurnEntry? Second)>();
        foreach (SearchNode node in states.Values)
        {
            ct.ThrowIfCancellationRequested();
            EarlyTurnStrategyKey strategy = CoreNoveltyFeatures.ExtractEarlyTurnStrategyKey(node, policy);
            var entry = new EarlyTurnEntry(node, strategy.Opening, strategy);
            if (!byStrategy.TryGetValue(strategy, out (EarlyTurnEntry First, EarlyTurnEntry? Second) pair))
            {
                byStrategy.Add(strategy, (entry, null));
            }
            else if (node.Score > pair.First.Node.Score)
            {
                byStrategy[strategy] = (entry, pair.First);
            }
            else if (pair.Second is null || node.Score > pair.Second.Node.Score)
            {
                byStrategy[strategy] = (pair.First, entry);
            }
        }

        var byOpening = new Dictionary<EarlyTurnOpeningKey, List<EarlyTurnEntry>>();
        foreach ((EarlyTurnEntry first, EarlyTurnEntry? second) in byStrategy.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (!byOpening.TryGetValue(first.Opening, out List<EarlyTurnEntry>? openingList))
            {
                openingList = new List<EarlyTurnEntry>(8);
                byOpening.Add(first.Opening, openingList);
            }
            InsertTopBounded(openingList, first, EarlyTurnCandidatesPerLayer);
            if (second is not null)
                InsertTopBounded(openingList, second, EarlyTurnCandidatesPerLayer);
        }

        var openings = new List<List<EarlyTurnEntry>>(byOpening.Count);
        foreach (List<EarlyTurnEntry> openingList in byOpening.Values)
        {
            ct.ThrowIfCancellationRequested();
            openings.Add(openingList);
        }
        ct.ThrowIfCancellationRequested();
        openings.Sort(static (a, b) => b[0].Node.Score.CompareTo(a[0].Node.Score));
        ct.ThrowIfCancellationRequested();

        var selected = new List<EarlyTurnFrontierCandidate>(Math.Min(EarlyTurnCandidatesPerLayer, states.Count));
        for (int rank = 0; selected.Count < EarlyTurnCandidatesPerLayer; rank++)
        {
            ct.ThrowIfCancellationRequested();
            bool found = false;
            for (int i = 0; i < openings.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                List<EarlyTurnEntry> opening = openings[i];
                if (rank >= opening.Count)
                    continue;
                found = true;
                SearchNode node = opening[rank].Node;
                selected.Add(new EarlyTurnFrontierCandidate(
                    completedTurns,
                    node,
                    node.Route().ToArray(),
                    node.Fingerprint,
                    node.Score));
                if (selected.Count == EarlyTurnCandidatesPerLayer)
                    break;
            }
            if (!found)
                break;
        }
        return selected;

        static void InsertTopBounded(List<EarlyTurnEntry> list, EarlyTurnEntry candidate, int limit)
        {
            int insertAt = list.Count;
            double score = candidate.Node.Score;
            for (int i = 0; i < list.Count; i++)
            {
                if (score > list[i].Node.Score)
                {
                    insertAt = i;
                    break;
                }
            }
            if (insertAt >= limit)
                return;
            if (list.Count == limit)
                list.RemoveAt(limit - 1);
            list.Insert(insertAt, candidate);
        }
    }
}

internal static class EarlyTurnExplorationRunner
{
    private const long EarlyTurnExplorationNodeBudget = 1_000_000L;
    private const int InitialRankLimit = 4;
    private const int RankLimit = 6;

    internal static async Task<ForecastPassOutcome> RunAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        BeamPassOptions baseOptions,
        ForecastPassOutcome selectedOutcome,
        Stopwatch requestWatch,
        SearchWorkLedger? ledger,
        ForecastLanePool? sharedLanePool,
        InCombatSearchTracker? tracker,
        CancellationToken ct,
        long minRemainingReserveMs = 10_000L)
    {
        if (!portfolio.UseEarlyTurnExploration || portfolio.EarlyTurnExplorationDepth <= 0)
            return selectedOutcome;

        if (BeamIncumbentPolicy.CanStopEarlyTurnExploration(
            policy,
            selectedOutcome.Evaluator,
            selectedOutcome,
            baseOptions.OpeningPreRootBattleHpLost))
        {
            return selectedOutcome;
        }

        long remainingMs = portfolio.EarlyTurnExplorationBudgetMilliseconds - requestWatch.ElapsedMilliseconds;
        if (remainingMs <= minRemainingReserveMs)
            return selectedOutcome;

        using var explorationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        explorationCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1L, remainingMs)));
        CancellationToken explorationToken = explorationCts.Token;

        var explorationSharedBudget = new SharedNodeBudget(EarlyTurnExplorationNodeBudget);
        int scoutBeamWidth = (int)Math.Clamp((long)budget.BeamWidth * 4L, 256L, 384L);
        int scoutNodes = (int)Math.Min(explorationSharedBudget.Remaining, Math.Min(150_000L, budget.NodeLimit));
        BeamBudget scoutBudget = new(
            BeamWidth: scoutBeamWidth,
            NodeLimit: Math.Max(1, scoutNodes),
            MaxCardBranches: (int)Math.Min(128L, (long)budget.MaxCardBranches * 2L),
            MaxPileChoiceBranches: (int)Math.Min(72L, (long)budget.MaxPileChoiceBranches * 2L),
            MaxHandChoiceBranches: (int)Math.Min(96L, (long)budget.MaxHandChoiceBranches * 2L),
            TimeLimit: TimeSpan.FromMilliseconds(Math.Max(1L, remainingMs)));

        CombatPolicy scoutPolicy = policy with { StopAtAcceptableBattleHpLoss = false };
        var seenFrontiers = new HashSet<(int Turn, CombatStateDescriptionDigest Digest)>();
        var frontiers = new List<EarlyTurnFrontierCandidate>();
        object frontierSync = new();

        void ObserveFrontier(int completedTurns, IReadOnlyList<EarlyTurnFrontierCandidate> candidates)
        {
            lock (frontierSync)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    EarlyTurnFrontierCandidate candidate = candidates[i];
                    if (seenFrontiers.Add((completedTurns, candidate.StateKey)))
                        frontiers.Add(candidate);
                }
            }
        }

        var scoutBudgetCtrl = new MemberBudgetController(
            scoutBudget.NodeLimit,
            explorationSharedBudget);
        BeamPassOptions scoutOptions = baseOptions with
        {
            MemberMode = BeamMemberSearchMode.Default,
            SecondRankBand = false,
            DisableTargetStop = true,
            PrimaryIncumbent = null,
            DisablePrimaryIncumbentPruning = true,
            AllowPolicyUnsatisfiedSelection = true,
            EarlyTurnScoutDepth = portfolio.EarlyTurnExplorationDepth,
            EarlyTurnScoutObserver = ObserveFrontier,
            BudgetController = scoutBudgetCtrl,
            SharedLanePool = sharedLanePool,
            WorkLedger = ledger,
            ActivePhaseLabel = "EarlyTurnScout",
        };

        ForecastPassOutcome currentBest = selectedOutcome;
        int initialHp = selectedOutcome.Evaluator.InitialHp;
        StateEvaluator baselineEvaluator = selectedOutcome.Evaluator;

        ForecastPassOutcome? scoutOutcome;
        using (ledger?.EnterPhase("EarlyTurnScout"))
        {
            try
            {
                scoutOutcome = await BeamSearch.RunSingleForecastPassOrNullAsync(
                    root.Clone(),
                    scoutBudget,
                    scoutPolicy,
                    explorationToken,
                    openingPreRootBattleHpLost: baseOptions.OpeningPreRootBattleHpLost,
                    maxDegreeOfParallelism: Math.Max(1, sharedLanePool?.LaneCount ?? 1),
                    inCombatPassTrack: tracker?.RegisterPass(),
                    passOptions: scoutOptions).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && explorationCts.IsCancellationRequested)
            {
                // fail-open: 早期回合探索局部预算到期，中止侦察并返回当前最优结果。
                return currentBest;
            }
            finally
            {
                scoutBudgetCtrl.ReclaimUnspent();
            }
        }

        if (scoutOutcome is { Selected.Won: true })
        {
            (ForecastPassOutcome chosen, int winnerIndex) = BeamIncumbentPolicy.ChooseBetterPassOutcomeWithOwner(
                currentBest, scoutOutcome, initialHp, baselineEvaluator);
            if (winnerIndex == 1 || !ReferenceEquals(chosen, currentBest))
                currentBest = chosen;
        }

        if (BeamIncumbentPolicy.CanStopEarlyTurnExploration(
            policy,
            baselineEvaluator,
            currentBest,
            baseOptions.OpeningPreRootBattleHpLost))
        {
            return currentBest;
        }

        try
        {
            int layerCount = Math.Max(1, portfolio.EarlyTurnExplorationDepth);
            var layers = new List<EarlyTurnFrontierCandidate>[layerCount];
            for (int i = 0; i < layerCount; i++)
            {
                explorationToken.ThrowIfCancellationRequested();
                int targetDepth = i + 1;
                layers[i] = InterleavePotionStates(frontiers, targetDepth, explorationToken);
            }

            using (ledger?.EnterPhase("EarlyTurnContinuation"))
            {
                bool parallelContinuations = sharedLanePool is { LaneCount: > 1 }
                    && portfolio.SchedulingMode != PortfolioSchedulingMode.Sequential;
                bool[] extendLayer = new bool[layerCount];

                if (!parallelContinuations)
                {
                    await RunSerialContinuationsAsync().ConfigureAwait(false);
                }
                else
                {
                    await RunParallelContinuationsAsync().ConfigureAwait(false);
                }

                return currentBest;

                async Task RunSerialContinuationsAsync()
                {
                    for (int rank = 0; rank < RankLimit; rank++)
                    {
                        for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (explorationToken.IsCancellationRequested)
                                return;
                            List<EarlyTurnFrontierCandidate> layer = layers[layerIndex];
                            long remMs = portfolio.EarlyTurnExplorationBudgetMilliseconds - requestWatch.ElapsedMilliseconds;
                            long remNodes = explorationSharedBudget.Remaining;
                            if (rank >= layer.Count || remMs <= minRemainingReserveMs || remNodes < 100)
                                continue;
                            if (rank >= InitialRankLimit && !extendLayer[layerIndex])
                                continue;

                            EarlyTurnFrontierCandidate frontier = layer[rank];
                            ForecastPassOutcome? candidate;
                            try
                            {
                                candidate = await RunOneContinuationAsync(
                                    frontier,
                                    remMs,
                                    remNodes,
                                    laneOffset: 0,
                                    explorationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested && explorationCts.IsCancellationRequested)
                            {
                                // fail-open: 早期回合探索局部预算到期，中止串行续搜并保留已得最优结果。
                                return;
                            }

                            if (candidate is { Selected.Won: true })
                            {
                                (ForecastPassOutcome chosen, int winnerIndex) = BeamIncumbentPolicy.ChooseBetterPassOutcomeWithOwner(
                                    currentBest, candidate, initialHp, baselineEvaluator);
                                if (winnerIndex == 1 || !ReferenceEquals(chosen, currentBest))
                                    currentBest = chosen;
                                if (winnerIndex == 1 && rank < InitialRankLimit)
                                    extendLayer[layerIndex] = true;
                            }

                            if (BeamIncumbentPolicy.CanStopEarlyTurnExploration(
                                policy,
                                baselineEvaluator,
                                currentBest,
                                baseOptions.OpeningPreRootBattleHpLost))
                            {
                                return;
                            }
                        }
                    }
                }

                async Task RunParallelContinuationsAsync()
                {
                    await ExecuteContinuationBandAsync(0, InitialRankLimit).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (explorationToken.IsCancellationRequested)
                        return;
                    if (BeamIncumbentPolicy.CanStopEarlyTurnExploration(
                        policy,
                        baselineEvaluator,
                        currentBest,
                        baseOptions.OpeningPreRootBattleHpLost))
                    {
                        return;
                    }
                    bool anyExtended = false;
                    for (int i = 0; i < extendLayer.Length; i++)
                    {
                        if (extendLayer[i])
                        {
                            anyExtended = true;
                            break;
                        }
                    }
                    if (anyExtended)
                        await ExecuteContinuationBandAsync(InitialRankLimit, RankLimit).ConfigureAwait(false);
                }

                async Task ExecuteContinuationBandAsync(int startRank, int endRank)
                {
                    var jobs = new List<(int Rank, int LayerIndex, EarlyTurnFrontierCandidate Frontier, int Offset)>();
                    int dop = sharedLanePool?.LaneCount ?? 1;
                    int offset = 0;
                    for (int rank = startRank; rank < endRank; rank++)
                    {
                        for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
                        {
                            if (rank >= layers[layerIndex].Count)
                                continue;
                            if (rank >= InitialRankLimit && !extendLayer[layerIndex])
                                continue;
                            jobs.Add((rank, layerIndex, layers[layerIndex][rank], offset++ % dop));
                        }
                    }
                    if (jobs.Count == 0)
                        return;

                    long remMs = portfolio.EarlyTurnExplorationBudgetMilliseconds - requestWatch.ElapsedMilliseconds;
                    long remNodes = explorationSharedBudget.Remaining;
                    if (remMs <= minRemainingReserveMs || remNodes < 100 || explorationToken.IsCancellationRequested)
                        return;

                    var slotCts = new CancellationTokenSource[jobs.Count];
                    for (int i = 0; i < jobs.Count; i++)
                        slotCts[i] = CancellationTokenSource.CreateLinkedTokenSource(explorationToken);

                    var results = new ForecastPassOutcome?[jobs.Count];
                    using var semaphore = new SemaphoreSlim(Math.Min(dop, jobs.Count));
                    var tasks = new Task[jobs.Count];

                    try
                    {
                        for (int i = 0; i < jobs.Count; i++)
                        {
                            int jobIndex = i;
                            tasks[i] = Task.Run(async () =>
                            {
                                CancellationToken jobToken = slotCts[jobIndex].Token;
                                bool acquired = false;
                                try
                                {
                                    await semaphore.WaitAsync(jobToken).ConfigureAwait(false);
                                    acquired = true;
                                    jobToken.ThrowIfCancellationRequested();
                                    long jobRemMs = portfolio.EarlyTurnExplorationBudgetMilliseconds - requestWatch.ElapsedMilliseconds;
                                    long jobRemNodes = explorationSharedBudget.Remaining;
                                    if (jobRemMs <= minRemainingReserveMs || jobRemNodes < 100)
                                        return;

                                    (int rank, int layerIndex, EarlyTurnFrontierCandidate frontier, int homeOffset) = jobs[jobIndex];
                                    ForecastPassOutcome? outcome = await RunOneContinuationAsync(
                                        frontier,
                                        jobRemMs,
                                        jobRemNodes,
                                        homeOffset,
                                        jobToken).ConfigureAwait(false);
                                    results[jobIndex] = outcome;

                                    if (outcome is not null
                                        && BeamIncumbentPolicy.CanStopEarlyTurnExploration(
                                            policy,
                                            baselineEvaluator,
                                            outcome,
                                            baseOptions.OpeningPreRootBattleHpLost))
                                    {
                                        for (int j = jobIndex + 1; j < slotCts.Length; j++)
                                            slotCts[j].Cancel();
                                    }
                                }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested
                                    && (slotCts[jobIndex].IsCancellationRequested || explorationCts.IsCancellationRequested))
                                {
                                    // fail-open: 更高优先级续搜提前达标或早期探索局部预算到期，按序取消当前等待或执行中的分支。
                                }
                                finally
                                {
                                    if (acquired)
                                        semaphore.Release();
                                }
                            }, CancellationToken.None);
                        }

                        try
                        {
                            await Task.WhenAll(tasks).ConfigureAwait(false);
                        }
                        catch
                        {
                            // fail-open: 任一分支异常或取消前先取消其余分支并等待全部任务汇合再清理令牌源。
                            for (int i = 0; i < slotCts.Length; i++)
                                slotCts[i].Cancel();
                            try
                            {
                                await Task.WhenAll(tasks).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // fail-open: 清理汇合期间忽略次级异常，交由外层重抛首因。
                            }
                            throw;
                        }
                    }
                    finally
                    {
                        for (int i = 0; i < slotCts.Length; i++)
                            slotCts[i].Dispose();
                    }
                    ct.ThrowIfCancellationRequested();

                    for (int j = 0; j < jobs.Count; j++)
                    {
                        ForecastPassOutcome? candidate = results[j];
                        if (candidate is null || !candidate.Selected.Won)
                            continue;
                        (ForecastPassOutcome chosen, int winnerIndex) = BeamIncumbentPolicy.ChooseBetterPassOutcomeWithOwner(
                            currentBest, candidate, initialHp, baselineEvaluator);
                        if (winnerIndex == 1 || !ReferenceEquals(chosen, currentBest))
                            currentBest = chosen;
                        if (winnerIndex == 1 && jobs[j].Rank < InitialRankLimit)
                            extendLayer[jobs[j].LayerIndex] = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && explorationCts.IsCancellationRequested)
        {
            // fail-open: 早期回合探索局部预算到期，中止前沿构建与续搜并返回当前最优结果。
            return currentBest;
        }

        async Task<ForecastPassOutcome?> RunOneContinuationAsync(
            EarlyTurnFrontierCandidate frontier,
            long remMs,
            long remNodes,
            int laneOffset,
            CancellationToken? tokenOverride = null)
        {
            CancellationToken passCt = tokenOverride ?? explorationToken;
            bool prefixUsesPotion = frontier.FrontierNode.PotionUses > 0;
            CombatPolicy continuationPolicy = prefixUsesPotion
                ? policy with { PotionPolicy = CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne }
                : policy;

            int continuationCap = (int)Math.Min(remNodes, Math.Min(120_000L, budget.NodeLimit));
            if (continuationCap < 1)
                return null;

            long reserveTailMs = Math.Min(5_000L, minRemainingReserveMs / 2L);
            long continuationMs = Math.Max(1, remMs - reserveTailMs);
            BeamBudget continuationBudget = budget with
            {
                NodeLimit = continuationCap,
                TimeLimit = TimeSpan.FromMilliseconds(continuationMs),
            };

            BeamPrimarySearchIncumbent? continuationIncumbent = SelectContinuationIncumbent(
                BeamIncumbentPolicy.BuildGeneralIncumbent(
                    policy,
                    baselineEvaluator,
                    currentBest.Selected,
                    currentBest.PassBoundary),
                policy.TheftPolicy == CombatSolver.Beam.Policy.SolverTheftPolicy.PreserveResources,
                baselineEvaluator.SatisfiesPotionPolicy(currentBest.Selected),
                currentBest.Selected.PotionUses > 0,
                prefixUsesPotion,
                baselineEvaluator.HasGrowthTargets || baselineEvaluator.HasRelicTargets,
                !currentBest.Selected.Won);

            var controller = new MemberBudgetController(
                continuationCap,
                explorationSharedBudget);
            BeamPassOptions contOptions = baseOptions with
            {
                MemberMode = BeamMemberSearchMode.Default,
                SecondRankBand = false,
                StartNodeOverride = frontier.FrontierNode,
                RootBaselineEvaluator = baselineEvaluator,
                PrimaryIncumbent = continuationIncumbent,
                DisablePrimaryIncumbentPruning = continuationIncumbent is null,
                EarlyTurnScoutDepth = null,
                EarlyTurnScoutObserver = null,
                BudgetController = controller,
                SharedLanePool = sharedLanePool,
                HomeLaneOffset = laneOffset,
                WorkLedger = ledger,
                ActivePhaseLabel = "EarlyTurnContinuation",
                AllowPolicyUnsatisfiedSelection = true,
            };

            try
            {
                return await BeamSearch.RunSingleForecastPassOrNullAsync(
                    root,
                    continuationBudget,
                    continuationPolicy,
                    passCt,
                    openingPreRootBattleHpLost: baseOptions.OpeningPreRootBattleHpLost,
                    maxDegreeOfParallelism: Math.Max(1, sharedLanePool?.LaneCount ?? 1),
                    inCombatPassTrack: tracker?.RegisterPass(),
                    passOptions: contOptions).ConfigureAwait(false);
            }
            finally
            {
                controller.ReclaimUnspent();
            }
        }
    }

    internal static BeamPrimarySearchIncumbent? SelectContinuationIncumbent(
        BeamPrimarySearchIncumbent? incumbent,
        bool preserveResources,
        bool selectedForcedUsesSatisfied,
        bool selectedHasExplicitPotionUses,
        bool prefixUsesPotion,
        bool selectedHasStrategicCredit,
        bool selectedHasRisk)
    {
        if (incumbent is null
            || preserveResources
            || !selectedForcedUsesSatisfied
            || selectedHasStrategicCredit
            || selectedHasRisk
            || (selectedHasExplicitPotionUses && !prefixUsesPotion))
        {
            return null;
        }
        return incumbent.Value with { CombatEndedTurn = int.MaxValue };
    }

    private static List<EarlyTurnFrontierCandidate> InterleavePotionStates(
        List<EarlyTurnFrontierCandidate> allFrontiers,
        int completedTurns,
        CancellationToken ct)
    {
        var withPotion = new List<EarlyTurnFrontierCandidate>();
        var withoutPotion = new List<EarlyTurnFrontierCandidate>();
        for (int i = 0; i < allFrontiers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            EarlyTurnFrontierCandidate cand = allFrontiers[i];
            if (cand.CompletedTurns != completedTurns)
                continue;
            if (cand.FrontierNode.PotionUses > 0)
                withPotion.Add(cand);
            else
                withoutPotion.Add(cand);
        }

        int maxCount = Math.Max(withPotion.Count, withoutPotion.Count);
        var interleaved = new List<EarlyTurnFrontierCandidate>(withPotion.Count + withoutPotion.Count);
        for (int i = 0; i < maxCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i < withPotion.Count)
                interleaved.Add(withPotion[i]);
            if (i < withoutPotion.Count)
                interleaved.Add(withoutPotion[i]);
        }
        return interleaved;
    }
}
