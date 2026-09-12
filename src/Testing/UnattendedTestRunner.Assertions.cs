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
            if (request.ScenarioId == "MIRRORED-HOOK-FILTER")
            {
                runner.SetStage("mirrored_hook_filter");
                runner.AssertMirroredHookFilter(scenario.CombatState, scenario.Player);
            }
            if (request.ScenarioId == "HAND-POTENTIAL-COSTS")
            {
                runner.SetStage("hand_potential_costs");
                runner.AssertHandPotentialCosts(scenario.CombatState, scenario.Player);
            }
            if (request.ScenarioId == "KNOWN-GAMEPLAY-MOD-BOUNDARY")
            {
                runner.SetStage("known_gameplay_mod_boundary");
                AssertKnownGameplayModBoundary();
                runner._completedChecks.Add("KnownGameplayModBoundary");
            }
            if (request.ScenarioId == "CYCLE-EXIT-REVOKED-PARENT")
            {
                runner.SetStage("cycle_exit_revoked_parent");
                CombatBeamSolver.VerifyCycleExitAdmissionMaterializationPolicyForTesting();
                runner._completedChecks.Add("CycleExitRevokedParent");
            }
            if (request.ScenarioId == "NARROW-ORDERED-PILE-CAPACITY")
            {
                runner.SetStage("narrow_ordered_pile_capacity");
                AssertNarrowOrderedPileCapacity(scenario.CombatState, scenario.Player);
                runner._completedChecks.Add("NarrowOrderedPileCapacity");
            }
            if (request.ScenarioId == "DISCARD-DRAW-SLY-PENDING")
            {
                runner.SetStage("discard_draw_sly_pending");
                AssertPotionDiscardAndDrawStopsAtNestedPending(scenario.CombatState, scenario.Player);
                runner._completedChecks.Add("DiscardDrawSlyPending");
            }
            if (request.ScenarioId == "TURN-END-POWER-ORDER-FORK")
            {
                runner.SetStage("turn_end_power_order_fork");
                runner.AssertTurnEndPowerOrderFork(scenario.CombatState, scenario.Player);
                runner._completedChecks.Add("TurnEndPowerOrderFork");
            }
            if (request.ScenarioId == "PLAYER-END-PHASE-TWO-PENDING")
            {
                runner.SetStage("player_end_phase_two_pending");
                AssertAfterSideTurnEndRelicChoiceSuspends(scenario.CombatState, scenario.Player);
                AssertEndTurnPowerChoiceSuspends(scenario.CombatState, scenario.Player);
                runner._completedChecks.Add("PlayerEndPhaseTwoPending");
            }
            if (request.ScenarioId == "BOUND-COUNTER-FORK")
            {
                runner.SetStage("bound_counter_fork");
                AssertBoundCounterFork(scenario.CombatState);
                runner._completedChecks.Add("BoundCounterFork");
            }
            if (request.ScenarioId == "SURROUNDED-STATE-IDENTITY")
            {
                runner.SetStage("surrounded_state_identity");
                AssertSurroundedStateIdentity(scenario.CombatState);
                runner._completedChecks.Add("SurroundedStateIdentity");
            }
            if (request.VerifyPredictionFailureBoundaries)
            {
                runner.SetStage("prediction_failure_boundaries");
                AssertPredictionFailureBoundaries(scenario.CombatState, scenario.Player);
                runner._completedChecks.Add("PredictionFailureBoundaries");
            }
            if (request.VerifySearchPolicySnapshot)
            {
                runner.SetStage("search_policy_snapshot");
                await AssertSearchPolicySnapshotAsync(
                    scenario.CombatState,
                    verifyStandPatBatches: request.ScenarioId == "STAND-PAT-PROBE-BATCHES");
                runner._completedChecks.Add("SearchPolicySnapshot");
            }
            if (request.VerifyGrowthPolicy)
            {
                runner.SetStage("growth_policy");
                await runner.AssertGrowthPolicyAsync(scenario.CombatState);
                runner._completedChecks.Add("GrowthPolicy");
            }
            if (request.ScenarioId == "DEFAULT-SEARCH-PARALLELISM")
            {
                AssertDefaultSearchParallelism();
                runner._completedChecks.Add("DefaultSearchParallelism");
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
                runner.AssertRoundPrefixReplayBoundaries(scenario.CombatState, scenario.Player);
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
