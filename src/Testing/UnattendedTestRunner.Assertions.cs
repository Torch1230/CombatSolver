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

    private sealed class Assertions(UnattendedTestRunner runner)
    {
        public async Task RunBeforeExecutionAsync(ScenarioContext scenario)
        {
            UnattendedTestRequest request = runner._request;
            if (request.VerifyPredictionFailureBoundaries)
            {
                runner.SetStage("prediction_failure_boundaries");
                AssertPredictionFailureBoundaries(scenario.CombatState, scenario.Player);
                runner._completedChecks.Add("PredictionFailureBoundaries");
            }
            if (request.VerifySearchPolicySnapshot)
            {
                runner.SetStage("search_policy_snapshot");
                await AssertSearchPolicySnapshotAsync(scenario.CombatState);
                runner._completedChecks.Add("SearchPolicySnapshot");
            }
            if (request.VerifyControllerSessionLifecycle)
            {
                runner.SetStage("controller_session_lifecycle");
                await runner.AssertControllerSessionLifecycleAsync(scenario.CombatState);
                runner._completedChecks.Add("ControllerSessionLifecycle");
            }
            if (request.VerifyForkBoundaries)
            {
                runner.SetStage("fork_boundaries");
                AssertForkBoundaries(scenario.CombatState, scenario.Player);
                runner._completedChecks.Add("ForkBoundaries");
            }
            if (request.VerifyCombatRootSnapshot)
            {
                runner.SetStage("combat_root_snapshot");
                await AssertCombatRootSnapshotAsync(scenario.CombatState, scenario.Player);
                runner._completedChecks.Add("CombatRootSnapshot");
            }
            if (request.VerifyBaseLibCardModifierBoundary)
            {
                runner.SetStage("base_lib_card_modifier_boundary");
                await AssertBaseLibCardModifierBoundaryAsync(scenario.CombatState, scenario.Player);
                runner._completedChecks.Add("BaseLibCardModifierBoundary");
            }

            if (request.ExportBugReportAfterSetup)
            {
                runner.SetStage("export_bug_report");
                string directory = ProjectSettings.GlobalizePath("user://combat-solver-test-bug-reports");
                string archivePath = await CombatBugReportExporter.ExportCurrentAsync(directory);
                using ZipArchive archive = ZipFile.OpenRead(archivePath);
                AssertBugReportArchive(archive, "current", request.ExpectedBugReportControlMode);
                runner._completedChecks.Add($"BugReport:{Path.GetFileName(archivePath)}");
            }
        }

        public void AssertAfterExecution(ScenarioContext scenario, ExecutionOutcome outcome)
        {
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
