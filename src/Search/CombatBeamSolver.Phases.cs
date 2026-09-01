using System.Diagnostics;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using BufferCard = MegaCrit.Sts2.Core.Models.Cards.Buffer;

namespace CombatSolver;


internal sealed partial class CombatBeamSolver
{
    public SolverResult Solve()
    {
        using IDisposable notificationIsolation = SimulationNotificationIsolation.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        if (root.PlayerCount != 1)
            throw new NotSupportedException("第一版只支持单人战斗。");
        if (root.Enemies.Count > 64)
            throw new NotSupportedException("单场战斗超过 64 个敌人，无法编码路线存活位图。");
        PlayerTurnPhase requiredPhase = _includeTurnSetup
            ? PlayerTurnPhase.Start
            : PlayerTurnPhase.Play;
        if (root.CurrentSide != CombatSide.Player || root.PlayerPhase != requiredPhase)
        {
            throw new InvalidOperationException(
                _includeTurnSetup
                    ? "回合准备选牌搜索只能在玩家回合准备阶段计算。"
                    : "只能在玩家出牌阶段计算。");
        }

        int expansionParallelism = _detailedDiagnostics || policy.VerifyIncrementalSearch
            ? 1
            : Math.Clamp(
                policy.MaxDegreeOfParallelism,
                1,
                Math.Max(1, Environment.ProcessorCount));

        long allocatedBytesAtStart = GC.GetAllocatedBytesForCurrentThread();
        int gen0AtStart = GC.CollectionCount(0);
        int gen1AtStart = GC.CollectionCount(1);
        int gen2AtStart = GC.CollectionCount(2);
        TimeSpan gcPauseAtStart = GC.GetTotalPauseDuration();
        Stopwatch stopwatch = Stopwatch.StartNew();
        using ParallelExpansionExecutor? parallelExpansionExecutor = expansionParallelism > 1
            ? new ParallelExpansionExecutor(this, expansionParallelism)
            : null;
        long lastProgressMs = -100;
        void PublishProgress(
            int currentTurn,
            int completedTurns,
            int playDepth,
            int frontierNodes,
            int endedNodes,
            string phase,
            bool force = false)
        {
            long elapsedMs = stopwatch.ElapsedMilliseconds;
            if (!force && elapsedMs - lastProgressMs < 100)
                return;
            lastProgressMs = elapsedMs;
            bool checkpointPhase = _shortCheckpointMilliseconds is { } checkpoint
                && elapsedMs < checkpoint;
            progressCallback?.Invoke(new SolverProgress(
                _startTurnNumber,
                currentTurn,
                completedTurns,
                playDepth,
                _run.Expanded,
                _profile.MaxExpandedNodes,
                frontierNodes,
                endedNodes,
                elapsedMs,
                $"{(checkpointPhase || _profile.Phase == SolverSearchPhase.Short ? "快速搜索" : "深化搜索")}·{phase}"));
        }

        PublishProgress(_startTurnNumber, 0, 0, 1, 0, "初始化", force: true);
        IReadOnlyList<(IReadOnlyList<PlanCardChoice> Choices, SimulationSnapshot Snapshot)> rootCandidates =
            _includeTurnSetup
                ? BuildTurnSetupRoots()
                : [([], Replay([]))];
        if (rootCandidates.Count == 0)
            throw new InvalidOperationException("回合准备阶段没有生成可搜索状态。");
        _run.InitialPersistentBuffValue = _includeTurnSetup
            ? 0
            : rootCandidates[0].Snapshot.PersistentBuffValue;
        _run.InitialEnemyStrengthSuppression = _includeTurnSetup
            ? 0
            : rootCandidates[0].Snapshot.EnemyStrengthSuppression;
        _run.InitialEnemyWeakTurns = _includeTurnSetup
            ? 0
            : rootCandidates[0].Snapshot.EnemyWeakTurns;
        _run.InitialRetainedAttackValue = _includeTurnSetup
            ? 0
            : rootCandidates[0].Snapshot.RetainedAttackValue;
        List<SearchNode> frontier = new(rootCandidates.Count);
        foreach ((IReadOnlyList<PlanCardChoice> choices, SimulationSnapshot snapshot) in rootCandidates)
        {
            ContinuationStamp? turnSetupPlayState = _includeTurnSetup
                ? ContinuationStamp.CapturePredicted(
                    _player,
                    snapshot.Simulator,
                    _startTurnNumber,
                    _forecast,
                    _startTurnNumber)
                : null;
            SearchNode root = new(
                null,
                0,
                snapshot.PotionUseCount,
                snapshot.PotionStrategicCost,
                _startTurnNumber,
                SearchRouteTraits.None,
                0,
                snapshot.Score,
                snapshot.StateKey,
                snapshot.HasRisk,
                snapshot.BoundaryReason,
                snapshot.PlayerDead
                    || snapshot.AllEnemiesDead
                    || snapshot.BoundaryReason != SearchBoundaryReason.None,
                null,
                snapshot,
                CombatProgressState.Capture(snapshot),
                TurnSetupChoices: choices,
                TurnSetupPlayState: turnSetupPlayState);
            frontier.Add(root);
            if (_run.Transpositions.TryGetValue(root.StateKey, out TranspositionFrontier? existing))
                existing.TryAccept(new TranspositionLabel(
                    root.PotionCount,
                    root.PotionStrategicCost,
                    0,
                    root.Snapshot.CumulativePlayerHpLost,
                    0,
                    root.Score));
            else
                _run.Transpositions.Add(
                    root.StateKey,
                    new TranspositionFrontier(new TranspositionLabel(
                        root.PotionCount,
                        root.PotionStrategicCost,
                        0,
                        root.Snapshot.CumulativePlayerHpLost,
                        0,
                        root.Score)));
        }
        List<SearchNode> completed = [];
        SearchNode fallback = frontier.MaxBy(static node => node.Score)!;
        SearchNode? potionFreeBoundaryFallback = null;
        double potionFreeBoundaryFallbackScore = double.NegativeInfinity;
        SearchNode? potionBoundaryFallback = null;
        double potionBoundaryFallbackScore = double.NegativeInfinity;
        int initialHp = root.InitialPlayerHp;
        int searchedTurnLayers = 0;
        bool timeBudgetReached = false;

        while (frontier.Count > 0
            && (!policy.VerifyIncrementalSearch
                || searchedTurnLayers < SolverWeights.IncrementalVerificationMaxTurns)
            && _run.Expanded < _profile.MaxExpandedNodes
            && !timeBudgetReached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<SearchNode> active = frontier.Where(node => !node.IsTerminal).ToList();
            foreach (SearchNode terminal in frontier.Where(node => node.IsTerminal))
                completed.Add(terminal);
            if (active.Count == 0)
            {
                List<SearchNode> rankedCompleted = Retention.RankFinal(completed);
                ReleaseDroppedSnapshots(completed, rankedCompleted);
                completed = rankedCompleted;
                break;
            }

            List<SearchNode> ended = [];
            PublishProgress(active.Min(node => node.Turn), searchedTurnLayers, 0, active.Count, 0,
                "展开回合", force: true);
            for (int playDepth = 0;
                 active.Count > 0 && _run.Expanded < _profile.MaxExpandedNodes;
                 playDepth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!policy.VerifyIncrementalSearch
                    && policy.MemoryPressureSignal.IsLimitReached())
                {
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SEARCH_MEMORY_CHECKPOINT " +
                        $"allocated={policy.MemoryPressureSignal.AllocatedBytes} " +
                        $"limit={policy.MemoryPressureSignal.AllocationLimitBytes} " +
                        $"expanded={_run.Expanded} turn_layer={searchedTurnLayers} play_depth={playDepth}");
                    PublishProgress(
                        _startTurnNumber + searchedTurnLayers,
                        searchedTurnLayers,
                        playDepth,
                        active.Count,
                        ended.Count,
                        "回收内存",
                        force: true);
                    _run.ResetRebuildableCaches(active);
                    parallelExpansionExecutor?.ResetRebuildableCaches();
                    policy.MemoryPressureSignal.ReclaimAndContinue(cancellationToken);
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SEARCH_MEMORY_RESUMED " +
                        $"checkpoint={policy.MemoryPressureSignal.ReclaimCount} " +
                        $"frontier={active.Count} ended={ended.Count} expanded={_run.Expanded} " +
                        $"turn_layer={searchedTurnLayers} play_depth={playDepth}");
                    PublishProgress(
                        _startTurnNumber + searchedTurnLayers,
                        searchedTurnLayers,
                        playDepth,
                        active.Count,
                        ended.Count,
                        "继续搜索",
                        force: true);
                }
                if (!policy.VerifyIncrementalSearch
                    && playDepth > 0
                    && stopwatch.ElapsedMilliseconds >= _profile.SoftTimeBudgetMilliseconds)
                {
                    timeBudgetReached = true;
                    ended.AddRange(active);
                    active = [];
                    break;
                }
                List<SearchNode> nextPlays = [];
                void AcceptExpandedChild(SearchNode node, SearchNode child)
                {
                    if (child.Score > fallback.Score)
                        fallback = child;
                    if (child.IsTerminal || child.Turn > node.Turn)
                    {
                        if (child.PotionCount == 0 && child.Score > potionFreeBoundaryFallbackScore)
                        {
                            potionFreeBoundaryFallback = child;
                            potionFreeBoundaryFallbackScore = child.Score;
                        }
                        else if (child.PotionCount > 0 && child.Score > potionBoundaryFallbackScore)
                        {
                            potionBoundaryFallback = child;
                            potionBoundaryFallbackScore = child.Score;
                        }
                        ended.Add(child);
                    }
                    else
                        nextPlays.Add(child);
                }

