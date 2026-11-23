using System.IO.Compression;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class Executor(UnattendedTestRunner runner)
    {
        private SolverSettingsData? _settingsBeforeTest;

        public void RestoreSettings()
        {
            if (_settingsBeforeTest != null)
                SolverSettings.ApplyForTesting(_settingsBeforeTest);
        }

        public void PrepareArchiveSettings()
        {
            if (runner._checkpointImport?["resolvedPolicy"] == null) return;
            _settingsBeforeTest ??= SolverSettings.Current;
            SolverSettings.ApplyForTesting(runner.ApplyRecordedCheckpointPolicy(_settingsBeforeTest));
        }

        public async Task<ExecutionOutcome> ExecuteAsync(ScenarioContext scenario)
        {
            UnattendedTestRequest request = runner._request;
            CombatState combatState = scenario.CombatState;
            Player player = scenario.Player;
            int startedTurn = scenario.StartedTurn;
            bool expectedCardPlayed = request.ExpectedPlayedCardId == null;
            bool expectedPotionUsed = request.ExpectedUsedPotionId == null;
            bool expectedPlayerPowerObserved = request.ExpectedObservedPlayerPowerId == null;

            if (request.ScenarioId == "RUN-STATISTICS-SATURATION")
            {
                await RunStatistics.AssertHealthySaturationIsolationAsync(request.EvidenceDirectory
                    ?? throw new InvalidOperationException("Statistics checks require an evidence directory."));
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "RUN-STATISTICS-WORKER-FAILURE")
            {
                await RunStatistics.AssertWorkerFailureIsolationAsync(request.EvidenceDirectory
                    ?? throw new InvalidOperationException("Statistics fixture requires an evidence directory."));
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "DISPLAY-NAME-SUMMON")
            {
                await runner.AssertDisplayNameSummonAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "ROUTE-ROW-REUSE")
            {
                await runner.AssertRouteRowReuseAndMeasureAsync();
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "DEPLOYMENT-PRESENTATION-TURN")
            {
                runner.AssertDeploymentPresentationTurn();
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "PROCESS-DIAGNOSTICS")
            {
                await runner.AssertProcessDiagnosticsAsync();
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "NATIVE-HAND-CHOICE-REPLAY")
            {
                await runner.AssertNativeHandChoiceReplayAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "NATIVE-CHOOSE-OPEN-GATE")
            {
                await runner.AssertNativeChooseOpenGateAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "BUG-REPORT-UPLOAD")
            {
                await runner.AssertBugReportUploadBoundariesAsync();
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "SHOWCASE-ROUTE-IMPORT-V0111")
            {
                runner.SetStage("showcase_route_import");
                runner.AssertShowcaseRouteImport(combatState);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "SHOWCASE-BUNDLE-IMPORT-V0111")
                return Observation(combatEnded: false);

            if (request.StopAfterCombatRootSnapshotAssertion)
                return Observation(combatEnded: runner.HasNativeRecording && !CombatManager.Instance.IsInProgress);

            if (request.StopAfterInitialSetupAssertion)
            {
                runner.SetStage("assert_initial_setup_result");
                await runner.AssertInitialSolverResultAsync(startedTurn);
                runner._completedChecks.Add("InitialSetupChoices");
                return Observation(combatEnded: false);
            }

            runner.SetStage("full_auto");
            FastModeType? fastModeBeforeDeployment = ApplySettingsOverrides();
            if (!request.PreserveNativeCombatStateForTest && !runner.HasNativeRecording)
                SolverController.BeginCombat(combatState);
            SolverController.SetStopFullAutoOnCombatEnd(false, persist: false);
            SolverController.SetStopFullAutoOnDeathTurn(
                request.ExpectedFullAutoPausedAtDeathTurn || request.ExpectedFullAutoPausedAtLiveRisk,
                persist: false);
            SolverController.SetStopFullAutoOnWorseRecalculation(
                request.EnableStopOnWorseRecalculationForTest
                    || request.ExpectedFullAutoPausedAfterWorseRecalculation
                    || request.ExpectedFullAutoPausedAtLiveRisk,
                persist: false);
            runner._protocolHost.EnableAutomaticTurnSearch();

            if (request.HoldAfterInitialSearch
                || request.ManualEndTurnAfterInitialSearch
                || request.StopAfterInitialSolverResultAssertion)
            {
                SolverController.RequestSearch(runner._host, combatState, SearchReason.Manual);
            }
            else
            {
                SolverController.SetFullAuto(runner._host, combatState, enabled: true);
            }

            if (runner.HasInitialSolverExpectation() || request.StopAfterInitialSolverResultAssertion)
                await runner.AssertInitialSolverResultAsync(startedTurn);

            if (request.StopAfterInitialSolverResultAssertion)
                return Observation(combatEnded: false);

            if (request.HoldAfterInitialSearch)
            {
                if (!runner.HasInitialSolverExpectation())
                    await runner.AssertInitialSolverResultAsync(startedTurn);
                await Task.Delay(2000);
                return Observation(combatEnded: false, initialSearchHeld: true);
            }

            if (request.ManualEndTurnAfterInitialSearch)
            {
                if (!runner.HasInitialSolverExpectation())
                    await runner.AssertInitialSolverResultAsync(startedTurn);
                runner.SetStage("manual_end_turn");
                int manualTurn = player.PlayerCombatState?.TurnNumber
                    ?? throw new InvalidOperationException("手操偏离测试没有玩家回合状态。");
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new EndPlayerTurnAction(player, manualTurn));
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                if (request.EnableFullAutoAfterManualEndTurn)
                {
                    CombatState resumedState = await runner.WaitForPlayableCombatAsync();
                    SolverController.SetFullAuto(runner._host, resumedState, enabled: true);
                }
            }

            runner.SetStage("wait_combat_end");
            bool expectedReuseObserved = !request.ExpectedReusedTurn.HasValue;
            bool ObserveExpectedReuse()
            {
                SolverResult? latestResult = runner._lastObservedResult ?? SolverController.CurrentResultForBugReport;
                int? observedReusedTurn = runner._lastReusedTurn
                    ?? (latestResult?.WasReused == true ? latestResult.StartTurnNumber : null);
                if (observedReusedTurn != request.ExpectedReusedTurn)
                    return false;
                int observedProjectedBattleHpLost =
                    runner._lastReusedProjectedBattleHpLost
                    ?? latestResult?.ProjectedBattleHpLost
                    ?? throw new InvalidOperationException("复用测试没有记录整场预计战损。");
                if (request.ExpectedReusedProjectedBattleHpLost is { } expectedReusedLoss
                    && observedProjectedBattleHpLost != expectedReusedLoss)
                {
                    throw new InvalidOperationException(
                        $"第 {observedReusedTurn} 回合复用路线预计整场掉血 " +
                        $"{observedProjectedBattleHpLost}，预期为 {expectedReusedLoss}。");
                }
                return true;
            }

            bool stoppedAfterExpectedReuse = false;
            bool stoppedAfterExpectedPower = false;
            bool stoppedAfterDeathTurnPause = false;
            bool stoppedAfterExpectedUnexpectedReplan = false;
            int maximumUnexpectedReplans = 0;

            while (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding)
            {
                expectedCardPlayed |= runner.WasExpectedCardPlayed();
                expectedPotionUsed |= runner.WasExpectedPotionUsed();
                expectedPlayerPowerObserved |= runner.HasExpectedPlayerPower(player);
                maximumUnexpectedReplans = Math.Max(maximumUnexpectedReplans, SolverController.UnexpectedReplanCount);

                if (request.StopAfterExpectedUnexpectedReplan
                    && request.ExpectedUnexpectedReplansAtLeast is { } minimumUnexpectedReplans
                    && maximumUnexpectedReplans >= minimumUnexpectedReplans)
                {
                    stoppedAfterExpectedUnexpectedReplan = true;
                    runner._completedChecks.Add($"UnexpectedReplan:{maximumUnexpectedReplans}");
                    break;
                }
                if (!expectedReuseObserved)
                    expectedReuseObserved = ObserveExpectedReuse();
                if (request.StopAfterExpectedReuse && expectedReuseObserved)
                {
                    stoppedAfterExpectedReuse = true;
                    break;
                }
                if (request.StopAfterExpectedPlayerPower && expectedPlayerPowerObserved)
                {
                    stoppedAfterExpectedPower = true;
                    runner._completedChecks.Add($"PlayerPower:{request.ExpectedObservedPlayerPowerId}");
                    break;
                }
                if (request.ExpectedFullAutoPausedAtDeathTurn
                    && !SolverController.FullAutoEnabled
                    && !SolverController.IsSearching
                    && !SolverController.IsDeploying)
                {
                    stoppedAfterDeathTurnPause = true;
                    runner._completedChecks.Add($"FullAutoPaused:DeathTurn={startedTurn}");
                    break;
                }
                runner.EnsureWithinDeadline();
                await runner.NextFrameAsync();
            }

            expectedCardPlayed |= runner.WasExpectedCardPlayed();
            expectedPotionUsed |= runner.WasExpectedPotionUsed();
            expectedPlayerPowerObserved |= runner.HasExpectedPlayerPower(player);
            maximumUnexpectedReplans = Math.Max(maximumUnexpectedReplans, SolverController.UnexpectedReplanCount);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            while (SolverController.IsDeploying)
            {
                runner.EnsureWithinDeadline();
                await runner.NextFrameAsync();
            }
            if (!expectedReuseObserved)
                expectedReuseObserved = ObserveExpectedReuse();
            if (expectedReuseObserved && request.ExpectedReusedTurn.HasValue)
                runner._completedChecks.Add($"Reuse:Turn={request.ExpectedReusedTurn}");

            if (request.AssertDeploymentSpeedRestored && fastModeBeforeDeployment is { } expectedFastMode)
            {
                long restoreDeadline = System.Environment.TickCount64 + 5_000;
                while (SaveManager.Instance.PrefsSave.FastMode != expectedFastMode)
                {
                    runner.EnsureWithinDeadline();
                    if (System.Environment.TickCount64 >= restoreDeadline)
                    {
                        throw new InvalidOperationException(
                            $"自动执行后游戏速度为 {SaveManager.Instance.PrefsSave.FastMode}，" +
                            $"5 秒内没有恢复为 {expectedFastMode}。");
                    }
                    await runner.NextFrameAsync();
                }
                runner._completedChecks.Add($"DeploymentSpeedRestored:{fastModeBeforeDeployment}");
            }
            if (!expectedReuseObserved)
                throw new InvalidOperationException($"没有观察到第 {request.ExpectedReusedTurn} 回合复用首轮预测状态。");
            if (request.ExpectedUnexpectedReplansAtMost is { } maximumReplans && maximumUnexpectedReplans > maximumReplans)
            {
                throw new InvalidOperationException(
                    $"战斗出现 {maximumUnexpectedReplans} 次非预期重算，超过上限 {maximumReplans}。");
            }
            if (request.ExpectedUnexpectedReplansAtMost.HasValue)
                runner._completedChecks.Add($"UnexpectedReplans:{maximumUnexpectedReplans}");
            if (request.ExpectedUnexpectedReplansAtLeast is { } minimumReplans && maximumUnexpectedReplans < minimumReplans)
            {
                throw new InvalidOperationException(
                    $"战斗只出现 {maximumUnexpectedReplans} 次非预期重算，低于预期下限 {minimumReplans}。");
            }

            if (!stoppedAfterExpectedReuse
                && !stoppedAfterExpectedPower
                && !stoppedAfterDeathTurnPause
                && !stoppedAfterExpectedUnexpectedReplan)
            {
                if (request.ExpectedPlayerDeath)
                {
                    if (!player.Creature.IsDead)
                        throw new InvalidOperationException("预期玩家死亡，但战斗结束时玩家仍存活。");
                }
                else if (!combatState.Enemies.All(static enemy => enemy.IsDead))
                {
                    throw new InvalidOperationException("战斗结束，但仍存在未死亡敌人。");
                }
            }
            bool combatEnded = !stoppedAfterExpectedReuse
                && !stoppedAfterExpectedPower
                && !stoppedAfterDeathTurnPause
                && !stoppedAfterExpectedUnexpectedReplan;
            return Observation(combatEnded);

            ExecutionOutcome Observation(bool combatEnded, bool initialSearchHeld = false)
                => new(
                    combatEnded,
                    player.PlayerCombatState?.TurnNumber ?? startedTurn,
                    expectedCardPlayed,
                    expectedPotionUsed,
                    expectedPlayerPowerObserved,
                    initialSearchHeld);
        }

        private FastModeType? ApplySettingsOverrides()
        {
            UnattendedTestRequest request = runner._request;
            if (runner._checkpointImport == null
                && !request.PerformancePresetForTest.HasValue
                && !request.SearchBeamWidthForTest.HasValue
                && !request.SearchMaxExpandedNodesForTest.HasValue
                && !request.ShortMaxCardBranchesPerNodeForTest.HasValue
                && !request.DeepMaxCardBranchesPerNodeForTest.HasValue
                && !request.PotionPolicyForTest.HasValue
                && !request.DeploymentFastModeForTest.HasValue
                && !request.DeploymentInterActionDelaySecondsForTest.HasValue
                && !request.EnableDetailedDiagnosticLogsForTest.HasValue)
            {
                return null;
            }

            _settingsBeforeTest ??= SolverSettings.Current;
            SolverSettingsData recordedSettings = runner.ApplyRecordedCheckpointPolicy(_settingsBeforeTest);
            SolverSettingsData testSettings = request.PerformancePresetForTest is { } preset
                ? SolverSettings.ApplyPerformancePreset(recordedSettings, preset)
                : recordedSettings;
            if (request.SearchBeamWidthForTest is < 1 or > 512)
                throw new ArgumentOutOfRangeException(nameof(request.SearchBeamWidthForTest));
            if (request.SearchMaxExpandedNodesForTest is < 100)
                throw new ArgumentOutOfRangeException(nameof(request.SearchMaxExpandedNodesForTest));
            if (request.SearchBeamWidthForTest.HasValue || request.SearchMaxExpandedNodesForTest.HasValue)
            {
                testSettings = SolverSettings.ApplyPerformancePreset(testSettings, SolverPerformancePreset.Custom);
                testSettings = testSettings with
                {
                    PerformancePreset = SolverPerformancePreset.Custom,
                    SearchBeamWidth = request.SearchBeamWidthForTest ?? testSettings.SearchBeamWidth,
                    SearchMaxExpandedNodes = request.SearchMaxExpandedNodesForTest ?? testSettings.SearchMaxExpandedNodes,
                };
            }
            if (request.ShortMaxCardBranchesPerNodeForTest is { } shortMaxCardBranches)
            {
                testSettings = testSettings with
                {
                    PerformancePreset = SolverPerformancePreset.Custom,
                    SearchMaxCardBranchesPerNode = shortMaxCardBranches,
                };
            }
            if (request.DeepMaxCardBranchesPerNodeForTest is { } deepMaxCardBranches)
            {
                testSettings = testSettings with
                {
                    PerformancePreset = SolverPerformancePreset.Custom,
                    SearchMaxCardBranchesPerNode = deepMaxCardBranches,
                };
            }
            SolverSettings.ApplyForTesting(testSettings with
            {
                DeploymentFastMode = request.DeploymentFastModeForTest
                    ?? _settingsBeforeTest.DeploymentFastMode,
                DeploymentInterActionDelaySeconds = request.DeploymentInterActionDelaySecondsForTest
                    ?? _settingsBeforeTest.DeploymentInterActionDelaySeconds,
                EnableDetailedDiagnosticLogs = request.EnableDetailedDiagnosticLogsForTest
                    ?? _settingsBeforeTest.EnableDetailedDiagnosticLogs,
                PotionPolicy = request.PotionPolicyForTest
                    ?? testSettings.PotionPolicy,
            });
            FastModeType fastModeBeforeDeployment = SaveManager.Instance.PrefsSave.FastMode;
            SolverSettingsSnapshot snapshot = SolverSettings.Capture();
            if (request.SearchBeamWidthForTest is { } expectedBeam && snapshot.Profile.BeamWidth != expectedBeam)
                throw new InvalidOperationException($"测试 Beam 为 {snapshot.Profile.BeamWidth}，预期 {expectedBeam}。");
            if (request.SearchMaxExpandedNodesForTest is { } expectedNodes && snapshot.Profile.MaxExpandedNodes != expectedNodes)
                throw new InvalidOperationException($"测试节点额度为 {snapshot.Profile.MaxExpandedNodes}，预期 {expectedNodes}。");
            if (runner._writer.ReplayVerification != null)
            {
                runner._writer.ReplayVerification["executedPolicy"] = System.Text.Json.JsonSerializer.SerializeToNode(
                    new
                    {
                        snapshot.PotionPolicy,
                        SolverSettings.Current.PotionDirectives,
                        snapshot.ActTransitionBossHpStrategy,
                        snapshot.FinalBossHpStrategy,
                        snapshot.Profile,
                        snapshot.SearchMaxDegreeOfParallelism,
                    },
                    UnattendedTestFiles.JsonOptions);
            }
            return fastModeBeforeDeployment;
        }
    }
}
