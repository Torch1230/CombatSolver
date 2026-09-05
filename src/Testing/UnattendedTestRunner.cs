using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private enum RunCompletion
    {
        Passed,
        InitialSearchHeld,
        Failed,
    }

    private static readonly ProtocolHost Host = new();

    public static bool IsActive => Host.IsActive;
    public static bool AutomaticTurnSearchEnabled => Host.AutomaticTurnSearchEnabled;
    public static bool VerifyIncrementalSearch => Host.VerifyIncrementalSearch;
    public static bool ForceShortSearchOnly => Host.ForceShortSearchOnly;
    public static bool MeasureSearchPhases => Host.MeasureSearchPhases;
    public static int? ShortSearchBudgetOverrideMilliseconds => Host.ShortSearchBudgetOverrideMilliseconds;
    public static int? DeepSearchBudgetOverrideMilliseconds => Host.DeepSearchBudgetOverrideMilliseconds;
    public static int? SearchMaxDegreeOfParallelismOverride => Host.SearchMaxDegreeOfParallelismOverride;

    private readonly NGame _host;
    private readonly UnattendedTestRequest _request;
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

    private UnattendedTestRunner(NGame host, UnattendedTestRequest request, ProtocolHost protocolHost)
    {
        _host = host;
        _request = request;
        _protocolHost = protocolHost;
        _writer = new Writer(
            request,
            _stopwatch,
            _completedChecks,
            _startedAtUtc,
            CaptureStageTimings);
        _scenarioBuilder = new ScenarioBuilder(this);
        _assertions = new Assertions(this);
        _executor = new Executor(this);
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
        try
        {
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
            // SolverResult test observations are required through the post-combat assertions,
            // but must not survive into ReturnToMainMenu and the protocol's reuse Gen2.
            SolverController.ReleaseUnattendedResultReferencesForTesting();

            SetStage("cleanup");
            await _host.ReturnToMainMenu();
            EnsureWithinDeadline();
            if (_request.ExportBugReportAfterCombat)
            {
                SetStage("export_bug_report_after_combat");
                string directory = ProjectSettings.GlobalizePath("user://combat-solver-test-bug-reports");
                string archivePath = await CombatBugReportExporter.ExportCurrentAsync(directory);
                using ZipArchive archive = ZipFile.OpenRead(archivePath);
                AssertBugReportArchive(archive, "recent", _request.ExpectedBugReportControlMode);
                using Stream combatStateStream = archive.GetEntry("combat-solver/combat-state.json")!.Open();
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
            return RunCompletion.Failed;
        }
        finally
        {
            _executor.RestoreSettings();
            RestoreHeadlessFastModeOverride();
        }
    }

    private static void AssertBugReportArchive(
        ZipArchive archive,
        string forensicSlot,
        string? expectedControlMode)
    {
        string[] requiredEntries =
        [
            "combat-solver/combat-state.json",
            "combat-solver/current-route.txt",
            "combat-solver/replan-audit.txt",
            "combat-solver/settings.json",
            "combat-solver/export-context.json",
            "combat-solver/environment.json",
            "combat-solver/forensics/manifest.json",
            "combat-solver/checkpoint.json",
            $"combat-solver/forensics/{forensicSlot}/session.json",
            $"combat-solver/forensics/{forensicSlot}/pre-combat/in-memory-current_run.save",
            $"combat-solver/forensics/{forensicSlot}/last-route.txt",
            $"combat-solver/forensics/{forensicSlot}/replan-audit.txt",
            "combat-solver/README.txt",
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
                entry.FullName.StartsWith($"combat-solver/forensics/{otherForensicSlot}/", StringComparison.Ordinal)))
            throw new InvalidDataException("问题包同时包含当前战斗和此前战斗。");
        if (archive.GetEntry("screenshot.png") != null
            || archive.Entries.Any(entry => entry.FullName.StartsWith("saves/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("精简问题包仍包含截图或整批磁盘存档。");
        }
        if (archive.Entries.Count(entry =>
                entry.FullName.StartsWith($"combat-solver/forensics/{forensicSlot}/checkpoints/", StringComparison.Ordinal)) > 6)
        {
            throw new InvalidDataException("精简问题包归档了超过 6 个战斗检查点。");
        }
        ZipArchiveEntry[] generalLogs = archive.Entries
            .Where(entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal))
            .ToArray();
        if (generalLogs.Length > 1 || generalLogs.Any(entry => entry.Length > 2L * 1024 * 1024))
            throw new InvalidDataException("精简问题包的常规日志数量或大小超过上限。");

        using Stream settingsStream = archive.GetEntry("combat-solver/settings.json")!.Open();
        using JsonDocument settingsDocument = JsonDocument.Parse(settingsStream);
        if (settingsDocument.RootElement.TryGetProperty("reporterContactQq", out _))
            throw new InvalidDataException("问题包设置仍包含反馈联系QQ。");

        using Stream environmentStream = archive.GetEntry("combat-solver/environment.json")!.Open();
        using JsonDocument environmentDocument = JsonDocument.Parse(environmentStream);
        JsonElement environment = environmentDocument.RootElement;
        if (Path.IsPathRooted(environment.GetProperty("gameExecutable").GetString())
            || Path.IsPathRooted(environment.GetProperty("userDataDirectory").GetString())
            || environment.GetProperty("loadedAssemblies").EnumerateArray().Any(assembly =>
                assembly.TryGetProperty("location", out _)))
        {
            throw new InvalidDataException("问题包环境信息仍包含本机绝对路径。");
        }

        using Stream checkpointIndexStream = archive.GetEntry("combat-solver/checkpoint.json")!.Open();
        using JsonDocument checkpointIndexDocument = JsonDocument.Parse(checkpointIndexStream);
        JsonElement checkpointIndex = checkpointIndexDocument.RootElement;
        if (checkpointIndex.GetProperty("schemaVersion").GetInt32() != 1
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
        string checkpointPath = RequiredRelativeArchivePath(checkpointIndex, "metadataPath");
        string replayStatePath = RequiredRelativeArchivePath(checkpointIndex, "replayStatePath");
        string nativeStatePath = RequiredRelativeArchivePath(checkpointIndex, "nativeStatePath");
        string runStatePath = RequiredRelativeArchivePath(checkpointIndex, "runStatePath");
        ZipArchiveEntry checkpoint = archive.GetEntry(checkpointPath)
            ?? throw new InvalidDataException("问题包检查点索引引用的 metadata 不存在。");
        AssertUtf8JsonArchiveEntry(checkpoint, $"{forensicSlot} 检查点 metadata");
        using Stream checkpointStream = checkpoint.Open();
        using JsonDocument checkpointDocument = JsonDocument.Parse(checkpointStream);
        JsonElement root = checkpointDocument.RootElement;
        if (!root.TryGetProperty("settings", out _))
            throw new InvalidDataException("问题包检查点没有当时生效的求解设置。");
        if (!root.TryGetProperty("controlMode", out _)
            || !root.TryGetProperty("lastSolverDeployedTurn", out _))
        {
            throw new InvalidDataException("问题包检查点没有记录求解器/手操接管状态。");
        }
        if (!root.TryGetProperty("runRng", out JsonElement runRng)
            || !runRng.TryGetProperty("rngs", out JsonElement rngs)
            || rngs.EnumerateObject().Count() < 9)
        {
            throw new InvalidDataException("问题包检查点没有完整 Run RNG 流。");
        }
        if (!root.TryGetProperty("players", out JsonElement players)
            || players.GetArrayLength() == 0
            || !players[0].TryGetProperty("rng", out _)
            || !players[0].TryGetProperty("odds", out _))
        {
            throw new InvalidDataException("问题包检查点没有玩家 RNG/odds。");
        }

        ZipArchiveEntry replayState = archive.GetEntry(replayStatePath)
            ?? throw new InvalidDataException("问题包检查点索引引用的 replay-state 不存在。");
        ZipArchiveEntry nativeState = archive.GetEntry(nativeStatePath)
            ?? throw new InvalidDataException("问题包检查点索引引用的 native-state 不存在。");
        ZipArchiveEntry runState = archive.GetEntry(runStatePath)
            ?? throw new InvalidDataException("问题包检查点索引引用的 run-state 不存在。");
        if (nativeState.Length == 0)
            throw new InvalidDataException("问题包中的游戏原生战斗状态包为空。");

        AssertUtf8JsonArchiveEntry(replayState, $"{forensicSlot} 检查点 replay-state");
        using Stream replayStateStream = replayState.Open();
        using JsonDocument replayStateDocument = JsonDocument.Parse(replayStateStream);
        JsonElement replayRoot = replayStateDocument.RootElement;
        if (!replayRoot.TryGetProperty("settings", out _)
            || !replayRoot.TryGetProperty("history", out JsonElement history)
            || history.ValueKind != JsonValueKind.Array
            || !replayRoot.TryGetProperty("creatures", out JsonElement creatures)
            || creatures.GetArrayLength() == 0
            || !replayRoot.TryGetProperty("players", out JsonElement replayPlayers)
            || replayPlayers.GetArrayLength() == 0
            || !replayPlayers[0].TryGetProperty("piles", out JsonElement piles)
            || piles.GetArrayLength() < 5)
        {
            throw new InvalidDataException("问题包的结构化中途战斗状态不完整。");
        }
        if (!replayRoot.TryGetProperty("runRng", out JsonElement replayRunRng)
            || !replayRunRng.TryGetProperty("rngs", out JsonElement replayRngs)
            || replayRngs.EnumerateObject().Count() < 9)
        {
            throw new InvalidDataException("问题包的结构化中途战斗状态没有完整 Run RNG 流。");
        }

        using Stream runStateStream = runState.Open();
        using JsonDocument runStateDocument = JsonDocument.Parse(runStateStream);
        if (!runStateDocument.RootElement.TryGetProperty("rng", out JsonElement savedRunRng)
            || !savedRunRng.TryGetProperty("rngs", out JsonElement savedRngs)
            || savedRngs.EnumerateObject().Count() < 9)
        {
            throw new InvalidDataException("问题包的即时跑局存档没有完整 Run RNG 流。");
        }

        string rootPrefix = $"combat-solver/forensics/{forensicSlot}";
        using Stream sessionStream = archive.GetEntry($"{rootPrefix}/session.json")!.Open();
        using JsonDocument sessionDocument = JsonDocument.Parse(sessionStream);
        JsonElement sessionRoot = sessionDocument.RootElement;
        if (!sessionRoot.TryGetProperty("controlMode", out JsonElement sessionControlMode)
            || !sessionRoot.TryGetProperty("lastSolverDeployedTurn", out _))
        {
            throw new InvalidDataException("问题包战斗会话没有记录求解器/手操接管状态。");
        }

        using Stream exportContextStream = archive.GetEntry("combat-solver/export-context.json")!.Open();
        using JsonDocument exportContextDocument = JsonDocument.Parse(exportContextStream);
        string controlModeProperty = forensicSlot == "current"
            ? "currentControlMode"
            : "recentControlMode";
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
            if (prefixLength == prefix.Length
                && prefix.SequenceEqual(Encoding.UTF8.Preamble))
            {
                throw new InvalidDataException($"问题包的 {description} 不应包含 UTF-8 BOM。");
            }
        }

        UTF8Encoding strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        using Stream input = entry.Open();
        using StreamReader reader = new(
            input,
            strictUtf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192,
            leaveOpen: false);
        Span<char> buffer = stackalloc char[4096];
        try
        {
            while (reader.Read(buffer) > 0)
            {
            }
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
            && (SolverController.WasCardDeployedForTesting(expectedPlayedCardId)
                || CombatManager.Instance.History.CardPlaysFinished.Any(entry =>
                    entry.CardPlay.Card.Id.Entry.Equals(expectedPlayedCardId, StringComparison.OrdinalIgnoreCase)));

    private bool WasExpectedPotionUsed()
        => _request.ExpectedUsedPotionId is { } expectedPotionId
            && (SolverController.WasPotionDeployedForTesting(expectedPotionId)
                || CombatManager.Instance.History.Entries.OfType<PotionUsedEntry>().Any(entry =>
                    entry.Potion.Id.Entry.Equals(expectedPotionId, StringComparison.OrdinalIgnoreCase)
                    || entry.Potion.GetType().Name.Equals(expectedPotionId, StringComparison.OrdinalIgnoreCase)));

    private bool HasExpectedPlayerPower(Player player)
        => _request.ExpectedObservedPlayerPowerId is { } expectedPowerId
            && player.Creature.Powers.Any(power =>
                power.Id.Entry.Equals(expectedPowerId, StringComparison.OrdinalIgnoreCase)
                || power.GetType().Name.Equals(expectedPowerId, StringComparison.OrdinalIgnoreCase));

    private async Task<CombatState> WaitForPlayableCombatAsync()
    {
        bool turnSetupPlanAccepted = false;
        bool initialSetupPauseVerified = false;
        bool manualRecalculationSubmitted = false;
        bool manualRefreshRequested = false;
        bool manualRefreshCompleted = false;
        int manualRefreshCompletedAtRequest = 0;
        bool initialSearchControlsSubmitted = false;
        while (true)
        {
            EnsureWithinDeadline();
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            Player? player = state == null ? null : LocalContext.GetMe(state);
            if (_request.VerifyTurnSetupControlsDuringInitialSearch
                && !initialSearchControlsSubmitted
                && state != null
                && PlayerTurnSetupCoordinator.IsInitialChoiceSearchPendingForTesting(state))
            {
                if (!SolverController.CanAdoptCurrentRoute)
                {
                    await NextFrameAsync();
                    continue;
                }
                int turn = player?.PlayerCombatState?.TurnNumber
                    ?? throw new InvalidOperationException("开局搜索控件测试找不到玩家回合。");
                SolverController.AdoptCurrentRoute();
                if (!SolverController.IsAdoptingCurrentRoute)
                    throw new InvalidOperationException("开局搜索的当前完整路线没有进入采纳状态。");
                SolverController.RequestDeploy(_host, state);
                if (!PlayerTurnSetupCoordinator.TakeoverRequestedForTesting)
                    throw new InvalidOperationException("开局搜索期间的执行请求没有进入回合准备接管队列。");
                SolverController.RequestSearch(_host, state, SearchReason.Manual);
                if (!SolverController.ManualSearchAfterTurnSetupRequested)
                    throw new InvalidOperationException("开局搜索期间执行后的重算请求没有等待实际选牌完成。");
                initialSearchControlsSubmitted = true;
                turnSetupPlanAccepted = true;
                Entry.Logger.Info(
                    $"[CombatSolver/Test] TURN_SETUP_INITIAL_SEARCH_CONTROLS_SUBMITTED " +
                    $"turn={turn} adopted_interim=true");
            }
            if (!turnSetupPlanAccepted
                && state != null
                && PlayerTurnSetupCoordinator.HasPendingPlannedChoice(state))
            {
                if (_request.VerifyInitialSetupWaitsForUserStart && !initialSetupPauseVerified)
                {
                    int turn = player?.PlayerCombatState?.TurnNumber
                        ?? throw new InvalidOperationException("首回合选牌暂停断言找不到玩家回合。");
                    NativeChoiceTrace[] traces = NativeChoiceRuntime.TraceSnapshotForTesting
                        .Where(trace => trace.Owner == $"turn_setup:{turn}")
                        .ToArray();
                    bool pageReady = traces.Any(trace => trace.Stage == "Visible")
                        && traces.Any(trace => trace.Stage == "SearchStarted")
                        && traces.Any(trace => trace.Stage == "PlanReady");
                    if (!pageReady)
                        throw new InvalidOperationException("首回合选牌计划就绪时缺少页面、搜索或计划事件。");
                    if (traces.Any(trace => trace.Stage == "Selected"))
                        throw new InvalidOperationException("首回合计划展示后、用户启动前，求解器已经替玩家完成选牌。");
                    if (player?.PlayerCombatState?.Phase != PlayerTurnPhase.Start
                        || NPlayerHand.Instance?.IsInCardSelection != true)
                    {
                        throw new InvalidOperationException("首回合计划展示后没有停在原生选牌页面。");
                    }
                    if (CombatManager.Instance.History.CardPlaysFinished.Any())
                        throw new InvalidOperationException("首回合选牌等待用户启动前已经打出了卡牌。");
                    initialSetupPauseVerified = true;
                    _completedChecks.Add(
                        $"InitialSetupWaitsForUserStart:Turn={turn}:Selected=0:CardsPlayed=0");
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] TURN_SETUP_AWAITING_USER_START turn={turn} " +
                        "native_choice_pending=true selected=false cards_played=0");
                }
                if (_request.VerifyTurnSetupManualRefresh)
                {
                    int turn = player?.PlayerCombatState?.TurnNumber
                        ?? throw new InvalidOperationException("回合开始选项刷新测试找不到玩家回合。");
                    if (!manualRefreshRequested)
                    {
                        manualRefreshCompletedAtRequest =
                            PlayerTurnSetupCoordinator.ManualRecalculationCompletedCountForTesting;
                        SolverController.RequestSearch(_host, state, SearchReason.Manual);
                        if (SolverController.ManualSearchAfterTurnSetupRequested)
                            throw new InvalidOperationException("原生选牌页仍打开时，手动刷新被错误延迟到选牌结束后。");
                        manualRefreshRequested = true;
                        Entry.Logger.Info(
                            $"[CombatSolver/Test] TURN_SETUP_MANUAL_REFRESH_SUBMITTED turn={turn}");
                        await NextFrameAsync();
                        continue;
                    }
                    int completedRefreshes =
                        PlayerTurnSetupCoordinator.ManualRecalculationCompletedCountForTesting;
                    if (completedRefreshes <= manualRefreshCompletedAtRequest
                        || PlayerTurnSetupCoordinator.IsSearching)
                    {
                        await NextFrameAsync();
                        continue;
                    }
                    if (!manualRefreshCompleted)
                    {
                        manualRefreshCompleted = true;
                        _completedChecks.Add(
                            $"TurnSetupManualRefresh:Turn={turn}:Refreshes={completedRefreshes - manualRefreshCompletedAtRequest}");
                    }
                    turnSetupPlanAccepted = PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                        _host,
                        state,
                        deployAfterSetup: false);
                }
                else if (_request.VerifyTurnSetupManualRecalculate && !manualRecalculationSubmitted)
                {
                    int turn = player?.PlayerCombatState?.TurnNumber
                        ?? throw new InvalidOperationException("筹码重算测试找不到玩家回合。");
                    await PlayerTurnSetupCoordinator.SubmitEmptyChoiceForTesting(_host, state);
                    SolverController.RequestSearch(_host, state, SearchReason.Manual);
                    bool queued = SolverController.ManualSearchAfterTurnSetupRequested;
                    manualRecalculationSubmitted = true;
                    turnSetupPlanAccepted = true;
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] TURN_SETUP_MANUAL_RECALCULATE_SUBMITTED " +
                        $"turn={turn} queued={queued.ToString().ToLowerInvariant()}");
                }
                else
                {
                    turnSetupPlanAccepted = PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                        _host,
                        state,
                        deployAfterSetup: false);
                }
            }
            if (state != null
                && CombatManager.Instance.IsInProgress
                && state.CurrentSide == CombatSide.Player
                && player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
            {
                if (_request.VerifyTurnSetupManualRecalculate)
                {
                    if (!manualRecalculationSubmitted)
                    {
                        await NextFrameAsync();
                        continue;
                    }
                    if (SolverController.LastSearchFailureForTesting is { } failure)
                    {
                        throw new InvalidOperationException(
                            $"筹码选择完成后的手动重算失败：{failure.GetType().Name}: {failure.Message}",
                            failure);
                    }
                    SolverResult? recalculated = SolverController.LastCompletedResultForTesting;
                    if (SolverController.IsSearching
                        || recalculated == null
                        || recalculated.StartTurnNumber != player.PlayerCombatState.TurnNumber)
                    {
                        await NextFrameAsync();
                        continue;
                    }
                    if (SolverController.ManualSearchAfterTurnSetupRequested)
                        throw new InvalidOperationException("进入出牌阶段后，筹码手动重算仍停留在等待队列。");
                    _completedChecks.Add(
                        $"TurnSetupManualRecalculate:Turn={recalculated.StartTurnNumber}:Actions={recalculated.BestNode.Actions.Count}");
                }
                if (_request.VerifyTurnSetupManualRefresh && !manualRefreshCompleted)
                {
                    await NextFrameAsync();
                    continue;
                }
                if (_request.VerifyTurnSetupControlsDuringInitialSearch)
                {
                    if (!initialSearchControlsSubmitted)
                    {
                        await NextFrameAsync();
                        continue;
                    }
                    if (SolverController.LastSearchFailureForTesting is { } failure)
                    {
                        throw new InvalidOperationException(
                            $"开局搜索期间的执行/重算请求失败：{failure.GetType().Name}: {failure.Message}",
                            failure);
                    }
                    SolverResult? recalculated = SolverController.LastCompletedResultForTesting;
                    if (SolverController.IsSearching
                        || recalculated == null
                        || recalculated.StartTurnNumber != player.PlayerCombatState.TurnNumber)
                    {
                        await NextFrameAsync();
                        continue;
                    }
                    if (SolverController.ManualSearchAfterTurnSetupRequested)
                        throw new InvalidOperationException("进入出牌阶段后，开局搜索期间请求的重算仍未消费。");
                    _completedChecks.Add(
                        $"TurnSetupInitialSearchControls:Turn={recalculated.StartTurnNumber}:Actions={recalculated.BestNode.Actions.Count}");
                }
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
    {
        if (_request.MonsterMoveChecks.Length > 0)
            return _request.MonsterMoveChecks;
        return _request.MonsterMoveCheck == null ? [] : [_request.MonsterMoveCheck];
    }

    private IReadOnlyList<UnattendedPotionCheck> GetPotionChecks()
    {
        if (_request.PotionChecks.Length > 0)
            return _request.PotionChecks;
        return _request.PotionCheck == null ? [] : [_request.PotionCheck];
    }

    private async Task RunMonsterMoveSearchBoundaryAsync(
        CombatState combatState,
        UnattendedMonsterMoveCheck check,
        SearchBoundaryReason expectedBoundary)
    {
        Creature enemy = ResolveMonsterMoveTarget(combatState, check);
        MonsterModel monster = ConfigureMonsterMove(enemy, check);
        Player boundaryPlayer = LocalContext.GetMe(combatState)
            ?? throw new InvalidOperationException("搜索边界测试找不到本地玩家。");
        await PrepareSearchBoundaryStateAsync(combatState, boundaryPlayer, enemy, check);
        SolverDisplayNames displayNames = SolverDisplayNames.Capture(combatState);
        BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(combatState);
        SearchPolicySnapshot searchPolicy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(),
            combatState,
            includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combatState));
        CombatRootSnapshot rootSnapshot = CombatRootSnapshot.Capture(combatState);

        bool solverRanOnWorkerThread = false;
        SolverResult result = await Task.Run(() =>
        {
            solverRanOnWorkerThread = !NGame.IsMainThread();
            return new CombatBeamSolver(
                rootSnapshot,
                displayNames,
                battleDamage,
                searchPolicy).Solve();
        });
        if (!solverRanOnWorkerThread)
            throw new InvalidOperationException("动态边界测试的正式搜索没有在工作线程运行。");
        if (result.BoundaryReason != expectedBoundary)
        {
            throw new InvalidOperationException(
                $"怪物 {monster.Id.Entry} 行动 {monster.NextMove.Id} 的搜索边界为 {result.BoundaryReason}，预期 {expectedBoundary}。");
        }
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] SEARCH_BOUNDARY monster={monster.Id.Entry} move={monster.NextMove.Id} boundary={result.BoundaryReason} searched_turns={result.SearchedTurns} expanded={result.ExpandedNodes} worker_thread={solverRanOnWorkerThread}");
    }

    private static Creature ResolveMonsterMoveTarget(
        CombatState combatState,
        UnattendedMonsterMoveCheck check)
        => string.IsNullOrWhiteSpace(check.MonsterId)
            ? ResolveEnemyByIndex(combatState, check.EnemyIndex)
            : combatState.Enemies
                .Where(candidate => candidate.IsAlive
                    && candidate.Monster != null
                    && ModelMatches(candidate.Monster, check.MonsterId))
                .Skip(check.MonsterOccurrence)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"找不到怪物 {check.MonsterId} 的第 {check.MonsterOccurrence + 1} 个存活实例。");

    private static MonsterModel ConfigureMonsterMove(
        Creature enemy,
        UnattendedMonsterMoveCheck check)
    {
        MonsterModel monster = enemy.Monster
            ?? throw new InvalidOperationException("怪物行动测试目标没有 MonsterModel。");
        MonsterMoveStateMachine machine = monster.MoveStateMachine
            ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
        if (check.UseCurrentMove)
        {
            if (!string.IsNullOrWhiteSpace(check.MoveId)
                && !monster.NextMove.Id.Equals(check.MoveId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"怪物 {monster.Id.Entry} 当前行动为 {monster.NextMove.Id}，预期 {check.MoveId}。");
            }
            return monster;
        }

        if (!machine.States.TryGetValue(check.MoveId, out MonsterState? state) || state is not MoveState move)
            throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动 {check.MoveId}。");
        monster.SetMoveImmediate(move, true);
        return monster;
    }

    private async Task RunMonsterMoveDifferentialAsync(
        CombatState combatState,
        Player player,
        UnattendedMonsterMoveCheck check)
    {
        Creature enemy = ResolveMonsterMoveTarget(combatState, check);
        MonsterModel monster = ConfigureMonsterMove(enemy, check);
        MoveState move = monster.NextMove;
        if (check.MonsterStateLogBefore.Length > 0)
        {
            MonsterMoveStateMachine machine = monster.MoveStateMachine
                ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
            machine.StateLog.Clear();
            foreach (string stateId in check.MonsterStateLogBefore)
            {
                if (!machine.States.TryGetValue(stateId, out MonsterState? loggedState))
                    throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有历史状态 {stateId}。");
                machine.StateLog.Add(loggedState);
            }
        }

        if (check.PlayerHpBefore is { } playerHpBefore)
        {
            if (playerHpBefore < 1 || playerHpBefore > player.Creature.MaxHp)
            {
                throw new InvalidOperationException(
                    $"玩家测试生命必须在 1..{player.Creature.MaxHp}，实际为 {playerHpBefore}。");
            }
            await CreatureCmd.SetCurrentHp(player.Creature, playerHpBefore);
        }
        if (check.PlayerBlockBefore is { } playerBlockBefore)
            await SetBlockAsync(player.Creature, playerBlockBefore);
        if (check.PlayerEnergyBefore is { } playerEnergyBefore)
            SetEnergy(player, playerEnergyBefore);
        if (check.PlayerStarsBefore is { } playerStarsBefore)
            SetStars(player, playerStarsBefore);
        if (check.PlayerGoldBefore is { } playerGoldBefore)
        {
            if (playerGoldBefore < 0)
                throw new InvalidOperationException($"测试金币不能为负数：{playerGoldBefore}。");
            player.Gold = playerGoldBefore;
        }
        if (check.EnemyHpBefore is { } enemyHpBefore)
            await CreatureCmd.SetCurrentHp(enemy, Math.Clamp(enemyHpBefore, 1, enemy.MaxHp));
        if (check.EnemyBlockBefore is { } enemyBlockBefore)
            await SetBlockAsync(enemy, enemyBlockBefore);
        if (check.ClearAllRelicsBeforeMove)
        {
            foreach (RelicModel relic in player.Relics.ToArray())
                await RelicCmd.Remove(relic);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (check.ClearAllPowersBeforeMove)
        {
            foreach (PowerModel power in combatState.Creatures.SelectMany(creature => creature.Powers).ToArray())
                await PowerCmd.Remove(power);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (check.OstyHpBefore is { } ostyHpBefore)
        {
            if (ostyHpBefore < 1)
                throw new InvalidOperationException($"奥斯蒂测试生命必须大于 0，实际为 {ostyHpBefore}。");
            await OstyCmd.Summon(
                new BlockingPlayerChoiceContext(),
                player,
                ostyHpBefore,
                null);
            Creature osty = player.Osty
                ?? throw new InvalidOperationException("召唤后仍找不到奥斯蒂。");
            await CreatureCmd.SetMaxHp(osty, ostyHpBefore);
            await CreatureCmd.SetCurrentHp(osty, ostyHpBefore);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (check.RoundNumberBefore is { } roundNumberBefore)
            combatState.RoundNumber = roundNumberBefore;
        if (check.PlayerTurnNumberBefore is { } playerTurnNumberBefore)
        {
            PlayerCombatState playerState = player.PlayerCombatState
                ?? throw new InvalidOperationException("测试玩家没有战斗状态。");
            if (playerTurnNumberBefore < playerState.TurnNumber)
            {
                throw new InvalidOperationException(
                    $"玩家测试回合号不能从 {playerState.TurnNumber} 回退到 {playerTurnNumberBefore}。");
            }
            while (playerState.TurnNumber < playerTurnNumberBefore)
                playerState.IncrementTurnNumber();
        }
        if (check.ClearPlayerOrbsBeforeMove)
        {
            OrbQueue queue = player.PlayerCombatState!.OrbQueue;
            foreach (OrbModel orb in queue.Orbs.ToArray())
            {
                _ = queue.Remove(orb);
                orb.RemoveInternal();
            }
        }
        if (check.ClearPlayerPilesBeforeMove)
        {
            await ClearPlayerPilesAsync(player);
        }
        else if (check.ClearPlayerHandBeforeMove)
        {
            await CardCmd.Discard(
                new BlockingPlayerChoiceContext(),
                player.PlayerCombatState!.Hand.Cards.ToArray());
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        foreach (UnattendedRelicInjection relicBeforeMove in check.RelicsBeforeMove)
        {
            await InjectRelicAsync(player, relicBeforeMove);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        foreach (UnattendedOrbInjection orbBeforeMove in check.OrbsBeforeMove)
        {
            await InjectOrbAsync(player, orbBeforeMove);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (check.PowerBeforeMove is { } powerBeforeMove)
        {
            await InjectPowerAsync(combatState, player, powerBeforeMove, enemy);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        foreach (UnattendedPowerInjection injectedPowerBeforeMove in check.PowersBeforeMove)
        {
            await InjectPowerAsync(combatState, player, injectedPowerBeforeMove, enemy);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (check.CardBeforeMove is { } cardBeforeMove)
        {
            await InjectCardAsync(combatState, player, cardBeforeMove);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        foreach (UnattendedCardInjection injectedBeforeMove in check.CardsBeforeMove)
        {
            await InjectCardAsync(combatState, player, injectedBeforeMove);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        SimulatedCombatState simulatedCombat;
        CombatPredictionSimulator simulator;
        using (BranchMonsterStaticSnapshot.AllowUnreachableConditionalsForTesting())
        {
            simulatedCombat = new SimulatedCombatState(combatState);
            simulator = new CombatPredictionSimulator(simulatedCombat);
        }
        int simulatedRoundHistoryEntryStart = simulator.History.Entries.Count;
        AssertDerivedPowerHooks(combatState, simulatedCombat, player, enemy, check);
        MoveStateSnapshot before = CaptureActual(combatState, player, enemy);
        foreach (UnattendedCardPlayCheck playCheck in check.CardPlayChecksBeforeMove)
        {
            PredictedCard card = FindSimulatedHandCard(simulator, player, playCheck.CardId, playCheck.Occurrence);
            bool playable = simulatedCombat.CanPlayCard(simulator, card);
            if (playable != playCheck.ExpectedPlayable)
            {
                throw new InvalidOperationException(
                    $"模拟中怪物行动前卡牌 {playCheck.CardId}#{playCheck.Occurrence} 可打出={playable}，预期 {playCheck.ExpectedPlayable}。");
            }
            if (playable)
            {
                PlaySimulatedCard(
                    simulator,
                    simulatedCombat,
                    card,
                    ResolvePlayTarget(playCheck.Target, enemy),
                    combatState.Enemies,
                    playCheck.UseChoice ? playCheck.ChoiceCardIds : null,
                    playCheck.ExpectedExcludedChoiceCardIds);
                AssertSimulatedForkableAfterPlay(simulatedCombat, playCheck);
            }
        }
        if (check.TriggerPlayerSideTurnEndBeforeMove)
            CorePowerSupport.TriggerPlayerSideTurnEndEffects(simulator, simulatedCombat, [player.Creature]);
        if (check.TriggerEnemySideTurnEndBeforeMove)
        {
            CorePowerSupport.TriggerEnemySideTurnEndEffects(
                simulator,
                simulatedCombat,
                combatState.Enemies);
        }
        ForecastMove simulatedMove = simulatedCombat.CurrentMonsterMoves()
            .Single(candidate => ReferenceEquals(candidate.Owner, enemy));
        _ = MonsterMoveSemantics.ApplyForecastMove(
            simulator,
            simulatedCombat,
            simulatedMove,
            player.Creature,
            new HashSet<uint>());
        foreach (UnattendedPowerInjection injectedPowerAfterMove in check.PowersAfterMove)
            ApplySimulatedPowerInjection(simulator, simulatedCombat, combatState, player, injectedPowerAfterMove, enemy);
        if (check.CardAfterMove is { } simulatedCardAfterMove)
            InjectSimulatedCard(simulator, simulatedCombat, player, simulatedCardAfterMove);
        foreach (UnattendedCardInjection injectedAfterMove in check.CardsAfterMove)
            InjectSimulatedCard(simulator, simulatedCombat, player, injectedAfterMove);
        foreach (UnattendedCardTransformCheck transform in check.CardTransformsAfterMove)
            TransformSimulatedCard(simulator, player, transform);
        if (check.PlayCardAfterMove is { } simulatedPlay)
        {
            PredictedCard card = InjectSimulatedCard(simulator, simulatedCombat, player, simulatedPlay);
            if (!simulatedCombat.CanPlayCard(simulator, card))
                throw new InvalidOperationException($"模拟中无法在怪物行动后打出测试卡牌 {simulatedPlay.CardId}。");
            PlaySimulatedCard(simulator, simulatedCombat, card, enemy, combatState.Enemies);
        }
        foreach (UnattendedCardPlayCheck playCheck in check.CardPlayChecksAfterMove)
        {
            PredictedCard card = FindSimulatedHandCard(simulator, player, playCheck.CardId, playCheck.Occurrence);
            bool playable = simulatedCombat.CanPlayCard(simulator, card);
            if (playable != playCheck.ExpectedPlayable)
            {
                SimPlayerCombatState cardOwner = simulator.State.GetPlayerCombatState(card.Preview.Owner);
                throw new InvalidOperationException(
                    $"模拟中卡牌 {playCheck.CardId}#{playCheck.Occurrence} 可打出={playable}，预期 {playCheck.ExpectedPlayable}；" +
                    $"energy={cardOwner.Energy} cost={card.GetEnergyCostWithModifiers(simulator, cardOwner)} " +
                    $"stars={cardOwner.Stars} star_cost={card.GetStarCostWithModifiers(simulator, cardOwner)} " +
                    $"is_clone={card.Preview.IsClone} played={simulatedCombat.GetCardsPlayedThisTurn(card.Preview.Owner.Creature)} " +
                    $"attacks={simulatedCombat.GetAttacksPlayedThisTurn(card.Preview.Owner.Creature)} " +
                    $"skills={simulatedCombat.GetSkillCardsPlayedThisTurn(card.Preview.Owner.Creature)}。");
            }
            if (playable)
            {
                PlaySimulatedCard(
                    simulator,
                    simulatedCombat,
                    card,
                    ResolvePlayTarget(playCheck.Target, enemy),
                    combatState.Enemies,
                    playCheck.UseChoice ? playCheck.ChoiceCardIds : null,
                    playCheck.ExpectedExcludedChoiceCardIds);
                AssertSimulatedCardPileAfterPlay(simulator, player, playCheck);
                AssertSimulatedForkableAfterPlay(simulatedCombat, playCheck);
            }
        }
        int simulatedPlayerBlockAfterMoveActions =
            simulator.State.GetCreature(player.Creature).Block;
        if (check.TriggerPlayerSideTurnEndAfterMove)
            CorePowerSupport.TriggerPlayerSideTurnEndEffects(simulator, simulatedCombat, [player.Creature]);
        foreach (UnattendedCardPlayCheck playCheck in check.CardPlayChecksAfterPlayerSideTurnEnd)
        {
            PredictedCard card = FindSimulatedHandCard(simulator, player, playCheck.CardId, playCheck.Occurrence);
            bool playable = simulatedCombat.CanPlayCard(simulator, card);
            if (playable != playCheck.ExpectedPlayable)
            {
                throw new InvalidOperationException(
                    $"模拟中玩家回合结束钩子后卡牌 {playCheck.CardId}#{playCheck.Occurrence} 可打出={playable}，预期 {playCheck.ExpectedPlayable}。");
            }
            if (playable)
            {
                PlaySimulatedCard(
                    simulator,
                    simulatedCombat,
                    card,
                    ResolvePlayTarget(playCheck.Target, enemy),
                    combatState.Enemies,
                    playCheck.UseChoice ? playCheck.ChoiceCardIds : null);
                AssertSimulatedForkableAfterPlay(simulatedCombat, playCheck);
            }
        }
        int enemySideTurnEndCount = Math.Max(
            check.TriggerEnemySideTurnEndAfterMove ? 1 : 0,
            check.EnemySideTurnEndTriggerCount);
        for (int trigger = 0; trigger < enemySideTurnEndCount; trigger++)
        {
            simulatedCombat.CurrentSide = CombatSide.Enemy;
            CorePowerSupport.TriggerEnemySideTurnEndEffects(
                simulator,
                simulatedCombat,
                combatState.Enemies);
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                simulatedCombat,
                combatState.Enemies,
                new HashSet<uint>());
        }
        if (check.TriggerPlayerSideTurnStartAfterMove)
        {
            TurnStartRelicSupport.TriggerBeforeSideTurnStart(simulator, simulatedCombat, [player.Creature]);
            TurnStartPowerSupport.TriggerBeforeSideTurnStart(
                simulator,
                simulatedCombat,
                [player.Creature]);
            SimCreatureState simulatedPlayer = simulator.State.GetCreature(player.Creature);
            if (simulatedPlayer.Block > 0)
            {
                if (simulatedCombat.ShouldClearBlock(player.Creature, out AbstractModel? preventer))
                    simulatedPlayer.DamageBlock(simulatedPlayer.Block, ValueProp.Move);
                else
                    PersistentRelicSupport.TriggerAfterPreventingBlockClear(
                        simulator,
                        preventer,
                        player.Creature);
            }
            CorePowerSupport.TriggerAfterBlockCleared(simulator, simulatedCombat, player.Creature);
            CorePowerSupport.TriggerPoison(simulator, simulatedCombat, [player.Creature]);
            TurnStartChoiceCursor choices = new(null);
            if (simulatedCombat.TriggerAfterPlayerTurnStart(simulator, player.Creature, choices))
                throw new InvalidOperationException("模拟玩家回合开始遇到未计划选择。");
            simulatedCombat.TriggerSideTurnStart(
                simulator,
                CombatSide.Player,
                [player.Creature],
                decrementPlating: simulatedCombat.GetPlayerTurnNumber(player) != 1);
            EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(simulator, player);
        }
        if (check.TriggerEnemySideTurnStartAfterMove)
        {
            simulatedCombat.SnapshotPowerAmountsAtTurnStart([enemy]);
            TurnStartRelicSupport.TriggerBeforeSideTurnStart(simulator, simulatedCombat, [enemy]);
            TurnStartPowerSupport.TriggerBeforeSideTurnStart(
                simulator,
                simulatedCombat,
                [enemy]);
            SimCreatureState simulatedEnemy = simulator.State.GetCreature(enemy);
            if (simulatedEnemy.Block > 0)
            {
                if (simulatedCombat.ShouldClearBlock(enemy, out AbstractModel? preventer))
                    simulatedEnemy.DamageBlock(simulatedEnemy.Block, ValueProp.Move);
                else
                    PersistentRelicSupport.TriggerAfterPreventingBlockClear(simulator, preventer, enemy);
            }
            CorePowerSupport.TriggerAfterBlockCleared(simulator, simulatedCombat, enemy);
            simulatedCombat.TriggerSideTurnStart(
                simulator,
                CombatSide.Enemy,
                [enemy],
                combatState.RoundNumber > 1);
            CorePowerSupport.TriggerPoison(simulator, simulatedCombat, [enemy]);
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                simulatedCombat,
                combatState.Enemies,
                new HashSet<uint>());
            simulatedCombat.RecordRelicRoundDamage(
                simulator,
                player,
                simulatedRoundHistoryEntryStart);
        }
        if (check.TriggerPlayerTurnEndAfterMove)
        {
            int etherealExhaustCount = simulatedCombat.CountEtherealCardsInHand(simulator, player);
            PlayerTurnEndLifecycle.RunPhaseOne(
                simulator,
                simulatedCombat,
                player,
                [player.Creature]);
            simulatedCombat.NormalizeAeonglassWithers(simulator);
            simulatedCombat.NormalizeCardAfflictions(simulator);
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                simulatedCombat,
                combatState.Enemies,
                new HashSet<uint>());
            CorePowerSupport.FlushPlayerHandAtTurnEnd(simulator, simulatedCombat, player);
            TurnStartRelicSupport.TriggerAfterSideTurnEnd(
                simulator,
                simulatedCombat,
                [player.Creature],
                etherealExhaustCount);
            CorePowerSupport.TriggerPlayerSideTurnEndEffects(
                simulator,
                simulatedCombat,
                [player.Creature],
                etherealExhaustCount);
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                simulatedCombat,
                combatState.Enemies,
                new HashSet<uint>());
        }
        if (check.TriggerPlayerSetupAfterMove)
        {
            if (!check.TriggerEnemySideTurnStartAfterMove)
            {
                simulatedCombat.RecordRelicRoundDamage(
                    simulator,
                    player,
                    simulatedRoundHistoryEntryStart);
            }
            TriggerSimulatedPlayerSetup(
                simulator,
                simulatedCombat,
                player,
                check.PlayerSetupChoiceCardIds);
        }
        if (check.TriggerAutoPrePlayAfterPlayerSetup)
        {
            TurnStartChoiceCursor plannedChoices = check.AutoPrePlayChoiceCardIds.Length == 0
                ? new TurnStartChoiceCursor(null)
                : TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                {
                    CardChoiceSpec spec = TurnStartChoiceSupport.BuildSpec(simulator, player, request);
                    return CardChoiceSupport.BuildRequestedChoice(
                        spec,
                        check.AutoPrePlayChoiceCardIds) with
                    {
                        SourceId = request.SourceId,
                        ContextId = request.ContextId,
                        Timing = request.Timing,
                    };
                });
            simulatedCombat.BeginActionChoices(plannedChoices);
            bool pendingChoice;
            try
            {
                pendingChoice = simulatedCombat.TriggerAutoPrePlayEarly(
                    simulator,
                    player,
                    player.PlayerCombatState?.TurnNumber ?? 1,
                    plannedChoices,
                    new HashSet<uint>());
            }
            finally
            {
                simulatedCombat.EndActionChoices();
            }
            if (pendingChoice)
                throw new InvalidOperationException("助能自动出牌仍产生未计划选择。");
        }
        foreach (UnattendedCardPlayCheck playCheck in check.CardPlayChecksAfterPlayerSetup)
        {
            PredictedCard card = FindSimulatedHandCard(simulator, player, playCheck.CardId, playCheck.Occurrence);
            bool playable = simulatedCombat.CanPlayCard(simulator, card);
            if (playable != playCheck.ExpectedPlayable)
                throw new InvalidOperationException(
                    $"模拟中玩家回合准备后卡牌 {playCheck.CardId}#{playCheck.Occurrence} 可打出={playable}，预期 {playCheck.ExpectedPlayable}。");
            if (playable)
            {
                PlaySimulatedCard(
                    simulator,
                    simulatedCombat,
                    card,
                    ResolvePlayTarget(playCheck.Target, enemy),
                    combatState.Enemies,
                    playCheck.UseChoice ? playCheck.ChoiceCardIds : null);
                AssertSimulatedForkableAfterPlay(simulatedCombat, playCheck);
            }
        }
        if (check.KillMonsterAfterMove)
        {
            simulator.Kill(enemy, force: true);
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                simulatedCombat,
                combatState.Enemies,
                new HashSet<uint>());
        }
        if (check.KillEnemyIndexAfterMove is { } simulatedKillIndex)
        {
            Creature killTarget = simulatedCombat.Enemies.ElementAtOrDefault(simulatedKillIndex)
                ?? throw new InvalidOperationException($"模拟怪物行动测试目标索引 {simulatedKillIndex} 越界。");
            simulator.Kill(killTarget, force: true);
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                simulatedCombat,
                simulatedCombat.KnownEnemies,
                new HashSet<uint>());
        }
        if (check.ExpectedSimulatedDynamicResolution is { } expectedDynamic
            && simulatedCombat.HasPendingChoice != expectedDynamic)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 模拟待选分支={simulatedCombat.HasPendingChoice}，预期 {expectedDynamic}。");
        }
        if (check.RollNextMoveAfterActual)
            simulatedCombat.PrepareMonsterMoveForNextRound(simulator, enemy, simulatedMove.Move);
        MoveStateSnapshot predicted = CaptureSimulated(simulator, simulatedCombat, player, enemy);
        if (check.ExpectedSimulatedSkipNextMove is { } expectedSkip
            && simulatedCombat.WillSkipNextMove(enemy) != expectedSkip)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 模拟待跳过行动={simulatedCombat.WillSkipNextMove(enemy)}，预期 {expectedSkip}。");
        }
        PlayerSetupDerivedHookSnapshot simulatedPlayerSetupHooks =
            CaptureSimulatedDerivedHooksAfterPlayerSetup(simulatedCombat, player, check);

        foreach (UnattendedCardPlayCheck playCheck in check.CardPlayChecksBeforeMove)
        {
            CardModel card = FindActualHandCard(player, playCheck.CardId, playCheck.Occurrence);
            using IDisposable? selector = playCheck.UseChoice
                ? CardSelectCmd.PushSelector(new UnattendedCardSelector(
                    playCheck.ChoiceCardIds,
                    playCheck.ExpectedExcludedChoiceCardIds))
                : null;
            bool playable = card.TryManualPlay(ResolvePlayTarget(playCheck.Target, enemy));
            if (playable != playCheck.ExpectedPlayable)
            {
                throw new InvalidOperationException(
                    $"实机中怪物行动前卡牌 {playCheck.CardId}#{playCheck.Occurrence} 可打出={playable}，预期 {playCheck.ExpectedPlayable}。");
            }
            if (playable)
            {
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                AssertActualCardPileAfterPlay(player, playCheck);
            }
        }
        if (check.TriggerPlayerSideTurnEndBeforeMove)
            await TriggerActualSideTurnEndAsync(combatState, CombatSide.Player, [player.Creature]);
        if (check.TriggerEnemySideTurnEndBeforeMove)
            await TriggerActualSideTurnEndAsync(combatState, CombatSide.Enemy, combatState.Enemies);
        await monster.PerformMove();
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (check.LiveEndTurnRiskCardId != null)
            AssertLiveEndTurnRiskChoices(combatState, player, check);
        if (check.LiveEndTurnRiskKnowledgeChoiceCardId != null)
            AssertLiveEndTurnRiskKnowledgeChoice(combatState, player, enemy, check);
        foreach (UnattendedPowerInjection injectedPowerAfterMove in check.PowersAfterMove)
        {
            await InjectPowerAsync(combatState, player, injectedPowerAfterMove, enemy);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (check.CardAfterMove is { } actualCardAfterMove)
        {
            await InjectCardAsync(combatState, player, actualCardAfterMove);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        foreach (UnattendedCardInjection injectedAfterMove in check.CardsAfterMove)
        {
            await InjectCardAsync(combatState, player, injectedAfterMove);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        foreach (UnattendedCardTransformCheck transform in check.CardTransformsAfterMove)
        {
            await TransformActualCardAsync(combatState, player, transform);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (check.PlayCardAfterMove is { } actualPlay)
        {
            IReadOnlyList<CardModel> cards = await InjectCardAsync(combatState, player, actualPlay);
            if (cards.Count != 1 || !cards[0].TryManualPlay(enemy))
                throw new InvalidOperationException($"无法在怪物行动后打出测试卡牌 {actualPlay.CardId}。");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        foreach (UnattendedCardPlayCheck playCheck in check.CardPlayChecksAfterMove)
        {
            CardModel card = FindActualHandCard(player, playCheck.CardId, playCheck.Occurrence);
            using IDisposable? selector = playCheck.UseChoice
                ? CardSelectCmd.PushSelector(new UnattendedCardSelector(
                    playCheck.ChoiceCardIds,
                    playCheck.ExpectedExcludedChoiceCardIds))
                : null;
            bool playable = card.TryManualPlay(ResolvePlayTarget(playCheck.Target, enemy));
            if (playable != playCheck.ExpectedPlayable)
            {
                throw new InvalidOperationException(
                    $"实机中卡牌 {playCheck.CardId}#{playCheck.Occurrence} 可打出={playable}，预期 {playCheck.ExpectedPlayable}。");
            }
            if (playable)
            {
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                AssertActualCardPileAfterPlay(player, playCheck);
            }
        }
        if (check.ExpectedPlayerBlockAfterMoveActions is { } expectedBlockAfterMoveActions)
        {
            int actualBlockAfterMoveActions = player.Creature.Block;
            if (simulatedPlayerBlockAfterMoveActions != expectedBlockAfterMoveActions
                || actualBlockAfterMoveActions != expectedBlockAfterMoveActions)
            {
                throw new InvalidOperationException(
                    $"{monster.Id.Entry}.{move.Id} 行动后格挡实机={actualBlockAfterMoveActions}、" +
                    $"模拟={simulatedPlayerBlockAfterMoveActions}，预期 {expectedBlockAfterMoveActions}。");
            }
        }
        if (check.TriggerPlayerSideTurnEndAfterMove)
            await TriggerActualSideTurnEndAsync(combatState, CombatSide.Player, [player.Creature]);
        foreach (UnattendedCardPlayCheck playCheck in check.CardPlayChecksAfterPlayerSideTurnEnd)
        {
            CardModel card = FindActualHandCard(player, playCheck.CardId, playCheck.Occurrence);
            using IDisposable? selector = playCheck.UseChoice
                ? CardSelectCmd.PushSelector(new UnattendedCardSelector(
                    playCheck.ChoiceCardIds,
                    playCheck.ExpectedExcludedChoiceCardIds))
                : null;
            bool playable = card.TryManualPlay(ResolvePlayTarget(playCheck.Target, enemy));
            if (playable != playCheck.ExpectedPlayable)
            {
                throw new InvalidOperationException(
                    $"实机中玩家回合结束钩子后卡牌 {playCheck.CardId}#{playCheck.Occurrence} 可打出={playable}，预期 {playCheck.ExpectedPlayable}。");
            }
            if (playable)
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        for (int trigger = 0; trigger < enemySideTurnEndCount; trigger++)
            await TriggerActualSideTurnEndAsync(combatState, CombatSide.Enemy, combatState.Enemies);
        if (check.TriggerPlayerSideTurnStartAfterMove)
            await TriggerActualSideTurnStartAsync(combatState, CombatSide.Player, player.Creature);
        if (check.TriggerEnemySideTurnStartAfterMove)
            await TriggerActualSideTurnStartAsync(combatState, CombatSide.Enemy, enemy);
        if (check.KillMonsterAfterMove)
        {
            await CreatureCmd.Kill(enemy, force: true);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (check.KillEnemyIndexAfterMove is { } actualKillIndex)
        {
            await CreatureCmd.Kill(ResolveEnemyByIndex(combatState, actualKillIndex), force: true);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (check.TriggerPlayerTurnEndAfterMove)
        {
            await CombatManager.Instance.EndPlayerTurnPhaseOneInternal();
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            await CombatManager.Instance.EndPlayerTurnPhaseTwoInternal();
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        if (check.TriggerPlayerSetupAfterMove)
            await TriggerActualPlayerSetupAsync(
                combatState,
                player,
                check.PlayerSetupChoiceCardIds);
        if (check.TriggerAutoPrePlayAfterPlayerSetup)
        {
            HookPlayerChoiceContext context = new(player, player.NetId, GameActionType.Combat);
            using IDisposable? selector = check.AutoPrePlayChoiceCardIds.Length == 0
                ? null
                : CardSelectCmd.PushSelector(new UnattendedCardSelector(check.AutoPrePlayChoiceCardIds));
            await Hook.AfterAutoPrePlayPhaseEntered(context, combatState, player);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        foreach (UnattendedCardPlayCheck playCheck in check.CardPlayChecksAfterPlayerSetup)
        {
            CardModel card = FindActualHandCard(player, playCheck.CardId, playCheck.Occurrence);
            using IDisposable? selector = playCheck.UseChoice
                ? CardSelectCmd.PushSelector(new UnattendedCardSelector(
                    playCheck.ChoiceCardIds,
                    playCheck.ExpectedExcludedChoiceCardIds))
                : null;
            bool playable = card.TryManualPlay(ResolvePlayTarget(playCheck.Target, enemy));
            if (playable != playCheck.ExpectedPlayable)
                throw new InvalidOperationException(
                    $"实机中玩家回合准备后卡牌 {playCheck.CardId}#{playCheck.Occurrence} 可打出={playable}，预期 {playCheck.ExpectedPlayable}。");
            if (playable)
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        AssertDerivedHooksAfterPlayerSetup(
            combatState,
            player,
            check,
            simulatedPlayerSetupHooks);
        if (check.ExpectedMirrorRelicState is { } expectedMirrorRelics)
        {
            StringBuilder liveRelics = new();
            RelicPredictionStateSupport.AppendLiveContinuation(liveRelics, player);
            StringBuilder predictedRelics = new();
            RelicPredictionStateSupport.AppendPredictedContinuation(
                predictedRelics,
                simulator,
                simulatedCombat.RelicsOf(player));
            AssertDerivedHookValue(
                "MirrorRelicState",
                liveRelics.ToString(),
                predictedRelics.ToString(),
                expectedMirrorRelics);
        }
        if (check.RollNextMoveAfterActual)
            monster.RollMove(combatState.PlayerCreatures);
        MoveStateSnapshot actual = CaptureActual(combatState, player, enemy);
        if (check.ExpectedNextMoveId is { } expectedNextMoveId
            && !monster.NextMove.Id.Equals(expectedNextMoveId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 后续行动为 {monster.NextMove.Id}，预期 {expectedNextMoveId}。");
        }
        if (check.ExpectedNextMoveId is { } expectedSimulatedNextMoveId
            && !simulatedCombat.GetPredictedMoveId(enemy).Equals(
                expectedSimulatedNextMoveId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 模拟后续行动为 {simulatedCombat.GetPredictedMoveId(enemy)}，" +
                $"预期 {expectedSimulatedNextMoveId}。");
        }
        AssertSnapshotEqual(predicted, actual, monster.Id.Entry, move.Id);
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] MOVE_DIFF_OK run_id={_request.RunId} " +
            $"monster={monster.Id.Entry} move={move.Id} next_move={monster.NextMove.Id}");
        if (check.ExpectedPlayerHp is { } expectedPlayerHp && actual.PlayerHp != expectedPlayerHp)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 玩家生命 {actual.PlayerHp}，预期 {expectedPlayerHp}。");
        }
        if (check.ExpectedPlayerHpLoss is { } expectedHpLoss
            && before.PlayerHp - actual.PlayerHp != expectedHpLoss)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 玩家掉血 {before.PlayerHp - actual.PlayerHp}，预期 {expectedHpLoss}。");
        }
        if (check.ExpectedOstyHp is { } expectedOstyHp && actual.OstyHp != expectedOstyHp)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 后奥斯蒂生命 {actual.OstyHp}，预期 {expectedOstyHp}。");
        }
        if (check.ExpectedOstyMaxHp is { } expectedOstyMaxHp && actual.OstyMaxHp != expectedOstyMaxHp)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 后奥斯蒂最大生命 {actual.OstyMaxHp}，预期 {expectedOstyMaxHp}。");
        }
        AssertExpectedPowers(
            actual.OstyPowers,
            check.ExpectedOstyPowers,
            "奥斯蒂",
            monster.Id.Entry,
            move.Id);
        if (check.ExpectedPlayerBlock is { } expectedPlayerBlock
            && actual.PlayerBlock != expectedPlayerBlock)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 玩家格挡 {actual.PlayerBlock}，预期 {expectedPlayerBlock}。");
        }
        if (check.ExpectedPlayerBlockGain is { } expectedPlayerBlockGain
            && actual.PlayerBlock - before.PlayerBlock != expectedPlayerBlockGain)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 玩家格挡变化 {actual.PlayerBlock - before.PlayerBlock}，预期 {expectedPlayerBlockGain}。");
        }
        if (check.ExpectedPlayerEnergy is { } expectedPlayerEnergy
            && actual.PlayerEnergy != expectedPlayerEnergy)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 玩家能量 {actual.PlayerEnergy}，预期 {expectedPlayerEnergy}。");
        }
        if (check.ExpectedPlayerStars is { } expectedPlayerStars
            && actual.PlayerStars != expectedPlayerStars)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 玩家星能 {actual.PlayerStars}，预期 {expectedPlayerStars}。");
        }
        if (check.ExpectedPlayerGold is { } expectedPlayerGold
            && actual.PlayerGold != expectedPlayerGold)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 玩家金币 {actual.PlayerGold}，预期 {expectedPlayerGold}。");
        }
        if (check.ExpectedPlayerOrbCapacity is { } expectedPlayerOrbCapacity
            && actual.PlayerOrbCapacity != expectedPlayerOrbCapacity)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 玩家充能球槽位 {actual.PlayerOrbCapacity}，预期 {expectedPlayerOrbCapacity}。");
        }
        if (check.ExpectedPlayerHandCount is { } expectedPlayerHandCount)
        {
            int handCount = actual.PlayerPileCards
                .Where(entry => entry.Key.StartsWith("Hand:", StringComparison.Ordinal))
                .Sum(static entry => entry.Value);
            if (handCount != expectedPlayerHandCount)
            {
                throw new InvalidOperationException(
                    $"{monster.Id.Entry}.{move.Id} 玩家手牌数 {handCount}，预期 {expectedPlayerHandCount}。");
            }
        }
        if (check.ExpectedEnemyBlockGain is { } expectedBlockGain
            && actual.EnemyBlock - before.EnemyBlock != expectedBlockGain)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 怪物格挡变化 {actual.EnemyBlock - before.EnemyBlock}，预期 {expectedBlockGain}。");
        }
        if (check.ExpectedEnemyHpGain is { } expectedHpGain
            && actual.EnemyHp - before.EnemyHp != expectedHpGain)
        {
            throw new InvalidOperationException(
                $"{monster.Id.Entry}.{move.Id} 怪物生命变化 {actual.EnemyHp - before.EnemyHp}，预期 {expectedHpGain}。");
        }
        AssertExpectedPowers(actual.PlayerPowers, check.ExpectedPlayerPowers, "玩家", monster.Id.Entry, move.Id);
        AssertAbsentPowers(
            actual.PlayerPowers,
            check.ExpectedAbsentPlayerPowers,
            "玩家",
            monster.Id.Entry,
            move.Id);
        AssertExpectedPowers(actual.EnemyPowers, check.ExpectedEnemyPowers, "怪物", monster.Id.Entry, move.Id);
        AssertAbsentPowers(
            actual.EnemyPowers,
            check.ExpectedAbsentEnemyPowers,
            "怪物",
            monster.Id.Entry,
            move.Id);
        AssertExpectedPowers(
            actual.PlayerPowerStates,
            check.ExpectedPlayerPowerStates,
            "玩家 Power 状态",
            monster.Id.Entry,
            move.Id);
        AssertExpectedPowers(
            actual.EnemyPowerStates,
            check.ExpectedEnemyPowerStates,
            "怪物 Power 状态",
            monster.Id.Entry,
            move.Id);
        AssertExpectedPiles(actual.PlayerPileCards, check.ExpectedPlayerPileCards, monster.Id.Entry, move.Id);
        AssertExpectedPileDamageTotals(
            actual.PlayerPileCardDamageTotals,
            check.ExpectedPlayerPileCardDamageTotals,
            monster.Id.Entry,
            move.Id);
        AssertExpectedCardStates(
            actual.PlayerCardStates,
            check.ExpectedPlayerCardStates,
            monster.Id.Entry,
            move.Id);
        AssertExpectedCardStates(
            actual.PlayerCardCosts,
            check.ExpectedPlayerCardCosts,
            monster.Id.Entry,
            move.Id);
        AssertExpectedCardStates(
            actual.PlayerCardEnchantments,
            check.ExpectedPlayerCardEnchantments,
            monster.Id.Entry,
            move.Id);
        AssertExpectedCardStates(
            actual.PlayerCardUpgrades,
            check.ExpectedPlayerCardUpgrades,
            monster.Id.Entry,
            move.Id);
        AssertExpectedPowers(
            actual.EnemyHpsByModel,
            check.ExpectedEnemyHpsByModel,
            "怪物生命合计",
            monster.Id.Entry,
            move.Id);
        AssertExpectedEnemyBlocks(
            actual.EnemyBlocksByModel,
            check.ExpectedEnemyBlocksByModel,
            monster.Id.Entry,
            move.Id);
        AssertExpectedPowers(
            actual.PlayerOrbs,
            check.ExpectedPlayerOrbs,
            "玩家充能球",
            monster.Id.Entry,
            move.Id);
    }

    private static void AssertDerivedPowerHooks(
        CombatState actualCombat,
        SimulatedCombatState simulatedCombat,
        Player player,
        Creature enemy,
        UnattendedMonsterMoveCheck check)
    {
        if (check.ExpectedModifiedHandDraw is { } expectedDraw)
        {
            int actual = (int)Hook.ModifyHandDraw(
                actualCombat,
                player,
                CombatManager.baseHandDrawCount,
                out _);
            int simulated = (int)Hook.ModifyHandDraw(
                simulatedCombat,
                player,
                CombatManager.baseHandDrawCount,
                out _);
            AssertDerivedHookValue("ModifyHandDraw", actual, simulated, expectedDraw);
        }
        if (check.ExpectedModifiedMaxEnergy is { } expectedEnergy)
        {
            int actual = (int)Hook.ModifyMaxEnergy(actualCombat, player, player.MaxEnergy);
            int simulated = PersistentPowerSupport.GetModifiedMaxEnergy(simulatedCombat, player);
            AssertDerivedHookValue("ModifyMaxEnergy", actual, simulated, expectedEnergy);
        }
        if (check.ExpectedShouldFlush is { } expectedFlush)
        {
            bool actual = Hook.ShouldFlush(actualCombat, player);
            bool simulated = PersistentRelicSupport.ShouldFlush(simulatedCombat, player);
            AssertDerivedHookValue("ShouldFlush", actual, simulated, expectedFlush);
        }
        if (check.ExpectedShouldPlayerResetEnergy is { } expectedReset)
        {
            bool actual = Hook.ShouldPlayerResetEnergy(actualCombat, player);
            bool simulated = PersistentRelicSupport.ShouldPlayerResetEnergy(simulatedCombat, player);
            AssertDerivedHookValue("ShouldPlayerResetEnergy", actual, simulated, expectedReset);
        }
        if (check.ExpectedModifiedXValue is { } expectedX)
        {
            CardModel card = FindActualHandCard(player, check.DerivedHookCardId, 0);
            int actual = Hook.ModifyXValue(actualCombat, card, check.DerivedHookBaseValue);
            int simulated = Hook.ModifyXValue(simulatedCombat, card, check.DerivedHookBaseValue);
            AssertDerivedHookValue("ModifyXValue", actual, simulated, expectedX);
        }
        if (check.ExpectedModifiedOrbValue is { } expectedOrbValue)
        {
            OrbModel orb = player.PlayerCombatState!.OrbQueue.Orbs
                .First(candidate => candidate.Id.Entry.Equals(
                    check.DerivedHookOrbId,
                    StringComparison.OrdinalIgnoreCase));
            int actual = (int)Hook.ModifyOrbValue(actualCombat, orb, check.DerivedHookBaseValue);
            int simulated = (int)Hook.ModifyOrbValue(simulatedCombat, orb, check.DerivedHookBaseValue);
            AssertDerivedHookValue("ModifyOrbValue", actual, simulated, expectedOrbValue);
        }
        if (check.ExpectedShouldClearBlock is { } expectedClear)
        {
            Creature target = check.DerivedHookTarget.Equals("Enemy", StringComparison.OrdinalIgnoreCase)
                ? enemy
                : player.Creature;
            bool actual = Hook.ShouldClearBlock(actualCombat, target, out _);
            bool simulated = simulatedCombat.ShouldClearBlock(target);
            AssertDerivedHookValue("ShouldClearBlock", actual, simulated, expectedClear);
        }
    }

    private static void AssertDerivedHookValue<T>(
        string hook,
        T actual,
        T simulated,
        T expected)
        where T : IEquatable<T>
    {
        if (!actual.Equals(expected) || !simulated.Equals(expected))
        {
            throw new InvalidOperationException(
                $"派生 Hook {hook} 不一致：actual={actual} simulated={simulated} expected={expected}。");
        }
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] DERIVED_HOOK hook={hook} actual={actual} simulated={simulated} expected={expected}");
    }

    private readonly record struct PlayerSetupDerivedHookSnapshot(
        int? ModifiedHandDraw,
        int? RawModifiedHandDraw,
        string? HandDrawModifiers,
        int? ModifiedMaxEnergy,
        bool? ShouldFlush,
        bool? ShouldPlayerResetEnergy,
        string? StatefulRelicState);

    private static PlayerSetupDerivedHookSnapshot CaptureSimulatedDerivedHooksAfterPlayerSetup(
        SimulatedCombatState simulatedCombat,
        Player player,
        UnattendedMonsterMoveCheck check)
    {
        int? modifiedHandDraw = null;
        int? rawModifiedHandDraw = null;
        string? handDrawModifiers = null;
        if (check.ExpectedModifiedHandDrawAfterPlayerSetup is not null)
        {
            rawModifiedHandDraw = (int)Hook.ModifyHandDraw(
                simulatedCombat,
                player,
                CombatManager.baseHandDrawCount,
                out IEnumerable<AbstractModel> modifiers);
            handDrawModifiers = string.Join(',', modifiers.Select(static model => model.Id.Entry));
            modifiedHandDraw = PersistentPowerSupport.GetModifiedHandDraw(
                simulatedCombat,
                player,
                CombatManager.baseHandDrawCount);
        }

        int? modifiedMaxEnergy = check.ExpectedModifiedMaxEnergyAfterPlayerSetup is null
            ? null
            : PersistentPowerSupport.GetModifiedMaxEnergy(simulatedCombat, player);
        bool? shouldFlush = check.ExpectedShouldFlushAfterPlayerSetup is null
            ? null
            : PersistentRelicSupport.ShouldFlush(simulatedCombat, player);
        bool? shouldPlayerResetEnergy = check.ExpectedShouldPlayerResetEnergyAfterPlayerSetup is null
            ? null
            : PersistentRelicSupport.ShouldPlayerResetEnergy(simulatedCombat, player);
        string? statefulRelicState = null;
        if (check.ExpectedStatefulRelicStateAfterPlayerSetup is not null)
        {
            StringBuilder simulated = new();
            simulatedCombat.AppendPredictedStatefulRelics(simulated, player);
            statefulRelicState = simulated.ToString();
        }

        return new PlayerSetupDerivedHookSnapshot(
            modifiedHandDraw,
            rawModifiedHandDraw,
            handDrawModifiers,
            modifiedMaxEnergy,
            shouldFlush,
            shouldPlayerResetEnergy,
            statefulRelicState);
    }

    private static void AssertDerivedHooksAfterPlayerSetup(
        CombatState actualCombat,
        Player player,
        UnattendedMonsterMoveCheck check,
        PlayerSetupDerivedHookSnapshot simulatedHooks)
    {
        if (check.ExpectedModifiedHandDrawAfterPlayerSetup is { } expectedDraw)
        {
            int actual = (int)Hook.ModifyHandDraw(
                actualCombat,
                player,
                CombatManager.baseHandDrawCount,
                out IEnumerable<AbstractModel> actualModifiers);
            int rawSimulated = simulatedHooks.RawModifiedHandDraw
                ?? throw new InvalidOperationException("缺少模拟玩家回合准备后的原始抽牌 Hook 快照。");
            int simulated = simulatedHooks.ModifiedHandDraw
                ?? throw new InvalidOperationException("缺少模拟玩家回合准备后的抽牌 Hook 快照。");
            string simulatedModifiers = simulatedHooks.HandDrawModifiers
                ?? throw new InvalidOperationException("缺少模拟玩家回合准备后的抽牌修改器快照。");
            Entry.Logger.Info(
                $"[CombatSolver/Unattended] DERIVED_HOOK_TRACE hook=ModifyHandDrawAfterPlayerSetup " +
                $"actual_modifiers={string.Join(',', actualModifiers.Select(static model => model.Id.Entry))} " +
                $"simulated_raw={rawSimulated} simulated_modifiers={simulatedModifiers}");
            AssertDerivedHookValue("ModifyHandDrawAfterPlayerSetup", actual, simulated, expectedDraw);
        }
        if (check.ExpectedModifiedMaxEnergyAfterPlayerSetup is { } expectedEnergy)
        {
            int actual = (int)Hook.ModifyMaxEnergy(actualCombat, player, player.MaxEnergy);
            int simulated = simulatedHooks.ModifiedMaxEnergy
                ?? throw new InvalidOperationException("缺少模拟玩家回合准备后的最大能量 Hook 快照。");
            AssertDerivedHookValue("ModifyMaxEnergyAfterPlayerSetup", actual, simulated, expectedEnergy);
        }
        if (check.ExpectedShouldFlushAfterPlayerSetup is { } expectedFlush)
        {
            bool actual = Hook.ShouldFlush(actualCombat, player);
            bool simulated = simulatedHooks.ShouldFlush
                ?? throw new InvalidOperationException("缺少模拟玩家回合准备后的弃牌 Hook 快照。");
            AssertDerivedHookValue("ShouldFlushAfterPlayerSetup", actual, simulated, expectedFlush);
        }
        if (check.ExpectedShouldPlayerResetEnergyAfterPlayerSetup is { } expectedReset)
        {
            bool actual = Hook.ShouldPlayerResetEnergy(actualCombat, player);
            bool simulated = simulatedHooks.ShouldPlayerResetEnergy
                ?? throw new InvalidOperationException("缺少模拟玩家回合准备后的能量重置 Hook 快照。");
            AssertDerivedHookValue("ShouldPlayerResetEnergyAfterPlayerSetup", actual, simulated, expectedReset);
        }
        if (check.ExpectedStatefulRelicStateAfterPlayerSetup is { } expectedRelics)
        {
            StringBuilder actual = new();
            SimulatedCombatState.AppendLiveStatefulRelics(actual, player);
            string simulated = simulatedHooks.StatefulRelicState
                ?? throw new InvalidOperationException("缺少模拟玩家回合准备后的有状态遗物快照。");
            AssertDerivedHookValue(
                "StatefulRelicStateAfterPlayerSetup",
                actual.ToString(),
                simulated,
                expectedRelics);
        }
    }

    private static PredictedCard InjectSimulatedCard(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        UnattendedCardInjection injection)
    {
        if (!Enum.TryParse(injection.Pile, true, out PileType pileType)
            || pileType is not (PileType.Hand or PileType.Draw or PileType.Discard or PileType.Exhaust))
        {
            throw new InvalidOperationException($"无人差分不支持模拟注入牌堆 {injection.Pile}。");
        }
        if (injection.Count != 1)
            throw new InvalidOperationException($"一步差分的模拟注入卡牌数量必须为 1，实际为 {injection.Count}。");

        CardModel canonical = ResolveUnique(ModelDb.AllCards, injection.CardId, "卡牌");
        PredictedCard card = PredictedCard.Create(canonical, player);
        for (int level = 0; level < injection.UpgradeLevels && card.Preview.IsUpgradable; level++)
            card.Upgrade();
        ApplyCardEnumMembers(card.MutablePreview, injection.EnumMembers);
        if (!string.IsNullOrWhiteSpace(injection.EnchantmentId))
        {
            EnchantmentModel enchantment = ResolveUnique(
                ModelDb.DebugEnchantments,
                injection.EnchantmentId,
                "附魔").ToMutable();
            if (!enchantment.CanEnchant(card.Preview))
            {
                throw new InvalidOperationException(
                    $"附魔 {enchantment.Id} 不能用于模拟测试卡牌 {card.Preview.Id}。");
            }
            card.Enchant(enchantment, injection.EnchantmentAmount);
        }
        if (!string.IsNullOrWhiteSpace(injection.AfflictionId))
        {
            AfflictionModel affliction = ResolveUnique(
                ModelDb.DebugAfflictions,
                injection.AfflictionId,
                "苦难").ToMutable();
            card.Afflict(affliction, injection.AfflictionAmount);
        }
        simulator.AddGeneratedCardToCombat(card, pileType, player);
        combat.NormalizeCardAfflictions(simulator);
        return card;
    }

    private static void ApplySimulatedPowerInjection(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CombatState combatState,
        Player player,
        UnattendedPowerInjection injection,
        Creature checkedEnemy)
    {
        if (injection.DynamicVars.Count > 0 || injection.InternalIntegerMembers.Count > 0)
            throw new InvalidOperationException("行动后模拟 Power 注入不支持动态变量或预置内部状态。");
        Creature owner = ResolvePowerInjectionCreature(
            combatState,
            player,
            checkedEnemy,
            injection.Target,
            injection.TargetIndex,
            "所有者");
        if (!string.IsNullOrWhiteSpace(injection.PowerTarget)
            && !ReferenceEquals(owner, ResolvePowerInjectionCreature(
                combatState,
                player,
                checkedEnemy,
                injection.PowerTarget,
                injection.PowerTargetIndex,
                "效果目标")))
        {
            throw new InvalidOperationException("行动后模拟 Power 注入暂不支持独立效果目标。");
        }
        Creature applier = string.IsNullOrWhiteSpace(injection.Applier)
            ? player.Creature
            : ResolvePowerInjectionCreature(
                combatState,
                player,
                checkedEnemy,
                injection.Applier,
                injection.ApplierIndex,
                "施加者");
        PowerModel canonical = ResolveUnique(ModelDb.AllPowers, injection.PowerId, "Power");
        if (canonical is VoidFormPower)
            TurnStartPowerSupport.PrepareVoidFormApplication(simulator, combat, owner);
        combat.ApplyPower(canonical.GetType(), owner, injection.Amount, applier);
    }

    private static PredictedCard FindSimulatedHandCard(
        CombatPredictionSimulator simulator,
        Player player,
        string cardId,
        int occurrence)
        => simulator.State.GetPlayerCombatState(player).Hand.Cards
            .Where(card => card.Preview.Id.Entry.Equals(cardId, StringComparison.Ordinal))
            .Skip(occurrence)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"模拟手牌中找不到 {cardId}#{occurrence}。");

    private static void TransformSimulatedCard(
        CombatPredictionSimulator simulator,
        Player player,
        UnattendedCardTransformCheck transform)
    {
        PredictedCard original = FindSimulatedHandCard(
            simulator,
            player,
            transform.OriginalCardId,
            transform.Occurrence);
        CardModel replacement = ResolveUnique(
            ModelDb.AllCards,
            transform.ReplacementCardId,
            "变换替代卡牌");
        CardChoiceSupport.TransformCards(simulator, [original], replacement, upgradeReplacement: false);
    }

    private static async Task TransformActualCardAsync(
        CombatState combatState,
        Player player,
        UnattendedCardTransformCheck transform)
    {
        CardModel original = FindActualHandCard(
            player,
            transform.OriginalCardId,
            transform.Occurrence);
        CardModel canonical = ResolveUnique(
            ModelDb.AllCards,
            transform.ReplacementCardId,
            "变换替代卡牌");
        CardModel replacement = combatState.CreateCard(canonical, player);
        CardPileAddResult? result = await CardCmd.Transform(
            original,
            replacement,
            CardPreviewStyle.None);
        if (result?.success != true)
        {
            throw new InvalidOperationException(
                $"实机未能把 {transform.OriginalCardId} 变换为 {transform.ReplacementCardId}。");
        }
    }

    private static void AssertLiveEndTurnRiskChoices(
        CombatState combatState,
        Player player,
        UnattendedMonsterMoveCheck check)
    {
        SimulatedCombatState combat = new(combatState);
        CombatPredictionSimulator simulator = new(combat);
        PredictedCard choiceCard = FindSimulatedHandCard(
            simulator,
            player,
            check.LiveEndTurnRiskCardId!,
            0);
        CardChoiceSpec spec = CardChoiceSupport.GetSpec(simulator, choiceCard)
            ?? throw new InvalidOperationException(
                $"结束回合风险复核测试牌 {check.LiveEndTurnRiskCardId} 没有选牌定义。");
        PlanCardChoice choice = CardChoiceSupport.BuildRequestedChoice(
            spec,
            check.LiveEndTurnRiskChoiceCardIds) with
        {
            SourceId = check.LiveEndTurnRiskChoiceSourceId,
            Timing = PlanChoiceTiming.PlayerTurnEnd,
        };
        PlanCardChoice futureTurnChoice = choice with
        {
            SourceId = "FUTURE_PLAYER_TURN",
            Timing = PlanChoiceTiming.PlayerTurnStart,
        };
        _ = LiveEndTurnRiskEvaluator.Evaluate(combatState, [choice, futureTurnChoice]);
    }

    private static void AssertLiveEndTurnRiskKnowledgeChoice(
        CombatState combatState,
        Player player,
        Creature enemy,
        UnattendedMonsterMoveCheck check)
    {
        SimulatedCombatState combat = new(combatState);
        combat.ForceMonsterMove(enemy, "CURSE_OF_KNOWLEDGE_MOVE");
        int counterBefore = combat.GetKnowledgeDemonCurseCounter(enemy);
        string cardId = check.LiveEndTurnRiskKnowledgeChoiceCardId!;
        PlanCardChoice choice = new(
            PlanChoiceEffect.ApplyKnowledgeCurse,
            PileType.None,
            [new PlanCardToken(cardId, 0, string.Empty, 0, 0, cardId)],
            $"KNOWLEDGE_DEMON:{enemy.CombatId ?? uint.MaxValue}:{counterBefore}",
            Timing: PlanChoiceTiming.EnemyTurn);
        _ = LiveEndTurnRiskEvaluator.Evaluate(player, combat, [choice]);
        int counterAfter = combat.GetKnowledgeDemonCurseCounter(enemy);
        if (counterAfter != counterBefore + 1)
        {
            throw new InvalidOperationException(
                $"结束回合风险复核没有消费知识恶魔诅咒计划：{counterBefore} -> {counterAfter}。");
        }
    }

    private static void AssertSimulatedCardPileAfterPlay(
        CombatPredictionSimulator simulator,
        Player player,
        UnattendedCardPlayCheck check)
    {
        if (check.ExpectedCardIdAfterPlay == null || check.ExpectedCardPileAfterPlay == null)
            return;
        PredictedCard card = simulator.State.GetPlayerCombatState(player).AllCards
            .Single(candidate => candidate.Preview.Id.Entry.Equals(
                check.ExpectedCardIdAfterPlay,
                StringComparison.Ordinal));
        string actualPile = card.GetPile(simulator.State)?.Type.ToString() ?? "None";
        if (!actualPile.Equals(check.ExpectedCardPileAfterPlay, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"模拟中打出 {check.CardId} 后 {check.ExpectedCardIdAfterPlay} 位于 {actualPile}，" +
                $"预期 {check.ExpectedCardPileAfterPlay}。");
        }
    }

    private static void AssertSimulatedForkableAfterPlay(
        SimulatedCombatState combat,
        UnattendedCardPlayCheck check)
    {
        if (check.AssertForkableAfterPlay)
            combat.AssertForkable();
    }

    private static CardModel FindActualHandCard(Player player, string cardId, int occurrence)
        => player.PlayerCombatState!.Hand.Cards
            .Where(card => card.Id.Entry.Equals(cardId, StringComparison.Ordinal))
            .Skip(occurrence)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"实机手牌中找不到 {cardId}#{occurrence}。");

    private static void AssertActualCardPileAfterPlay(
        Player player,
        UnattendedCardPlayCheck check)
    {
        if (check.ExpectedCardIdAfterPlay == null || check.ExpectedCardPileAfterPlay == null)
            return;
        CardModel card = player.PlayerCombatState!.AllCards
            .Single(candidate => candidate.Id.Entry.Equals(
                check.ExpectedCardIdAfterPlay,
                StringComparison.Ordinal));
        string actualPile = card.Pile?.Type.ToString() ?? "None";
        if (!actualPile.Equals(check.ExpectedCardPileAfterPlay, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"实机中打出 {check.CardId} 后 {check.ExpectedCardIdAfterPlay} 位于 {actualPile}，" +
                $"预期 {check.ExpectedCardPileAfterPlay}。");
        }
    }

    private static void PlaySimulatedCard(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard card,
        Creature? target,
        IReadOnlyList<Creature> enemies,
        IReadOnlyList<string>? choiceCardIds = null,
        IReadOnlyList<string>? expectedExcludedChoiceCardIds = null)
    {
        CardPlayPowerSuppression suppression = combat.SuppressHistorySensitiveCardModifiers(card);
        HashSet<uint> processedEnemyDeaths = [];
        IReadOnlyList<string> requestedChoiceCardIds = choiceCardIds ?? [];
        TurnStartChoiceCursor choices = choiceCardIds == null
            ? new TurnStartChoiceCursor(null)
            : TurnStartChoiceCursor.ForAutomaticPolicy(request =>
            {
                CardChoiceSpec spec = TurnStartChoiceSupport.BuildSpec(
                    simulator,
                    card.Preview.Owner,
                    request);
                foreach (string excludedCardId in expectedExcludedChoiceCardIds ?? [])
                {
                    if (spec.Options.Any(option => option.Preview.Id.Entry.Equals(
                            excludedCardId,
                            StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            $"模拟测试选牌候选不应包含 {excludedCardId}。");
                    }
                }
                return CardChoiceSupport.BuildRequestedChoice(spec, requestedChoiceCardIds);
            });
        combat.BeginActionChoices(choices);
        using IDisposable cardExecutionScope = combat.BeginCardExecutionScope(processedEnemyDeaths);
        try
        {
            simulator.ManualPlay(card, target, out _);
        }
        finally
        {
            combat.RestoreHistorySensitiveCardModifiers(suppression);
            combat.EndActionChoices();
        }
        combat.NormalizeAeonglassWithers(simulator);
        combat.NormalizeCardAfflictions(simulator);
        CorePowerSupport.ApplyEnemyDeathPowers(simulator, combat, enemies, processedEnemyDeaths);
    }

    private static Creature? ResolvePlayTarget(string target, Creature enemy)
        => target switch
        {
            "Enemy" => enemy,
            "Player" => LocalContext.GetMe(enemy.CombatState
                ?? throw new InvalidOperationException("测试敌人没有战斗状态。"))?.Creature
                ?? throw new InvalidOperationException("测试战斗找不到本地玩家。"),
            "None" => null,
            _ => throw new InvalidOperationException($"不支持的测试出牌目标 {target}。"),
        };

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
