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

    private sealed partial class Writer(
        Func<UnattendedTestRequest> getRequest,
        Stopwatch stopwatch,
        IReadOnlyList<string> completedChecks,
        DateTimeOffset startedAtUtc,
        Func<UnattendedStageTiming[]> captureStageTimings)
    {
        private UnattendedSolverMetrics? _solverMetrics;
        public bool HasSolverMetrics => _solverMetrics != null;
        public System.Text.Json.Nodes.JsonObject? ReplayVerification { get; set; }
        public bool ProcessReusable { get; set; }
        public System.Text.Json.Nodes.JsonObject? GeneratedScenario { get; set; }

        public void WriteGeneratedArtifact(string name, object value)
        {
            string directory = getRequest().EvidenceDirectory
                ?? throw new InvalidDataException("生成场景必须指定EvidenceDirectory。");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, UnattendedTestFiles.JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }

        public void CaptureSolverResult(SolverResult result)
        {
            if (ReplayVerification != null && CombatBugReportExporter.LatestEffectivePolicy is { } policy)
                ReplayVerification["executedPolicy"] = JsonSerializer.SerializeToNode(policy, UnattendedTestFiles.JsonOptions);
            RuntimeMemorySnapshot memory = CaptureRuntimeMemory();
            _solverMetrics = new UnattendedSolverMetrics
            {
                Boundary = result.BoundaryReason,
                SelectedExpanded = result.ExpandedNodes,
                SelectedTransitions = result.TransitionCount,
                SelectedChoiceBranches = result.ChoiceBranchesEvaluated,
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
                SearchedTurns = result.SearchedTurns,
                ShufflesCrossed = result.Snapshot.ShufflesCrossed,
                Score = result.BestNode.Score,
                ProjectedBattleHpLost = result.ProjectedBattleHpLost,
                UnavoidableHpLost = result.UnavoidableHpLost,
                PotionCount = result.PotionCount,
                PotionUses = result.BestNode.Actions
                    .Where(static action => action.Kind == PlanActionKind.UsePotion)
                    .Select(static action => new UnattendedPotionUse
                    {
                        Id = action.PotionId,
                        Title = action.PotionTitle,
                        Turn = action.Turn,
                        Slot = action.PotionSlot,
                    })
                    .ToArray(),
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
            };

            if (!string.IsNullOrWhiteSpace(getRequest().EvidenceDirectory))
            {
                WriteGeneratedArtifact("search-result.json", new
                {
                    actions = result.BestNode.Actions,
                    snapshot = result.Snapshot,
                    comparisonQuality = result.ComparisonQuality,
                    rootContinuationStamp = result.ComparisonRootState,
                    continuations = result.Continuations.Select(continuation => new
                    {
                        continuation.StartTurnNumber,
                        continuation.ForecastOffset,
                        state = continuation.ExpectedState.StateText,
                    }).ToArray(),
                    pruneCounters = new
                    {
                        result.DominatedActionsPruned,
                        result.TopQueueActionsDropped,
                        result.ActionAdmissionRepresentativesProtected,
                        result.DuplicateCardBranchesPruned,
                        result.ShuffleBranchesPruned,
                        result.SoldHpBranchesPruned,
                        result.TranspositionBranchesPruned,
                        result.RepeatableNoProgressBranchesPruned,
                        result.PrimaryIncumbentBranchesPruned,
                    },
                    result.ResultScope,
                    result.BoundaryReason,
                    policy = CombatBugReportExporter.LatestEffectivePolicy,
                    runtime = new
                    {
                        clr = Environment.Version.ToString(),
                        serverGc = GCSettings.IsServerGC,
                    },
                });
                CombatLogArchive journal = Entry.Logger.Journal.CaptureAsync().GetAwaiter().GetResult();
                if (journal.Events.Error != null)
                    throw new IOException($"Cannot export runtime timing evidence: {journal.Events.Error}");
                WriteGeneratedArtifact("runtime-events.json", System.Text.Encoding.UTF8
                    .GetString(journal.Events.JsonLines).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(static line => JsonSerializer.Deserialize<CombatLogEntry>(line))
                    .ToArray());
            }
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
            UnattendedTestRequest request = getRequest();
            RuntimeMemorySnapshot memory = CaptureRuntimeMemory();
            WriteResult(new UnattendedTestResult
            {
                ProcessReusable = ProcessReusable,
                ReusedProcess = UnattendedTestRunner.ReusedProcess,
                DevelopmentStrategyScriptHash = request.DevelopmentStrategyScriptHash,
                DevelopmentStrategyParametersHash = request.DevelopmentStrategyParametersHash,
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
                ReplayVerification = ReplayVerification,
                GeneratedScenario = GeneratedScenario,
                StageTimings = captureStageTimings(),
                CompletedChecks = completedChecks.ToArray(),
                Error = error,
            }, request);
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

        private static void WriteResult(UnattendedTestResult result, UnattendedTestRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.EvidenceDirectory))
            {
                Directory.CreateDirectory(request.EvidenceDirectory);
                WriteEvidence("request.json", request);
                WriteEvidence("result.json", result);
                WriteEvidence("policy.json", result.ReplayVerification);
                WriteEvidence("timings.json", result.StageTimings);
                WriteEvidence("difference.json", result.ReplayVerification?["firstDifference"]);
                void WriteEvidence(string name, object? value)
                {
                    string path = Path.Combine(request.EvidenceDirectory, name);
                    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, UnattendedTestFiles.JsonOptions));
                    File.Move(path + ".tmp", path, true);
                }
            }
            string resultPath = UnattendedTestFiles.GlobalPath(UnattendedTestFiles.ResultUri);
            string tempPath = resultPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(result, UnattendedTestFiles.JsonOptions));
            File.Move(tempPath, resultPath, true);
        }
    }
}
