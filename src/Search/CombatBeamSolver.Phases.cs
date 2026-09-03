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
        SearchRequestWorkTotals? requestWorkTotals = policy.RequestWorkTotals;
        long startedTimestamp = requestWorkTotals == null ? 0 : Stopwatch.GetTimestamp();
        long allocatedBytesAtStart = requestWorkTotals == null
            ? 0
            : GC.GetAllocatedBytesForCurrentThread();
        int gen0AtStart = requestWorkTotals == null ? 0 : GC.CollectionCount(0);
        int gen1AtStart = requestWorkTotals == null ? 0 : GC.CollectionCount(1);
        int gen2AtStart = requestWorkTotals == null ? 0 : GC.CollectionCount(2);
        TimeSpan gcPauseAtStart = requestWorkTotals == null
            ? TimeSpan.Zero
            : GC.GetTotalPauseDuration();
        try
        {
            return SolveCore();
        }
        finally
        {
            if (requestWorkTotals != null)
            {
                RecordRequestWork(
                    requestWorkTotals,
                    startedTimestamp,
                    allocatedBytesAtStart,
                    gen0AtStart,
                    gen1AtStart,
                    gen2AtStart,
                    gcPauseAtStart);
            }
        }
    }

    private void RecordRequestWork(
        SearchRequestWorkTotals requestWorkTotals,
        long startedTimestamp,
        long allocatedBytesAtStart,
        int gen0AtStart,
        int gen1AtStart,
        int gen2AtStart,
        TimeSpan gcPauseAtStart)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
        bool deepTriggered = false;
        TimeSpan shortElapsed = elapsed;
        if (_shortCheckpointMilliseconds is { } checkpointMilliseconds
            && elapsed.TotalMilliseconds > checkpointMilliseconds)
        {
            deepTriggered = true;
            shortElapsed = TimeSpan.FromMilliseconds(checkpointMilliseconds);
        }
        TimeSpan gcPauseDuration = GC.GetTotalPauseDuration() - gcPauseAtStart;
        requestWorkTotals.Record(new SearchSolverWorkContribution(
            _run.Expanded,
            _run.TransitionCount,
            _run.ChoiceBranchesEvaluated,
            shortElapsed,
            elapsed - shortElapsed,
            Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBytesAtStart)
                + _run.OffThreadAllocatedBytes,
            deepTriggered ? 0 : _run.Expanded,
            deepTriggered ? _run.Expanded : 0,
            deepTriggered ? 0 : _run.TransitionCount,
            deepTriggered ? _run.TransitionCount : 0,
            Math.Max(0, GC.CollectionCount(0) - gen0AtStart),
            Math.Max(0, GC.CollectionCount(1) - gen1AtStart),
            Math.Max(0, GC.CollectionCount(2) - gen2AtStart),
            gcPauseDuration < TimeSpan.Zero ? TimeSpan.Zero : gcPauseDuration,
            _run.WorkPacer.MaxObservedGcPause,
            deepTriggered));
    }

    private SolverResult SolveCore()
    {
        using IDisposable notificationIsolation = SimulationNotificationIsolation.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        if (_minimumPotionUses < 0
            || _maximumPotionUses is { } maximumPotionUses
                && _minimumPotionUses > maximumPotionUses)
        {
            throw new ArgumentOutOfRangeException(
                nameof(_minimumPotionUses),
                "最少用药数必须非负且不能超过最多用药数。");
        }
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
        if (!policy.MemoryPressureSignal.IsEnabled
            && policy.MemoryPressureSignal.ConservativeParallelismRequired)
        {
            // A requested NoGC region that could not be established means system/runtime
            // headroom is already constrained. Do not allocate idle worker lanes above the
            // same two-way cap used by fallback parent/action/choice microbatches.
            expansionParallelism = Math.Min(2, expansionParallelism);
        }

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
        SolverInterimResult? currentBestResult = null;
        SearchNode? currentBestNode = null;
        SolverInterimResult? currentTurnCandidateResult = null;
        SearchNode? currentTurnCandidateNode = null;
        SearchNode? currentTurnPreviewNode = null;
        SolverCurrentTurnPreview? currentTurnPreview = null;
        IReadOnlyList<PlanAction>? publishedCurrentTurnActions = null;
        int currentTurnPreviewVersion = 0;
        SolverSpeculativeRoutePreview? speculativeRoutePreview = null;
        SolverRouteAdoptionSeed? routeAdoptionSeed = null;
        SolverRouteAdoptionSeed? requestedRouteAdoptionSeed = null;
        IReadOnlyList<SearchNode>? interruptedActive = null;
        int routePreviewVersion = 0;
        long lastRoutePreviewAt = System.Environment.TickCount64 - 100;
        bool adoptionReached = false;
        bool currentTurnAdoptionReached = false;
        int initialHp = root.InitialPlayerHp;
        int searchedTurnLayers = 0;
        bool timeBudgetReached = false;
        bool acceptableBattleHpLossReached = false;

        SolverInterimResult SummarizeCandidate(SearchNode node, bool won)
        {

            int ambergrisCount = node.Actions.Count(action =>
                action.Kind == PlanActionKind.UsePotion
                && string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal));
            return new SolverInterimResult(
                Won: won,
                OutstandingStolenResource: node.Snapshot.OutstandingStolenResource,
                ProjectedBattleHpLost: battleDamage.HpLostSoFar
                    + node.Snapshot.CumulativePlayerHpLost,
                StrategicHpDeficit: node.Snapshot.CumulativePlayerHpLost
                    + Math.Max(0, root.InitialPlayerMaxHp - node.Snapshot.PlayerMaxHp),
                PotionStrategicCost: PotionUsePolicy.EffectiveStrategicHpCost(
                    node.PotionStrategicCost,
                    ambergrisCount,
                    root.InitialPlayerMaxHp),
                ProjectedBattlePotionCount: battleDamage.PotionsUsedSoFar + node.PotionCount,
                EnemyHp: node.Snapshot.EnemyHp,
                Score: node.Score,
                CombatEndedTurn: won ? node.Action?.Turn : null);
        }



        IReadOnlyList<SolverFrontierTurn>? BuildFrontierTurns(SearchNode candidate)
        {
            if (candidate.ActionCount == 0)
                return null;
            Dictionary<int, (TurnOutcome Outcome, bool CombatEnded)> outcomesByTurn = [];
            for (SearchNode? node = candidate; node != null; node = node.Parent)
            {
                if (node.Outcome is { } outcome)
                    outcomesByTurn.TryAdd(outcome.Turn, (outcome, node.Snapshot.AllEnemiesDead));
            }
            if (outcomesByTurn.Count == 0)
                return null;

            List<SolverFrontierTurn> turns = new(outcomesByTurn.Count);
            foreach (IGrouping<int, PlanAction> actions in candidate.Actions.GroupBy(action => action.Turn))
            {
                if (!outcomesByTurn.TryGetValue(actions.Key, out var materialized))
                    continue;
                turns.Add(new SolverFrontierTurn(
                    actions.Key,
                    actions.Select(WithDisplayNames).ToArray(),
                    materialized.Outcome.HpLost,
                    materialized.Outcome.EnemyHpLost,
                    materialized.Outcome.EnergyLeft,
                    materialized.CombatEnded));
            }
            turns.Sort((a, b) => a.Turn.CompareTo(b.Turn));
            return turns.Count == 0 ? null : turns;
        }

        static bool FrontierTurnsEqual(
            IReadOnlyList<SolverFrontierTurn>? current,
            IReadOnlyList<SolverFrontierTurn>? next)
        {
            if (ReferenceEquals(current, next))
                return true;
            if (current == null || next == null || current.Count != next.Count)
                return false;
            for (int i = 0; i < current.Count; i++)
            {
                SolverFrontierTurn a = current[i];
                SolverFrontierTurn b = next[i];
                if (a.Turn != b.Turn || a.HpLost != b.HpLost || a.EnemyHpLost != b.EnemyHpLost
                    || a.EnergyLeft != b.EnergyLeft || a.CombatEnded != b.CombatEnded
                    || !a.Actions.SequenceEqual(b.Actions))
                {
                    return false;
                }
            }
            return true;
        }

        SearchNode? FindCurrentTurnBoundary(SearchNode node)
        {
            for (SearchNode? current = node; current?.Parent != null; current = current.Parent)
            {
                if (current.Outcome?.Turn == _startTurnNumber)
                    return current;
            }
            return null;
        }

        void ConsiderCompleteVictory(SearchNode node)
        {
            if (ExplicitPotionUseCount(node) < _minimumPotionUses
                || _enforcePotionDirectives
                    && !_potionStrategy.EvaluateForcedUses(
                            node.Actions,
                            root.HasRenewablePotionShapedRock)
                        .AllForcedUsesSatisfied
                || !SolverInterimResultOrdering.IsCompleteVictory(
                    node.ActionCount,
                    node.Snapshot.AllEnemiesDead,
                    node.Snapshot.PlayerDead,
                    node.Snapshot.ProjectedPlayerHp))
            {
                return;
            }

            SolverInterimResult candidate = SummarizeCandidate(node, won: true);
            if (candidate.ProjectedBattleHpLost <= _acceptableBattleHpLoss)
            {
                acceptableBattleHpLossReached = true;
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] ACCEPTABLE_BATTLE_HP_LOSS_REACHED " +
                    $"projected_battle_hp_lost={candidate.ProjectedBattleHpLost} " +
                    $"threshold={_acceptableBattleHpLoss} " +
                    $"turn={candidate.CombatEndedTurn?.ToString() ?? "-"}");
            }
            if (currentBestResult != null
                && !SolverInterimResultOrdering.IsBetter(candidate, currentBestResult))
            {
                return;
            }
            currentBestResult = candidate;
            currentBestNode = node;
        }

        void ConsiderCurrentTurnCandidate(SearchNode node)
        {
            SearchNode? boundary = FindCurrentTurnBoundary(node);
            if (boundary == null
                || ExplicitPotionUseCount(boundary) < _minimumPotionUses
                || _enforcePotionDirectives
                    && !_potionStrategy.EvaluateForcedUses(
                            boundary.Actions,
                            root.HasRenewablePotionShapedRock)
                        .AllForcedUsesSatisfied
                || !IsCurrentTurnCandidate(
                    boundary.ActionCount,
                    turnBoundaryReached: boundary.Action is { } boundaryAction
                        && (boundaryAction.Kind == PlanActionKind.EndTurn
                            || boundaryAction.EndsPlayerTurn
                            || boundary.Snapshot.AllEnemiesDead),
                    boundary.Snapshot.PlayerDead,
                    boundary.Snapshot.ProjectedPlayerHp))
            {
                return;
            }

            SolverInterimResult candidate = SummarizeCandidate(
                boundary,
                boundary.Snapshot.AllEnemiesDead);
            if (currentTurnCandidateResult != null
                && !SolverInterimResultOrdering.IsBetter(candidate, currentTurnCandidateResult))
            {
                if (ReferenceEquals(boundary, currentTurnCandidateNode)
                    && node.Outcome is { } nodeOutcome
                    && nodeOutcome.Turn > (currentTurnPreviewNode?.Outcome?.Turn ?? int.MinValue))
                {
                    currentTurnPreviewNode = node;
                }
                return;
            }
            currentTurnCandidateResult = candidate;
            currentTurnCandidateNode = boundary;
            currentTurnPreviewNode = node;
        }
        void RefreshCurrentTurnPreview()
        {
            SearchNode? candidate = currentBestNode
                ?? currentTurnPreviewNode
                ?? currentTurnCandidateNode;
            SearchNode? boundary = candidate == null
                ? null
                : FindCurrentTurnBoundary(candidate);
            if (candidate == null || boundary?.Outcome is not { } outcome)
                return;
            PlanAction[] actions = candidate.Actions
                .Where(action => action.Turn == _startTurnNumber)
                .ToArray();
            bool combatEnded = boundary.Snapshot.AllEnemiesDead;
            IReadOnlyList<SolverFrontierTurn>? frontierTurns = BuildFrontierTurns(candidate);
            if (publishedCurrentTurnActions != null
                && publishedCurrentTurnActions.SequenceEqual(actions)
                && currentTurnPreview is { } published
                && published.HpLost == outcome.HpLost
                && published.EnemyHpLost == outcome.EnemyHpLost
                && published.EnergyLeft == outcome.EnergyLeft
                && published.CombatEnded == combatEnded
                && FrontierTurnsEqual(published.FrontierTurns, frontierTurns))
            {
                return;
            }

            publishedCurrentTurnActions = actions;
            currentTurnPreview = new SolverCurrentTurnPreview(
                ++currentTurnPreviewVersion,
                _startTurnNumber,
                actions.Select(WithDisplayNames).ToArray(),
                outcome.HpLost,
                outcome.EnemyHpLost,
                outcome.EnergyLeft,
                combatEnded,
                frontierTurns);
        }



        SolverResult MaterializeSelectedRoute(
            FinalPlanSelection ordering,
            bool onlyDeathRoutesFound,
            SolverResultScope resultScope,
            int candidateSearchedTurnLayers,
            bool candidateTimeBudgetReached,
            IReadOnlyList<PlanAction>? routeAdoptionActions = null)
        {
            SearchMeasurement finalMeasurement = _run.Performance.Begin();
            FinalPlanCandidate publishedCandidate = ordering.Candidate;
            SearchNode materializedNode = publishedCandidate.Node.Snapshot.HasSimulator
                ? publishedCandidate.Node
                : RefreshReleasedFallback(publishedCandidate.Node);
            RouteAnnotations materializedAnnotations = BuildRouteAnnotations(materializedNode);
            FinalPlanCandidate selectedCandidate = publishedCandidate with
            {
                Node = materializedNode,
                Snapshot = materializedNode.Snapshot,
                Features = SearchFeatures.Capture(materializedNode),
                FutureSold = materializedNode.FutureSoldHp,
                BattleSold = battleDamage.SoldHpCommitted + materializedNode.FutureSoldHp,
                PotionCount = materializedNode.PotionCount,
            };
            int potionBranchesRejected = ordering.PotionBranchesRejected;
            int potionHpSaved = ordering.PotionHpSaved;
            int potionHpRequired = ordering.PotionHpRequired;
            int sellThreshold = SoldHpThreshold();
            int annotatedFutureSold = materializedAnnotations.SoldHpByTurn.Values.Sum();
            if (annotatedFutureSold != selectedCandidate.FutureSold)
            {
                throw new InvalidOperationException(
                    $"卖血路径状态不一致：节点累计 {selectedCandidate.FutureSold}，逐回合累计 {annotatedFutureSold}。");
            }
            SearchNode best = selectedCandidate.Node with { Score = selectedCandidate.Score };

            SimulationSnapshot finalSnapshot = selectedCandidate.Snapshot;
            RouteAnnotations annotations = materializedAnnotations;
            IReadOnlyList<CachedContinuation> continuations = BuildContinuations(best);
            int searchedTurns = Math.Max(1, best.Actions
                .Select(action => action.Turn)
                .DefaultIfEmpty(_startTurnNumber)
                .Max() - _startTurnNumber + 1);
            SearchBoundaryReason boundary = finalSnapshot.BoundaryReason;
            if (resultScope != SolverResultScope.RouteAdoption)
            {
                if (boundary == SearchBoundaryReason.None && candidateTimeBudgetReached)
                    boundary = SearchBoundaryReason.TimeLimit;
                else if (boundary == SearchBoundaryReason.None && _run.Expanded >= _profile.MaxExpandedNodes)
                    boundary = SearchBoundaryReason.NodeLimit;
                else if (boundary == SearchBoundaryReason.None
                         && policy.VerifyIncrementalSearch
                         && candidateSearchedTurnLayers >= SolverWeights.IncrementalVerificationMaxTurns)
                    boundary = SearchBoundaryReason.TurnLimit;
            }
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
            RouteAnnotations replayAnnotations = BuildRouteAnnotations(best, relicTriggerRecorder);
            annotations = annotations with { KillsAfterAction = replayAnnotations.KillsAfterAction };
            annotationReplay.ReleaseSimulator();
            IReadOnlyList<PlanAction> annotatedActions = resultScope == SolverResultScope.RouteAdoption
                && routeAdoptionActions != null
                    ? routeAdoptionActions
                    : best.Actions
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
            SolverResult result = new()
            {
                ResultScope = resultScope,
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
                TotalExpandedNodes = _run.Expanded,
                DominatedActionsPruned = _run.DominatedActionsPruned,
                TopQueueActionsDropped = _run.TopQueueActionsDropped,
                ActionAdmissionRepresentativesProtected = _run.ActionAdmissionRepresentativesProtected,
                DuplicateCardBranchesPruned = _run.DuplicateCardBranchesPruned,
                ChoiceBranchesEvaluated = _run.ChoiceBranchesEvaluated,
                TotalChoiceBranchesEvaluated = _run.ChoiceBranchesEvaluated,
                ChoiceReplayAttempts = _run.ChoiceReplayAttempts,
                ChoiceReplayBudgetExhaustions = _run.ChoiceReplayBudgetExhaustions,
                ShuffleBranchesPruned = _run.ShuffleBranchesPruned,
                SoldHpBranchesPruned = _run.SoldHpBranchesPruned,
                HpInvestmentBranchesProtected = _run.HpInvestmentBranchesProtected,
                ReplayCount = _run.ReplayCount,
                ForkCount = _run.ForkCount,
                TransitionCount = _run.TransitionCount,
                TotalTransitionCount = _run.TransitionCount,
                ReusedNodeSnapshots = _run.ReusedNodeSnapshots,
                TranspositionBranchesPruned = _run.TranspositionBranchesPruned,
                RepeatableNoProgressBranchesPruned = _run.RepeatableNoProgressBranchesPruned,
                CycleShapesDetected = _run.CycleShapesDetected,
                CycleProbeContinuationsExpanded = _run.CycleProbeContinuationsExpanded,
                CycleCandidatesProtected = _run.CycleCandidatesProtected,
                CycleContinuationsStopped = _run.CycleContinuationsStopped,
                CrossTurnCandidatesProtected = _run.CrossTurnCandidatesProtected,
                CrossTurnContinuationsStopped = _run.CrossTurnContinuationsStopped,
                PrimaryIncumbentBranchesPruned = _run.PrimaryIncumbentBranchesPruned,
                PrimaryIncumbentUpdates = _run.PrimaryIncumbentUpdates,
                StandPatProbes = _run.StandPatProbes,
                ParallelExpansionWaves = _run.ParallelExpansionWaves,
                ParallelExpansionWorkItems = _run.ParallelExpansionWorkItems,
                MaxParallelExpansionConcurrency = _run.MaxParallelExpansionConcurrency,
                ParallelActionReplayWaves = _run.ParallelActionReplayWaves,
                ParallelActionReplayWorkItems = _run.ParallelActionReplayWorkItems,
                MaxParallelActionReplayConcurrency = _run.MaxParallelActionReplayConcurrency,
                DeferredRoundChoiceActions = _run.DeferredRoundChoiceActions,
                DeferredRoundChoiceLayerWidthTotal = _run.DeferredRoundChoiceLayerWidthTotal,
                MaxDeferredRoundChoiceLayerWidth = _run.MaxDeferredRoundChoiceLayerWidth,
                DeferredRoundChoiceFiniteQuotaFallbacks =
                    _run.DeferredRoundChoiceFiniteQuotaFallbacks,
                DeferredRoundChoiceFinitePrimaryLayers =
                    _run.DeferredRoundChoiceFinitePrimaryLayers,
                DeferredRoundChoiceFinitePendingFallbacks =
                    _run.DeferredRoundChoiceFinitePendingFallbacks,
                ParallelRoundChoiceReplayWaves = _run.ParallelRoundChoiceReplayWaves,
                ParallelRoundChoiceReplayWorkItems = _run.ParallelRoundChoiceReplayWorkItems,
                MaxParallelRoundChoiceReplayConcurrency =
                    _run.MaxParallelRoundChoiceReplayConcurrency,
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
                ExplicitPotionCount = annotatedActions.Count(action =>
                    action.Kind == PlanActionKind.UsePotion),
                PotionHpSaved = potionHpSaved,
                PotionHpRequired = potionHpRequired,
                PotionBranchesRejected = potionBranchesRejected,
                TheftPolicy = _theftPolicy,
                OutstandingStolenResource = finalSnapshot.OutstandingStolenResource,
                SoldHpThreshold = sellThreshold,
                SoldHpByTurn = annotations.SoldHpByTurn,
                HpLostByTurn = annotations.HpLostByTurn,
                EnemyHpLostByTurn = annotations.EnemyHpLostByTurn,
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
                Continuations = resultScope == SolverResultScope.CurrentTurnAdoption ? [] : continuations,
            };
            finalSnapshot.ReleaseSimulator();
            return result;
        }

        SolverSpeculativeRoutePreview BuildRoutePreview(
            FinalPlanSelection selection,
            bool onlyDeathRoutesFound,
            int candidateVersion)
        {
            FinalPlanCandidate selected = selection.Candidate;
            RouteAnnotations annotations = BuildRouteAnnotations(selected.Node);
            List<SearchNode> path = [];
            for (SearchNode? node = selected.Node; node?.Parent != null; node = node.Parent)
                path.Add(node);
            path.Reverse();

            SolverFrontierTurn[] turns = path
                .GroupBy(node => node.Action!.Turn)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    SearchNode[] nodes = group.ToArray();
                    SearchNode first = nodes[0];
                    SearchNode last = nodes[^1];
                    int hpLost = annotations.HpLostByTurn.TryGetValue(
                        group.Key,
                        out int annotatedHpLost)
                            ? annotatedHpLost
                            : Math.Max(
                                0,
                                last.Snapshot.CumulativePlayerHpLost
                                - first.Parent!.Snapshot.CumulativePlayerHpLost);
                    int enemyHpLost = annotations.EnemyHpLostByTurn.TryGetValue(
                        group.Key,
                        out int annotatedEnemyHpLost)
                            ? annotatedEnemyHpLost
                            : last.CumulativeEnemyHpLost - first.Parent!.CumulativeEnemyHpLost;
                    int energyLeft = annotations.EnergyLeftByTurn.TryGetValue(
                        group.Key,
                        out int annotatedEnergyLeft)
                            ? annotatedEnergyLeft
                            : last.Snapshot.Energy;
                    return new SolverFrontierTurn(
                        group.Key,
                        nodes.Select(node => WithDisplayNames(node.Action!)).ToArray(),
                        hpLost,
                        enemyHpLost,
                        energyLeft,
                        annotations.CombatEndedTurn == group.Key);
                })
                .ToArray();
            return new SolverSpeculativeRoutePreview(
                candidateVersion,
                _startTurnNumber,
                battleDamage.PotionsUsedSoFar + selected.Node.PotionCount,
                battleDamage.HpLostSoFar + selected.Snapshot.CumulativePlayerHpLost,
                annotations.CombatEndedTurn.HasValue,
                onlyDeathRoutesFound,
                selected.Snapshot.HasRisk,
                turns);
        }

        void PublishRoutePreview(
            IReadOnlyList<SearchNode> retained,
            IReadOnlyList<SearchNode>? additional = null,
            bool force = false)
        {
            if (progressCallback == null)
                return;
            long now = System.Environment.TickCount64;
            if (!force && now - lastRoutePreviewAt < 100)
                return;
            IEnumerable<SearchNode> pool = additional == null
                ? retained
                : retained.Concat(additional);
            List<SearchNode> viable = pool
                .Where(node => node.ActionCount > 0 && node.Snapshot.HasSimulator)
                .DistinctBy(node => node.Snapshot)
                .ToList();
            if (viable.Count == 0)
                return;
            (SearchNode Node, int RetentionRank)[] savedRanks = viable
                .Select(node => (node, node.RetentionRank))
                .ToArray();
            List<SearchNode> candidates;
            try
            {
                candidates = Retention.RankFinal(viable);
            }
            finally
            {
                foreach ((SearchNode node, int retentionRank) in savedRanks)
                    node.RetentionRank = retentionRank;
            }
            List<(SearchNode Node, SimulationSnapshot Snapshot)> evaluated = candidates
                .Select(node => (Node: node, Snapshot: node.Snapshot))
                .ToList();
            FinalPlanSelection ordering;
            try
            {
                ordering = FinalOrdering.Select(
                    evaluated,
                    root.InitialPlayerHp,
                    emitDiagnostics: false);
            }
            catch (PotionPolicyUnsatisfiedException)
            {
                return;
            }
            bool onlyDeathRoutesFound = evaluated.All(candidate =>
                candidate.Snapshot.PlayerDead || candidate.Snapshot.ProjectedPlayerHp <= 0);
            int candidateVersion = ++routePreviewVersion;
            speculativeRoutePreview = BuildRoutePreview(
                ordering,
                onlyDeathRoutesFound,
                candidateVersion);
            int candidateSearchedTurnLayers = searchedTurnLayers;
            PlanAction[] adoptionActions = speculativeRoutePreview.Turns
                .SelectMany(turn => turn.Actions)
                .ToArray();
            routeAdoptionSeed = new SolverRouteAdoptionSeed(
                candidateVersion,
                adoptionActions,
                () => MaterializeSelectedRoute(
                    ordering,
                    onlyDeathRoutesFound,
                    SolverResultScope.RouteAdoption,
                    candidateSearchedTurnLayers,
                    candidateTimeBudgetReached: false,
                    routeAdoptionActions: adoptionActions));
            lastRoutePreviewAt = System.Environment.TickCount64;
        }

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
                _run.Expanded,
                _profile.MaxExpandedNodes,
                frontierNodes,
                endedNodes,
                elapsedMs,
                _progressPhaseOverride
                ?? $"{(checkpointPhase || _profile.Phase == SolverSearchPhase.Short ? "快速搜索" : "深化搜索")}·{phase}",
                currentBestResult,
                currentTurnPreview,
                speculativeRoutePreview,
                routeAdoptionSeed));
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
            SearchNode? compatibleRoot = ApplyFixedPrefix(root);
            if (compatibleRoot == null)
                continue;
            root = compatibleRoot;
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
        if (frontier.Count == 0)
            throw new InvalidOperationException("固定搜索前缀与全部回合准备选牌分支都不相容。");

        List<SearchNode> completed = [];
        SearchNode fallback = frontier.MaxBy(static node => node.Score)!;
        SearchNode? potionFreeBoundaryFallback = null;
        double potionFreeBoundaryFallbackScore = double.NegativeInfinity;
        SearchNode? potionBoundaryFallback = null;
        double potionBoundaryFallbackScore = double.NegativeInfinity;
        // A cheap first parent is not a safe predictor for the rest of a later play depth.
        // Retain the largest observed parent for the whole search so a new depth cannot
        // immediately rematerialize a wide wave that exceeds the No-GC allocation budget.
        long parentAllocatedHighWater = 64L * 1024 * 1024;
        int reservedTurnLayers = _profile.Phase == SolverSearchPhase.Deep
            && root.EncounterRoomType == RoomType.Boss
                ? SolverWeights.BossEnemyStrengthSuppressionHorizon
                : SolverWeights.StandardEnemyStrengthSuppressionHorizon;

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
            long turnLayerStartedMs = stopwatch.ElapsedMilliseconds;
            int remainingReservedLayers = Math.Max(1, reservedTurnLayers - searchedTurnLayers);
            long remainingSearchMs = Math.Max(
                1,
                _profile.SoftTimeBudgetMilliseconds - turnLayerStartedMs);
            long turnLayerBudgetMs = Math.Max(250, remainingSearchMs / remainingReservedLayers);
            PublishProgress(active.Min(node => node.Turn), searchedTurnLayers, 0, active.Count, 0,
                "展开回合", force: true);
            for (int playDepth = 0;
                 active.Count > 0 && _run.Expanded < _profile.MaxExpandedNodes;
                 playDepth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SearchTakeoverRequest? takeover = _interaction?.CurrentTakeoverRequest;
                if (takeover?.Kind == SearchTakeoverKind.AdoptRoute
                    && takeover.RouteAdoptionSeed != null)
                {
                    requestedRouteAdoptionSeed = takeover.RouteAdoptionSeed;
                    interruptedActive = active;
                    timeBudgetReached = true;
                    break;
                }
                SearchNode? adoptableNode = currentBestNode ?? currentTurnCandidateNode;
                if (takeover?.Kind == SearchTakeoverKind.ApplyCurrentTurn && adoptableNode != null)
                {
                    adoptionReached = true;
                    currentTurnAdoptionReached = currentBestNode == null;
                    timeBudgetReached = true;
                    break;
                }
                if (!policy.VerifyIncrementalSearch
                    && searchedTurnLayers < reservedTurnLayers - 1
                    && playDepth > 0
                    && ended.Count > 0
                    && stopwatch.ElapsedMilliseconds - turnLayerStartedMs >= turnLayerBudgetMs)
                {
                    int forcedEndTurnCandidates = 0;
                    foreach (SearchNode node in active)
                    {
                        foreach (SearchNode endNode in BuildAcceptedEndTurnNodes(node))
                        {
                            ended.Add(endNode);
                            forcedEndTurnCandidates++;
                        }
                        node.Snapshot.ReleaseSimulator();
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] TURN_LAYER_BUDGET " +
                        $"completed_turns={searchedTurnLayers} play_depth={playDepth} " +
                        $"elapsed_ms={stopwatch.ElapsedMilliseconds - turnLayerStartedMs} " +
                        $"budget_ms={turnLayerBudgetMs} forced_end_turn={forcedEndTurnCandidates}");
                    active = [];
                    break;
                }
                if (!policy.VerifyIncrementalSearch
                    && (policy.MemoryPressureSignal.HasUnexpectedNoGcLoss()
                        || policy.MemoryPressureSignal.IsLimitReached()))
                {
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SEARCH_MEMORY_CHECKPOINT " +
                        $"allocated={policy.MemoryPressureSignal.AllocatedBytes} " +
                        $"limit={policy.MemoryPressureSignal.AllocationLimitBytes} " +
                        $"projected_memory_load={policy.MemoryPressureSignal.ProjectedMemoryLoadBytes} " +
                        $"system_memory_limit={policy.MemoryPressureSignal.SystemMemoryLimitBytes} " +
                        $"system_pressure_dominates={policy.MemoryPressureSignal.SystemPressureDominates.ToString().ToLowerInvariant()} " +
                        $"expanded={_run.Expanded} turn_layer={searchedTurnLayers} play_depth={playDepth}");
                    PublishProgress(
                        _startTurnNumber + searchedTurnLayers,
                        searchedTurnLayers,
                        playDepth,
                        active.Count,
                        ended.Count,
                        "内存压力较高，正在整理内存",
                        force: true);
                    // Preserve coordinator transposition order across a GC-only safe point.
                    // Rebuilding it from a mid-search subset would change which later branches win.
                    _run.ResetReclaimableCaches();
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
                    int forcedEndTurnCandidates = 0;
                    foreach (SearchNode node in active)
                    {
                        foreach (SearchNode endNode in BuildAcceptedEndTurnNodes(node))
                        {
                            ended.Add(endNode);
                            forcedEndTurnCandidates++;
                        }
                        node.Snapshot.ReleaseSimulator();
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SEARCH_TIME_BUDGET " +
                        $"completed_turns={searchedTurnLayers} play_depth={playDepth} " +
                        $"elapsed_ms={stopwatch.ElapsedMilliseconds} " +
                        $"budget_ms={_profile.SoftTimeBudgetMilliseconds} " +
                        $"forced_end_turn={forcedEndTurnCandidates}");
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
                        int explicitPotionUses = ExplicitPotionUseCount(child);
                        if (explicitPotionUses == 0 && child.Score > potionFreeBoundaryFallbackScore)
                        {
                            potionFreeBoundaryFallback = child;
                            potionFreeBoundaryFallbackScore = child.Score;
                        }
                        else if (explicitPotionUses > 0 && child.Score > potionBoundaryFallbackScore)
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
                int parallelWaveCapacity = policy.MemoryPressureSignal.ConservativeParallelismRequired
                    ? Math.Min(2, expansionParallelism)
                    : expansionParallelism;

                long ParentAllocationReserve()
                {
                    return parentAllocatedHighWater >= long.MaxValue / 3 * 2
                        ? long.MaxValue
                        : parentAllocatedHighWater + parentAllocatedHighWater / 2;
                }

                long ParallelWaveAllocationReserve(int parentCount)
                {
                    if (parentCount <= 0)
                        return 0;
                    long parentReserve = ParentAllocationReserve();
                    return parentReserve > long.MaxValue / parentCount
                        ? long.MaxValue
                        : parentReserve * parentCount;
                }

                int MemorySafeParallelWaveCapacity(int desiredCapacity)
                {
                    SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
                    if (!signal.IsEnabled)
                    {
                        return signal.ConservativeParallelismRequired
                            ? Math.Min(2, desiredCapacity)
                            : desiredCapacity;
                    }
                    long parentReserve = ParentAllocationReserve();
                    long capacity = signal.AllocationLimitBytes / Math.Max(1, parentReserve);
                    return Math.Max(
                        1,
                        Math.Min(
                            desiredCapacity,
                            capacity >= int.MaxValue ? int.MaxValue : (int)capacity));
                }

                void ObserveParentAllocation(long allocatedBytes)
                {
                    if (allocatedBytes > parentAllocatedHighWater)
                        parentAllocatedHighWater = allocatedBytes;
                }

                void ReclaimAtCommittedBoundary(string reason)
                {
                    SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
                    long allocated = signal.AllocatedBytes;
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SEARCH_MEMORY_CHECKPOINT " +
                        $"reason={reason} allocated={allocated} " +
                        $"limit={signal.AllocationLimitBytes} " +
                        $"projected_memory_load={signal.ProjectedMemoryLoadBytes} " +
                        $"system_memory_limit={signal.SystemMemoryLimitBytes} " +
                        $"system_pressure_dominates={signal.SystemPressureDominates.ToString().ToLowerInvariant()} " +
                        $"parent_reserve={ParentAllocationReserve()} expanded={_run.Expanded} " +
                        $"turn_layer={searchedTurnLayers} play_depth={playDepth}");
                    PublishProgress(
                        _startTurnNumber + searchedTurnLayers,
                        searchedTurnLayers,
                        playDepth,
                        Math.Max(0, active.Count - activeIndex) + nextPlays.Count,
                        ended.Count,
                        "内存压力较高，正在整理内存",
                        force: true);
                    _run.ResetReclaimableCaches();
                    parallelExpansionExecutor?.ResetRebuildableCaches();
                    signal.ReclaimAndContinue(cancellationToken);
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SEARCH_MEMORY_RESUMED " +
                        $"reason={reason} checkpoint={signal.ReclaimCount} " +
                        $"frontier={Math.Max(0, active.Count - activeIndex) + nextPlays.Count} " +
                        $"ended={ended.Count} expanded={_run.Expanded} " +
                        $"turn_layer={searchedTurnLayers} play_depth={playDepth}");
                    PublishProgress(
                        _startTurnNumber + searchedTurnLayers,
                        searchedTurnLayers,
                        playDepth,
                        Math.Max(0, active.Count - activeIndex) + nextPlays.Count,
                        ended.Count,
                        "继续搜索",
                        force: true);
                }

                bool EnsureMemoryForNextCommit(long reservedBytes, string reason)
                {
                    SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
                    if (!policy.VerifyIncrementalSearch && signal.HasUnexpectedNoGcLoss())
                    {
                        ReclaimAtCommittedBoundary("unexpected_no_gc_loss");
                        return signal.IsEnabled;
                    }
                    if (policy.VerifyIncrementalSearch
                        || !signal.IsEnabled
                        || signal.CanReachCommit(reservedBytes))
                    {
                        return true;
                    }
                    bool reserveCanEverFit = reservedBytes <= signal.AllocationLimitBytes;
                    if (signal.AllocatedBytes > 0
                        && (reserveCanEverFit || signal.IsLimitReached()))
                        ReclaimAtCommittedBoundary(reason);
                    return signal.CanReachCommit(reservedBytes);
                }

                void ReclaimAfterCommittedWork(string reason)
                {
                    SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
                    if (!policy.VerifyIncrementalSearch && signal.HasUnexpectedNoGcLoss())
                    {
                        ReclaimAtCommittedBoundary("unexpected_no_gc_loss");
                        return;
                    }
                    bool hasMoreParents = activeIndex < active.Count
                        && _run.Expanded < _profile.MaxExpandedNodes;
                    if (policy.VerifyIncrementalSearch || !signal.IsEnabled || !hasMoreParents)
                        return;
                    long reserve = ParentAllocationReserve();
                    bool reserveCanEverFit = reserve <= signal.AllocationLimitBytes;
                    if (signal.IsLimitReached()
                        || (reserveCanEverFit && !signal.CanReachCommit(reserve)))
                        ReclaimAtCommittedBoundary(reason);
                }

                void ExpandNextSerially()
                {
                    SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
                    EnsureMemoryForNextCommit(ParentAllocationReserve(), "before_serial_parent");
                    long allocatedBefore = signal.AllocatedBytes;
                    SearchNode node = active[activeIndex];
                    foreach (SearchNode child in Expand(node))
                    {
                        AcceptExpandedChild(node, child);
                        if (_run.Expanded >= _profile.MaxExpandedNodes)
                            break;
                    }
                    FinishExpandedParent(node);
                    activeIndex++;
                    ObserveParentAllocation(Math.Max(0, signal.AllocatedBytes - allocatedBefore));
                    ReclaimAfterCommittedWork("after_serial_parent");
                }

                if (expansionParallelism == 1)
                {
                    while (activeIndex < active.Count)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ExpandNextSerially();
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
                                ExpandNextSerially();
                                if (_run.Expanded >= _profile.MaxExpandedNodes)
                                    break;
                            }
                            break;
                        }
                        int desiredCapacity = Math.Min(
                            parallelWaveCapacity,
                            remainingBudget - 1);
                        int acceptedCapacity = MemorySafeParallelWaveCapacity(desiredCapacity);
                        bool materializedParentFits = EnsureMemoryForNextCommit(
                            ParallelWaveAllocationReserve(acceptedCapacity),
                            "before_parallel_wave");
                        if (!materializedParentFits)
                        {
                            ExpandNextSerially();
                            continue;
                        }

                        List<(SearchNode Node, int WorkerIndex)> entries = [];
                        List<SearchNode> workerNodes = new(acceptedCapacity);
                        while (activeIndex < active.Count && workerNodes.Count < acceptedCapacity)
                        {
                            SearchNode node = active[activeIndex];
                            activeIndex++;
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
                        long waveAllocatedBefore = policy.MemoryPressureSignal.AllocatedBytes;
                        try
                        {
                            outcomes = parallelExpansionExecutor!.Evaluate(
                                workerNodes,
                                enableSingleParentActionReplay:
                                    workerNodes.Count == 1);
                            foreach (ExpansionWorkerOutcome outcome in outcomes)
                                MergeExpansionWorker(outcome);
                            ExpansionWorkerOutcome? failed = outcomes.FirstOrDefault(
                                outcome => outcome.Error != null);
                            failed?.Error!.Throw();

                            foreach ((SearchNode node, int workerIndex) in entries)
                            {
                                if (workerIndex >= 0)
                                {
                                    ExpansionWorkerOutcome outcome = outcomes[workerIndex];
                                    ExpansionBatch batch = outcome.Batch
                                        ?? throw new InvalidOperationException("并行展开没有返回候选批次。");
                                    CommitExpansionBatch(
                                        node,
                                        batch,
                                        child => AcceptExpandedChild(node, child));
                                    batch.Dispose();
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
                        long waveAllocated = Math.Max(
                            0,
                            policy.MemoryPressureSignal.AllocatedBytes - waveAllocatedBefore);
                        long reservedWaveBytes = ParallelWaveAllocationReserve(workerNodes.Count);
                        bool waveStayedWithinReserve = waveAllocated <= reservedWaveBytes;
                        if (workerNodes.Count > 0)
                            ObserveParentAllocation(waveAllocated / workerNodes.Count);
                        if (outcomes != null)
                        {
                            foreach (ExpansionWorkerOutcome outcome in outcomes)
                                ObserveParentAllocation(outcome.AllocatedBytes);
                        }
                        if (workerNodes.Count > 1)
                        {
                            // Multiplicative increase / multiplicative decrease. Collapsing straight
                            // back to two lanes after a single heavy wave left most of the user's
                            // requested lanes idle for the following waves.
                            parallelWaveCapacity = waveStayedWithinReserve
                                ? Math.Min(expansionParallelism, parallelWaveCapacity * 2)
                                : Math.Max(
                                    Math.Min(2, expansionParallelism),
                                    parallelWaveCapacity / 2);
                        }
                        ReclaimAfterCommittedWork("after_parallel_wave");
                    }
                }
                for (; activeIndex < active.Count; activeIndex++)
                    ReleaseNodeLimitSnapshot(active[activeIndex]);
                List<SearchNode> prunedPlays = Prune(nextPlays);
                ReleaseDroppedSnapshots(nextPlays, prunedPlays);
                active = prunedPlays;
                PublishRoutePreview(completed, active);
                if (_detailedDiagnostics && searchedTurnLayers == 0)
                {
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Debug] ROOT_DEPTH_POTIONS depth={playDepth + 1} " +
                        $"frontier={SummarizePotionCandidates(active)} " +
                        $"routes={SummarizeDiagnosticRoutes(active, 24)}");
                }
                PublishProgress(_startTurnNumber + searchedTurnLayers, searchedTurnLayers, playDepth,
                    active.Count, ended.Count, "剪枝候选", force: true);
            }
            if (adoptionReached || requestedRouteAdoptionSeed != null)
                break;

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
            foreach (SearchNode candidate in retainedAfterRound)
            {
                ConsiderCompleteVictory(candidate);
                ConsiderCurrentTurnCandidate(candidate);
            }
            if (TightenPrimarySearchIncumbentAtTurnLayer(
                    retainedAfterRound,
                    searchedTurnLayers + 1))
            {
                List<SearchNode> boundedFrontier = ApplyPrimaryIncumbentBound(frontier);
                ReleaseDroppedSnapshots(frontier, boundedFrontier);
                frontier = boundedFrontier;
                retainedAfterRound = [.. completed, .. frontier];
            }
            RefreshCurrentTurnPreview();
            PublishRoutePreview(retainedAfterRound, force: true);
            searchedTurnLayers++;
            if (_detailedDiagnostics)
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Debug] TURN_LAYER_POTIONS completed_turns={searchedTurnLayers} " +
                    $"frontier={SummarizePotionCandidates(frontier)} " +
                    $"completed={SummarizePotionCandidates(completed)} " +
                    $"opening_lineages={SummarizeOpeningLineages(frontier)} " +
                    $"frontier_routes={SummarizeDiagnosticRoutes(frontier, 24)} " +
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
            SearchTakeoverRequest? layerTakeover = _interaction?.CurrentTakeoverRequest;
            if (layerTakeover?.Kind == SearchTakeoverKind.AdoptRoute
                && layerTakeover.RouteAdoptionSeed != null)
            {
                requestedRouteAdoptionSeed = layerTakeover.RouteAdoptionSeed;
                timeBudgetReached = true;
                break;
            }
            if (layerTakeover?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                && (currentBestNode != null || currentTurnCandidateNode != null))
            {
                adoptionReached = true;
                currentTurnAdoptionReached = currentBestNode == null;
                timeBudgetReached = true;
                break;
            }
            if (acceptableBattleHpLossReached)
            {
                foreach (SearchNode node in frontier)
                    node.Snapshot.ReleaseSimulator();
                frontier = [];
                break;
            }
            if (completed.Any(node =>
                    node.Snapshot.AllEnemiesDead
                    && ExplicitPotionUseCount(node) == 0
                    && node.FutureSoldHp == 0
                    && node.Snapshot.CumulativePlayerHpLost == 0
                    && node.Snapshot.PlayerMaxHp >= root.InitialPlayerMaxHp))
            {
                foreach (SearchNode node in frontier)
                    node.Snapshot.ReleaseSimulator();
                frontier = [];
                break;
            }
        }

        if (requestedRouteAdoptionSeed != null)
        {
            foreach (SearchNode candidate in completed)
                candidate.Snapshot.ReleaseSimulator();
            foreach (SearchNode candidate in frontier)
                candidate.Snapshot.ReleaseSimulator();
            if (interruptedActive != null)
            {
                foreach (SearchNode candidate in interruptedActive)
                    candidate.Snapshot.ReleaseSimulator();
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_ROUTE_ADOPTION_CHECKPOINT " +
                $"candidate_version={requestedRouteAdoptionSeed.CandidateVersion} " +
                $"expanded={_run.Expanded}");
            return requestedRouteAdoptionSeed.Materialize();
        }

        List<SearchNode> finalPool;
        SearchNode? adoptedNode = currentBestNode ?? currentTurnCandidateNode;
        if (adoptionReached && adoptedNode != null)
        {
            SearchNode adopted = RefreshReleasedFallback(adoptedNode);
            List<SearchNode> remaining = [.. completed, .. frontier];
            ReleaseDroppedSnapshots(remaining, [adopted]);
            finalPool = [adopted];
            SolverInterimResult adoptedSummary = currentBestResult ?? currentTurnCandidateResult!;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_CHECKPOINT_ADOPTED " +
                $"scope={(currentTurnAdoptionReached ? "current_turn" : "complete_victory")} " +
                $"potions={adoptedSummary.ProjectedBattlePotionCount} " +
                $"projected_battle_hp_lost={adoptedSummary.ProjectedBattleHpLost} " +
                $"expanded={_run.Expanded}");
        }
        else
        {
            finalPool = completed.Count == 0 && frontier.Count == 0
                ? [RefreshReleasedFallback(fallback)]
                : [.. completed, .. frontier];
        }
        if (!adoptionReached
            && !finalPool.Any(node => ExplicitPotionUseCount(node) == 0)
            && potionFreeBoundaryFallback != null)
        {
            finalPool.Add(RefreshReleasedFallback(potionFreeBoundaryFallback));
        }
        if (!adoptionReached
            && !finalPool.Any(node => ExplicitPotionUseCount(node) > 0)
            && potionBoundaryFallback != null)
        {
            finalPool.Add(RefreshReleasedFallback(potionBoundaryFallback));
        }
        List<SearchNode> finalCandidates = Retention.RankFinal(finalPool);
        ReleaseDroppedSnapshots(finalPool, finalCandidates);
        ValidateHistoricalSimulatorsReleased(finalCandidates);
        PublishProgress(_startTurnNumber + searchedTurnLayers, searchedTurnLayers, 0,
            finalCandidates.Count, completed.Count, "复核最终候选", force: true);
        List<(SearchNode Node, SimulationSnapshot Snapshot)> evaluated = finalCandidates
            .Select(node => (Node: node, Snapshot: node.Snapshot))
            .ToList();
        bool onlyDeathRoutesFound = evaluated.All(candidate =>
            candidate.Snapshot.PlayerDead || candidate.Snapshot.ProjectedPlayerHp <= 0);
        _run.ReusedNodeSnapshots += evaluated.Count;
        FinalPlanSelection ordering = FinalOrdering.Select(
            evaluated,
            initialHp,
            emitDiagnostics: true);
        SolverResult result = MaterializeSelectedRoute(
            ordering,
            onlyDeathRoutesFound,
            currentTurnAdoptionReached
                ? SolverResultScope.CurrentTurnAdoption
                : SolverResultScope.SearchCompletion,
            searchedTurnLayers,
            timeBudgetReached);
        foreach (SearchNode candidate in finalCandidates)
            candidate.Snapshot.ReleaseSimulator();
        return result;
    }

    private SearchNode? ApplyFixedPrefix(SearchNode seed)
    {
        SearchNode node = seed;
        foreach (PlanAction action in _fixedPrefixActions)
        {
            if (action.Kind == PlanActionKind.EndTurn
                || action.EndsPlayerTurn
                || action.Turn != node.Turn)
            {
                throw new InvalidOperationException(
                    $"固定搜索前缀动作无效：kind={action.Kind} actionTurn={action.Turn} " +
                    $"nodeTurn={node.Turn} endsPlayerTurn={action.EndsPlayerTurn} " +
                    $"card={(string.IsNullOrEmpty(action.CardId) ? "-" : action.CardId)} " +
                    $"potion={(string.IsNullOrEmpty(action.PotionId) ? "-" : action.PotionId)}。");
            }

            if (!CanApplyFixedPrefixAction(node, action))
            {
                node.Snapshot.ReleaseSimulator();
                return null;
            }

            SimulationSnapshot snapshot = Replay(
                [action],
                node.Snapshot,
                node.Turn,
                node.ActionCount);
            bool terminal = snapshot.PlayerDead
                || snapshot.AllEnemiesDead
                || snapshot.BoundaryReason != SearchBoundaryReason.None;
            SearchRouteTraits traits = action.Kind == PlanActionKind.UsePotion
                ? ClassifyPotionTraits(node.Traits, node.Snapshot, snapshot)
                : node.Traits;
            node = new SearchNode(
                action,
                node.ActionCount + 1,
                snapshot.PotionUseCount,
                snapshot.PotionStrategicCost,
                node.Turn,
                traits,
                node.FutureSoldHp,
                ApplySoldHpPenalty(snapshot.Score, node.FutureSoldHp),
                snapshot.StateKey,
                snapshot.HasRisk,
                snapshot.BoundaryReason,
                terminal,
                node,
                snapshot,
                node.CombatProgress)
            {
                CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, snapshot),
            };
            node.Parent!.Snapshot.ReleaseSimulator();
        }
        return node;
    }

    private bool CanApplyFixedPrefixAction(SearchNode node, PlanAction action)
    {
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)node.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (action.Kind == PlanActionKind.UsePotion)
        {
            PotionModel? potion = combat.GetPotionAtSlot(_player, action.PotionSlot);
            return potion != null
                && string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal)
                && combat.IsPotionAvailable(_player, action.PotionSlot);
        }

        SimPlayerCombatState player = simulator.State.GetPlayerCombatState(_player);
        PredictedCard? card = FindCardForReplay(player.Hand.Cards, action);
        return card != null && combat.CanPlayCard(simulator, card);
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

    internal static bool IsCurrentTurnCandidate(
        int actionCount,
        bool turnBoundaryReached,
        bool playerDead,
        int projectedPlayerHp)
        => actionCount > 0
            && turnBoundaryReached
            && !playerDead
            && projectedPlayerHp > 0;
}
