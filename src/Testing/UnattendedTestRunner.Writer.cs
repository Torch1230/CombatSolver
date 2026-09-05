using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private readonly record struct RuntimeMemorySnapshot(
        long ManagedHeapBytes,
        long ManagedFragmentedBytes,
        long WorkingSetBytes,
        long PrivateMemoryBytes);

    private sealed class Writer(
        UnattendedTestRequest request,
        Stopwatch stopwatch,
        IReadOnlyList<string> completedChecks,
        DateTimeOffset startedAtUtc,
        Func<UnattendedStageTiming[]> captureStageTimings)
    {
        private UnattendedSolverMetrics? _solverMetrics;

        public void CaptureSolverResult(SolverResult result)
        {
            RuntimeMemorySnapshot memory = CaptureRuntimeMemory();
            SolverSettingsSnapshot configuredSettings = SolverSettings.Capture();
            GCLatencyMode gcLatencyMode = GCSettings.LatencyMode;
            long activeNoGcRegionBudgetBytes =
                SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting;
            _solverMetrics = new UnattendedSolverMetrics
            {
                Phase = result.SearchPhase,
                Boundary = result.BoundaryReason,
                SelectedExpanded = result.ExpandedNodes,
                SelectedTransitions = result.TransitionCount,
                SelectedChoiceBranches = result.ChoiceBranchesEvaluated,
                ChoiceReplayAttempts = result.ChoiceReplayAttempts,
                ChoiceReplayBudgetExhaustions = result.ChoiceReplayBudgetExhaustions,
                ChoiceBranchesDroppedByBudget = result.ChoiceBranchesDroppedByBudget,
                CycleRegionsDetected = result.CycleRegionsDetected,
                CycleRegionCandidatesConsidered = result.CycleRegionCandidatesConsidered,
                CycleRegionCandidatesAdmitted = result.CycleRegionCandidatesAdmitted,
                CycleRegionCandidatesDropped = result.CycleRegionCandidatesDropped,
                CycleRegionProgressEpochs = result.CycleRegionProgressEpochs,
                CycleRegionProbeCandidatesAdmitted =
                    result.CycleRegionProbeCandidatesAdmitted,
                CycleRegionProgressCandidatesAdmitted =
                    result.CycleRegionProgressCandidatesAdmitted,
                CycleRegionMaxActionFamilies = result.CycleRegionMaxActionFamilies,
                OrderedMutationCandidatesAdmitted =
                    result.OrderedMutationCandidatesAdmitted,
                OrderedMutationLeaseExpiredBudget =
                    result.OrderedMutationLeaseExpiredBudget,
                OrderedMutationOrdinaryFallbacks =
                    result.OrderedMutationOrdinaryFallbacks,
                OrderedMutationColdAtomicCommitted =
                    result.OrderedMutationColdAtomicCommitted,
                OrderedMutationColdAtomicRejected =
                    result.OrderedMutationColdAtomicRejected,
                TotalExpanded = result.TotalExpandedNodes,
                TotalTransitions = result.TotalTransitionCount,
                TotalChoiceBranches = result.TotalChoiceBranchesEvaluated,
                ElapsedMilliseconds = result.Elapsed.TotalMilliseconds,
                TotalElapsedMilliseconds = result.TotalSearchElapsed.TotalMilliseconds,
                WorkerAllocatedBytes = result.WorkerAllocatedBytes,
                TotalWorkerAllocatedBytes = result.TotalWorkerAllocatedBytes,
                TotalGen0Collections = result.TotalGen0Collections,
                TotalGen1Collections = result.TotalGen1Collections,
                TotalGen2Collections = result.TotalGen2Collections,
                TotalGcPauseMilliseconds = result.TotalGcPauseDuration.TotalMilliseconds,
                MaxGcPauseMilliseconds = result.TotalMaxObservedGcPause.TotalMilliseconds,
                MaxParallelConcurrency = result.MaxParallelExpansionConcurrency,
                ParallelActionReplayWaves = result.ParallelActionReplayWaves,
                ParallelActionReplayWorkItems = result.ParallelActionReplayWorkItems,
                MaxParallelActionReplayConcurrency = result.MaxParallelActionReplayConcurrency,
                DeferredRoundChoiceActions = result.DeferredRoundChoiceActions,
                DeferredRoundChoiceLayerWidthTotal = result.DeferredRoundChoiceLayerWidthTotal,
                MaxDeferredRoundChoiceLayerWidth = result.MaxDeferredRoundChoiceLayerWidth,
                DeferredRoundChoiceFiniteQuotaFallbacks =
                    result.DeferredRoundChoiceFiniteQuotaFallbacks,
                DeferredRoundChoiceFinitePrimaryLayers =
                    result.DeferredRoundChoiceFinitePrimaryLayers,
                DeferredRoundChoiceFinitePendingFallbacks =
                    result.DeferredRoundChoiceFinitePendingFallbacks,
                ParallelRoundChoiceReplayWaves = result.ParallelRoundChoiceReplayWaves,
                ParallelRoundChoiceReplayWorkItems = result.ParallelRoundChoiceReplayWorkItems,
                MaxParallelRoundChoiceReplayConcurrency =
                    result.MaxParallelRoundChoiceReplayConcurrency,
                SearchedTurns = result.SearchedTurns,
                ShufflesCrossed = result.Snapshot.ShufflesCrossed,
                Score = result.BestNode.Score,
                ProjectedBattleHpLost = result.ProjectedBattleHpLost,
                PotionCount = result.PotionCount,
                OnlyDeathRoutes = result.OnlyDeathRoutesFound,
                FinalHp = result.Snapshot.PlayerHp,
                FinalEnemyHp = result.Snapshot.EnemyHp,
                CombatEndedTurn = result.CombatEndedTurn,
                CapturedAtElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                ManagedLiveBytes = GC.GetTotalMemory(forceFullCollection: false),
                ManagedHeapBytes = memory.ManagedHeapBytes,
                ManagedFragmentedBytes = memory.ManagedFragmentedBytes,
                WorkingSetBytes = memory.WorkingSetBytes,
                PrivateMemoryBytes = memory.PrivateMemoryBytes,
                ConfiguredNoGcRegionEnabled = configuredSettings.EnableNoGcRegion,
                ConfiguredNoGcRegionBudgetBytes = configuredSettings.NoGcRegionBudgetBytes,
                GcLatencyMode = gcLatencyMode,
                NoGcRegionActive = gcLatencyMode == GCLatencyMode.NoGCRegion,
                NoGcRegionBudgetBytes = activeNoGcRegionBudgetBytes,
                NoGcRegionRolloverCount = SearchGcPolicy.RolloverCountForTesting,
            };
        }

        public RuntimeMemorySnapshot Write(
            string status,
            string stage,
            string characterId,
            string encounterId,
            bool combatEnded,
            int startedTurn,
            int finishedTurn,
            string? error = null)
        {
            RuntimeMemorySnapshot memory = CaptureRuntimeMemory();
            WriteResult(new UnattendedTestResult
            {
                RunId = request.RunId,
                ScenarioId = request.ScenarioId,
                Status = status,
                Stage = stage,
                CharacterId = characterId,
                EncounterId = encounterId,
                Seed = request.Seed,
                StartedAtUtc = startedAtUtc,
                ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                MainThread = NGame.IsMainThread(),
                CombatEnded = combatEnded,
                StartedTurn = startedTurn,
                FinishedTurn = finishedTurn,
                ManagedHeapBytes = memory.ManagedHeapBytes,
                ManagedFragmentedBytes = memory.ManagedFragmentedBytes,
                WorkingSetBytes = memory.WorkingSetBytes,
                PrivateMemoryBytes = memory.PrivateMemoryBytes,
                SolverMetrics = _solverMetrics,
                StageTimings = captureStageTimings(),
                CompletedChecks = completedChecks.ToArray(),
                Error = error,
            });
            return memory;
        }

        private static RuntimeMemorySnapshot CaptureRuntimeMemory()
        {
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            using Process process = Process.GetCurrentProcess();
            return new RuntimeMemorySnapshot(
                gc.HeapSizeBytes,
                gc.FragmentedBytes,
                process.WorkingSet64,
                process.PrivateMemorySize64);
        }

        private static void WriteResult(UnattendedTestResult result)
        {
            string resultPath = UnattendedTestFiles.GlobalPath(UnattendedTestFiles.ResultUri);
            string tempPath = resultPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(result, UnattendedTestFiles.JsonOptions));
            File.Move(tempPath, resultPath, true);
        }
    }
}
