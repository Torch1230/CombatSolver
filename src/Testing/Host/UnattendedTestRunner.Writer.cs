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
            SolverSettingsSnapshot configuredSettings = SolverSettings.Capture();
            GCLatencyMode gcLatencyMode = GCSettings.LatencyMode;
            long activeNoGcRegionBudgetBytes =
                SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting;
            _solverMetrics = new UnattendedSolverMetrics
            {
                GcLifecycle = result.GcLifecycle,
                GcLifecycleAttribution = result.GcLifecycleAttribution,
                Boundary = result.BoundaryReason,
                SelectedExpanded = result.ExpandedNodes,
                SelectedTransitions = result.TransitionCount,
                SelectedChoiceBranches = result.ChoiceBranchesEvaluated,
                ChoiceReplayAttempts = result.ChoiceReplayAttempts,
                RoundReplayPrefixCaptures = result.RoundReplayPrefixCaptures,
                RoundReplayPrefixReuses = result.RoundReplayPrefixReuses,
                ExecutionChoiceCaptures = result.ExecutionChoiceCaptures,
                ExecutionChoiceReuses = result.ExecutionChoiceReuses,
                CardChoicePrefixAttempts = result.CardChoicePrefixAttempts,
                CardChoicePrefixCaptures = result.CardChoicePrefixCaptures,
                CardChoicePrefixReuses = result.CardChoicePrefixReuses,
                CardChoicePrefixFallbacks = result.CardChoicePrefixFallbacks,
                PotionChoicePrefixForks = result.PotionChoicePrefixForks,
                PotionChoicePrefixCaptures = result.PotionChoicePrefixCaptures,
                PotionChoicePrefixReuses = result.PotionChoicePrefixReuses,
                PotionChoicePrefixFallbacks = result.PotionChoicePrefixFallbacks,
                ChoiceReplayBudgetExhaustions = result.ChoiceReplayBudgetExhaustions,
                ChoiceBranchesDroppedByBudget = result.ChoiceBranchesDroppedByBudget,
                CycleReplayAttempts = result.CycleReplayAttempts,
                CycleReplayActions = result.CycleReplayActions,
                TotalCycleReplayActions = result.TotalCycleReplayActions,
                CycleReplayVictories = result.CycleReplayVictories,
                CycleReplayContinuations = result.CycleReplayContinuations,
                CycleStoppedUnproductive = result.CycleStoppedUnproductive,
                CycleStoppedRepetitionBudget = result.CycleStoppedRepetitionBudget,
                CycleStoppedFamilyBudget = result.CycleStoppedFamilyBudget,
                CycleStoppedExitBudget = result.CycleStoppedExitBudget,
                TurnLayerBudgetStops = result.TurnLayerBudgetStops,
                TurnLayerTimeBudgetStops = result.TurnLayerTimeBudgetStops,
                TurnLayerNodeBudgetStops = result.TurnLayerNodeBudgetStops,
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
                SearchWorkAttributions = result.SearchWorkAttributions,
                BossTempoSearch = result.BossTempoSearch,
                BossTempoIteration = result.BossTempoIteration,
                WorkerAllocatedBytes = result.WorkerAllocatedBytes,
                TotalWorkerAllocatedBytes = result.TotalWorkerAllocatedBytes,
                TotalGen0Collections = result.TotalGen0Collections,
                TotalGen1Collections = result.TotalGen1Collections,
                TotalGen2Collections = result.TotalGen2Collections,
                TotalGcPauseMilliseconds = result.TotalGcPauseDuration.TotalMilliseconds,
                MaxGcPauseMilliseconds = result.TotalMaxObservedGcPause.TotalMilliseconds,
                // E1：早期回合探索摘要直接透出；探索跳过时为 null，
                // 与 SearchWorkAttributions 里有没有 EarlyTurnContinuation 归因正交。
                EarlyTurnExploration = CaptureEarlyTurnExplorationMetrics(result.EarlyTurnExploration),
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
                // 组合开关关闭时这三项照样有（单成员一行），A/B 才能直接并排比。
                FirstRoutePublishedMilliseconds = result.PortfolioTelemetry?.FirstRoutePublishedMilliseconds,
                PortfolioMembers = result.PortfolioTelemetry?.Members.ToArray() ?? [],
                PowerRouteMembers = result.PortfolioTelemetry?.PowerRouteMembers.ToArray() ?? [],
                PeakManagedHeapBytes = result.PortfolioTelemetry?.PeakManagedHeapBytes ?? 0,
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
            // Search timing has already stopped. Keep full route/policy/snapshot
            // evidence outside the timed work and only for explicit test output.
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
                        result.CycleRegionsDetected,
                        result.CycleRegionCandidatesDropped,
                        result.CrossTurnCandidatesProtected,
                    },
                    result.ResultScope,
                    result.BoundaryReason,
                    policy = CombatBugReportExporter.LatestEffectivePolicy,
                    runtime = new
                    {
                        clr = Environment.Version.ToString(),
                        serverGc = GCSettings.IsServerGC,
                        profile = RuntimeGcProfile.Current.Status.ToString(),
                        savedNoGcRegionEnabled = SolverSettings.Current.EnableNoGcRegion,
                        effectiveNoGcRegionEnabled = configuredSettings.EnableNoGcRegion,
                    },
                });
                // The journal buffers file writes. Its FIFO snapshot makes the
                // already completed worker's timestamps available before exit.
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

        // 逐续搜纯值随结果输出，便于离线批量数据关联 rank、开局与搜索成本；
        // 它们不参与候选裁决，也不进入战斗状态键。
        private static UnattendedEarlyTurnExplorationMetrics? CaptureEarlyTurnExplorationMetrics(
            EarlyTurnExplorationTelemetry? telemetry)
            => telemetry is null
                ? null
                : new UnattendedEarlyTurnExplorationMetrics
                {
                    ScoutExpanded = telemetry.ScoutExpanded,
                    FrontierCandidates = telemetry.FrontierCandidates,
                    Attempted = telemetry.Attempted,
                    Improvements = telemetry.Improvements,
                    BestLoss = telemetry.BestLoss,
                    BestPotions = telemetry.BestPotions,
                    BestEndedTurn = telemetry.BestEndedTurn,
                    FirstImprovementDepth = telemetry.FirstImprovementDepth,
                    FirstImprovementRank = telemetry.FirstImprovementRank,
                    Expanded = telemetry.Expanded,
                    Stop = telemetry.Stop,
                    Continuations = telemetry.Continuations.ToArray(),
                };

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
