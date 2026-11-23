using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private enum RunCompletion
    {
        Passed,
        InitialSearchHeld,
        Failed,
        FailedReusable,
    }

    private static readonly ProtocolHost Host = new();

    public static bool IsActive => Host.IsActive;
    internal static bool ReusedProcess => Host.ReusedProcess;
    internal static bool IsReplayingRecordedInputs => CombatReplayRecording.TestObserver != null;
    public static bool AutomaticTurnSearchEnabled => Host.AutomaticTurnSearchEnabled;
    public static bool VerifyIncrementalSearch => Host.VerifyIncrementalSearch;
    public static bool FixedSearchBudget => Host.FixedSearchBudget;
    public static bool MeasureSearchPhases => Host.MeasureSearchPhases;
    public static int? SearchBudgetOverrideMilliseconds => Host.SearchBudgetOverrideMilliseconds;
    public static int? SearchMaxDegreeOfParallelismOverride => Host.SearchMaxDegreeOfParallelismOverride;
    internal static SolverSearchProfile? CheckpointProfileOverride => Host.CheckpointProfileOverride;
    public static int TranspositionPruningDisabledMaskOverride => Host.TranspositionPruningDisabledMaskOverride;
    public static int? TranspositionEntryLimitOverride => Host.TranspositionEntryLimitOverride;
    public static bool? Act3BossStrategyOverride => Host.Act3BossStrategyOverride;

    private readonly NGame _host;
    private UnattendedTestRequest _request;
    private readonly ProtocolHost _protocolHost;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly List<string> _completedChecks = [];
    private readonly List<UnattendedStageTiming> _completedStageTimings = [];
    private readonly Writer _writer;
    private readonly ScenarioBuilder _scenarioBuilder;
    private readonly Assertions _assertions;
    private readonly Executor _executor;
    private string _stage = "created";
    private double _stageStartedMilliseconds;
    private FastModeType? _headlessFastModeBeforeTest;
    private SolverResult? _lastObservedResult;
    private int? _lastReusedTurn;
    private int? _lastReusedProjectedBattleHpLost;

    private UnattendedTestRunner(NGame host, UnattendedTestRequest request, ProtocolHost protocolHost)
    {
        _host = host;
        _request = request;
        _protocolHost = protocolHost;
        _writer = new Writer(
            () => _request,
            _stopwatch,
            _completedChecks,
            _startedAtUtc,
            CaptureStageTimings);
        _scenarioBuilder = new ScenarioBuilder(this);
        _assertions = new Assertions(this);
        _executor = new Executor(this);
        CombatReplayRecording.TestSearchResultObserver = result =>
        {
            _lastObservedResult = result;
            if (result.WasReused)
            {
                _lastReusedTurn = result.StartTurnNumber;
                _lastReusedProjectedBattleHpLost = result.ProjectedBattleHpLost;
            }
            if (!_writer.HasSolverMetrics)
                _writer.CaptureSolverResult(result);
        };
    }

    public static void TryStart(NGame? host) => Host.TryStart(host);

    internal static Task ApplyScheduledStateDriftAsync(CombatState state, int turn)
        => Host.ApplyScheduledStateDriftAsync(state, turn);

    internal static Task ApplyScheduledPreEndTurnDriftAsync(CombatState state, int turn)
        => Host.ApplyScheduledPreEndTurnDriftAsync(state, turn);

    private async Task<RunCompletion> RunAsync()
    {
        CombatState? combatState = null;
        int startedTurn = 0;
        DevelopmentMonitorPublisher? monitor = DevelopmentMonitorPublisher.Start(_request);
        try
        {
            if (_request.ReplayMode == "SessionStart")
            {
                SetStage("passed");
                _writer.Write("Passed", _stage, _request.CharacterId, _request.EncounterId,
                    combatEnded: false, startedTurn: 0, finishedTurn: 0);
                return RunCompletion.Passed;
            }
            ScenarioContext scenario = await _scenarioBuilder.BuildAsync();
            combatState = scenario.CombatState;
            startedTurn = scenario.StartedTurn;

            await _assertions.RunBeforeExecutionAsync(scenario);

            ExecutionOutcome outcome = await _executor.ExecuteAsync(scenario);
            if (outcome.InitialSearchHeld)
            {
                SetStage("initial_search_held");
                RuntimeMemorySnapshot heldMemory = _writer.Write(
                    "Passed",
                    _stage,
                    scenario.Character.Id.ToString(),
                    scenario.Encounter.Id.ToString(),
                    combatEnded: false,
                    startedTurn,
                    finishedTurn: startedTurn);
                Entry.Logger.Info(
                    $"[CombatSolver/Unattended] PASSED run_id={_request.RunId} scenario={_request.ScenarioId} " +
                    $"stage=initial_search_held elapsed_ms={_stopwatch.Elapsed.TotalMilliseconds:F1} " +
                    $"managed_heap_bytes={heldMemory.ManagedHeapBytes} fragmented_bytes={heldMemory.ManagedFragmentedBytes} " +
                    $"working_set_bytes={heldMemory.WorkingSetBytes} private_bytes={heldMemory.PrivateMemoryBytes}");
                return RunCompletion.InitialSearchHeld;
            }
            _assertions.AssertAfterExecution(scenario, outcome);
            if (_writer.GeneratedScenario != null)
            {
                _writer.GeneratedScenario["combatEnded"] = outcome.CombatEnded;
                _writer.GeneratedScenario["actualOutcome"] = JsonSerializer.SerializeToNode(
                    CombatBugReportExporter.CaptureOutcome(combatState), UnattendedTestFiles.JsonOptions);
            }
            if (_writer.ReplayVerification != null && outcome.CombatEnded)
            {
                _writer.ReplayVerification["actualOutcome"] = JsonSerializer.SerializeToNode(
                    CombatBugReportExporter.CaptureOutcome(combatState), UnattendedTestFiles.JsonOptions);
                CombatBugReportClassificationSnapshot classification = SolverController.CaptureBugReportClassificationForExport();
                _writer.ReplayVerification["unexpectedReplans"] = classification.StateMismatchReplans
                    + classification.DeploymentDriftReplans + classification.ContinuationMissingReplans + classification.PlanExhaustedReplans;
                if (_request.ReplayMode == "DeploySolver")
                    _writer.ReplayVerification["status"] = "deployment_completed";
            }
            _lastObservedResult = null;

            SetStage("cleanup");
            if (outcome.CombatEnded && !string.IsNullOrWhiteSpace(_request.ShowcaseBundlePath))
                await ReturnShowcaseToMainMenuThroughTerminalRewardsAsync();
            else
                await _host.ReturnToMainMenu();
            EnsureWithinDeadline();
            if (_request.ExportBugReportAfterCombat)
            {
                SetStage("export_bug_report_after_combat");
                string directory = _request.EvidenceDirectory is { Length: > 0 } evidence
                    ? Path.Combine(evidence, "bug-reports")
                    : ProjectSettings.GlobalizePath("user://combat-solver-test-bug-reports");
                string archivePath = await CombatBugReportExporter.ExportCurrentAsync(directory);
                using ZipArchive archive = ZipFile.OpenRead(archivePath);
                AssertBugReportArchive(archive, "recent", _request.ExpectedBugReportControlMode);
                using Stream combatStateStream = archive.GetEntry("diagnostics/combat-state.json")!.Open();
                using JsonDocument combatStateDocument = JsonDocument.Parse(combatStateStream);
                if (combatStateDocument.RootElement.GetProperty("combatActive").GetBoolean())
                    throw new InvalidDataException("战后问题包错误标记为活动战斗。");
                _completedChecks.Add($"BugReportAfterCombat:{Path.GetFileName(archivePath)}");
            }

            SetStage("passed");
            _writer.Write(
                "Passed",
                _stage,
                scenario.Character.Id.ToString(),
                scenario.Encounter.Id.ToString(),
                outcome.CombatEnded,
                startedTurn,
                outcome.FinishedTurn);
            Entry.Logger.Info($"[CombatSolver/Unattended] PASSED run_id={_request.RunId} scenario={_request.ScenarioId} elapsed_ms={_stopwatch.Elapsed.TotalMilliseconds:F1}");
            await ExitIfRequestedAsync(0);
            return RunCompletion.Passed;
        }
        catch (Exception ex)
        {
            bool reusableInputFailure = _stage == "archive_preflight" && !RunManager.Instance.IsInProgress;
            _writer.ProcessReusable = reusableInputFailure;
            if (_writer.ReplayVerification != null)
            {
                string reason = ex.Message;
                _writer.ReplayVerification["status"] = ex is TimeoutException ? "timeout"
                    : reason.Contains("environment_mismatch", StringComparison.Ordinal) ? "environment_mismatch"
                    : _stage == "archive_preflight" ? "materials_missing"
                    : _writer.ReplayVerification["status"]?.GetValue<string>() is "recorded_action_mismatch" or "recorded_input_stalled"
                        ? "recorded_action_mismatch"
                        : _writer.ReplayVerification["restorationVerified"]?.GetValue<bool>() == true ? "execution_failed" : "restore_mismatch";
                _writer.ReplayVerification["reason"] = reason;
                if (ex.Data["firstDifference"] is System.Text.Json.Nodes.JsonObject difference)
                    _writer.ReplayVerification["firstDifference"] = difference;
            }
            _protocolHost.EnableAutomaticTurnSearch();
            combatState ??= _scenarioBuilder.CombatState;
            if (startedTurn == 0)
                startedTurn = _scenarioBuilder.StartedTurn;
            _writer.Write(
                "Failed",
                _stage,
                _request.CharacterId,
                _request.EncounterId,
                combatState != null && !CombatManager.Instance.IsInProgress,
                startedTurn,
                combatState == null
                    ? startedTurn
                    : LocalContext.GetMe(combatState)?.PlayerCombatState?.TurnNumber ?? startedTurn,
                ex.ToString());
            Entry.Logger.Error($"[CombatSolver/Unattended] FAILED run_id={_request.RunId} scenario={_request.ScenarioId} stage={_stage} exception={ex}");
            try
            {
                if (RunManager.Instance.IsInProgress)
                    await _host.ReturnToMainMenu();
            }
            finally
            {
                await ExitIfRequestedAsync(1);
            }
            return reusableInputFailure ? RunCompletion.FailedReusable : RunCompletion.Failed;
        }
        finally
        {
            if (monitor != null)
                await monitor.DisposeAsync();
            _executor.RestoreSettings();
            RestoreHeadlessFastModeOverride();
            ReleaseCheckpointImport();
        }
    }

    private static void AssertBugReportArchive(
        ZipArchive archive,
        string forensicSlot,
        string? expectedControlMode)
    {
        string[] requiredEntries =
        [
            "diagnostics/combat-state.json",
            "diagnostics/current-route.txt",
            "diagnostics/replan-audit.txt",
            "diagnostics/settings.json",
            "diagnostics/export-context.json",
            "diagnostics/environment.json",
            "diagnostics/logs/index.json",
            "diagnostics/logs/history.json",
            "replay/manifest.json",
            "replay/checkpoint.json",
            $"replay/{forensicSlot}/session.json",
            $"replay/{forensicSlot}/pre-combat/in-memory-current_run.save",
            $"replay/{forensicSlot}/last-route.txt",
            $"replay/{forensicSlot}/replan-audit.txt",
            "README.txt",
            "report.json",
        ];
        foreach (string entry in requiredEntries)
        {
            if (archive.GetEntry(entry) == null)
                throw new InvalidDataException($"问题包缺少 {entry}。");
        }
        if (archive.Entries.Any(entry =>
                Path.IsPathRooted(entry.FullName)
                || entry.FullName.Split('/').Contains("..", StringComparer.Ordinal))
            || archive.Entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("问题包存在不安全或重复的条目名。");
        }
        string otherForensicSlot = forensicSlot == "current" ? "recent" : "current";
        if (archive.Entries.Any(entry =>
                entry.FullName.StartsWith($"replay/{otherForensicSlot}/", StringComparison.Ordinal)))
            throw new InvalidDataException("问题包同时包含当前战斗和此前战斗。");
        if (archive.GetEntry("screenshot.png") != null
            || archive.Entries.Any(entry => entry.FullName.StartsWith("saves/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("精简问题包仍包含截图或整批磁盘存档。");
        }
        if (archive.Entries.Count(entry =>
                entry.FullName.StartsWith($"replay/{forensicSlot}/checkpoints/", StringComparison.Ordinal)) > 6)
        {
            throw new InvalidDataException("精简问题包归档了超过 6 个战斗检查点。");
        }
        ZipArchiveEntry[] generalLogs = archive.Entries
            .Where(entry => entry.FullName.StartsWith("diagnostics/logs/", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".jsonl", StringComparison.Ordinal))
            .ToArray();
        if (generalLogs.Sum(entry => entry.Length) > 36L * 1024 * 1024
            || archive.Entries.Any(entry => entry.FullName.Contains("godot", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("独立诊断日志超过记录上限或包含 Godot 全局日志。");

        using Stream settingsStream = archive.GetEntry("diagnostics/settings.json")!.Open();
        using JsonDocument settingsDocument = JsonDocument.Parse(settingsStream);
        if (settingsDocument.RootElement.TryGetProperty("reporterContactQq", out _))
            throw new InvalidDataException("问题包设置仍包含反馈联系QQ。");

        using Stream environmentStream = archive.GetEntry("diagnostics/environment.json")!.Open();
        using JsonDocument environmentDocument = JsonDocument.Parse(environmentStream);
        JsonElement environment = environmentDocument.RootElement;
        if (Path.IsPathRooted(environment.GetProperty("gameExecutable").GetString())
            || Path.IsPathRooted(environment.GetProperty("userDataDirectory").GetString())
            || environment.GetProperty("loadedAssemblies").EnumerateArray().Any(assembly =>
                assembly.TryGetProperty("location", out _)))
        {
            throw new InvalidDataException("问题包环境信息仍包含本机绝对路径。");
        }

        using Stream checkpointIndexStream = archive.GetEntry("replay/checkpoint.json")!.Open();
        using JsonDocument checkpointIndexDocument = JsonDocument.Parse(checkpointIndexStream);
        JsonElement checkpointIndex = checkpointIndexDocument.RootElement;
        if (checkpointIndex.GetProperty("schemaVersion").GetInt32() != 2
            || !checkpointIndex.GetProperty("available").GetBoolean())
        {
            throw new InvalidDataException("问题包没有可还原的战斗检查点。");
        }
        if (!string.Equals(
                checkpointIndex.GetProperty("slot").GetString(),
                forensicSlot,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("问题包检查点索引与战斗会话不一致。");
        }
        string defaultCheckpointId = checkpointIndex.GetProperty("defaultCheckpointId").GetString()!;
        checkpointIndex = checkpointIndex.GetProperty("checkpoints").EnumerateArray().Single(item =>
            item.GetProperty("checkpointId").GetString() == defaultCheckpointId);
        string checkpointPath = RequiredRelativeArchivePath(checkpointIndex, "metadataPath");
        string replayStatePath = RequiredRelativeArchivePath(checkpointIndex, "replayStatePath");
        string nativeStatePath = RequiredRelativeArchivePath(checkpointIndex, "nativeStatePath");
        string runStatePath = RequiredRelativeArchivePath(checkpointIndex, "runStatePath");
        ZipArchiveEntry checkpoint = archive.GetEntry(checkpointPath)
            ?? throw new InvalidDataException("问题包检查点索引引用的 metadata 不存在。");
        AssertUtf8JsonArchiveEntry(checkpoint, $"{forensicSlot} 检查点 metadata");
        ZipArchiveEntry replayState = archive.GetEntry(replayStatePath)
            ?? throw new InvalidDataException("问题包检查点索引引用的 replay-state 不存在。");
        ZipArchiveEntry nativeState = archive.GetEntry(nativeStatePath)
            ?? throw new InvalidDataException("问题包检查点索引引用的 native-state 不存在。");
        ZipArchiveEntry runState = archive.GetEntry(runStatePath)
            ?? throw new InvalidDataException("问题包检查点索引引用的 run-state 不存在。");
        if (nativeState.Length == 0)
            throw new InvalidDataException("问题包中的游戏原生战斗状态包为空。");
        AssertUtf8JsonArchiveEntry(replayState, $"{forensicSlot} 检查点 replay-state");

        using Stream runStateStream = runState.Open();
        using JsonDocument runStateDocument = JsonDocument.Parse(runStateStream);
        if (!runStateDocument.RootElement.TryGetProperty("rng", out JsonElement savedRunRng)
            || !savedRunRng.TryGetProperty("rngs", out JsonElement savedRngs)
            || savedRngs.EnumerateObject().Count() < 9)
        {
            throw new InvalidDataException("问题包的即时跑局存档没有完整 Run RNG 流。");
        }

        string rootPrefix = $"replay/{forensicSlot}";
        using Stream sessionStream = archive.GetEntry($"{rootPrefix}/session.json")!.Open();
        using JsonDocument sessionDocument = JsonDocument.Parse(sessionStream);
        JsonElement sessionRoot = sessionDocument.RootElement;
        if (!sessionRoot.TryGetProperty("controlMode", out JsonElement sessionControlMode)
            || !sessionRoot.TryGetProperty("lastSolverDeployedTurn", out _))
        {
            throw new InvalidDataException("问题包战斗会话没有记录求解器/手操接管状态。");
        }

        using Stream exportContextStream = archive.GetEntry("diagnostics/export-context.json")!.Open();
        using JsonDocument exportContextDocument = JsonDocument.Parse(exportContextStream);
        string controlModeProperty = forensicSlot == "current" ? "currentControlMode" : "recentControlMode";
        if (!exportContextDocument.RootElement.TryGetProperty(controlModeProperty, out JsonElement contextControlMode))
            throw new InvalidDataException("问题包导出上下文没有记录求解器/手操接管状态。");
        if (expectedControlMode != null
            && (!string.Equals(sessionControlMode.GetString(), expectedControlMode, StringComparison.Ordinal)
                || !string.Equals(contextControlMode.GetString(), expectedControlMode, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"问题包玩法标记不符：预期 {expectedControlMode}，" +
                $"会话为 {sessionControlMode.GetString()}，上下文为 {contextControlMode.GetString()}。");
        }
    }

    private static void AssertUtf8JsonArchiveEntry(ZipArchiveEntry entry, string description)
    {
        using (Stream prefixStream = entry.Open())
        {
            Span<byte> prefix = stackalloc byte[3];
            int prefixLength = 0;
            while (prefixLength < prefix.Length)
            {
                int read = prefixStream.Read(prefix[prefixLength..]);
                if (read == 0)
                    break;
                prefixLength += read;
            }
            if (prefixLength == prefix.Length && prefix.SequenceEqual(Encoding.UTF8.Preamble))
                throw new InvalidDataException($"问题包的 {description} 不应包含 UTF-8 BOM。");
        }

        UTF8Encoding strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        using Stream input = entry.Open();
        using StreamReader reader = new(input, strictUtf8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: false);
        Span<char> buffer = stackalloc char[4096];
        try
        {
            while (reader.Read(buffer) > 0) { }
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"问题包的 {description} 不是有效 UTF-8。", ex);
        }
    }

    private static string RequiredRelativeArchivePath(JsonElement index, string propertyName)
    {
        string path = index.GetProperty(propertyName).GetString()
            ?? throw new InvalidDataException($"问题包检查点索引缺少 {propertyName}。");
        if (path.Length == 0
            || Path.IsPathRooted(path)
            || path.Split('/').Contains("..", StringComparer.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"问题包检查点索引的 {propertyName} 不是安全的相对路径。");
        }
        return path;
    }

    private bool WasExpectedCardPlayed()
        => _request.ExpectedPlayedCardId is { } expectedPlayedCardId
            && CombatManager.Instance.History.CardPlaysFinished.Any(entry =>
                entry.CardPlay.Card.Id.Entry.Equals(expectedPlayedCardId, StringComparison.OrdinalIgnoreCase));

    private bool WasExpectedPotionUsed()
        => _request.ExpectedUsedPotionId is { } expectedPotionId
            && CombatManager.Instance.History.Entries.OfType<PotionUsedEntry>().Any(entry =>
                entry.Potion.Id.Entry.Equals(expectedPotionId, StringComparison.OrdinalIgnoreCase)
                || entry.Potion.GetType().Name.Equals(expectedPotionId, StringComparison.OrdinalIgnoreCase));

    private bool HasExpectedPlayerPower(Player player)
        => _request.ExpectedObservedPlayerPowerId is { } expectedPowerId
            && player.Creature.Powers.Any(power =>
                power.Id.Entry.Equals(expectedPowerId, StringComparison.OrdinalIgnoreCase)
                || power.GetType().Name.Equals(expectedPowerId, StringComparison.OrdinalIgnoreCase));

    private async Task<CombatState> WaitForPlayableCombatAsync()
    {
        bool turnSetupPlanAccepted = false;
        while (true)
        {
            EnsureWithinDeadline();
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            Player? player = state == null ? null : LocalContext.GetMe(state);
            if (!turnSetupPlanAccepted
                && state != null
                && PlayerTurnSetupCoordinator.HasPendingPlannedChoice(state))
            {
                turnSetupPlanAccepted = PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                    _host,
                    state,
                    deployAfterSetup: false);
            }
            if (state != null
                && CombatManager.Instance.IsInProgress
                && state.CurrentSide == CombatSide.Player
                && player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
            {
                return state;
            }
            await NextFrameAsync();
        }
    }

    private async Task<CombatState> WaitForPendingTurnSetupChoiceAsync()
    {
        while (true)
        {
            EnsureWithinDeadline();
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            if (state != null
                && PlayerTurnSetupCoordinator.IsInitialChoiceSearchPendingForTesting(state)
                && NPlayerHand.Instance?.IsInCardSelection == true)
            {
                return state;
            }
            await NextFrameAsync();
        }
    }

    private IReadOnlyList<UnattendedMonsterMoveCheck> GetMonsterMoveChecks()
        => _request.MonsterMoveChecks.Length > 0
            ? _request.MonsterMoveChecks
            : _request.MonsterMoveCheck != null ? [_request.MonsterMoveCheck] : [];

    private IReadOnlyList<UnattendedPotionCheck> GetPotionChecks()
        => _request.PotionChecks.Length > 0
            ? _request.PotionChecks
            : _request.PotionCheck != null ? [_request.PotionCheck] : [];

    private async Task NextFrameAsync()
    {
        await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void EnsureWithinDeadline()
    {
        if (_stopwatch.Elapsed.TotalSeconds > _request.TimeoutSeconds)
            throw new TimeoutException($"无人测试在阶段 {_stage} 超过 {_request.TimeoutSeconds:F0} 秒。");
    }

    private void SetStage(string stage)
    {
        double elapsedMilliseconds = _stopwatch.Elapsed.TotalMilliseconds;
        double previousStageMilliseconds = Math.Max(0, elapsedMilliseconds - _stageStartedMilliseconds);
        _completedStageTimings.Add(new UnattendedStageTiming
        {
            Stage = _stage,
            StartedMilliseconds = _stageStartedMilliseconds,
            DurationMilliseconds = previousStageMilliseconds,
        });
        string previousStage = _stage;
        _stage = stage;
        _stageStartedMilliseconds = elapsedMilliseconds;
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] STAGE run_id={_request.RunId} stage={stage} " +
            $"elapsed_ms={elapsedMilliseconds:F1} previous_stage={previousStage} " +
            $"previous_stage_ms={previousStageMilliseconds:F1}");
    }

    private UnattendedStageTiming[] CaptureStageTimings()
    {
        double elapsedMilliseconds = _stopwatch.Elapsed.TotalMilliseconds;
        UnattendedStageTiming[] timings = new UnattendedStageTiming[_completedStageTimings.Count + 1];
        _completedStageTimings.CopyTo(timings);
        timings[^1] = new UnattendedStageTiming
        {
            Stage = _stage,
            StartedMilliseconds = _stageStartedMilliseconds,
            DurationMilliseconds = Math.Max(0, elapsedMilliseconds - _stageStartedMilliseconds),
        };
        return timings;
    }

    private void ApplyHeadlessFastModeOverride()
    {
        if (_request.HeadlessFastModeForTest is not { } requestedMode
            || requestedMode == SolverDeploymentFastMode.FollowGame
            || _headlessFastModeBeforeTest.HasValue)
        {
            return;
        }

        _headlessFastModeBeforeTest = SaveManager.Instance.PrefsSave.FastMode;
        SaveManager.Instance.PrefsSave.FastMode = requestedMode switch
        {
            SolverDeploymentFastMode.Normal => FastModeType.Normal,
            SolverDeploymentFastMode.Fast => FastModeType.Fast,
            SolverDeploymentFastMode.Instant => FastModeType.Instant,
            _ => throw new InvalidOperationException($"不支持的无头测试速度 {requestedMode}。"),
        };
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] HEADLESS_FAST_MODE run_id={_request.RunId} " +
            $"requested={requestedMode} previous={_headlessFastModeBeforeTest}");
    }

    private void RestoreHeadlessFastModeOverride()
    {
        if (_headlessFastModeBeforeTest is not { } originalFastMode)
            return;
        SaveManager.Instance.PrefsSave.FastMode = originalFastMode;
        _headlessFastModeBeforeTest = null;
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] HEADLESS_FAST_MODE_RESTORED run_id={_request.RunId} " +
            $"restored={originalFastMode}");
    }

    private async Task ExitIfRequestedAsync(int exitCode)
    {
        if (!_request.ExitOnComplete)
            return;
        await NextFrameAsync();
        _host.GetTree().Quit(exitCode);
    }
}
