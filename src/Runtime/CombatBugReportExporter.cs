using System.IO.Compression;
using System.Collections.Concurrent;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CombatSolver;

internal static class CombatBugReportExporter
{
    private const string ExportFolderName = "CombatSolver-BugReports";
    private const long MaximumCapturedSaveBytes = 4L * 1024 * 1024;
    private const int MaximumCheckpoints = 6;
    private const int MaximumArchivedCheckpoints = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(),
            new SerializableCardJsonConverter(),
            new CapturedObjectFieldsJsonConverter(),
        },
    };

    private sealed record CapturedFile(string SourceRelativePath, byte[] Bytes);
    private sealed record ForensicCheckpoint(
        long Sequence,
        long? EventCursor,
        bool Searchable,
        string Label,
        byte[] MetadataJsonUtf8,
        byte[] ReplayStateJsonUtf8,
        byte[] NativeCombatState,
        byte[] InMemoryRunSave);
    private sealed record ForensicCheckpointCapture(
        long Sequence,
        long? EventCursor,
        bool Searchable,
        string Label,
        DateTimeOffset CapturedAt,
        string? SearchRootId,
        string StateText,
        ForensicCombatCapture Combat,
        JsonObject Settings,
        object EffectivePolicy,
        CombatReplayOutcomeSnapshot Outcome,
        object SearchProfiles,
        object? MetadataResult,
        object? ReplayResult,
        bool HasResult,
        string Route,
        string ReplanAudit,
        string ControlMode,
        int? LastSolverDeployedTurn);
    private sealed record ForensicCombatCapture(
        ForensicMetadataStateCapture MetadataState,
        ForensicReplayStateCapture ReplayState,
        NetFullCombatState NativeCombatState,
        SerializableRun InMemoryRunSave);
    private sealed record ForensicMetadataStateCapture(
        string? EncounterId,
        int RoundNumber,
        string CurrentSide,
        int? PlayerTurn,
        string? PlayerPhase,
        int AscensionLevel,
        int CurrentActIndex,
        int ActFloor,
        int TotalFloor,
        string ReadableDiagnostic,
        object RunRng,
        object[] Players);
    private sealed record ForensicReplayStateCapture(
        string? EncounterId,
        string? EncounterType,
        int RoundNumber,
        string CurrentSide,
        int AscensionLevel,
        int CurrentActIndex,
        int ActFloor,
        int TotalFloor,
        object RunRng,
        int ActualPotionsUsedThisCombat,
        object[] Players,
        object[] Creatures,
        object[] History);
    private sealed record CapturedObjectFields(IReadOnlyList<CapturedObjectField> Items);
    private sealed record CapturedObjectField(string Name, object? Value);
    private sealed record CapturedFieldDescriptor(string Name, FieldInfo Field);
    private sealed record ForensicArchiveCheckpoint(
        string Name,
        long Sequence,
        long? EventCursor,
        bool Searchable,
        string Label,
        byte[] MetadataJsonUtf8,
        byte[] ReplayStateJsonUtf8,
        byte[] NativeCombatState,
        byte[] InMemoryRunSave);

    private sealed class SerializableCardJsonConverter : JsonConverter<SerializableCard>
    {
        public override SerializableCard? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            SerializableCard value,
            JsonSerializerOptions options)
            => JsonSerializer.Serialize(
                writer,
                value,
                JsonSerializationUtility.GetTypeInfo<SerializableCard>());
    }

    private sealed class CapturedObjectFieldsJsonConverter : JsonConverter<CapturedObjectFields>
    {
        public override CapturedObjectFields? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            CapturedObjectFields value,
            JsonSerializerOptions options)
        {
            SortedDictionary<string, object?> fields = new(StringComparer.Ordinal);
            foreach (CapturedObjectField field in value.Items)
                fields[field.Name] = field.Value;
            writer.WriteStartObject();
            foreach ((string name, object? fieldValue) in fields)
            {
                writer.WritePropertyName(name);
                if (fieldValue == null)
                    writer.WriteNullValue();
                else
                    JsonSerializer.Serialize(writer, fieldValue, fieldValue.GetType(), options);
            }
            writer.WriteEndObject();
        }
    }

    private sealed class ForensicSession
    {
        public required string SessionId { get; init; }
        public required string EncounterId { get; init; }
        public required string EncounterType { get; init; }
        public required string Seed { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public required string UserDataDirectory { get; init; }
        public DateTimeOffset? EndedAt { get; set; }
        public string? EndReason { get; set; }
        public CapturedFile? InMemoryRunSave { get; set; }
        public ulong CachedCombatCaptureFrame { get; set; }
        public string? CachedCombatStateText { get; set; }
        public int CachedCombatHistoryCount { get; set; }
        public SolverSettingsSnapshot? CachedCombatProfiles { get; set; }
        public ForensicCombatCapture? CachedCombatCapture { get; set; }
        public List<ForensicCheckpoint> Checkpoints { get; } = [];
        public long NextCheckpointSequence { get; set; }
        public CombatReplayRecording? Recording { get; init; }
        public required CombatReplayOutcome Outcome { get; set; }
        public BugReportCombat? ReportCombat { get; set; }
        public string? SearchRootId { get; set; }
        public string? LastCompletedSearchRootId { get; set; }
        public string? ComparisonReferenceRootId { get; set; }
        public string? FirstErrorRootId { get; set; }
        public string? LastSearchableRootId { get; set; }
        public int PendingCheckpointWrites;
        public int PeakPendingCheckpointWrites;
        public long CaptureCount;
        public long CaptureTicks;
        public long MaximumCaptureTicks;
        public long LastSearchableEventCursor = -1;
        public List<object> SearchResults { get; } = [];
        public long SearchResultBytes;
        public int DroppedCaptureErrors;
        public object? LatestEffectivePolicy { get; set; }
        public List<object> SearchPolicies { get; } = [];
        public string LastRoute { get; set; } = "当前没有已完成的求解路线。";
        public string ReplanAudit { get; set; } = string.Empty;
        public string ControlMode { get; set; } = "solver_only";
        public int? LastSolverDeployedTurn { get; set; }
        public ConcurrentQueue<Exception> BackgroundErrors { get; } = new();
    }

    private sealed record ForensicArchiveSession(
        string SessionId,
        string EncounterId,
        string SessionJson,
        IReadOnlyList<ForensicArchiveCheckpoint> Checkpoints,
        CapturedFile? InMemoryRunSave,
        string LastRoute,
        string ReplanAudit,
        RecordedCombatArchive? Recording,
        object[] SearchPolicies,
        object[] SearchResults);

    private sealed record ForensicArchiveBundle(
        string ManifestJson,
        string CheckpointJson,
        ForensicArchiveSession? Current,
        ForensicArchiveSession? Recent);

    private static ForensicSession? _currentSession;
    private static ForensicSession? _lastSession;
    private static readonly BlockingCollection<Action> BackgroundOperations = new();
    private static readonly ConcurrentDictionary<Type, CapturedFieldDescriptor[]> ObjectFieldPlans = new();
    private static readonly ConcurrentDictionary<Type, CapturedFieldDescriptor[]> NestedFieldPlans = new();
    static CombatBugReportExporter()
    {
        StartBackgroundThread();
    }

    public static void BeginCombat(ICombatState? rawState)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("战斗取证只能从游戏主线程采集。");
        if (rawState is not CombatState state)
            return;

        if (_currentSession != null)
            _ = CompleteCombat("combat_replaced", null, string.Empty);
        // Once a new combat exists, exports intentionally select that current session and never
        // the previous one. Drop the old serialized checkpoints instead of retaining both fights.
        _lastSession = null;
        string userDataDirectory = OS.GetUserDataDir();
        _currentSession = new ForensicSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            EncounterId = state.Encounter?.Id.Entry ?? "unknown",
            EncounterType = state.Encounter?.RoomType.ToString() ?? "unknown",
            Seed = state.RunState.Rng.StringSeed,
            Recording = CombatReplayRecording.Pending,
            Outcome = new CombatReplayOutcome(state),
            StartedAt = DateTimeOffset.Now,
            UserDataDirectory = userDataDirectory,
        };
        Entry.Logger.Journal.BeginCombat(_currentSession.SessionId, _currentSession.EncounterId, _currentSession.Seed);
        RecordCheckpointCore(state, "combat_start", null, string.Empty);
        CombatReplayRecording.TestCombatStartObserver?.Invoke(state);
    }

    public static void RecordCheckpoint(
        CombatState state,
        string label,
        SolverResult? result,
        string replanAudit)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("战斗取证只能从游戏主线程采集。");
        EnsureSession(state);
        RecordCheckpointCore(state, label, result, replanAudit);
    }

    public static Task CompleteCombat(string reason, SolverResult? result, string replanAudit)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("战斗取证只能从游戏主线程采集。");
        ForensicSession? session = _currentSession;
        if (session == null)
            return Task.CompletedTask;

        CombatState? live = CombatManager.Instance.DebugOnlyGetState();
        if (live != null && live.RunState.Rng.StringSeed == session.Seed)
        {
            RecordCheckpointCore(live, "combat_end", result, replanAudit);
            CombatReplayRecording.TestCombatEndObserver?.Invoke(live);
            session.Outcome.Complete(live);
            session.Recording?.Dispose();
        }
        Entry.Logger.Journal.EndCombat(reason);
        Task completion = QueueSessionCompletion(
            session,
            reason,
            result,
            replanAudit,
            DateTimeOffset.Now);
        _lastSession = session;
        _currentSession = null;
        return completion;
    }

    public static Task<string> ExportCurrentAsync(string? outputDirectory = null, string? playerDescription = null, string? submissionId = null)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("问题包只能从游戏主线程导出。");

        CombatState? state = CombatManager.Instance.IsInProgress
            ? CombatManager.Instance.DebugOnlyGetState()
            : null;
        SolverResult? result = SolverController.CurrentResultForBugReport;
        string replanAudit = SolverController.ReplanAuditForBugReport;
        if (state != null)
            RecordCheckpoint(state, "export_clicked", result, replanAudit);

        SolverSettingsSnapshot profiles = SolverSettings.Capture();
        string settingsJson = CaptureSettingsForBugReport();
        string combatJson = CaptureCombatState(state, profiles);
        string routeText = DescribeRoute(result);
        string exportContextJson = CaptureExportContext(state);
        string reportId = submissionId ?? Guid.NewGuid().ToString("N");
        string reportJson = CombatBugReportMetadata.Serialize(reportId, playerDescription,
            (_currentSession ?? _lastSession)?.ReportCombat,
            SolverController.CaptureBugReportClassificationForExport(),
            SolverController.ManualProjectionComparisonForExport);
        string environmentJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            capturedAt = DateTimeOffset.Now,
            modVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            gameExecutable = Path.GetFileName(OS.GetExecutablePath()),
            userDataDirectory = Path.GetFileName(OS.GetUserDataDir()),
            os = System.Environment.OSVersion.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .Select(assembly => new
                {
                    name = assembly.GetName().Name,
                    version = assembly.GetName().Version?.ToString(),
                })
                .OrderBy(assembly => assembly.name, StringComparer.Ordinal)
                .ToArray(),
        }, JsonOptions);
        ForensicSession? currentSession = _currentSession;
        ForensicSession? recentSession = _lastSession;
        Task<RecordedCombatArchive>? recording = (currentSession ?? recentSession)?.Recording?.CaptureAsync();
        Task<CombatLogArchive> diagnosticLogs = Entry.Logger.Journal.CaptureAsync();
        Task<ForensicArchiveBundle> forensicsTask = QueueBackground(
            () => CaptureForensicBundle(currentSession, recentSession, recording?.GetAwaiter().GetResult()));

        string exportDirectory = outputDirectory ?? DefaultExportDirectory();
        Directory.CreateDirectory(exportDirectory);
        string encounter = SanitizeFileName(
            state?.Encounter?.Id.Entry
            ?? recentSession?.EncounterId
            ?? currentSession?.EncounterId
            ?? "no-combat");
        string path = Path.Combine(
            exportDirectory,
            $"CombatSolver-{CombatBugReportDescription.CurrentModVersion}-{encounter}-{reportId}.zip");
        string userDataDirectory = OS.GetUserDataDir();
        string executableDirectory = Path.GetDirectoryName(OS.GetExecutablePath())
            ?? throw new DirectoryNotFoundException("无法定位游戏目录。");

        return Task.Run(async () =>
        {
            ForensicArchiveBundle forensics = await forensicsTask.ConfigureAwait(false);
            return WriteArchive(
                path,
                userDataDirectory,
                executableDirectory,
                combatJson,
                routeText,
                replanAudit,
                settingsJson,
                exportContextJson,
                reportJson,
                environmentJson,
                forensics,
                await diagnosticLogs.ConfigureAwait(false));
        });
    }

    private static string WriteArchive(
        string path,
        string userDataDirectory,
        string executableDirectory,
        string combatJson,
        string routeText,
        string replanAudit,
        string settingsJson,
        string exportContextJson,
        string reportJson,
        string environmentJson,
        ForensicArchiveBundle forensics,
        CombatLogArchive diagnosticLogs)
    {
        using FileStream output = new(path, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);
        WriteDiagnosticLogs(archive, diagnosticLogs);

        string releaseInfo = Path.Combine(executableDirectory, "release_info.json");
        if (File.Exists(releaseInfo))
            AddFile(archive, releaseInfo, "diagnostics/release_info.json");

        AddText(archive, "diagnostics/combat-state.json", combatJson);
        AddText(archive, "diagnostics/current-route.txt", routeText);
        AddText(archive, "diagnostics/replan-audit.txt", replanAudit);
        AddText(archive, "diagnostics/settings.json", settingsJson);
        AddText(archive, "diagnostics/export-context.json", exportContextJson);
        AddText(archive, CombatBugReportMetadata.EntryPath, reportJson);
        AddText(archive, "diagnostics/environment.json", environmentJson);
        AddText(archive, "replay/manifest.json", forensics.ManifestJson);
        AddText(archive, "replay/checkpoint.json", forensics.CheckpointJson);
        WriteForensicSession(archive, "current", forensics.Current);
        WriteForensicSession(archive, "recent", forensics.Recent);
        AddText(
            archive,
            "README.txt",
            "此问题包由 CombatSolver 设置页导出。\n" +
            "report.json 是问题包身份与筛选元数据；diagnostics 保存环境与文字诊断；replay 保存检查点索引、战斗录制与恢复材料。\n" +
            "当前战斗保存详细记录；离开战斗后仍可提交最近一场，进入下一场后历史战斗只保留摘要。归档保留最近关键的 6 个检查点和完整已记录输入。\n" +
            "CombatSolver 独立日志位于 diagnostics/logs，按大小分片；index.json 标明记录范围、截断或写盘错误。包不包含 Godot 全局日志。提交时冻结日志前缀，后续战斗不会改变此包。\n" +
            "新包通过 recording 中的真实战前存档与原生动作、选择事件恢复；replay-state 和 native-state 用于独立对账。反射诊断字段存在显式截断时不能作为完整恢复材料。\n" +
            "checkpoint.json v2 列出稳定检查点身份、事件位置、实际搜索政策与材料路径。默认选择最近可搜索检查点；材料检查、恢复、录制回放、搜索和实际部署是不同验证阶段。\n" +
            "forensics/*/pre-combat 保存战前内存跑局快照。截图、整批磁盘存档和更早战斗不会进入问题包。\n" +
            "session.json、检查点和 export-context.json 会标记 controlMode：solver_only 表示全程由求解器接管，manual_plus_solver 表示本场曾手操后再交给求解器；lastSolverDeployedTurn 记录最近一次完整自动执行的回合。\n" +
            "设置页另有独立的“上传问题包”按钮。\n");
        Entry.Logger.Info($"[CombatSolver/Test] BUG_REPORT_EXPORTED path={path}");
        return path;
    }

    private static void WriteForensicSession(
        ZipArchive archive,
        string slot,
        ForensicArchiveSession? session)
    {
        if (session == null)
            return;
        string root = $"replay/{slot}";
        AddText(archive, $"{root}/session.json", session.SessionJson);
        foreach (ForensicArchiveCheckpoint checkpoint in session.Checkpoints)
        {
            AddBytes(
                archive,
                $"{root}/checkpoints/{checkpoint.Name}",
                checkpoint.MetadataJsonUtf8);
            AddBytes(
                archive,
                $"{root}/replay-state/{checkpoint.Name}",
                checkpoint.ReplayStateJsonUtf8);
            string stem = Path.GetFileNameWithoutExtension(checkpoint.Name);
            AddBytes(
                archive,
                $"{root}/native-state/{stem}.bin",
                checkpoint.NativeCombatState);
            AddBytes(
                archive,
                $"{root}/run-state/{stem}.save",
                checkpoint.InMemoryRunSave);
        }
        if (session.InMemoryRunSave != null)
            AddBytes(archive, $"{root}/pre-combat/in-memory-current_run.save", session.InMemoryRunSave.Bytes);
        if (session.Recording is { } recording)
        {
            AddBytes(archive, $"{root}/recording/origin.save", recording.Origin.RunSave);
            AddText(archive, $"{root}/recording/origin.json", JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                recording.Origin.NextActionId, recording.Origin.NextHookId,
                recording.Origin.ChoiceIds, recording.Origin.RewardIds,
                recording.Origin.GameVersion, recording.Origin.GameCommit, recording.Origin.ModelIdHash,
            }, JsonOptions));
            AddBytes(archive, $"{root}/recording/events.jsonl", recording.Events.JsonLines);
        }
        AddText(archive, $"{root}/last-route.txt", session.LastRoute);
        AddText(archive, $"{root}/replan-audit.txt", session.ReplanAudit);
    }

    private static void WriteDiagnosticLogs(ZipArchive archive, CombatLogArchive logs)
    {
        AddText(archive, "diagnostics/logs/index.json", JsonSerializer.Serialize(new
        {
            schemaVersion = 1, combat = logs.Current,
            current = new { logs.Events.EventCount, logs.Events.Error, logs.Events.PeakPendingBytes, logs.Events.WrittenBytes },
            process = new { logs.Process.EventCount, logs.Process.Error, logs.Process.WrittenBytes },
            detailScope = "current_or_most_recent_combat", previousCombatScope = "summary_only",
        }, JsonOptions));
        AddText(archive, "diagnostics/logs/history.json", JsonSerializer.Serialize(logs.History, JsonOptions));
        AddLogChunks(archive, "diagnostics/logs/combat", logs.Events.JsonLines);
        AddLogChunks(archive, "diagnostics/logs/process", logs.Process.JsonLines);
    }

    private static void AddLogChunks(ZipArchive archive, string root, byte[] bytes)
    {
        int start = 0, index = 0;
        while (start < bytes.Length)
        {
            int end = Math.Min(start + 256 * 1024, bytes.Length);
            while (end < bytes.Length && bytes[end - 1] != (byte)'\n') end++;
            ZipArchiveEntry entry = archive.CreateEntry($"{root}/{index++:D3}.jsonl", CompressionLevel.Fastest);
            using Stream output = entry.Open();
            output.Write(bytes.AsSpan(start, end - start));
            start = end;
        }
    }

    private static void EnsureSession(CombatState state)
    {
        if (_currentSession != null
            && _currentSession.Seed == state.RunState.Rng.StringSeed
            && _currentSession.EncounterId == (state.Encounter?.Id.Entry ?? "unknown"))
        {
            return;
        }
        BeginCombat(state);
    }

    private static void RecordCheckpointCore(
        CombatState state, string label, SolverResult? result, string replanAudit)
    {
        ForensicSession session = _currentSession
            ?? throw new InvalidOperationException("Missing forensic session.");
        if (label.Contains("fail", StringComparison.OrdinalIgnoreCase))
            session.FirstErrorRootId ??= session.LastSearchableRootId;
        int pending = Interlocked.Increment(ref session.PendingCheckpointWrites);
        if (pending > MaximumCheckpoints)
        {
            Interlocked.Decrement(ref session.PendingCheckpointWrites);
            session.NextCheckpointSequence++;
            RegisterBackgroundFailure(session, label, new InvalidDataException("checkpoint_capture_backlog_limit"));
            return;
        }
        session.PeakPendingCheckpointWrites = Math.Max(session.PeakPendingCheckpointWrites, pending);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool queued = false;
        try
        {
            CaptureCheckpointCore(state, label, result, replanAudit);
            queued = true;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or JsonException or IOException)
        {
            RegisterBackgroundFailure(session, label, error);
        }
        finally
        {
            if (!queued) Interlocked.Decrement(ref session.PendingCheckpointWrites);
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            session.CaptureCount++;
            session.CaptureTicks += elapsed;
            session.MaximumCaptureTicks = Math.Max(session.MaximumCaptureTicks, elapsed);
        }
    }

    private static void CaptureCheckpointCore(
        CombatState state,
        string label,
        SolverResult? result,
        string replanAudit)
    {
        ForensicSession session = _currentSession
            ?? throw new InvalidOperationException("记录战斗取证检查点时没有活动会话。");
        session.ReportCombat = CombatBugReportMetadata.CaptureCombat(state, session.SessionId, session.ReportCombat);
        long sequence = session.NextCheckpointSequence++;
        if (label == "combat_start" || label.StartsWith("search_request_", StringComparison.Ordinal))
            session.SearchRootId = $"{session.SessionId}:{sequence}";
        if (label == "search_completed")
        {
            session.LastCompletedSearchRootId = session.SearchRootId;
            if (result != null)
                CombatReplayRecording.TestSearchResultObserver?.Invoke(result);
        }
        if (result != null)
        {
            object resultRecord = new
            {
                checkpointId = session.SearchRootId,
                eventCursor = session.Recording?.EventCursor,
                scope = result.ResultScope,
                result.StartTurnNumber, result.BattleHpLostSoFar, result.ProjectedBattleHpLost,
                result.CombatEndedTurn, result.DeathTurn, result.BoundaryReason,
                result.PotionCount, result.BattlePotionsUsedSoFar,
                plannedActions = result.BestNode.Actions.ToArray(),
                turnSetupChoices = result.TurnSetupChoices.ToArray(),
            };
            _ = QueueBackground(() =>
            {
                byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(resultRecord, JsonOptions);
                if (session.SearchResults.Count >= 2048 || session.SearchResultBytes + serialized.Length > 8L * 1024 * 1024)
                    RegisterBackgroundFailure(session, label, new InvalidDataException("search_result_log_limit"));
                else
                {
                    session.SearchResults.Add(JsonNode.Parse(serialized)!);
                    session.SearchResultBytes += serialized.Length;
                }
                return true;
            });
        }
        // A search can finish without any live input. Its result and policy need
        // recording, but the ordered native state at that root has not changed.
        if (label is "search_completed" or "search_reused"
            && session.Recording != null && session.LastSearchableEventCursor == session.Recording.EventCursor
            && LocalContext.GetMe(state)?.PlayerCombatState?.Phase.ToString() == "Play"
            && !RunManager.Instance.ActionExecutor.IsRunning
            && session.CachedCombatStateText == ContinuationStamp.CaptureLive(state).StateText)
        {
            string completedRoute = DescribeRoute(result);
            string control = SolverController.ControlModeForBugReport;
            int? deployed = SolverController.LastSolverDeployedTurnForBugReport;
            bool hasResult = result != null;
            _ = QueueBackground(() =>
            {
                try { UpdateSessionResult(session, hasResult, completedRoute, replanAudit, control, deployed); }
                finally { Interlocked.Decrement(ref session.PendingCheckpointWrites); }
                return true;
            });
            return;
        }
        DateTimeOffset capturedAt = DateTimeOffset.Now;
        SolverSettingsSnapshot profiles = SolverSettings.Capture();
        JsonObject settings = CaptureDiagnosticSettings();
        Player? localPlayer = LocalContext.GetMe(state);
        string stateText = localPlayer?.PlayerCombatState == null
            ? string.Empty
            : ContinuationStamp.CaptureLive(state).StateText;
        ulong captureFrame = Godot.Engine.GetProcessFrames();
        int historyEntryCount = CombatManager.Instance.History.Entries.Count();
        ForensicCombatCapture combatCapture;
        if (session.CachedCombatCapture != null
            && session.CachedCombatCaptureFrame == captureFrame
            && session.CachedCombatHistoryCount == historyEntryCount
            && session.CachedCombatProfiles == profiles
            && string.Equals(session.CachedCombatStateText, stateText, StringComparison.Ordinal))
        {
            combatCapture = session.CachedCombatCapture;
        }
        else
        {
            combatCapture = new ForensicCombatCapture(
                CaptureMetadataState(state, profiles),
                CaptureReplayState(state),
                CaptureNativeCombatState(state),
                CaptureInMemoryRunSave());
            session.CachedCombatCaptureFrame = captureFrame;
            session.CachedCombatStateText = stateText;
            session.CachedCombatHistoryCount = historyEntryCount;
            session.CachedCombatProfiles = profiles;
            session.CachedCombatCapture = combatCapture;
        }

        string route = DescribeRoute(result);
        string controlMode = SolverController.ControlModeForBugReport;
        int? lastSolverDeployedTurn = SolverController.LastSolverDeployedTurnForBugReport;
        object searchProfiles = new
        {
            profiles.Profile,
        };
        object? metadataResult = result == null ? null : new
        {
            result.StartTurnNumber,
            result.SearchedTurns,
            result.BattleHpLostSoFar,
            result.ProjectedBattleHpLost,
            result.BattlePotionsUsedSoFar,
            plannedPotionCount = result.PotionCount,
            result.TheftPolicy,
            result.OutstandingStolenResource,
            result.CombatEndedTurn,
            result.DeathTurn,
            result.BoundaryReason,
            result.OnlyDeathRoutesFound,
            plannedActions = result.BestNode.Actions,
            turnSetupChoices = result.TurnSetupChoices,
        };
        object? replayResult = result == null ? null : new
        {
            result.StartTurnNumber,
            result.SearchedTurns,
            result.BattleHpLostSoFar,
            result.ProjectedBattleHpLost,
            result.BattlePotionsUsedSoFar,
            plannedPotionCount = result.PotionCount,
            result.TheftPolicy,
            result.OutstandingStolenResource,
            result.CombatEndedTurn,
            result.DeathTurn,
            result.BoundaryReason,
        };
        ForensicCheckpointCapture capture = new(
            sequence,
            session.Recording?.EventCursor,
            CombatManager.Instance.IsInProgress
                && localPlayer?.PlayerCombatState?.Phase.ToString() == "Play"
                && !RunManager.Instance.ActionExecutor.IsRunning
                && !CombatManager.Instance.EndingPlayerTurnPhaseOne
                && !CombatManager.Instance.EndingPlayerTurnPhaseTwo,
            label,
            capturedAt,
            session.SearchRootId,
            stateText,
            combatCapture,
            settings,
            label.StartsWith("search_request_", StringComparison.Ordinal)
                ? CaptureEffectivePolicy(state, profiles)
                : session.LatestEffectivePolicy ?? CaptureEffectivePolicy(state, profiles),
            session.Outcome.Capture(state, label == "combat_end"),
            searchProfiles,
            metadataResult,
            replayResult,
            result != null,
            route,
            replanAudit,
            controlMode,
            lastSolverDeployedTurn);
        if (capture.Searchable)
        {
            session.LastSearchableRootId = $"{session.SessionId}:{sequence}";
            session.LastSearchableEventCursor = session.Recording?.EventCursor ?? -1;
        }
        QueueCheckpointWrite(session, capture);
    }

    private static ForensicMetadataStateCapture CaptureMetadataState(
        CombatState state,
        SolverSettingsSnapshot profiles)
    {
        Player? localPlayer = LocalContext.GetMe(state);
        return new ForensicMetadataStateCapture(
            state.Encounter?.Id.Entry,
            state.RoundNumber,
            state.CurrentSide.ToString(),
            localPlayer?.PlayerCombatState?.TurnNumber,
            localPlayer?.PlayerCombatState?.Phase.ToString(),
            state.RunState.AscensionLevel,
            state.RunState.CurrentActIndex,
            state.RunState.ActFloor,
            state.RunState.TotalFloor,
            SolverDiagnostics.DescribeStart(
                state,
                profiles.Profile),
            state.RunState.Rng.ToSerializable(),
            state.Players.Select(player => (object)new
            {
                netId = player.NetId,
                characterId = player.Character.Id.Entry,
                currentHp = player.Creature.CurrentHp,
                maxHp = player.Creature.MaxHp,
                rng = player.PlayerRng.ToSerializable(),
                odds = player.PlayerOdds.ToSerializable(),
            }).ToArray());
    }

    private static void QueueCheckpointWrite(
        ForensicSession session,
        ForensicCheckpointCapture capture)
    {
        _ = QueueBackground(() =>
        {
            try
            {
                byte[] runSave = SerializeInMemoryRunSave(capture.Combat.InMemoryRunSave);
                if (capture.Label == "combat_start" && session.InMemoryRunSave == null)
                    session.InMemoryRunSave = new CapturedFile("in-memory", runSave);
                ForensicCheckpoint checkpoint = new(
                    capture.Sequence,
                    capture.EventCursor,
                    capture.Searchable,
                    capture.Label,
                    SerializeSnapshotToUtf8Bytes(BuildCheckpointMetadata(session, capture)),
                    SerializeSnapshotToUtf8Bytes(BuildReplayState(capture)),
                    SerializeNativeCombatState(capture.Combat.NativeCombatState),
                    runSave);
                if ((long)checkpoint.MetadataJsonUtf8.Length + checkpoint.ReplayStateJsonUtf8.Length
                    + checkpoint.NativeCombatState.Length + checkpoint.InMemoryRunSave.Length > 8L * 1024 * 1024)
                    throw new InvalidDataException("checkpoint_serialized_size_limit");
                session.Checkpoints.Add(checkpoint);
                RetainKeyCheckpoints(session);
                UpdateSessionResult(
                    session,
                    capture.HasResult,
                    capture.Route,
                    capture.ReplanAudit,
                    capture.ControlMode,
                    capture.LastSolverDeployedTurn);
            }
            catch (Exception ex)
            {
                RegisterBackgroundFailure(session, capture.Label, ex);
            }
            finally { Interlocked.Decrement(ref session.PendingCheckpointWrites); }
            return true;
        });
    }

    private static object BuildCheckpointMetadata(
        ForensicSession session,
        ForensicCheckpointCapture capture)
    {
        ForensicMetadataStateCapture state = capture.Combat.MetadataState;
        return new
        {
            schemaVersion = 3,
            checkpointId = $"{session.SessionId}:{capture.Sequence}",
            sequence = capture.Sequence,
            eventCursor = capture.EventCursor,
            searchable = capture.Searchable,
            searchRootId = capture.SearchRootId,
            sessionId = session.SessionId,
            label = capture.Label,
            capturedAt = capture.CapturedAt,
            encounterId = state.EncounterId,
            round = state.RoundNumber,
            side = state.CurrentSide,
            playerTurn = state.PlayerTurn,
            playerPhase = state.PlayerPhase,
            ascension = state.AscensionLevel,
            actIndex = state.CurrentActIndex,
            actFloor = state.ActFloor,
            totalFloor = state.TotalFloor,
            exactContinuationState = capture.StateText,
            readableDiagnostic = state.ReadableDiagnostic,
            runRng = state.RunRng,
            players = state.Players,
            settings = capture.Settings,
            effectivePolicy = capture.EffectivePolicy,
            outcome = capture.Outcome,
            result = capture.MetadataResult,
            route = capture.Route,
            replanAudit = capture.ReplanAudit,
            controlMode = capture.ControlMode,
            lastSolverDeployedTurn = capture.LastSolverDeployedTurn,
        };
    }

    private static object BuildReplayState(ForensicCheckpointCapture capture)
    {
        ForensicReplayStateCapture state = capture.Combat.ReplayState;
        return new
        {
            schemaVersion = 1,
            capturedAt = capture.CapturedAt,
            restorableScope = "diagnostic_checkpoint",
            encounterId = state.EncounterId,
            encounterType = state.EncounterType,
            state.RoundNumber,
            currentSide = state.CurrentSide,
            state.AscensionLevel,
            state.CurrentActIndex,
            state.ActFloor,
            state.TotalFloor,
            exactContinuationState = capture.StateText,
            runRng = state.RunRng,
            settings = capture.Settings,
            effectivePolicy = capture.EffectivePolicy,
            searchProfiles = capture.SearchProfiles,
            actualPotionsUsedThisCombat = state.ActualPotionsUsedThisCombat,
            resultSummary = capture.ReplayResult,
            players = state.Players,
            creatures = state.Creatures,
            history = state.History,
            historyScope = "diagnostic_tail_16_native_events_are_authority",
            route = capture.Route,
        };
    }

    private static Task QueueSessionCompletion(
        ForensicSession session,
        string reason,
        SolverResult? result,
        string replanAudit,
        DateTimeOffset endedAt)
    {
        bool hasResult = result != null;
        string route = DescribeRoute(result);
        string controlMode = SolverController.ControlModeForBugReport;
        int? lastSolverDeployedTurn = SolverController.LastSolverDeployedTurnForBugReport;
        return QueueBackground(() =>
        {
            try
            {
                UpdateSessionResult(
                    session,
                    hasResult,
                    route,
                    replanAudit,
                    controlMode,
                    lastSolverDeployedTurn);
                session.EndedAt = endedAt;
                session.EndReason = reason;
            }
            catch (Exception ex)
            {
                RegisterBackgroundFailure(session, "combat_end", ex);
            }
            finally
            {
                // Every earlier checkpoint write shares this FIFO worker, so reaching the
                // completion item proves no serializer still needs the detached live capture.
                session.CachedCombatCapture = null;
                session.CachedCombatStateText = null;
                session.CachedCombatProfiles = null;
                session.CachedCombatCaptureFrame = 0;
                session.CachedCombatHistoryCount = 0;
                Entry.Logger.Info(
                    $"[CombatSolver/Test] BUG_REPORT_COMBAT_CACHE_RELEASED " +
                    $"session={session.SessionId} checkpoints={session.Checkpoints.Count}");
            }
            return true;
        });
    }

    private static void UpdateSessionResult(
        ForensicSession session,
        bool hasResult,
        string route,
        string replanAudit,
        string controlMode,
        int? lastSolverDeployedTurn)
    {
        if (hasResult)
            session.LastRoute = route;
        if (!string.IsNullOrWhiteSpace(replanAudit))
            session.ReplanAudit = replanAudit;
        session.ControlMode = controlMode;
        session.LastSolverDeployedTurn = lastSolverDeployedTurn;
    }

    private static void RegisterBackgroundFailure(
        ForensicSession session,
        string label,
        Exception exception)
    {
        if (session.BackgroundErrors.Count < 16) session.BackgroundErrors.Enqueue(exception);
        else Interlocked.Increment(ref session.DroppedCaptureErrors);
        session.FirstErrorRootId ??= session.LastSearchableRootId;
        Entry.Logger.Error(
            $"[CombatSolver/Test] BUG_REPORT_CHECKPOINT_FAILURE label={label} exception={exception}");
    }

    private static string DescribeRoute(SolverResult? result)
        => result == null
            ? "当前没有已完成的求解路线。"
            : SolverDiagnostics.DescribeResult(result) + System.Environment.NewLine + result.Format();

    private static ForensicArchiveBundle CaptureForensicBundle(
        ForensicSession? currentSession,
        ForensicSession? recentSession,
        RecordedCombatArchive? recording)
    {
        ForensicSession? selectedSession = currentSession ?? recentSession;
        List<Exception> backgroundErrors = selectedSession?.BackgroundErrors.ToList() ?? [];

        ForensicArchiveSession? current = currentSession == null
            ? null
            : CaptureForensicSession(currentSession, recording);
        ForensicArchiveSession? recent = currentSession != null
            ? null
            : CaptureForensicSession(recentSession, recording);
        string manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            capturedAt = DateTimeOffset.Now,
            diagnosticOnly = backgroundErrors.Count > 0 || recording?.IncompleteReason != null,
            captureErrors = backgroundErrors.Select(error => error.ToString()).ToArray(),
            droppedCaptureErrors = selectedSession?.DroppedCaptureErrors,
            currentSessionId = current?.SessionId,
            recentSessionId = recent?.SessionId,
            currentEncounterId = current?.EncounterId,
            recentEncounterId = recent?.EncounterId,
            checkpointLimitPerCombat = MaximumCheckpoints,
            archivedCheckpointLimit = MaximumArchivedCheckpoints,
            checkpointArtifacts = new[] { "metadata", "replay-state", "native-state", "run-state" },
            checkpointIndexPath = "replay/checkpoint.json",
            replayStateSchemaVersion = 1,
            nativeStateFormat = "MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState",
            currentCombatAvailable = current != null,
            recentCombatAvailable = recent != null,
        }, JsonOptions);
        ForensicArchiveSession? selected = current ?? recent;
        string? selectedSlot = current != null
            ? "current"
            : recent != null
                ? "recent"
                : null;
        ForensicArchiveCheckpoint? selectedCheckpoint = selected?.Checkpoints.LastOrDefault(item => item.Searchable);
        string checkpointJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            bundleId = Guid.NewGuid().ToString("N"),
            diagnosticOnly = backgroundErrors.Count > 0 || recording?.IncompleteReason != null,
            captureErrors = backgroundErrors.Select(error => error.Message).ToArray(),
            sessionId = selected?.SessionId,
            available = selectedCheckpoint != null,
            slot = selectedSlot,
            defaultCheckpointId = selectedCheckpoint == null ? null : $"{selected!.SessionId}:{selectedCheckpoint.Sequence}",
            combatStartCheckpointId = CheckpointIdForLabel(selected, "combat_start"),
            combatEndCheckpointId = CheckpointIdForLabel(selected, "combat_end"),
            searchPolicies = selected?.SearchPolicies,
            searchResults = selected?.SearchResults,
            collection = selectedSession == null ? null : new
            {
                checkpointCount = selectedSession.CaptureCount,
                mainThreadMilliseconds = selectedSession.CaptureTicks * 1000d / System.Diagnostics.Stopwatch.Frequency,
                maximumMainThreadMilliseconds = selectedSession.MaximumCaptureTicks * 1000d / System.Diagnostics.Stopwatch.Frequency,
                peakPendingCheckpoints = selectedSession.PeakPendingCheckpointWrites,
                firstErrorRootId = selectedSession.FirstErrorRootId,
            },
            recording = selected?.Recording == null ? null : new
            {
                format = "native_events_v1",
                complete = selected.Recording.IncompleteReason == null && backgroundErrors.Count == 0,
                incompleteReason = selected.Recording.IncompleteReason,
                eventCount = selected.Recording.Events.EventCount,
                inputOrigins = selected.Recording.InputOrigins,
                collection = new { selected.Recording.CaptureMilliseconds, selected.Recording.MaximumCaptureMilliseconds,
                    selected.Recording.Events.PeakPendingBytes, selected.Recording.Events.WrittenBytes },
                originPath = $"replay/{selectedSlot}/recording/origin.json",
                runSavePath = $"replay/{selectedSlot}/recording/origin.save",
                eventsPath = $"replay/{selectedSlot}/recording/events.jsonl",
            },
            build = new
            {
                solverVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                solverInformationalVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                solverModuleId = Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId,
                gameVersion = typeof(CombatState).Assembly.GetName().Version?.ToString(),
                gameModuleId = typeof(CombatState).Assembly.ManifestModule.ModuleVersionId,
                mods = CombatReplayRecording.CaptureModIdentity(),
            },
            checkpoints = selected?.Checkpoints.Select(item => new
            {
                checkpointId = $"{selected.SessionId}:{item.Sequence}",
                sequence = item.Sequence,
                eventCursor = item.EventCursor,
                label = item.Label,
                materialsComplete = true,
                canSearch = item.Searchable,
                combatEnded = item.Label == "combat_end",
                restorationVerified = false,
                restoreMethod = selected.Recording == null ? "legacy_checkpoint"
                    : selected.Recording.IncompleteReason == null && backgroundErrors.Count == 0 ? "native_events" : "diagnostic_only",
                metadataPath = $"replay/{selectedSlot}/checkpoints/{item.Name}",
                replayStatePath = $"replay/{selectedSlot}/replay-state/{item.Name}",
                nativeStatePath = $"replay/{selectedSlot}/native-state/{Path.GetFileNameWithoutExtension(item.Name)}.bin",
                runStatePath = $"replay/{selectedSlot}/run-state/{Path.GetFileNameWithoutExtension(item.Name)}.save",
            }).ToArray(),
        }, JsonOptions);
        return new ForensicArchiveBundle(manifest, checkpointJson, current, recent);
    }

    private static ForensicArchiveSession? CaptureForensicSession(ForensicSession? session, RecordedCombatArchive? recording)
    {
        if (session == null)
            return null;
        (ForensicCheckpoint Checkpoint, int Index)[] capturedCheckpoints = session.Checkpoints
            .Select((checkpoint, index) => (checkpoint, index))
            .ToArray();
        (ForensicCheckpoint Checkpoint, int Index)[] selectedCheckpoints = capturedCheckpoints.Length <= MaximumArchivedCheckpoints
            ? capturedCheckpoints
            : capturedCheckpoints.Take(1)
                .Concat(capturedCheckpoints.TakeLast(MaximumArchivedCheckpoints - 1))
                .ToArray();
        string sessionJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            session.SessionId,
            session.EncounterId,
            session.EncounterType,
            session.Seed,
            session.StartedAt,
            session.EndedAt,
            session.EndReason,
            session.ControlMode,
            session.LastSolverDeployedTurn,
            capturedCheckpointCount = session.Checkpoints.Count,
            checkpointCount = selectedCheckpoints.Length,
            checkpointArtifacts = new[] { "metadata", "replay-state", "native-state", "run-state" },
            inMemoryRunSaveCaptured = session.InMemoryRunSave != null,
            uncompressedBytes = new
            {
                metadata = selectedCheckpoints.Sum(item => (long)item.Checkpoint.MetadataJsonUtf8.Length),
                replayState = selectedCheckpoints.Sum(item => (long)item.Checkpoint.ReplayStateJsonUtf8.Length),
                nativeState = selectedCheckpoints.Sum(item => (long)item.Checkpoint.NativeCombatState.Length),
                runState = selectedCheckpoints.Sum(item => (long)item.Checkpoint.InMemoryRunSave.Length),
                recordedEvents = recording?.Events.WrittenBytes,
                recordedOrigin = recording?.Origin.RunSave.LongLength,
            },
            diagnosticLogs = "diagnostics/logs/index.json",
        }, JsonOptions);
        IReadOnlyList<ForensicArchiveCheckpoint> checkpoints = selectedCheckpoints
            .Select(item => new ForensicArchiveCheckpoint(
                $"{item.Checkpoint.Sequence:D6}-{SanitizeFileName(item.Checkpoint.Label)}.json",
                item.Checkpoint.Sequence,
                item.Checkpoint.EventCursor,
                item.Checkpoint.Searchable,
                item.Checkpoint.Label,
                item.Checkpoint.MetadataJsonUtf8,
                item.Checkpoint.ReplayStateJsonUtf8,
                item.Checkpoint.NativeCombatState,
                item.Checkpoint.InMemoryRunSave))
            .ToArray();
        return new ForensicArchiveSession(
            session.SessionId,
            session.EncounterId,
            sessionJson,
            checkpoints,
            session.InMemoryRunSave,
            session.LastRoute,
            session.ReplanAudit,
            recording,
            session.SearchPolicies.ToArray(),
            session.SearchResults.ToArray());
    }

    private static string CaptureExportContext(CombatState? state)
    {
        IRunState? runState = state?.RunState;
        if (runState == null && RunManager.Instance.IsInProgress)
            runState = RunManager.Instance.DebugOnlyGetState();
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            capturedAt = DateTimeOffset.Now,
            combatActive = state != null,
            runActive = runState != null,
            runSeed = runState?.Rng.StringSeed,
            runRng = runState?.Rng.ToSerializable(),
            players = runState?.Players.Select(player => new
            {
                netId = player.NetId,
                characterId = player.Character.Id.Entry,
                currentHp = player.Creature.CurrentHp,
                maxHp = player.Creature.MaxHp,
                rng = player.PlayerRng.ToSerializable(),
                odds = player.PlayerOdds.ToSerializable(),
            }).ToArray(),
            currentForensicSessionId = _currentSession?.SessionId,
            recentForensicSessionId = _lastSession?.SessionId,
            currentControlMode = _currentSession?.ControlMode,
            recentControlMode = _lastSession?.ControlMode,
            currentLastSolverDeployedTurn = _currentSession?.LastSolverDeployedTurn,
            recentLastSolverDeployedTurn = _lastSession?.LastSolverDeployedTurn,
        }, JsonOptions);
    }

    private static object CaptureEffectivePolicy(CombatState state, SolverSettingsSnapshot settings)
        => new
        {
            settings.PotionPolicy,
            potionDirectives = LocalContext.GetMe(state) is { } player
                ? Enumerable.Range(0, player.PotionSlots.Count)
                    .Where(slot => player.GetPotionAtSlotIndex(slot) != null)
                    .Select(slot => new
                    {
                        slot,
                        potionId = player.GetPotionAtSlotIndex(slot)!.Id.Entry,
                        directive = SolverController.ResolvePotionDirective(
                            state, slot, player.GetPotionAtSlotIndex(slot)!.Id.Entry),
                    }).ToArray()
                : [],
            settings.ActTransitionBossHpStrategy,
            settings.FinalBossHpStrategy,
            settings.AcceptableBattleHpLoss,
            settings.StopAtAcceptableBattleHpLoss,
            settings.GrowthBudgets,
            settings.RelicStrategyEnabled,
            settings.RelicCounterRules,
            settings.BrightestFlameMaxHpLossLimit,
            settings.IgnoreLongTermRewards,
            searchMaxDegreeOfParallelism = UnattendedTestRunner.SearchMaxDegreeOfParallelismOverride ?? settings.SearchMaxDegreeOfParallelism,
            profile = settings.Profile with { SoftTimeBudgetMilliseconds = UnattendedTestRunner.SearchBudgetOverrideMilliseconds
                ?? settings.Profile.SoftTimeBudgetMilliseconds },
            fixedBudget = UnattendedTestRunner.FixedSearchBudget,
            performancePreset = SolverSettings.Current.PerformancePreset,
        };

    internal static string? LastCompletedSearchRootId => _currentSession?.LastCompletedSearchRootId;
    internal static string? CurrentSearchRootId => _currentSession?.SearchRootId;
    internal static object? LatestEffectivePolicy => _currentSession?.LatestEffectivePolicy;
    internal static void RecordComparisonReference(string? checkpointId)
    {
        if (_currentSession != null)
            _currentSession.ComparisonReferenceRootId = checkpointId;
    }
    internal static CombatReplayOutcomeSnapshot CaptureOutcome(CombatState state)
        => (_currentSession ?? _lastSession
            ?? throw new InvalidOperationException("Missing forensic outcome session.")).Outcome.Capture(
                state, !CombatManager.Instance.IsInProgress);

    internal static void ResetOutcomeAtRestoredRoot(CombatState state)
    {
        ForensicSession session = _currentSession
            ?? throw new InvalidOperationException("Missing imported forensic session.");
        session.Outcome.Dispose();
        session.Outcome = new CombatReplayOutcome(state);
    }

    internal static void RecordSearchPolicy(CombatState state, SearchPolicySnapshot policy)
    {
        ForensicSession? session = _currentSession;
        if (session == null)
            return;
        JsonObject captured = JsonSerializer.SerializeToNode(CaptureEffectivePolicy(state, SolverSettings.Capture()), JsonOptions)!.AsObject();
        captured["profile"] = JsonSerializer.SerializeToNode(policy.Profile with
        {
            SoftTimeBudgetMilliseconds = policy.BudgetOverrideMilliseconds ?? policy.Profile.SoftTimeBudgetMilliseconds,
        }, JsonOptions);
        captured["fixedBudget"] = policy.FixedBudget;
        captured["searchMaxDegreeOfParallelism"] = policy.MaxDegreeOfParallelism;
        captured["includeTurnSetup"] = policy.IncludeTurnSetup;
        session.LatestEffectivePolicy = captured;
        object record = new { checkpointId = session.SearchRootId, eventCursor = session.Recording?.EventCursor, policy = captured };
        _ = QueueBackground(() => { session.SearchPolicies.Add(record); return true; });
    }

    private static string? CheckpointIdForLabel(ForensicArchiveSession? session, string label)
    {
        ForensicArchiveCheckpoint? checkpoint = session?.Checkpoints.LastOrDefault(item => item.Label == label);
        return checkpoint == null ? null : $"{session!.SessionId}:{checkpoint.Sequence}";
    }

    private static void RetainKeyCheckpoints(ForensicSession session)
    {
        List<ForensicCheckpoint> checkpoints = session.Checkpoints;
        if (checkpoints.Count <= MaximumCheckpoints)
            return;
        HashSet<ForensicCheckpoint> retained = [];
        void Keep(ForensicCheckpoint? checkpoint)
        {
            if (checkpoint != null)
                retained.Add(checkpoint);
        }
        Keep(checkpoints.FirstOrDefault(item => item.Label == "combat_start"));
        Keep(checkpoints.FirstOrDefault(item => item.Searchable));
        Keep(checkpoints.LastOrDefault(item => item.Searchable));
        Keep(checkpoints.FirstOrDefault(item => $"{session.SessionId}:{item.Sequence}" == session.FirstErrorRootId));
        string? reference = session.ComparisonReferenceRootId ?? session.LastCompletedSearchRootId;
        Keep(checkpoints.LastOrDefault(item => $"{session.SessionId}:{item.Sequence}" == reference));
        Keep(checkpoints.Last());
        foreach (ForensicCheckpoint checkpoint in checkpoints.AsEnumerable().Reverse())
        {
            if (retained.Count >= MaximumCheckpoints)
                break;
            Keep(checkpoint);
        }
        checkpoints.RemoveAll(item => !retained.Contains(item));
    }

    private static string CaptureSettingsForBugReport() => CaptureDiagnosticSettings().ToJsonString(JsonOptions);

    private static JsonObject CaptureDiagnosticSettings()
    {
        JsonObject settings = JsonSerializer.SerializeToNode(SolverSettings.Current, JsonOptions)?.AsObject()
            ?? throw new InvalidDataException("求解器设置无法序列化为问题包。");
        HashSet<string> fields = new(StringComparer.Ordinal)
        {
            "solverDisabled", "automaticCalculationEnabled", "stopFullAutoOnCombatEnd", "stopFullAutoOnDeathTurn", "relicStrategyEnabled", "relicCounterRules",
            "stopFullAutoOnWorseRecalculation", "enableDetailedDiagnosticLogs", "potionDirectives",
            "actTransitionBossHpStrategy", "finalBossHpStrategy", "acceptableBattleHpLoss", "stopAtAcceptableBattleHpLoss", "growthBudgets", "brightestFlameMaxHpLossLimit", "performancePreset",
            "searchMaxDegreeOfParallelism", "shortTimeLimitSeconds", "deepTimeLimitSeconds", "enableNoGcRegion",
            "noGcRegionBudgetGigabytes", "shortBeamWidth", "deepBeamWidth", "shortMaxExpandedNodes", "deepMaxExpandedNodes",
            "shortMaxCardBranchesPerNode", "deepMaxCardBranchesPerNode", "shortMaxPileChoiceBranchesPerAction",
            "deepMaxPileChoiceBranchesPerAction", "shortMaxHandChoiceBranchesPerAction", "deepMaxHandChoiceBranchesPerAction",
            "deploymentFastMode", "deploymentInterActionDelaySeconds",
        };
        foreach (string key in settings.Select(item => item.Key).Where(key => !fields.Contains(key)).ToArray())
            settings.Remove(key);
        return settings;
    }

    private static string CaptureCombatState(
        CombatState? state,
        SolverSettingsSnapshot profiles)
    {
        if (state == null)
        {
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                capturedAt = DateTimeOffset.Now,
                modVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                combatActive = false,
                recentForensicSessionId = _lastSession?.SessionId,
                recentEncounterId = _lastSession?.EncounterId,
            }, JsonOptions);
        }

        Player? player = LocalContext.GetMe(state);
        string diagnostic = SolverDiagnostics.DescribeStart(
            state,
            profiles.Profile);
        string exactState = player?.PlayerCombatState == null
            ? string.Empty
            : ContinuationStamp.CaptureLive(state).StateText;
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            capturedAt = DateTimeOffset.Now,
            modVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            combatActive = true,
            encounterId = state.Encounter?.Id.Entry,
            encounterType = state.Encounter?.RoomType.ToString(),
            round = state.RoundNumber,
            side = state.CurrentSide.ToString(),
            playerTurn = player?.PlayerCombatState?.TurnNumber,
            playerPhase = player?.PlayerCombatState?.Phase.ToString(),
            ascension = state.RunState.AscensionLevel,
            actIndex = state.RunState.CurrentActIndex,
            actFloor = state.RunState.ActFloor,
            totalFloor = state.RunState.TotalFloor,
            exactContinuationState = exactState,
            readableDiagnostic = diagnostic,
            runRng = state.RunState.Rng.ToSerializable(),
            playerRng = player?.PlayerRng.ToSerializable(),
            playerOdds = player?.PlayerOdds.ToSerializable(),
        }, JsonOptions);
    }

    private static ForensicReplayStateCapture CaptureReplayState(CombatState state)
    {
        object[] players = state.Players.Select(player =>
        {
            PlayerCombatState? combat = player.PlayerCombatState;
            CardPile[] piles = combat == null
                ? []
                : [combat.Hand, combat.DrawPile, combat.DiscardPile, combat.ExhaustPile, combat.PlayPile];
            return (object)new
            {
                netId = player.NetId,
                characterId = player.Character.Id.Entry,
                player.Creature.CombatId,
                player.Creature.CurrentHp,
                player.Creature.MaxHp,
                player.Creature.Block,
                player.Gold,
                turnNumber = combat?.TurnNumber,
                phase = combat?.Phase.ToString(),
                energy = combat?.Energy,
                maxEnergy = combat?.MaxEnergy,
                stars = combat?.Stars,
                maxPotionCount = player.MaxPotionCount,
                rng = player.PlayerRng.ToSerializable(),
                odds = player.PlayerOdds.ToSerializable(),
                piles = piles.Select(pile => new
                {
                    pile = pile.Type.ToString(),
                    cards = pile.Cards.Select((card, index) => CaptureCard(card, index)).ToArray(),
                }).ToArray(),
                potions = Enumerable.Range(0, player.PotionSlots.Count).Select(slot =>
                {
                    PotionModel? potion = player.GetPotionAtSlotIndex(slot);
                    return new
                    {
                        slot,
                        id = potion?.Id.Entry,
                        runtimeType = potion?.GetType().FullName,
                        fields = potion == null ? null : CaptureObjectFields(potion),
                    };
                }).ToArray(),
                relics = player.Relics.Select(relic => new
                {
                    id = relic.Id.Entry,
                    runtimeType = relic.GetType().FullName,
                    relic.IsMelted,
                    fields = CaptureObjectFields(relic),
                }).ToArray(),
                orbs = combat == null ? null : new
                {
                    combat.OrbQueue.Capacity,
                    items = combat.OrbQueue.Orbs.Select((orb, index) => new
                    {
                        index,
                        id = orb.Id.Entry,
                        runtimeType = orb.GetType().FullName,
                        passive = orb.PassiveVal,
                        evoke = orb.EvokeVal,
                        fields = CaptureObjectFields(orb),
                    }).ToArray(),
                },
            };
        }).ToArray();

        object[] creatures = state.Creatures.Select((creature, index) => new
        {
            index,
            creature.CombatId,
            creature.SlotName,
            side = creature.Side.ToString(),
            monsterId = creature.Monster?.Id.Entry,
            playerNetId = creature.Player?.NetId,
            petOwnerNetId = creature.PetOwner?.NetId,
            creature.CurrentHp,
            creature.MaxHp,
            creature.Block,
            creature.IsAlive,
            creature.IsDead,
            creature.IsHittable,
            nextMoveId = creature.Monster?.NextMove?.Id,
            moveStateLog = creature.Monster?.MoveStateMachine?.StateLog
                .Select(move => move.Id)
                .ToArray() ?? [],
            monsterFields = creature.Monster == null ? null : CaptureObjectFields(creature.Monster),
            powers = creature.Powers.Select((power, powerIndex) => new
            {
                index = powerIndex,
                id = power.Id.Entry,
                runtimeType = power.GetType().FullName,
                power.Amount,
                power.AmountOnTurnStart,
                ownerCombatId = power.Owner?.CombatId,
                applierCombatId = power.Applier?.CombatId,
                targetCombatId = power.Target?.CombatId,
                dynamicVars = power.DynamicVars.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        item => item.Key,
                        item => (object)new
                        {
                            runtimeType = item.Value.GetType().FullName,
                            item.Value.BaseValue,
                            item.Value.IntValue,
                        },
                        StringComparer.Ordinal),
                fields = CaptureObjectFields(power),
            }).ToArray(),
            creatureFields = CaptureObjectFields(creature),
        }).Cast<object>().ToArray();

        object[] history = CombatManager.Instance.History.Entries.TakeLast(16)
            .Select((entry, index) => new
            {
                index,
                runtimeType = entry.GetType().FullName,
                fields = CaptureObjectFields(entry),
            })
            .Cast<object>()
            .ToArray();
        return new ForensicReplayStateCapture(
            state.Encounter?.Id.Entry,
            state.Encounter?.RoomType.ToString(),
            state.RoundNumber,
            state.CurrentSide.ToString(),
            state.RunState.AscensionLevel,
            state.RunState.CurrentActIndex,
            state.RunState.ActFloor,
            state.RunState.TotalFloor,
            state.RunState.Rng.ToSerializable(),
            CombatManager.Instance.History.Entries
                .Count(entry => entry.GetType().Name == "PotionUsedEntry"),
            players,
            creatures,
            history);
    }

    private static object CaptureCard(CardModel card, int index)
    {
        SerializableCard serialized = card.ToSerializable();
        return new
        {
            index,
            id = card.Id.Entry,
            runtimeType = card.GetType().FullName,
            serialized,
            card.CurrentUpgradeLevel,
            energyCost = new
            {
                card.EnergyCost.Canonical,
                withModifiers = card.EnergyCost.GetWithModifiers(CostModifiers.All),
                card.EnergyCost.CostsX,
                fields = CaptureObjectFields(card.EnergyCost),
            },
            canonicalStarCost = card.CanonicalStarCost,
            starCostWithModifiers = card.GetStarCostWithModifiers(),
            keywords = card.Keywords.Select(keyword => keyword.ToString()).ToArray(),
            affliction = card.Affliction == null ? null : new
            {
                id = card.Affliction.Id.Entry,
                runtimeType = card.Affliction.GetType().FullName,
                card.Affliction.Amount,
                fields = CaptureObjectFields(card.Affliction),
            },
            enchantment = card.Enchantment == null ? null : new
            {
                id = card.Enchantment.Id.Entry,
                runtimeType = card.Enchantment.GetType().FullName,
                card.Enchantment.Amount,
                status = card.Enchantment.Status.ToString(),
                fields = CaptureObjectFields(card.Enchantment),
            },
            dynamicVars = card.DynamicVars.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(
                    item => item.Key,
                    item => (object)new
                    {
                        runtimeType = item.Value.GetType().FullName,
                        item.Value.BaseValue,
                        item.Value.IntValue,
                    },
                    StringComparer.Ordinal),
            fields = CaptureObjectFields(card),
        };
    }

    private static CapturedObjectFields CaptureObjectFields(object source)
    {
        CapturedFieldDescriptor[] plan = ObjectFieldPlans.GetOrAdd(
            source.GetType(),
            static type => BuildObjectFieldPlan(type));
        List<CapturedObjectField> fields = new(plan.Length);
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance) { source };
        foreach (CapturedFieldDescriptor descriptor in plan)
        {
            fields.Add(new CapturedObjectField(
                descriptor.Name,
                SnapshotFieldValue(descriptor.Field.GetValue(source), depth: 0, visited)));
        }
        return new CapturedObjectFields(fields);
    }

    private static CapturedFieldDescriptor[] BuildObjectFieldPlan(Type sourceType)
    {
        List<CapturedFieldDescriptor> plan = [];
        for (Type? type = sourceType; type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (ShouldCaptureField(field))
                    plan.Add(new CapturedFieldDescriptor($"{type.Name}.{field.Name}", field));
            }
        }
        return plan.ToArray();
    }

    private static CapturedFieldDescriptor[] BuildNestedFieldPlan(Type type)
        => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(ShouldCaptureField)
            .Select(field => new CapturedFieldDescriptor(field.Name, field))
            .ToArray();

    private static bool ShouldCaptureField(FieldInfo field)
        => !field.IsStatic
           && !typeof(Delegate).IsAssignableFrom(field.FieldType)
           && !typeof(GodotObject).IsAssignableFrom(field.FieldType);

    private static object? SnapshotFieldValue(object? value, int depth, HashSet<object> visited)
    {
        if (value == null)
            return null;
        Type type = value.GetType();
        if (type.IsPrimitive || value is decimal or string or DateTime or DateTimeOffset or Guid)
            return value;
        if (value is Enum or Type)
            return value.ToString();
        if (value is ModelId modelId)
            return modelId.ToString();
        if (value is Creature creature)
        {
            return new
            {
                creature.CombatId,
                monsterId = creature.Monster?.Id.Entry,
                playerNetId = creature.Player?.NetId,
            };
        }
        if (value is Player player)
            return new { player.NetId, characterId = player.Character.Id.Entry };
        if (value is AbstractModel model)
            return new { id = model.Id.Entry, runtimeType = model.GetType().FullName };
        if (depth >= 2 || !visited.Add(value))
            return new { diagnosticOnly = true, reason = "depth_or_reference_limit", runtimeType = type.FullName };
        if (value is IDictionary dictionary)
        {
            List<object?> entries = [];
            int count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= 256)
                {
                    entries.Add(new { truncated = true, reason = "collection_limit", countAtLeast = count });
                    break;
                }
                entries.Add(new
                {
                    key = SnapshotFieldValue(entry.Key, depth + 1, visited),
                    value = SnapshotFieldValue(entry.Value, depth + 1, visited),
                });
            }
            return entries;
        }
        if (value is IEnumerable enumerable && value is not string)
        {
            List<object?> items = [];
            int count = 0;
            foreach (object? item in enumerable)
            {
                if (count++ >= 256)
                {
                    items.Add(new { truncated = true, reason = "collection_limit", countAtLeast = count });
                    break;
                }
                items.Add(SnapshotFieldValue(item, depth + 1, visited));
            }
            return items;
        }
        CapturedFieldDescriptor[] plan = NestedFieldPlans.GetOrAdd(
            type,
            static nestedType => BuildNestedFieldPlan(nestedType));
        List<CapturedObjectField> nested = new(plan.Length);
        foreach (CapturedFieldDescriptor descriptor in plan)
        {
            nested.Add(new CapturedObjectField(
                descriptor.Name,
                SnapshotFieldValue(descriptor.Field.GetValue(value), depth + 1, visited)));
        }
        return new CapturedObjectFields(nested);
    }

    private static NetFullCombatState CaptureNativeCombatState(CombatState state)
        => NetFullCombatState.FromRun(state.RunState, justFinishedAction: null);

    private static byte[] SerializeNativeCombatState(NetFullCombatState native)
    {
        PacketWriter writer = new() { WarnOnGrow = false };
        native.Serialize(writer);
        writer.ZeroByteRemainder();
        return writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
    }

    private static SerializableRun CaptureInMemoryRunSave()
        => RunManager.Instance.ToSave(null);

    private static byte[] SerializeInMemoryRunSave(SerializableRun save)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            save,
            JsonSerializationUtility.GetTypeInfo<SerializableRun>());
        if (bytes.LongLength > MaximumCapturedSaveBytes)
        {
            throw new InvalidDataException(
                $"内存跑局快照超过上限：{bytes.LongLength} bytes。");
        }
        return bytes;
    }

    private static byte[] SerializeSnapshotToUtf8Bytes(object snapshot)
        => JsonSerializer.SerializeToUtf8Bytes(snapshot, snapshot.GetType(), JsonOptions);

    private static byte[] ReadSharedFile(string path, long maximumBytes)
    {
        using FileStream input = new(
            path,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > maximumBytes)
            throw new InvalidDataException($"取证文件超过上限：{path} ({input.Length} bytes)。");
        using MemoryStream output = new((int)input.Length);
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void StartBackgroundThread()
    {
        Thread worker = new(() =>
        {
            foreach (Action operation in BackgroundOperations.GetConsumingEnumerable())
                operation();
        })
        {
            IsBackground = true,
            Name = "CombatSolver Bug Report Writer",
            Priority = ThreadPriority.BelowNormal,
        };
        worker.Start();
    }

    private static Task<T> QueueBackground<T>(Func<T> operation)
    {
        TaskCompletionSource<T> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BackgroundOperations.Add(() =>
        {
            try
            {
                completion.SetResult(operation());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private static void AddText(ZipArchive archive, string name, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using Stream output = entry.Open();
        using StreamWriter writer = new(output, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void AddFile(
        ZipArchive archive,
        string path,
        string entryName,
        long maximumBytes = long.MaxValue)
    {
        using FileStream input = new(
            path,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"取证文件超过上限：{Path.GetFileName(path)} ({input.Length} bytes)。");
        }
        AddStream(archive, entryName, input);
    }

    private static void AddBytes(ZipArchive archive, string entryName, byte[] bytes)
    {
        using MemoryStream input = new(bytes, writable: false);
        AddStream(archive, entryName, input);
    }

    private static void AddStream(ZipArchive archive, string entryName, Stream input)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using Stream output = entry.Open();
        input.CopyTo(output);
    }

    private static string DefaultExportDirectory()
    {
        string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            throw new DirectoryNotFoundException("无法定位桌面目录。");
        return Path.Combine(desktop, ExportFolderName);
    }

    private static string SanitizeFileName(string value)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string sanitized = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}