                void FinishExpandedParent(SearchNode node)
                {
                    node.Snapshot.ReleaseSimulator();
                    PublishProgress(node.Turn, searchedTurnLayers, playDepth, active.Count + nextPlays.Count,
                        ended.Count, "展开出牌序列");
                }

                void ReleaseNodeLimitSnapshot(SearchNode node)
                {
                    if (!node.Snapshot.HasSimulator)
                        return;
                    node.Snapshot.ReleaseSimulator();
                    _run.NodeLimitSnapshotsReleased++;
                }

                int activeIndex = 0;
                if (expansionParallelism == 1)
                {
                    while (activeIndex < active.Count)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        SearchNode node = active[activeIndex];
                        foreach (SearchNode child in Expand(node))
                        {
                            AcceptExpandedChild(node, child);
                            if (_run.Expanded >= _profile.MaxExpandedNodes)
                                break;
                        }
                        FinishExpandedParent(node);
                        activeIndex++;
                        if (_run.Expanded >= _profile.MaxExpandedNodes)
                            break;
                    }
                }
                else
                {
                    while (activeIndex < active.Count
                           && _run.Expanded < _profile.MaxExpandedNodes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int remainingBudget = _profile.MaxExpandedNodes - _run.Expanded;
                        if (remainingBudget <= 1)
                        {
                            // The legacy iterator intentionally yields only the first child from the
                            // final budget slot. Keep that edge case out of the materialized worker path.
                            while (activeIndex < active.Count)
                            {
                                SearchNode node = active[activeIndex];
                                foreach (SearchNode child in Expand(node))
                                {
                                    AcceptExpandedChild(node, child);
                                    if (_run.Expanded >= _profile.MaxExpandedNodes)
                                        break;
                                }
                                FinishExpandedParent(node);
                                activeIndex++;
                                if (_run.Expanded >= _profile.MaxExpandedNodes)
                                    break;
                            }
                            break;
                        }

                        int acceptedCapacity = Math.Min(expansionParallelism, remainingBudget - 1);
                        List<(SearchNode Node, int WorkerIndex)> entries = [];
                        List<SearchNode> workerNodes = new(acceptedCapacity);
                        while (activeIndex < active.Count && workerNodes.Count < acceptedCapacity)
                        {
                            SearchNode node = active[activeIndex++];
                            int workerIndex = -1;
                            if (TryPrepareParallelExpansion(node))
                            {
                                workerIndex = workerNodes.Count;
                                workerNodes.Add(node);
                            }
                            entries.Add((node, workerIndex));
                        }

                        ExpansionWorkerOutcome[]? outcomes = null;
                        int finishedEntryCount = 0;
                        try
                        {
                            outcomes = parallelExpansionExecutor!.Evaluate(workerNodes);
                            ExpansionWorkerOutcome? failed = outcomes.FirstOrDefault(
                                outcome => outcome.Error != null);
                            failed?.Error!.Throw();

                            foreach ((SearchNode node, int workerIndex) in entries)
                            {
                                if (workerIndex >= 0)
                                {
                                    ExpansionWorkerOutcome outcome = outcomes[workerIndex];
                                    MergeExpansionWorker(outcome);
                                    ExpansionBatch batch = outcome.Batch
                                        ?? throw new InvalidOperationException("并行展开没有返回候选批次。");
                                    CommitExpansionBatch(
                                        node,
                                        batch,
                                        child => AcceptExpandedChild(node, child));
                                }
                                FinishExpandedParent(node);
                                finishedEntryCount++;
                            }
                        }
                        finally
                        {
                            if (outcomes != null)
                            {
                                foreach (ExpansionWorkerOutcome outcome in outcomes)
                                    outcome.Batch?.Dispose();
                            }
                            for (int index = finishedEntryCount; index < entries.Count; index++)
                                entries[index].Node.Snapshot.ReleaseSimulator();
                        }
                    }
                }
                for (; activeIndex < active.Count; activeIndex++)
                    ReleaseNodeLimitSnapshot(active[activeIndex]);
                List<SearchNode> prunedPlays = Prune(nextPlays);
                ReleaseDroppedSnapshots(nextPlays, prunedPlays);
                active = prunedPlays;
                if (_detailedDiagnostics && searchedTurnLayers == 0)
                {
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Debug] ROOT_DEPTH_POTIONS depth={playDepth + 1} " +
                        $"frontier={SummarizePotionCandidates(active)}");
                }
                PublishProgress(_startTurnNumber + searchedTurnLayers, searchedTurnLayers, playDepth,
                    active.Count, ended.Count, "剪枝候选", force: true);
            }

            if (_run.Expanded >= _profile.MaxExpandedNodes)
            {
                foreach (SearchNode node in active)
                {
                    if (!node.Snapshot.HasSimulator)
                        continue;
                    node.Snapshot.ReleaseSimulator();
                    _run.NodeLimitSnapshotsReleased++;
                }
                active = [];
            }

            List<SearchNode> unannotatedEnded = ended;
            ended = AnnotateTurnOutcomes(unannotatedEnded);
            ReleaseDroppedSnapshots(unannotatedEnded, ended);

            List<SearchNode> completedCandidates =
                [.. completed, .. ended.Where(node => node.IsTerminal)];
            List<SearchNode> rankedCompletedCandidates = Retention.RankFinal(completedCandidates);
            ReleaseDroppedSnapshots(completedCandidates, rankedCompletedCandidates);
            completed = rankedCompletedCandidates;
            frontier = Prune(ended.Where(node => !node.IsTerminal));
            foreach (SearchNode node in frontier)
                CaptureContinuation(node);
            List<SearchNode> retainedAfterRound = [.. completed, .. frontier];
            ReleaseDroppedSnapshots(ended, retainedAfterRound);
            searchedTurnLayers++;
            if (_detailedDiagnostics)
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Debug] TURN_LAYER_POTIONS completed_turns={searchedTurnLayers} " +
                    $"frontier={SummarizePotionCandidates(frontier)} " +
                    $"completed={SummarizePotionCandidates(completed)} " +
                    $"touch_choices={SummarizePotionChoiceTargets(frontier, "TOUCH_OF_INSANITY")}");
                if (searchedTurnLayers == 2)
                {
                    foreach (SearchNode candidate in frontier.Where(node => node.PotionCount == 2))
                    {
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Debug] TURN2_DUAL_POTION hp={candidate.Snapshot.PlayerHp} " +
                            $"projected_hp={candidate.Snapshot.ProjectedPlayerHp} " +
                            $"enemy_hp={candidate.Snapshot.EnemyHp} " +
                            $"actions={string.Join(',', candidate.Actions.Select(PolicyActionToken))}");
                    }
                }
            }
            PublishProgress(_startTurnNumber + searchedTurnLayers, searchedTurnLayers, 0,
                frontier.Count, completed.Count, "回合层完成", force: true);
            if (completed.Any(node =>
                    node.Snapshot.AllEnemiesDead
                    && node.PotionCount == 0
                    && node.FutureSoldHp == 0
                    && node.Snapshot.PlayerHp >= initialHp))
            {
                foreach (SearchNode node in frontier)
                    node.Snapshot.ReleaseSimulator();
                frontier = [];
                break;
            }
        }

        List<SearchNode> finalPool = completed.Count == 0 && frontier.Count == 0
            ? [RefreshReleasedFallback(fallback)]
            : [.. completed, .. frontier];
        if (!finalPool.Any(node => node.PotionCount == 0)
            && potionFreeBoundaryFallback != null)
        {
            finalPool.Add(RefreshReleasedFallback(potionFreeBoundaryFallback));
        }
        if (!finalPool.Any(node => node.PotionCount > 0)
            && potionBoundaryFallback != null)
        {
            finalPool.Add(RefreshReleasedFallback(potionBoundaryFallback));
        }
        List<SearchNode> finalCandidates = Retention.RankBest(finalPool, _profile.BeamWidth * 4);
        ReleaseDroppedSnapshots(finalPool, finalCandidates);
        ValidateHistoricalSimulatorsReleased(finalCandidates);
        PublishProgress(_startTurnNumber + searchedTurnLayers, searchedTurnLayers, 0,
            finalCandidates.Count, completed.Count, "复核最终候选", force: true);
        SearchMeasurement finalMeasurement = _run.Performance.Begin();
        List<(SearchNode Node, SimulationSnapshot Snapshot, RouteAnnotations Annotations)> evaluated = finalCandidates
            .Select(node => (Node: node, Snapshot: node.Snapshot, Annotations: BuildRouteAnnotations(node)))
            .ToList();
        bool onlyDeathRoutesFound = evaluated.All(candidate =>
            candidate.Snapshot.PlayerDead || candidate.Snapshot.ProjectedPlayerHp <= 0);
        _run.ReusedNodeSnapshots += evaluated.Count;
        int sellThreshold = SoldHpThreshold();
        FinalPlanSelection ordering = FinalOrdering.Select(
            evaluated,
            initialHp,
            emitDiagnostics: true);
        FinalPlanCandidate selectedCandidate = ordering.Candidate;
        int potionBranchesRejected = ordering.PotionBranchesRejected;
        int potionHpSaved = ordering.PotionHpSaved;
        int potionHpRequired = ordering.PotionHpRequired;
        int annotatedFutureSold = selectedCandidate.Annotations.SoldHpByTurn.Values.Sum();
        if (annotatedFutureSold != selectedCandidate.FutureSold)
        {
            throw new InvalidOperationException(
                $"卖血路径状态不一致：节点累计 {selectedCandidate.FutureSold}，逐回合累计 {annotatedFutureSold}。");
        }
        SearchNode best = selectedCandidate.Node with { Score = selectedCandidate.Score };

        SimulationSnapshot finalSnapshot = selectedCandidate.Snapshot;
        RouteAnnotations annotations = selectedCandidate.Annotations;
        IReadOnlyList<CachedContinuation> continuations = BuildContinuations(best);
        int searchedTurns = Math.Max(1, best.Actions
            .Select(action => action.Turn)
            .DefaultIfEmpty(_startTurnNumber)
            .Max() - _startTurnNumber + 1);
        SearchBoundaryReason boundary = finalSnapshot.BoundaryReason;
        if (boundary == SearchBoundaryReason.None && timeBudgetReached)
            boundary = SearchBoundaryReason.TimeLimit;
        else if (boundary == SearchBoundaryReason.None && _run.Expanded >= _profile.MaxExpandedNodes)
            boundary = SearchBoundaryReason.NodeLimit;
        else if (boundary == SearchBoundaryReason.None
                 && policy.VerifyIncrementalSearch
                 && searchedTurnLayers >= SolverWeights.IncrementalVerificationMaxTurns)
            boundary = SearchBoundaryReason.TurnLimit;
        int futureHpLost = finalSnapshot.CumulativePlayerHpLost;
        int futureUnavoidableHpLost = annotations.HpLostByTurn.Sum(item =>
            Math.Max(0, item.Value - annotations.SoldHpByTurn.GetValueOrDefault(item.Key)));
        int battleUnavoidableHpLost = Math.Max(0, battleDamage.HpLostSoFar - battleDamage.SoldHpCommitted)
            + futureUnavoidableHpLost;
        ActionRelicTriggerRecorder relicTriggerRecorder = new();
        SimulationSnapshot? annotationRoot = _includeTurnSetup
            ? ReplayTurnSetup(best.GetTurnSetupChoices())
            : null;
        SimulationSnapshot annotationReplay = Replay(
            best.Actions,
            annotationRoot,
            _startTurnNumber,
            priorActionCount: 0,
            triggerRecorder: relicTriggerRecorder);
        annotationRoot?.ReleaseSimulator();
        if (annotationReplay.StateKey != finalSnapshot.StateKey
            || annotationReplay.PlayerHp != finalSnapshot.PlayerHp
            || annotationReplay.EnemyHp != finalSnapshot.EnemyHp
            || annotationReplay.BoundaryReason != finalSnapshot.BoundaryReason)
        {
            ContinuationStamp expectedStamp = ContinuationStamp.CapturePredicted(
                _player,
                (CombatPredictionSimulator)finalSnapshot.Simulator,
                finalSnapshot.Turn,
                _forecast,
                _startTurnNumber);
            ContinuationStamp replayStamp = ContinuationStamp.CapturePredicted(
                _player,
                (CombatPredictionSimulator)annotationReplay.Simulator,
                annotationReplay.Turn,
                _forecast,
                _startTurnNumber);
            string difference = expectedStamp.DescribeFirstDifference(replayStamp);
            annotationReplay.ReleaseSimulator();
            throw new InvalidOperationException(
                $"最终路线的遗物标注回放与选中状态不一致：{difference}；" +
                $"hp={finalSnapshot.PlayerHp}/{annotationReplay.PlayerHp} " +
                $"enemy_hp={finalSnapshot.EnemyHp}/{annotationReplay.EnemyHp} " +
                $"boundary={finalSnapshot.BoundaryReason}/{annotationReplay.BoundaryReason}。");
        }
        annotationReplay.ReleaseSimulator();
        PlanAction[] annotatedActions = best.Actions
            .Select((action, actionIndex) => WithDisplayNames(action) with
            {
                RelicEffects = relicTriggerRecorder.ForAction(actionIndex)
                    .Select(trigger => new PlanRelicEffect(
                        trigger.RelicId,
                        displayNames.Relic(trigger.RelicId),
                        trigger.Summary))
                    .ToArray(),
            })
            .ToArray();
        _run.Performance.End(SearchMetricPhase.FinalSelection, finalMeasurement);
        stopwatch.Stop();
        long workerAllocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBytesAtStart
            + _run.OffThreadAllocatedBytes;
        int gen0Collections = GC.CollectionCount(0) - gen0AtStart;
        int gen1Collections = GC.CollectionCount(1) - gen1AtStart;
        int gen2Collections = GC.CollectionCount(2) - gen2AtStart;
        TimeSpan gcPauseDuration = GC.GetTotalPauseDuration() - gcPauseAtStart;
        // 必须在返回前把节点链和模拟器图压平成运行时真正需要的数据。
        // Coordinator 会在深化期间保留短搜结果；这里若返回 SearchNode/SimulationSnapshot，
        // 短搜的全部父链和每步模拟器都会成为长寿命 GC 根。
        SelectedSearchPlan selectedPlan = new(
            annotatedActions,
            best.ActionCount,
            best.Score);
        SolverSnapshot selectedSnapshot = new(
            finalSnapshot.HasRisk,
            finalSnapshot.PlayerDead,
            finalSnapshot.AllEnemiesDead,
            finalSnapshot.PlayerHp,
            finalSnapshot.PlayerMaxHp,
            finalSnapshot.CumulativePlayerHpLost,
            finalSnapshot.LongTermResourceValue,
            finalSnapshot.LongTermGoals,
            finalSnapshot.AngerCopiesGenerated,
            finalSnapshot.ProjectedPlayerHp,
            finalSnapshot.PlayerBlock,
            finalSnapshot.EnemyHp,
            finalSnapshot.AliveEnemyCount,
            finalSnapshot.Energy,
            finalSnapshot.Stars,
            finalSnapshot.HandCount,
            finalSnapshot.OutstandingStolenResource,
            finalSnapshot.Turn,
            finalSnapshot.ShufflesCrossed,
            finalSnapshot.BoundaryReason,
            finalSnapshot.PredictionGaps.ToArray());
        foreach (SearchNode candidate in finalCandidates)
            candidate.Snapshot.ReleaseSimulator();
        return new SolverResult
        {
            SearchPhase = _profile.Phase,
            TotalSearchElapsed = stopwatch.Elapsed,
            TotalWorkerAllocatedBytes = workerAllocatedBytes,
            TotalGen0Collections = gen0Collections,
            TotalGen1Collections = gen1Collections,
            TotalGen2Collections = gen2Collections,
            TotalGcPauseDuration = gcPauseDuration,
            ForkMetric = _run.Performance.Snapshot(SearchMetricPhase.Fork),
            ActionMetric = _run.Performance.Snapshot(SearchMetricPhase.Action),
            CardExecutionMetric = _run.Performance.Snapshot(SearchMetricPhase.CardExecution),
            CardPostProcessingMetric = _run.Performance.Snapshot(SearchMetricPhase.CardPostProcessing),
            PotionExecutionMetric = _run.Performance.Snapshot(SearchMetricPhase.PotionExecution),
            RoundAdvanceMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundAdvance),
            RoundPlayerEndMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundPlayerEnd),
            RoundEndSimulationMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundEndSimulation),
            RoundFlushMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundFlush),
            RoundPlayerEndPowersMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundPlayerEndPowers),
            RoundEnemyTurnMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundEnemyTurn),
            RoundEnemyStartMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundEnemyStart),
            RoundEnemyMovesMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundEnemyMoves),
            RoundEnemyEndPowersMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundEnemyEndPowers),
            RoundPlayerStartMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundPlayerStart),
            RoundDrawMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundDraw),
            SnapshotMetric = _run.Performance.Snapshot(SearchMetricPhase.Snapshot),
            ThreatProjectionMetric = _run.Performance.Snapshot(SearchMetricPhase.ThreatProjection),
            FingerprintMetric = _run.Performance.Snapshot(SearchMetricPhase.Fingerprint),
            ProjectedShuffleMetric = _run.Performance.Snapshot(SearchMetricPhase.ProjectedShuffle),
            PileFingerprintMetric = _run.Performance.Snapshot(SearchMetricPhase.PileFingerprint),
            PileFingerprintMissMetric = _run.Performance.Snapshot(SearchMetricPhase.PileFingerprintMiss),
            CardFingerprintMissMetric = _run.Performance.Snapshot(SearchMetricPhase.CardFingerprintMiss),
            CombatFingerprintMetric = _run.Performance.Snapshot(SearchMetricPhase.CombatFingerprint),
            PruneMetric = _run.Performance.Snapshot(SearchMetricPhase.Prune),
            FinalSelectionMetric = _run.Performance.Snapshot(SearchMetricPhase.FinalSelection),
            StartTurnNumber = _startTurnNumber,
            TurnSetupChoices = best.GetTurnSetupChoices().Select(WithDisplayNames).ToArray(),
            TurnSetupPlayState = best.GetTurnSetupPlayState(),
            BestNode = selectedPlan,
            Snapshot = selectedSnapshot,
            Forecast = _forecast,
            ExpandedNodes = _run.Expanded,
            DominatedActionsPruned = _run.DominatedActionsPruned,
            TopQueueActionsDropped = _run.TopQueueActionsDropped,
            DuplicateCardBranchesPruned = _run.DuplicateCardBranchesPruned,
            ChoiceBranchesEvaluated = _run.ChoiceBranchesEvaluated,
            ShuffleBranchesPruned = _run.ShuffleBranchesPruned,
            SoldHpBranchesPruned = _run.SoldHpBranchesPruned,
            ReplayCount = _run.ReplayCount,
            ForkCount = _run.ForkCount,
            TransitionCount = _run.TransitionCount,
            ReusedNodeSnapshots = _run.ReusedNodeSnapshots,
            TranspositionBranchesPruned = _run.TranspositionBranchesPruned,
            RepeatableNoProgressBranchesPruned = _run.RepeatableNoProgressBranchesPruned,
            StandPatProbes = _run.StandPatProbes,
            ParallelExpansionWaves = _run.ParallelExpansionWaves,
            ParallelExpansionWorkItems = _run.ParallelExpansionWorkItems,
            MaxParallelExpansionConcurrency = _run.MaxParallelExpansionConcurrency,
            NodeLimitSnapshotsReleased = _run.NodeLimitSnapshotsReleased,
            TransitionCacheHits = 0,
            WorkerAllocatedBytes = workerAllocatedBytes,
            Gen0Collections = gen0Collections,
            Gen1Collections = gen1Collections,
            Gen2Collections = gen2Collections,
            GcPauseDuration = gcPauseDuration,
            MaxObservedGcPause = _run.WorkPacer.MaxObservedGcPause,
            WorkerYieldCount = _run.WorkPacer.YieldCount,
            FrameRecoveryWaitCount = _run.WorkPacer.FrameRecoveryWaitCount,
            FrameRecoveryWaitDuration = _run.WorkPacer.FrameRecoveryWaitDuration,
            SearchedTurns = searchedTurns,
            BoundaryReason = boundary,
            UnavoidableHpLost = battleUnavoidableHpLost,
            SoldHp = selectedCandidate.BattleSold,
            FutureSoldHp = selectedCandidate.FutureSold,
            BattleHpLostSoFar = battleDamage.HpLostSoFar,
            ProjectedBattleHpLost = battleDamage.HpLostSoFar + futureHpLost,
            BattlePotionsUsedSoFar = battleDamage.PotionsUsedSoFar,
            PotionCount = selectedCandidate.PotionCount,
            PotionHpSaved = potionHpSaved,
            RequiredLongTermGoals = ordering.RequiredLongTermGoals,
            BankedLongTermGoals = ordering.BankedLongTermGoals,
            LongTermGoalHpPrice = ordering.LongTermGoalHpPrice,
            LongTermGoalPotionPrice = ordering.LongTermGoalPotionPrice,
            WorldLines = ordering.WorldLines,
            LongTermGoalOutcome = ordering.LongTermGoalOutcome,
            CompliantRouteCount = ordering.CompliantRouteCount,
            PotionHpRequired = potionHpRequired,
            PotionBranchesRejected = potionBranchesRejected,
            TheftPolicy = _theftPolicy,
            OutstandingStolenResource = finalSnapshot.OutstandingStolenResource,
            SoldHpThreshold = sellThreshold,
            SoldHpByTurn = annotations.SoldHpByTurn,
            HpLostByTurn = annotations.HpLostByTurn,
            MaxBlockByTurn = annotations.MaxBlockByTurn,
            ActualBlockByTurn = annotations.ActualBlockByTurn,
            EnergyLeftByTurn = annotations.EnergyLeftByTurn,
            PotionCountByTurn = annotations.PotionCountByTurn,
            PotionStrategicCostByTurn = annotations.PotionStrategicCostByTurn,
            KillsAfterAction = annotations.KillsAfterAction,
            CombatEndedTurn = annotations.CombatEndedTurn,
            DeathTurn = annotations.DeathTurn,
            OnlyDeathRoutesFound = onlyDeathRoutesFound,
            IsActEndingBoss = _isActEndingBoss,
            BossHpRelief = _bossHpRelief,
            Elapsed = stopwatch.Elapsed,
            Continuations = continuations,
        };
    }

    private PlanAction WithDisplayNames(PlanAction action)
        => action with
        {
            Choice = action.Choice == null ? null : WithDisplayNames(action.Choice),
            NestedChoices = action.NestedChoices?.Select(WithDisplayNames).ToArray(),
            TurnStartChoices = action.TurnStartChoices?.Select(WithDisplayNames).ToArray(),
        };

    private PlanCardChoice WithDisplayNames(PlanCardChoice choice)
        => choice with
        {
            Cards = choice.Cards
                .Select(card => card with
                {
                    Title = displayNames.Card(card.CardId, card.UpgradeLevel),
                })
                .ToArray(),
        };

}
