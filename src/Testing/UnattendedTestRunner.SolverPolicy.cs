using System.Runtime;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertPerformancePreset(SolverPerformancePreset preset)
    {
        SolverSettingsSnapshot snapshot = SolverSettings.Capture();
        if (SolverSettings.ResolvePerformancePreset(SolverSettings.Current) != preset)
            throw new InvalidOperationException($"性能预设没有保持为 {preset}。");
        if (preset == SolverPerformancePreset.Custom)
        {
            _completedChecks.Add(
                $"PerformancePreset:Custom:{snapshot.ShortProfile.SoftTimeBudgetMilliseconds}/" +
                $"{snapshot.DeepProfile.SoftTimeBudgetMilliseconds}ms:" +
                $"Beam={snapshot.ShortProfile.BeamWidth}/{snapshot.DeepProfile.BeamWidth}:" +
                $"Nodes={snapshot.ShortProfile.MaxExpandedNodes}/{snapshot.DeepProfile.MaxExpandedNodes}:" +
                $"Branches={snapshot.ShortProfile.MaxCardBranchesPerNode}/" +
                $"{snapshot.DeepProfile.MaxCardBranchesPerNode}:" +
                $"NoGC={snapshot.EnableNoGcRegion}/{snapshot.NoGcRegionBudgetBytes}");
            return;
        }
        (int ShortMs, int DeepMs, int ShortBeam, int DeepBeam, int ShortNodes, int DeepNodes,
            int ShortBranches, int DeepBranches) expected = preset switch
        {
            SolverPerformancePreset.Low => (5_000, 60_000, 18, 45, 1_200, 6_000, 14, 24),
            SolverPerformancePreset.Medium => (8_000, 120_000, 24, 60, 2_400, 12_000, 20, 32),
            SolverPerformancePreset.High => (12_000, 180_000, 36, 90, 5_000, 25_000, 30, 48),
            SolverPerformancePreset.VeryHigh => (20_000, 300_000, 54, 135, 10_000, 50_000, 45, 72),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
        if (snapshot.ShortProfile.SoftTimeBudgetMilliseconds != expected.ShortMs
            || snapshot.DeepProfile.SoftTimeBudgetMilliseconds != expected.DeepMs
            || snapshot.ShortProfile.BeamWidth != expected.ShortBeam
            || snapshot.DeepProfile.BeamWidth != expected.DeepBeam
            || snapshot.ShortProfile.MaxExpandedNodes != expected.ShortNodes
            || snapshot.DeepProfile.MaxExpandedNodes != expected.DeepNodes
            || snapshot.ShortProfile.MaxCardBranchesPerNode != expected.ShortBranches
            || snapshot.DeepProfile.MaxCardBranchesPerNode != expected.DeepBranches)
        {
            throw new InvalidOperationException($"性能预设 {preset} 解析结果与固定规格不一致。");
        }
        _completedChecks.Add(
            $"PerformancePreset:{preset}:{expected.ShortMs}/{expected.DeepMs}ms:" +
            $"Beam={expected.ShortBeam}/{expected.DeepBeam}:Nodes={expected.ShortNodes}/{expected.DeepNodes}:" +
            $"Branches={expected.ShortBranches}/{expected.DeepBranches}:" +
            $"NoGC={snapshot.EnableNoGcRegion}/{snapshot.NoGcRegionBudgetBytes}");
    }

    private void AssertAppliedNoGcConfiguration()
    {
        if (!_request.PerformancePresetForTest.HasValue
            && !_request.EnableNoGcRegionForTest.HasValue
            && !_request.NoGcRegionBudgetGigabytesForTest.HasValue)
        {
            return;
        }

        SolverSettingsSnapshot configured = SolverSettings.Capture();
        bool actualActive = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
        long actualBudget = SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting;
        long establishedBudget = SearchGcPolicy.LastEstablishedNoGcRegionBudgetBytesForTesting;
        if (configured.EnableNoGcRegion)
        {
            if (!actualActive
                || actualBudget <= 0
                || actualBudget > configured.NoGcRegionBudgetBytes
                || establishedBudget <= 0
                || establishedBudget > configured.NoGcRegionBudgetBytes)
            {
                throw new InvalidOperationException(
                    $"No-GC 搜索没有按配置建立并保留战斗级区域：" +
                    $"configured_enabled=true configured_budget={configured.NoGcRegionBudgetBytes} " +
                    $"established_budget={establishedBudget} " +
                    $"actual_active={actualActive} actual_budget={actualBudget} " +
                    $"latency={GCSettings.LatencyMode}。");
            }
        }
        else if (actualActive || actualBudget != 0)
        {
            throw new InvalidOperationException(
                $"关闭 No-GC 后仍保留运行时区域：" +
                $"configured_budget={configured.NoGcRegionBudgetBytes} " +
                $"actual_active={actualActive} actual_budget={actualBudget} " +
                $"latency={GCSettings.LatencyMode}。");
        }

        _completedChecks.Add(
            $"NoGcConfigurationApplied:Configured={configured.EnableNoGcRegion}/" +
            $"{configured.NoGcRegionBudgetBytes}:Established={establishedBudget}:" +
            $"Actual={actualActive}/{actualBudget}:" +
            $"Latency={GCSettings.LatencyMode}");
    }

    private bool HasInitialSolverExpectation()
        => _request.ExpectedInitialSoldHp.HasValue
            || _request.ExpectedInitialSoldHpAtMost.HasValue
            || _request.ExpectedInitialSoldHpBranchesPrunedAtLeast.HasValue
            || _request.ExpectedInitialActionAdmissionRepresentativesProtectedAtLeast.HasValue
            || _request.ExpectedInitialHpInvestmentBranchesProtectedAtLeast.HasValue
            || _request.ExpectedInitialPotionCount.HasValue
            || _request.ExpectedInitialPotionHpSavedAtLeast.HasValue
            || _request.ExpectedInitialPotionBranchesRejectedAtLeast.HasValue
            || _request.ExpectedInitialTheftPolicy.HasValue
            || _request.ExpectedInitialOutstandingStolenResource.HasValue
            || _request.ExpectedInitialSearchedTurnsAtLeast.HasValue
            || _request.ExpectedInitialShufflesCrossedAtLeast.HasValue
            || _request.ExpectedInitialUnmirroredCount.HasValue
            || _request.ExpectedInitialHpLostAtMost.HasValue
            || _request.ExpectedInitialProjectedBattleHpLost.HasValue
            || _request.ExpectedInitialProjectedBattleHpLostAtMost.HasValue
            || _request.ExpectedInitialLongTermResourceValueAtLeast.HasValue
            || _request.ExpectedInitialFinalMaxHp.HasValue
            || _request.ExpectedInitialMaxBlockAtLeast.HasValue
            || _request.ExpectedInitialActualBlockAtLeast.HasValue
            || _request.ExpectedInitialSearchPhase.HasValue
            || _request.ExpectedInitialDeepSearchTriggered.HasValue
            || _request.ExpectedInitialDeepSearchImprovedResult.HasValue
            || _request.ExpectedInitialTotalElapsedMillisecondsAtMost.HasValue
            || _request.ExpectedInitialTotalAllocatedBytesAtMost.HasValue
            || _request.ExpectedInitialGen2CollectionsAtMost.HasValue
            || _request.ExpectedInitialTotalGcPauseMillisecondsAtMost.HasValue
            || _request.ExpectedInitialMaxGcPauseMillisecondsAtMost.HasValue
            || _request.ExpectedInitialMaxMainThreadFrameGapMillisecondsAtMost.HasValue
            || _request.ExpectedInitialMainThreadFramesOver50MillisecondsAtMost.HasValue
            || _request.ExpectedInitialMainThreadFramesOver100MillisecondsAtMost.HasValue
            || _request.ExpectedInitialTransitionCacheHitsAtLeast.HasValue
            || _request.ExpectedInitialRepeatableNoProgressBranchesPrunedAtLeast.HasValue
            || _request.ExpectedInitialCycleShapesDetectedAtLeast.HasValue
            || _request.ExpectedInitialCycleProbeContinuationsExpandedAtLeast.HasValue
            || _request.ExpectedInitialCycleCandidatesProtectedAtLeast.HasValue
            || _request.ExpectedInitialCycleContinuationsStoppedAtLeast.HasValue
            || _request.ExpectedInitialCrossTurnCandidatesProtectedAtLeast.HasValue
            || _request.ExpectedInitialCrossTurnContinuationsStoppedAtLeast.HasValue
            || _request.ExpectedInitialNodeLimitSnapshotsReleasedAtLeast.HasValue
            || _request.ExpectedInitialChoiceBranchesEvaluatedAtLeast.HasValue
            || _request.ExpectedInitialExecutableActionCountAtLeast.HasValue
            || _request.ExpectedInitialOnlyDeathRoutesFound.HasValue
            || _request.ExpectedInitialCombatEndedTurn.HasValue
            || _request.ExpectedInitialDeathTurn.HasValue
            || _request.ExpectedInitialDeathTurnAtLeast.HasValue
            || _request.ExpectedInitialFinalEnemyHpAtMost.HasValue
            || _request.ExpectedInitialActEndingBoss.HasValue
            || _request.ExpectedInitialBossHpRelief.HasValue
            || _request.ExpectedFullAutoPausedAtDeathTurn
            || _request.ExpectedInitialSetupChoiceCountAtLeast.HasValue
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialSetupChoiceSourceId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialPlannedChoiceCardId)
            || _request.ExpectedInitialTurnStartChoiceTurn.HasValue
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceSourceId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceCardId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceStateContains)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceStateExcludes)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialRelicEffectId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialRelicEffectSummary)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialActionCardId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialAbsentActionCardId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialFirstActionCardId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialFirstActionPotionId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialActionTitle)
            || _request.ExpectedInitialActionReplayCount.HasValue
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialSetupChoiceTextStartsWith);

    private async Task AssertInitialSolverResultAsync(int startedTurn)
    {
        SetStage("assert_initial_solver_result");
        bool expectsInitialSetup = _request.ExpectedInitialSetupChoiceCountAtLeast.HasValue
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialSetupChoiceSourceId);
        SolverResult? result;
        while ((result = expectsInitialSetup
                   ? SolverController.LastTurnSetupResultForTesting
                   : SolverController.LastCompletedResultForTesting) == null
               || result.StartTurnNumber != startedTurn)
        {
            if (SolverController.LastSearchFailureForTesting is { } searchFailure)
                throw new InvalidOperationException("后台搜索在首轮断言前失败。", searchFailure);
            EnsureWithinDeadline();
            await NextFrameAsync();
        }

        _writer.CaptureSolverResult(result);
        AssertAppliedNoGcConfiguration();
        long reviewedWorldlines = (long)result.ShortExpandedNodes + result.DeepExpandedNodes;
        SolverOverlaySnapshot reviewSnapshot = SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
            result,
            unexpectedReplan: false,
            reviewedWorldlines);
        bool validReviewSummary = result.WasReused
            ? reviewSnapshot.ReviewSummaryText.StartsWith("路线已复用，共查阅了 ", StringComparison.Ordinal)
            : reviewSnapshot.ReviewSummaryText.StartsWith("花费了 ", StringComparison.Ordinal)
                && reviewSnapshot.ReviewSummaryText.Contains("秒，共查阅了 ", StringComparison.Ordinal);
        if (!validReviewSummary
            || !reviewSnapshot.ReviewSummaryText.EndsWith(" 条世界线", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("搜索完成快照没有生成耗时与世界线汇总。");
        }
        _completedChecks.Add("InitialWorldlineSummary");

        if (_request.ExpectedInitialSetupChoiceCountAtLeast is { } minimumTurnSetupChoices
            && result.TurnSetupChoices.Count < minimumTurnSetupChoices)
        {
            throw new InvalidOperationException(
                $"首回合准备阶段仅计划 {result.TurnSetupChoices.Count} 个选牌，低于下限 {minimumTurnSetupChoices}。");
        }
        if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialSetupChoiceSourceId)
            && !result.TurnSetupChoices.Any(choice => choice.SourceId.Equals(
                _request.ExpectedInitialSetupChoiceSourceId,
                StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"首回合准备阶段没有计划 {_request.ExpectedInitialSetupChoiceSourceId} 的选牌。");
        }
        if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialSetupChoiceTextStartsWith))
        {
            SolverOverlayTurnSnapshot firstTurn = SolverOverlaySnapshot.Capture(result, unexpectedReplan: false)
                .Turns
                .First(turn => turn.Turn == startedTurn);
            if (!firstTurn.TurnStartChoices.Any(choice => choice.StartsWith(
                    _request.ExpectedInitialSetupChoiceTextStartsWith,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"首回合路线没有以“{_request.ExpectedInitialSetupChoiceTextStartsWith}”开头的准备选牌胶囊；" +
                    $"实际={string.Join('|', firstTurn.TurnStartChoices)}。");
            }
            _completedChecks.Add("InitialSetupChoicePill");
        }
        if (expectsInitialSetup)
        {
            NativeChoiceTrace[] traces = NativeChoiceRuntime.TraceSnapshotForTesting
                .Where(trace => trace.Owner == $"turn_setup:{startedTurn}")
                .ToArray();
            long visible = traces.Where(trace => trace.Stage == "Visible")
                .Select(trace => trace.Order)
                .DefaultIfEmpty(long.MaxValue)
                .Min();
            long searchStarted = traces.Where(trace => trace.Stage == "SearchStarted")
                .Select(trace => trace.Order)
                .DefaultIfEmpty(long.MaxValue)
                .Min();
            long planReady = traces.Where(trace => trace.Stage == "PlanReady")
                .Select(trace => trace.Order)
                .DefaultIfEmpty(long.MaxValue)
                .Min();
            long selected = traces.Where(trace => trace.Stage == "Selected")
                .Select(trace => trace.Order)
                .DefaultIfEmpty(long.MaxValue)
                .Min();
            if (!(visible < searchStarted && searchStarted < planReady && planReady < selected))
            {
                throw new InvalidOperationException(
                    $"回合准备选牌事件顺序错误：visible={visible}, search={searchStarted}, " +
                    $"plan_ready={planReady}, selected={selected}。 ");
            }
            _completedChecks.Add("TurnSetupNativeChoiceOrder");
        }

        if (_request.ExpectedInitialSoldHp is { } expectedSoldHp && result.SoldHp != expectedSoldHp)
        {
            throw new InvalidOperationException(
                $"首轮路线累计卖血为 {result.SoldHp}，预期为 {expectedSoldHp}。");
        }
        if (_request.ExpectedInitialSoldHpAtMost is { } maximumSoldHp && result.SoldHp > maximumSoldHp)
        {
            throw new InvalidOperationException(
                $"首轮路线累计卖血为 {result.SoldHp}，超过预期上限 {maximumSoldHp}。");
        }
        if (_request.ExpectedInitialSoldHpBranchesPrunedAtLeast is { } minimumPruned
            && result.SoldHpBranchesPruned < minimumPruned)
        {
            throw new InvalidOperationException(
                $"首轮卖血预算剪枝为 {result.SoldHpBranchesPruned}，低于预期下限 {minimumPruned}。");
        }
        if (_request.ExpectedInitialActionAdmissionRepresentativesProtectedAtLeast is { } minimumProtected
            && result.ActionAdmissionRepresentativesProtected < minimumProtected)
        {
            throw new InvalidOperationException(
                $"首轮动作分族保路为 {result.ActionAdmissionRepresentativesProtected}，" +
                $"低于预期下限 {minimumProtected}。");
        }
        if (_request.ExpectedInitialHpInvestmentBranchesProtectedAtLeast is { } minimumInvestments
            && result.HpInvestmentBranchesProtected < minimumInvestments)
        {
            throw new InvalidOperationException(
                $"首轮生命投资保路为 {result.HpInvestmentBranchesProtected}，" +
                $"低于预期下限 {minimumInvestments}。");
        }
        if (_request.ExpectedInitialPotionCount is { } expectedPotionCount
            && result.PotionCount != expectedPotionCount)
        {
            throw new InvalidOperationException(
                $"首轮路线使用药水 {result.PotionCount} 瓶，预期为 {expectedPotionCount} 瓶。");
        }
        if (_request.ExpectedInitialTheftPolicy is { } expectedTheftPolicy
            && result.TheftPolicy != expectedTheftPolicy)
        {
            throw new InvalidOperationException(
                $"首轮偷窃策略为 {result.TheftPolicy?.ToString() ?? "-"}，预期为 {expectedTheftPolicy}。");
        }
        if (_request.ExpectedInitialTheftPolicy.HasValue && !SolverOverlay.TheftPolicyVisibleForTesting)
            throw new InvalidOperationException("偷窃战斗中没有显示保资源/放走策略按钮。");
        if (_request.ExpectedInitialOutstandingStolenResource is { } expectedOutstanding
            && result.OutstandingStolenResource != expectedOutstanding)
        {
            throw new InvalidOperationException(
                $"首轮路线结束时未追回资源为 {result.OutstandingStolenResource}，预期为 {expectedOutstanding}。");
        }
        if (_request.ExpectedInitialPotionHpSavedAtLeast is { } minimumPotionHpSaved
            && result.PotionHpSaved < minimumPotionHpSaved)
        {
            throw new InvalidOperationException(
                $"首轮路线预计由药水省血 {result.PotionHpSaved}，低于预期 {minimumPotionHpSaved}。");
        }
        if (_request.ExpectedInitialPotionBranchesRejectedAtLeast is { } minimumPotionBranchesRejected
            && result.PotionBranchesRejected < minimumPotionBranchesRejected)
        {
            throw new InvalidOperationException(
                $"首轮药水门槛仅淘汰 {result.PotionBranchesRejected} 条候选，低于预期 {minimumPotionBranchesRejected}。");
        }
        if (_request.ExpectedInitialSearchedTurnsAtLeast is { } minimumSearchedTurns
            && result.SearchedTurns < minimumSearchedTurns
            && !result.CombatEndedTurn.HasValue)
        {
            throw new InvalidOperationException(
                $"首轮路线仅搜索 {result.SearchedTurns} 回合，低于预期 {minimumSearchedTurns}。");
        }
        if (_request.ExpectedInitialShufflesCrossedAtLeast is { } minimumShuffles
            && result.Snapshot.ShufflesCrossed < minimumShuffles)
        {
            throw new InvalidOperationException(
                $"首轮路线仅跨过 {result.Snapshot.ShufflesCrossed} 次洗牌，低于预期 {minimumShuffles}。");
        }
        int unmirroredCount = result.Snapshot.PredictionGaps.Count(gap => !gap.Compensated);
        if (_request.ExpectedInitialUnmirroredCount is { } expectedUnmirroredCount
            && unmirroredCount != expectedUnmirroredCount)
        {
            throw new InvalidOperationException(
                $"首轮路线有 {unmirroredCount} 项未镜像效果，预期为 {expectedUnmirroredCount} 项。");
        }
        int initialHpLost = result.HpLostByTurn.GetValueOrDefault(startedTurn);
        int initialMaxBlock = result.MaxBlockByTurn.GetValueOrDefault(startedTurn);
        int initialActualBlock = result.ActualBlockByTurn.GetValueOrDefault(startedTurn);
        if (_request.ExpectedInitialHpLostAtMost is { } maximumHpLost && initialHpLost > maximumHpLost)
        {
            throw new InvalidOperationException(
                $"首轮路线预计掉血 {initialHpLost}，超过预期上限 {maximumHpLost}。");
        }
        if (_request.ExpectedInitialProjectedBattleHpLost is { } expectedProjectedBattleHpLost
            && result.ProjectedBattleHpLost != expectedProjectedBattleHpLost)
        {
            throw new InvalidOperationException(
                $"首轮路线预计整场掉血 {result.ProjectedBattleHpLost}，预期为 {expectedProjectedBattleHpLost}。");
        }
        if (_request.ExpectedInitialProjectedBattleHpLostAtMost is { } maximumProjectedBattleHpLost
            && result.ProjectedBattleHpLost > maximumProjectedBattleHpLost)
        {
            throw new InvalidOperationException(
                $"首轮路线预计整场掉血 {result.ProjectedBattleHpLost}，超过预期上限 {maximumProjectedBattleHpLost}。");
        }
        if (_request.ExpectedInitialLongTermResourceValueAtLeast is { } minimumLongTermResource
            && result.Snapshot.LongTermResourceValue < minimumLongTermResource)
        {
            throw new InvalidOperationException(
                $"首轮路线长期资源价值为 {result.Snapshot.LongTermResourceValue}，" +
                $"低于预期 {minimumLongTermResource}。");
        }
        if (_request.ExpectedInitialFinalMaxHp is { } expectedFinalMaxHp
            && result.Snapshot.PlayerMaxHp != expectedFinalMaxHp)
        {
            throw new InvalidOperationException(
                $"首轮路线终局最大生命为 {result.Snapshot.PlayerMaxHp}，预期为 {expectedFinalMaxHp}。");
        }
        if (_request.ExpectedInitialMaxBlockAtLeast is { } minimumMaxBlock && initialMaxBlock < minimumMaxBlock)
        {
            throw new InvalidOperationException(
                $"首轮最高可起防仅 {initialMaxBlock}，低于预期 {minimumMaxBlock}。");
        }
        if (_request.ExpectedInitialActualBlockAtLeast is { } minimumActualBlock && initialActualBlock < minimumActualBlock)
        {
            throw new InvalidOperationException(
                $"首轮路线实际起防仅 {initialActualBlock}，低于预期 {minimumActualBlock}。");
        }
        if (_request.ExpectedInitialSearchPhase is { } expectedPhase && result.SearchPhase != expectedPhase)
            throw new InvalidOperationException($"首轮最终采用 {result.SearchPhase}，预期为 {expectedPhase}。");
        if (_request.ExpectedInitialDeepSearchTriggered is { } expectedDeepTriggered
            && result.DeepSearchTriggered != expectedDeepTriggered)
        {
            throw new InvalidOperationException(
                $"首轮深化触发状态为 {result.DeepSearchTriggered}，预期为 {expectedDeepTriggered}。");
        }
        if (_request.ExpectedInitialDeepSearchImprovedResult is { } expectedDeepImproved
            && result.DeepSearchImprovedResult != expectedDeepImproved)
        {
            throw new InvalidOperationException(
                $"首轮深化改善状态为 {result.DeepSearchImprovedResult}，预期为 {expectedDeepImproved}。");
        }
        if (_request.ExpectedInitialTotalElapsedMillisecondsAtMost is { } maximumElapsed
            && result.TotalSearchElapsed.TotalMilliseconds > maximumElapsed)
        {
            throw new InvalidOperationException(
                $"首轮总搜索耗时 {result.TotalSearchElapsed.TotalMilliseconds:F1} ms，超过上限 {maximumElapsed:F1} ms。");
        }
        if (_request.ExpectedInitialTotalAllocatedBytesAtMost is { } maximumAllocated
            && result.TotalWorkerAllocatedBytes > maximumAllocated)
        {
            throw new InvalidOperationException(
                $"首轮总分配 {result.TotalWorkerAllocatedBytes} B，超过上限 {maximumAllocated} B。");
        }
        if (_request.ExpectedInitialGen2CollectionsAtMost is { } maximumGen2
            && result.TotalGen2Collections > maximumGen2)
        {
            throw new InvalidOperationException(
                $"首轮 Gen2 次数 {result.TotalGen2Collections}，超过上限 {maximumGen2}。");
        }
        if (_request.ExpectedInitialTotalGcPauseMillisecondsAtMost is { } maximumGcPause
            && result.TotalGcPauseDuration.TotalMilliseconds > maximumGcPause)
        {
            throw new InvalidOperationException(
                $"首轮 GC 累计暂停 {result.TotalGcPauseDuration.TotalMilliseconds:F1} ms，" +
                $"超过上限 {maximumGcPause:F1} ms。");
        }
        if (_request.ExpectedInitialMaxGcPauseMillisecondsAtMost is { } maximumSingleGcPause
            && result.TotalMaxObservedGcPause.TotalMilliseconds > maximumSingleGcPause)
        {
            throw new InvalidOperationException(
                $"首轮单次 GC 最大暂停 {result.TotalMaxObservedGcPause.TotalMilliseconds:F1} ms，" +
                $"超过上限 {maximumSingleGcPause:F1} ms。");
        }
        if (_request.ExpectedInitialMaxMainThreadFrameGapMillisecondsAtMost is { } maximumFrameGap
            && result.MaxMainThreadFrameGapMilliseconds > maximumFrameGap)
        {
            throw new InvalidOperationException(
                $"首轮搜索期间主线程最大帧间隔 {result.MaxMainThreadFrameGapMilliseconds:F1} ms，" +
                $"超过上限 {maximumFrameGap:F1} ms。");
        }
        if (_request.ExpectedInitialMainThreadFramesOver50MillisecondsAtMost is { } maximumFramesOver50
            && result.MainThreadFramesOver50Milliseconds > maximumFramesOver50)
        {
            throw new InvalidOperationException(
                $"首轮搜索期间超过 50 ms 的主线程帧有 {result.MainThreadFramesOver50Milliseconds} 个，" +
                $"超过上限 {maximumFramesOver50} 个。");
        }
        if (_request.ExpectedInitialMainThreadFramesOver100MillisecondsAtMost is { } maximumFramesOver100
            && result.MainThreadFramesOver100Milliseconds > maximumFramesOver100)
        {
            throw new InvalidOperationException(
                $"首轮搜索期间超过 100 ms 的主线程帧有 {result.MainThreadFramesOver100Milliseconds} 个，" +
                $"超过上限 {maximumFramesOver100} 个。");
        }
        if (_request.ExpectedInitialTransitionCacheHitsAtLeast is { } minimumCacheHits
            && result.TransitionCacheHits < minimumCacheHits)
        {
            throw new InvalidOperationException(
                $"首轮转移缓存命中 {result.TransitionCacheHits}，低于下限 {minimumCacheHits}。");
        }
        if (_request.ExpectedInitialRepeatableNoProgressBranchesPrunedAtLeast is { } minimumLoopPruned
            && result.RepeatableNoProgressBranchesPruned < minimumLoopPruned)
        {
            throw new InvalidOperationException(
                $"首轮无进展循环剪枝为 {result.RepeatableNoProgressBranchesPruned}，低于预期下限 {minimumLoopPruned}。");
        }
        if (_request.ExpectedInitialCycleShapesDetectedAtLeast is { } minimumCycleShapes
            && result.CycleShapesDetected < minimumCycleShapes)
        {
            throw new InvalidOperationException(
                $"首轮检测到的循环形状为 {result.CycleShapesDetected}，低于预期下限 {minimumCycleShapes}。");
        }
        if (_request.ExpectedInitialCycleProbeContinuationsExpandedAtLeast is { } minimumCycleProbes
            && result.CycleProbeContinuationsExpanded < minimumCycleProbes)
        {
            throw new InvalidOperationException(
                $"首轮展开的循环探测延续为 {result.CycleProbeContinuationsExpanded}，低于预期下限 {minimumCycleProbes}。");
        }
        if (_request.ExpectedInitialCycleCandidatesProtectedAtLeast is { } minimumCycleCandidates
            && result.CycleCandidatesProtected < minimumCycleCandidates)
        {
            throw new InvalidOperationException(
                $"首轮保护的循环候选为 {result.CycleCandidatesProtected}，低于预期下限 {minimumCycleCandidates}。");
        }
        if (_request.ExpectedInitialCycleContinuationsStoppedAtLeast is { } minimumCycleStops
            && result.CycleContinuationsStopped < minimumCycleStops)
        {
            throw new InvalidOperationException(
                $"首轮停止的循环延续为 {result.CycleContinuationsStopped}，低于预期下限 {minimumCycleStops}。");
        }
        if (_request.ExpectedInitialCrossTurnCandidatesProtectedAtLeast is { } minimumCrossTurnCandidates
            && result.CrossTurnCandidatesProtected < minimumCrossTurnCandidates)
        {
            throw new InvalidOperationException(
                $"首轮保护的跨回合候选为 {result.CrossTurnCandidatesProtected}，低于预期下限 {minimumCrossTurnCandidates}。");
        }
        if (_request.ExpectedInitialCrossTurnContinuationsStoppedAtLeast is { } minimumCrossTurnStops
            && result.CrossTurnContinuationsStopped < minimumCrossTurnStops)
        {
            throw new InvalidOperationException(
                $"首轮停止的跨回合延续为 {result.CrossTurnContinuationsStopped}，低于预期下限 {minimumCrossTurnStops}。");
        }
        if (_request.ExpectedInitialNodeLimitSnapshotsReleasedAtLeast is { } minimumReleasedSnapshots
            && result.NodeLimitSnapshotsReleased < minimumReleasedSnapshots)
        {
            throw new InvalidOperationException(
                $"首轮节点上限释放的快照为 {result.NodeLimitSnapshotsReleased}，低于预期下限 {minimumReleasedSnapshots}。");
        }
        if (_request.ExpectedInitialChoiceBranchesEvaluatedAtLeast is { } minimumChoiceBranches
            && result.ChoiceBranchesEvaluated < minimumChoiceBranches)
        {
            throw new InvalidOperationException(
                $"首轮选牌分支仅评估 {result.ChoiceBranchesEvaluated} 条，低于下限 {minimumChoiceBranches}。");
        }
        int initialExecutableActions = result.BestNode.Actions.Count(action =>
            action.Turn == startedTurn && action.IsExecutable);
        if (_request.ExpectedInitialExecutableActionCountAtLeast is { } minimumExecutableActions
            && initialExecutableActions < minimumExecutableActions)
        {
            throw new InvalidOperationException(
                $"首轮路线只有 {initialExecutableActions} 个可执行动作，低于下限 {minimumExecutableActions}。");
        }
        if (_request.ExpectedInitialOnlyDeathRoutesFound is { } expectedOnlyDeath
            && result.OnlyDeathRoutesFound != expectedOnlyDeath)
        {
            throw new InvalidOperationException(
                $"首轮仅死亡路线标记为 {result.OnlyDeathRoutesFound}，预期为 {expectedOnlyDeath}。");
        }
        if (_request.ExpectedInitialCombatEndedTurn is { } expectedCombatEndedTurn
            && result.CombatEndedTurn != expectedCombatEndedTurn)
        {
            throw new InvalidOperationException(
                $"首轮预计战斗结束回合为 {result.CombatEndedTurn?.ToString() ?? "-"}，" +
                $"预期为 {expectedCombatEndedTurn}。");
        }
        if (_request.ExpectedInitialDeathTurn is { } expectedDeathTurn
            && result.DeathTurn != expectedDeathTurn)
        {
            throw new InvalidOperationException(
                $"首轮预计死亡回合为 {result.DeathTurn?.ToString() ?? "-"}，预期为 {expectedDeathTurn}。");
        }
        if (_request.ExpectedInitialDeathTurnAtLeast is { } minimumDeathTurn
            && result.DeathTurn is { } actualDeathTurn
            && actualDeathTurn < minimumDeathTurn)
        {
            throw new InvalidOperationException(
                $"首轮预计死亡回合为 {actualDeathTurn}，早于预期下限 {minimumDeathTurn}。");
        }
        if (_request.ExpectedInitialFinalEnemyHpAtMost is { } maximumFinalEnemyHp
            && result.Snapshot.EnemyHp > maximumFinalEnemyHp)
        {
            throw new InvalidOperationException(
                $"首轮路线终局敌方总生命为 {result.Snapshot.EnemyHp}，超过预期上限 {maximumFinalEnemyHp}。");
        }
        if (_request.ExpectedInitialActEndingBoss is { } expectedActEndingBoss
            && result.IsActEndingBoss != expectedActEndingBoss)
        {
            throw new InvalidOperationException(
                $"首轮幕末 Boss 标记为 {result.IsActEndingBoss}，预期为 {expectedActEndingBoss}。");
        }
        if (_request.ExpectedInitialBossHpRelief is { } expectedBossHpRelief
            && result.BossHpRelief != expectedBossHpRelief)
        {
            throw new InvalidOperationException(
                $"首轮 Boss 战后血量政策为 {result.BossHpRelief}，预期为 {expectedBossHpRelief}。");
        }
        if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialPlannedChoiceCardId))
        {
            bool found = result.BestNode.Actions
                .SelectMany(action => action.TurnStartChoices ?? [])
                .Any(choice => choice.Effect == PlanChoiceEffect.ApplyKnowledgeCurse
                    && choice.Cards.Any(card => card.CardId.Equals(
                        _request.ExpectedInitialPlannedChoiceCardId,
                        StringComparison.Ordinal)));
            if (!found)
            {
                throw new InvalidOperationException(
                    $"首轮路线没有计划知识恶魔选择 {_request.ExpectedInitialPlannedChoiceCardId}。");
            }
        }
        if (_request.ExpectedInitialTurnStartChoiceTurn.HasValue
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceSourceId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceCardId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceStateContains)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceStateExcludes))
        {
            if (_request.ExpectedInitialTurnStartChoiceTurn is not { } choiceTurn
                || string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceSourceId)
                || string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceCardId))
            {
                throw new InvalidOperationException(
                    "回合开始选牌身份断言必须提供回合、来源和卡牌 ID。");
            }

            PlanCardToken[] tokens = result.BestNode.Actions
                .Where(action => action.Kind == PlanActionKind.EndTurn && action.Turn == choiceTurn)
                .SelectMany(action => action.TurnStartChoices ?? [])
                .Where(choice => choice.SourceId.Equals(
                    _request.ExpectedInitialTurnStartChoiceSourceId,
                    StringComparison.Ordinal))
                .SelectMany(choice => choice.Cards)
                .Where(card => card.CardId.Equals(
                    _request.ExpectedInitialTurnStartChoiceCardId,
                    StringComparison.Ordinal))
                .ToArray();
            if (tokens.Length == 0)
            {
                throw new InvalidOperationException(
                    $"首轮路线第 {choiceTurn} 回合结束后没有计划 " +
                    $"{_request.ExpectedInitialTurnStartChoiceSourceId} 选择 " +
                    $"{_request.ExpectedInitialTurnStartChoiceCardId}。");
            }
            if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceStateContains)
                && tokens.All(token => !token.StateKey.Contains(
                    _request.ExpectedInitialTurnStartChoiceStateContains,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"计划卡牌身份不包含 {_request.ExpectedInitialTurnStartChoiceStateContains}：" +
                    string.Join(" || ", tokens.Select(token => token.StateKey)));
            }
            if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialTurnStartChoiceStateExcludes)
                && tokens.Any(token => token.StateKey.Contains(
                    _request.ExpectedInitialTurnStartChoiceStateExcludes,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"计划卡牌身份仍包含 {_request.ExpectedInitialTurnStartChoiceStateExcludes}：" +
                    string.Join(" || ", tokens.Select(token => token.StateKey)));
            }
            _completedChecks.Add(
                $"TurnStartChoice:{choiceTurn}:{_request.ExpectedInitialTurnStartChoiceSourceId}:" +
                $"{_request.ExpectedInitialTurnStartChoiceCardId}:" +
                string.Join(',', tokens.Select(token => $"src{token.SourceOccurrence}/opt{token.OptionOccurrence}")));
        }
        if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialRelicEffectId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialRelicEffectSummary))
        {
            if (string.IsNullOrWhiteSpace(_request.ExpectedInitialRelicEffectId))
                throw new InvalidOperationException("遗物联动断言必须提供遗物 ID。");
            PlanAction[] effectActions = result.BestNode.Actions
                .Where(action => action.Turn == startedTurn)
                .ToArray();
            PlanRelicEffect[] effects = effectActions
                .SelectMany(action => action.RelicEffects ?? [])
                .Where(effect => effect.RelicId.Equals(
                    _request.ExpectedInitialRelicEffectId,
                    StringComparison.Ordinal))
                .ToArray();
            if (effects.Length == 0)
                throw new InvalidOperationException($"首轮路线没有遗物联动 {_request.ExpectedInitialRelicEffectId}。");
            if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialRelicEffectSummary)
                && effects.All(effect => !effect.Summary.Equals(
                    _request.ExpectedInitialRelicEffectSummary,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"遗物 {_request.ExpectedInitialRelicEffectId} 的联动标注为 " +
                    $"{string.Join(',', effects.Select(effect => effect.Summary))}，" +
                    $"预期为 {_request.ExpectedInitialRelicEffectSummary}。");
            }
            SolverOverlayTurnSnapshot overlayTurn = SolverOverlaySnapshot.Capture(result, unexpectedReplan: false)
                .Turns
                .Single(turn => turn.Turn == startedTurn);
            string[] overlayRelicLabels = overlayTurn.Actions
                .Append(overlayTurn.EndTurnAction)
                .OfType<SolverOverlayActionSnapshot>()
                .SelectMany(action => action.RelicLabels)
                .ToArray();
            if (effects.All(effect => !overlayRelicLabels.Contains(
                    effect.RelicTitle + effect.Summary,
                    StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Overlay 没有显示遗物联动 {_request.ExpectedInitialRelicEffectId}：" +
                    string.Join(',', overlayRelicLabels));
            }
            _completedChecks.Add($"OverlayRelicEffect:{_request.ExpectedInitialRelicEffectId}");
        }
        if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialFirstActionCardId))
        {
            PlanAction? firstAction = result.BestNode.Actions.FirstOrDefault(action =>
                action.Turn == startedTurn && action.IsExecutable);
            if (firstAction?.Kind != PlanActionKind.PlayCard
                || !firstAction.CardId.Equals(
                    _request.ExpectedInitialFirstActionCardId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"首轮第一个可执行动作是 " +
                    $"{(firstAction == null ? "-" : firstAction.Kind == PlanActionKind.PlayCard ? firstAction.CardId : firstAction.PotionId)}，" +
                    $"预期为卡牌 {_request.ExpectedInitialFirstActionCardId}。");
            }
            _completedChecks.Add($"InitialFirstAction:{firstAction.CardId}");
        }
        if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialFirstActionPotionId))
        {
            PlanAction? firstAction = result.BestNode.Actions.FirstOrDefault(action =>
                action.Turn == startedTurn && action.IsExecutable);
            if (firstAction?.Kind != PlanActionKind.UsePotion
                || !firstAction.PotionId.Equals(
                    _request.ExpectedInitialFirstActionPotionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"首轮第一个可执行动作是 " +
                    $"{(firstAction == null ? "-" : firstAction.Kind == PlanActionKind.PlayCard ? firstAction.CardId : firstAction.PotionId)}，" +
                    $"预期为药水 {_request.ExpectedInitialFirstActionPotionId}。");
            }
            _completedChecks.Add($"InitialFirstPotion:{firstAction.PotionId}");
        }
        if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialAbsentActionCardId)
            && result.BestNode.Actions.Any(action =>
                action.Kind == PlanActionKind.PlayCard
                && action.CardId.Equals(
                    _request.ExpectedInitialAbsentActionCardId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"首轮路线仍包含禁止动作卡牌 {_request.ExpectedInitialAbsentActionCardId}。");
        }
        if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialAbsentActionCardId))
            _completedChecks.Add($"InitialAbsentAction:{_request.ExpectedInitialAbsentActionCardId}");
        if (!string.IsNullOrWhiteSpace(_request.ExpectedInitialActionCardId)
            || !string.IsNullOrWhiteSpace(_request.ExpectedInitialActionTitle))
        {
            if (string.IsNullOrWhiteSpace(_request.ExpectedInitialActionCardId)
                || string.IsNullOrWhiteSpace(_request.ExpectedInitialActionTitle))
            {
                throw new InvalidOperationException("生成牌标题断言必须同时提供卡牌 ID 和标题。");
            }
            PlanAction[] matchingActions = result.BestNode.Actions
                .Where(action => action.Kind == PlanActionKind.PlayCard
                    && action.CardId.Equals(_request.ExpectedInitialActionCardId, StringComparison.Ordinal))
                .ToArray();
            if (matchingActions.Length == 0)
            {
                throw new InvalidOperationException(
                    $"首轮路线没有卡牌 {_request.ExpectedInitialActionCardId}，无法核对生成牌标题。");
            }
            if (matchingActions.Any(action => !action.CardTitle.Equals(
                    _request.ExpectedInitialActionTitle,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"卡牌 {_request.ExpectedInitialActionCardId} 的路线标题为 " +
                    $"{string.Join(',', matchingActions.Select(action => action.CardTitle).Distinct(StringComparer.Ordinal))}，" +
                    $"预期为 {_request.ExpectedInitialActionTitle}。");
            }
        }
        if (_request.ExpectedInitialActionReplayCount is { } expectedReplayCount)
        {
            if (string.IsNullOrWhiteSpace(_request.ExpectedInitialActionCardId))
                throw new InvalidOperationException("重放次数断言必须提供卡牌 ID。");
            PlanAction[] matchingActions = result.BestNode.Actions
                .Where(action => action.Turn == startedTurn
                    && action.Kind == PlanActionKind.PlayCard
                    && action.CardId.Equals(_request.ExpectedInitialActionCardId, StringComparison.Ordinal))
                .ToArray();
            if (matchingActions.Length == 0
                || matchingActions.Any(action => action.ReplayCount != expectedReplayCount))
            {
                throw new InvalidOperationException(
                    $"卡牌 {_request.ExpectedInitialActionCardId} 的路线重放次数为 " +
                    $"{string.Join(',', matchingActions.Select(action => action.ReplayCount))}，" +
                    $"预期为 {expectedReplayCount}。");
            }
            SolverOverlayActionSnapshot[] overlayActions = SolverOverlaySnapshot
                .Capture(result, unexpectedReplan: false)
                .Turns
                .Single(turn => turn.Turn == startedTurn)
                .Actions
                .Where(action => action.Title.Equals(matchingActions[0].CardTitle, StringComparison.Ordinal))
                .ToArray();
            if (overlayActions.Length == 0
                || overlayActions.Any(action => action.ReplayCount != expectedReplayCount))
            {
                throw new InvalidOperationException(
                    $"Overlay 没有显示 {_request.ExpectedInitialActionCardId} 的重放×{expectedReplayCount}。");
            }
            _completedChecks.Add($"OverlayReplay:{_request.ExpectedInitialActionCardId}:x{expectedReplayCount}");
        }

        _completedChecks.Add(
            $"InitialPolicy:SoldHp={result.SoldHp}/{result.SoldHpThreshold};" +
            $"Potion={result.PotionCount},Saved={result.PotionHpSaved}/{result.PotionHpRequired},Rejected={result.PotionBranchesRejected};" +
            $"Theft={result.TheftPolicy?.ToString() ?? "-"},Outstanding={result.OutstandingStolenResource};" +
            $"Turns={result.SearchedTurns};Shuffles={result.Snapshot.ShufflesCrossed};" +
            $"Unmirrored={unmirroredCount};HpLost={initialHpLost};" +
            $"ProjectedBattleHpLost={result.ProjectedBattleHpLost};" +
            $"Block={initialActualBlock}/{initialMaxBlock};Pruned={result.SoldHpBranchesPruned};" +
            $"Phase={result.SearchPhase};Deep={result.DeepSearchTriggered}/{result.DeepSearchImprovedResult};" +
            $"Total={result.TotalSearchElapsed.TotalMilliseconds:F1}ms/{result.TotalWorkerAllocatedBytes}B;" +
            $"Gc={result.TotalGcPauseDuration.TotalMilliseconds:F1}ms/{result.TotalMaxObservedGcPause.TotalMilliseconds:F1}msMax;" +
            $"Frame={result.MaxMainThreadFrameGapMilliseconds:F1}msMax/{result.MainThreadFramesOver50Milliseconds}Over50;" +
            $"CacheHits={result.TransitionCacheHits};ChoiceBranches={result.ChoiceBranchesEvaluated};Actions={initialExecutableActions};" +
            $"OnlyDeath={result.OnlyDeathRoutesFound};CombatEndedTurn={result.CombatEndedTurn?.ToString() ?? "-"};" +
            $"DeathTurn={result.DeathTurn?.ToString() ?? "-"};ActEndingBoss={result.IsActEndingBoss};" +
            $"BossHpRelief={result.BossHpRelief}");
    }

    private void ForceInitialEnemyMoves(CombatState combatState)
    {
        if (_request.InitialEnemyMoveIds.Length > combatState.Enemies.Count)
        {
            throw new InvalidOperationException(
                $"配置了 {_request.InitialEnemyMoveIds.Length} 个初始行动，但战斗只有 {combatState.Enemies.Count} 个敌人。");
        }
        for (int index = 0; index < _request.InitialEnemyMoveIds.Length; index++)
        {
            string moveId = _request.InitialEnemyMoveIds[index];
            if (string.IsNullOrWhiteSpace(moveId))
                continue;
            MonsterModel monster = combatState.Enemies[index].Monster
                ?? throw new InvalidOperationException($"敌人 {index} 没有 MonsterModel。");
            MonsterMoveStateMachine machine = monster.MoveStateMachine
                ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
            if (!machine.States.TryGetValue(moveId, out MonsterState? state) || state is not MoveState move)
                throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动 {moveId}。");
            monster.SetMoveImmediate(move, true);
        }
    }

    private void ForceInitialEnemyStateLogs(CombatState combatState)
    {
        if (_request.InitialEnemyStateLogs is not { Length: > 0 } logs)
            return;
        if (logs.Length != combatState.Enemies.Count)
        {
            throw new InvalidOperationException(
                $"怪物行动历史数量 {logs.Length} 与敌人数 {combatState.Enemies.Count} 不同。");
        }
        for (int enemyIndex = 0; enemyIndex < combatState.Enemies.Count; enemyIndex++)
        {
            MonsterModel monster = combatState.Enemies[enemyIndex].Monster
                ?? throw new InvalidOperationException($"敌人 {enemyIndex} 没有 MonsterModel。");
            MonsterMoveStateMachine machine = monster.MoveStateMachine
                ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
            machine.StateLog.Clear();
            foreach (string stateId in logs[enemyIndex])
            {
                if (!machine.States.TryGetValue(stateId, out MonsterState? state))
                    throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有历史状态 {stateId}。");
                machine.StateLog.Add(state);
            }
        }
    }

    private static async Task ClearPlayerPilesAsync(Player player)
    {
        PlayerCombatState playerCombatState = player.PlayerCombatState!;
        CardModel[] cards =
        [
            .. playerCombatState.Hand.Cards,
            .. playerCombatState.DrawPile.Cards,
            .. playerCombatState.DiscardPile.Cards,
            .. playerCombatState.ExhaustPile.Cards,
        ];
        await CardPileCmd.RemoveFromCombat(cards, skipVisuals: true);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }
}
