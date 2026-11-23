using System.IO.Compression;
using Godot;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private readonly record struct ExecutionOutcome(
        bool CombatEnded,
        int FinishedTurn,
        bool ExpectedCardPlayed,
        bool ExpectedPotionUsed,
        bool ExpectedPlayerPowerObserved,
        bool InitialSearchHeld);

    private bool HasInitialSolverExpectation()
        => _request.ExpectedInitialExpandedNodesAtMost.HasValue
            || _request.ExpectedInitialTransitionsAtMost.HasValue
            || _request.ExpectedInitialTotalExpandedNodesAtMost.HasValue
            || _request.ExpectedInitialTotalTransitionsAtMost.HasValue
            || _request.ExpectedInitialBoundaryReason.HasValue
            || _request.ExpectedInitialTotalElapsedMillisecondsAtMost.HasValue
            || _request.ExpectedInitialExecutableActionCountAtLeast.HasValue
            || _request.ExpectedInitialUnavoidableHpLost.HasValue
            || _request.ExpectedInitialPotionCount.HasValue
            || _request.ExpectedInitialSearchedTurnsAtLeast.HasValue
            || _request.ExpectedInitialHpLostAtMost.HasValue
            || _request.ExpectedInitialProjectedBattleHpLost.HasValue
            || _request.ExpectedInitialProjectedBattleHpLostAtMost.HasValue
            || _request.ExpectedInitialActionCardId != null
            || _request.ExpectedInitialFirstActionCardId != null
            || _request.ExpectedInitialOnlyDeathRoutesFound.HasValue
            || _request.ExpectedInitialCombatEndedTurn.HasValue
            || _request.ExpectedInitialFinalEnemyHpAtMost.HasValue;

    private async Task AssertInitialSolverResultAsync(int startedTurn)
    {
        while (SolverController.IsSearching || (_lastObservedResult == null && SolverController.CurrentResultForBugReport == null))
        {
            EnsureWithinDeadline();
            await NextFrameAsync();
        }
        SolverResult result = _lastObservedResult
            ?? SolverController.CurrentResultForBugReport
            ?? throw new InvalidOperationException("首轮搜索完成后没有可用结果。");
        if (!_writer.HasSolverMetrics)
            _writer.CaptureSolverResult(result);

        if (_request.ExpectedInitialBoundaryReason is { } expectedBoundary && result.BoundaryReason != expectedBoundary)
            throw new InvalidOperationException($"首轮搜索边界为 {result.BoundaryReason}，预期 {expectedBoundary}。");
        if (_request.ExpectedInitialExpandedNodesAtMost is { } maxExpanded && result.ExpandedNodes > maxExpanded)
            throw new InvalidOperationException($"首轮展开 {result.ExpandedNodes} 节点，超过上限 {maxExpanded}。");
        if (_request.ExpectedInitialTransitionsAtMost is { } maxTransitions && result.TransitionCount > maxTransitions)
            throw new InvalidOperationException($"首轮转移 {result.TransitionCount}，超过上限 {maxTransitions}。");
        if (_request.ExpectedInitialTotalExpandedNodesAtMost is { } maxTotalExpanded && result.TotalExpandedNodes > maxTotalExpanded)
            throw new InvalidOperationException($"总展开 {result.TotalExpandedNodes}，超过上限 {maxTotalExpanded}。");
        if (_request.ExpectedInitialTotalTransitionsAtMost is { } maxTotalTransitions && result.TotalTransitionCount > maxTotalTransitions)
            throw new InvalidOperationException($"总转移 {result.TotalTransitionCount}，超过上限 {maxTotalTransitions}。");
        if (_request.ExpectedInitialTotalElapsedMillisecondsAtMost is { } maxElapsed
            && result.TotalSearchElapsed.TotalMilliseconds > maxElapsed)
            throw new InvalidOperationException($"总耗时 {result.TotalSearchElapsed.TotalMilliseconds:F1}ms，超过上限 {maxElapsed:F1}ms。");
        if (_request.ExpectedInitialUnavoidableHpLost is { } expectedUnavoidable && result.UnavoidableHpLost != expectedUnavoidable)
            throw new InvalidOperationException($"必然战损 {result.UnavoidableHpLost}，预期 {expectedUnavoidable}。");
        if (_request.ExpectedInitialPotionCount is { } expectedPotions && result.PotionCount != expectedPotions)
            throw new InvalidOperationException($"路线用药 {result.PotionCount}，预期 {expectedPotions}。");
        if (_request.ExpectedInitialSearchedTurnsAtLeast is { } minTurns && result.SearchedTurns < minTurns)
            throw new InvalidOperationException($"搜索回合 {result.SearchedTurns}，低于下限 {minTurns}。");
        if (_request.ExpectedInitialProjectedBattleHpLost is { } expectedProjectedLoss
            && result.ProjectedBattleHpLost != expectedProjectedLoss)
            throw new InvalidOperationException($"预计整场战损 {result.ProjectedBattleHpLost}，预期 {expectedProjectedLoss}。");
        if (_request.ExpectedInitialProjectedBattleHpLostAtMost is { } maxProjectedLoss
            && result.ProjectedBattleHpLost > maxProjectedLoss)
            throw new InvalidOperationException($"预计整场战损 {result.ProjectedBattleHpLost}，超过上限 {maxProjectedLoss}。");
        if (_request.ExpectedInitialHpLostAtMost is { } maxTurnLoss
            && result.HpLostByTurn.GetValueOrDefault(startedTurn) > maxTurnLoss)
            throw new InvalidOperationException($"首回合掉血超过上限 {maxTurnLoss}。");
        if (_request.ExpectedInitialOnlyDeathRoutesFound is { } expectedDeathOnly && result.OnlyDeathRoutesFound != expectedDeathOnly)
            throw new InvalidOperationException($"全死路标志为 {result.OnlyDeathRoutesFound}，预期 {expectedDeathOnly}。");
        if (_request.ExpectedInitialCombatEndedTurn is { } expectedEndTurn && result.CombatEndedTurn != expectedEndTurn)
            throw new InvalidOperationException($"预计结束回合 {result.CombatEndedTurn}，预期 {expectedEndTurn}。");
        if (_request.ExpectedInitialFinalEnemyHpAtMost is { } maxEnemyHp && result.Snapshot.EnemyHp > maxEnemyHp)
            throw new InvalidOperationException($"终局敌方生命 {result.Snapshot.EnemyHp}，超过上限 {maxEnemyHp}。");

        PlanAction[] turnActions = result.BestNode.Actions
            .Where(action => action.Turn == startedTurn && action.IsExecutable)
            .ToArray();
        if (_request.ExpectedInitialExecutableActionCountAtLeast is { } minActions && turnActions.Length < minActions)
            throw new InvalidOperationException($"首回合可执行动作 {turnActions.Length}，低于下限 {minActions}。");
        if (_request.ExpectedInitialActionCardId is { } expectedCard
            && !turnActions.Any(action => action.CardId.Equals(expectedCard, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"首回合路线未包含卡牌 {expectedCard}。");
        if (_request.ExpectedInitialFirstActionCardId is { } expectedFirstCard
            && (turnActions.Length == 0 || !turnActions[0].CardId.Equals(expectedFirstCard, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"首回合首动作不是预期卡牌 {expectedFirstCard}。");

        _completedChecks.Add(
            $"InitialSearch:Turn={startedTurn}:Boundary={result.BoundaryReason}:" +
            $"Expanded={result.TotalExpandedNodes}:HpLost={result.ProjectedBattleHpLost}:" +
            $"Potions={result.PotionCount}:EndTurn={result.CombatEndedTurn?.ToString() ?? "-"}");
    }

    private sealed class Assertions(UnattendedTestRunner runner)
    {
        public async Task RunBeforeExecutionAsync(ScenarioContext scenario)
        {
            UnattendedTestRequest request = runner._request;
            if (request.VerifyCombatRootSnapshot)
            {
                runner.SetStage("combat_root_snapshot");
                await AssertCombatRootSnapshotAsync(scenario.CombatState, scenario.Player);
                runner._completedChecks.Add("CombatRootSnapshot");
            }
            if (request.ExportBugReportAfterSetup)
            {
                runner.SetStage("export_bug_report");
                string directory = request.EvidenceDirectory is { Length: > 0 } evidence
                    ? Path.Combine(evidence, "bug-reports")
                    : ProjectSettings.GlobalizePath("user://combat-solver-test-bug-reports");
                string archivePath = await CombatBugReportExporter.ExportCurrentAsync(directory);
                using ZipArchive archive = ZipFile.OpenRead(archivePath);
                AssertBugReportArchive(archive, "current", request.ExpectedBugReportControlMode);
                runner._completedChecks.Add($"BugReport:{Path.GetFileName(archivePath)}");
            }
        }

        public void AssertAfterExecution(ScenarioContext scenario, ExecutionOutcome outcome)
        {
            runner._completedChecks.Add($"NativeOutcome:combatEnded={outcome.CombatEnded}:turn={outcome.FinishedTurn}"
                + $":hp={scenario.Player.Creature.CurrentHp}:maxHp={scenario.Player.Creature.MaxHp}");
            UnattendedTestRequest request = runner._request;
            if (request.ExpectedFinishedTurn is { } expectedFinishedTurn
                && outcome.FinishedTurn != expectedFinishedTurn)
            {
                throw new InvalidOperationException(
                    $"战斗在第 {outcome.FinishedTurn} 回合结束，预期为第 {expectedFinishedTurn} 回合。");
            }
            if (request.ExpectedFinishedTurnAtMost is { } maximumFinishedTurn
                && outcome.FinishedTurn > maximumFinishedTurn)
            {
                throw new InvalidOperationException(
                    $"战斗在第 {outcome.FinishedTurn} 回合结束，超过预期上限第 {maximumFinishedTurn} 回合。");
            }
            if (request.ExpectedFinishedPlayerHpAtLeast is { } minimumFinishedHp
                && scenario.Player.Creature.CurrentHp < minimumFinishedHp)
            {
                throw new InvalidOperationException(
                    $"战斗结束时玩家剩余 {scenario.Player.Creature.CurrentHp} HP，低于预期下限 {minimumFinishedHp}。");
            }
            if (request.ExpectedPlayedCardId is { } expectedPlayedCardId && !outcome.ExpectedCardPlayed)
                throw new InvalidOperationException($"战斗中没有打出预期卡牌 {expectedPlayedCardId}。");
            if (request.ExpectedUsedPotionId is { } expectedUsedPotionId && !outcome.ExpectedPotionUsed)
                throw new InvalidOperationException($"战斗中没有使用预期药水 {expectedUsedPotionId}。");
            if (request.ExpectedObservedPlayerPowerId is { } expectedPowerId
                && !outcome.ExpectedPlayerPowerObserved)
            {
                throw new InvalidOperationException($"战斗中没有观察到玩家获得 {expectedPowerId}。");
            }
            AssertNativeChoices(request);
        }

        private void AssertNativeChoices(UnattendedTestRequest request)
        {
            if (request.ExpectedNativeChoiceVisibleAtLeast is null
                && request.ExpectedNativeChoiceSearchStartedAtMost is null)
                return;
            IEnumerable<NativeChoiceTrace> traces = NativeChoiceRuntime.TraceSnapshotForTesting;
            if (!string.IsNullOrWhiteSpace(request.ExpectedNativeChoiceOwnerPrefix))
            {
                traces = traces.Where(trace => trace.Owner.StartsWith(
                    request.ExpectedNativeChoiceOwnerPrefix,
                    StringComparison.Ordinal));
            }
            if (request.ExpectedNativeChoiceSurface is { } surface)
                traces = traces.Where(trace => trace.Surface == surface);
            NativeChoiceTrace[] matching = traces.ToArray();
            int visible = matching.Count(trace => trace.Stage == "Visible");
            int selected = matching.Count(trace => trace.Stage == "Selected");
            int searchStarted = matching.Count(trace => trace.Stage == "SearchStarted");
            if (request.ExpectedNativeChoiceVisibleAtLeast is { } expectedVisible
                && (visible < expectedVisible || selected < expectedVisible))
            {
                throw new InvalidOperationException(
                    $"原生选牌页面可见/完成次数为 {visible}/{selected}，低于预期 {expectedVisible}。 ");
            }
            if (request.ExpectedNativeChoiceSearchStartedAtMost is { } maximumSearches
                && searchStarted > maximumSearches)
            {
                throw new InvalidOperationException(
                    $"原生选牌页面期间启动了 {searchStarted} 次搜索，超过上限 {maximumSearches}。 ");
            }
            runner._completedChecks.Add(
                $"NativeChoices:{request.ExpectedNativeChoiceOwnerPrefix ?? "*"}:" +
                $"{request.ExpectedNativeChoiceSurface?.ToString() ?? "*"}:" +
                $"visible={visible}:selected={selected}:search={searchStarted}");
        }
    }
}
