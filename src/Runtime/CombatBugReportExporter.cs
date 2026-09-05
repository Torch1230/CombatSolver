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
    private const long MaximumLogBytes = 2L * 1024 * 1024;
    private const long MaximumCombatLogBytes = 4L * 1024 * 1024;
    private const long MaximumCapturedSaveBytes = 4L * 1024 * 1024;
    private const int MaximumCheckpoints = 6;
    private const int MaximumArchivedCheckpoints = 6;
    private const int MaximumGeneralLogFiles = 1;

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
        string Label,
        byte[] MetadataJsonUtf8,
        byte[] ReplayStateJsonUtf8,
        byte[] NativeCombatState,
        byte[] InMemoryRunSave);
    private sealed record ForensicCheckpointCapture(
        string Label,
        DateTimeOffset CapturedAt,
        string StateText,
        ForensicCombatCapture Combat,
        SolverSettingsData Settings,
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
    private sealed record ForensicLogRange(string Path, string EntryName, long Start, long End);
    private sealed record ForensicArchiveCheckpoint(
        string Name,
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
        public Dictionary<string, long> LogStartOffsets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> LogEndOffsets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string LastRoute { get; set; } = "当前没有已完成的求解路线。";
        public string ReplanAudit { get; set; } = string.Empty;
        public string ControlMode { get; set; } = "solver_only";
        public int? LastSolverDeployedTurn { get; set; }
        public List<Exception> BackgroundErrors { get; } = [];
    }

    private sealed record ForensicArchiveSession(
        string SessionId,
        string EncounterId,
        string SessionJson,
        IReadOnlyList<ForensicArchiveCheckpoint> Checkpoints,
        CapturedFile? InMemoryRunSave,
        IReadOnlyList<ForensicLogRange> Logs,
        string LastRoute,
        string ReplanAudit);

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
            StartedAt = DateTimeOffset.Now,
            UserDataDirectory = userDataDirectory,
        };
        CaptureLogStarts(_currentSession);
        RecordCheckpointCore(state, "combat_start", null, string.Empty);
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
            RecordCheckpointCore(live, "combat_end", result, replanAudit);
        Task completion = QueueSessionCompletion(
            session,
            reason,
            result,
            replanAudit,
            DateTimeOffset.Now,
            CaptureLogEndOffsets(session));
        _lastSession = session;
        _currentSession = null;
        return completion;
    }

    public static Task<string> ExportCurrentAsync(string? outputDirectory = null)
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
        Task<ForensicArchiveBundle> forensicsTask = QueueBackground(
            () => CaptureForensicBundle(currentSession, recentSession));

        string exportDirectory = outputDirectory ?? DefaultExportDirectory();
        Directory.CreateDirectory(exportDirectory);
        string encounter = SanitizeFileName(
            state?.Encounter?.Id.Entry
            ?? recentSession?.EncounterId
            ?? currentSession?.EncounterId
            ?? "no-combat");
        string path = Path.Combine(
            exportDirectory,
            $"CombatSolver-{encounter}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");
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
                environmentJson,
                forensics);
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
        string environmentJson,
        ForensicArchiveBundle forensics)
    {
        using FileStream output = new(path, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);
        string logsDirectory = Path.Combine(userDataDirectory, "logs");
        if (Directory.Exists(logsDirectory))
        {
            int logIndex = 0;
            foreach (string log in EnumerateFilesSafely(logsDirectory, "*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(MaximumGeneralLogFiles))
            {
                AddFileTail(
                    archive,
                    log,
                    $"logs/{logIndex++:D2}-{SanitizeFileName(Path.GetFileName(log))}",
                    MaximumLogBytes);
            }
        }

        string releaseInfo = Path.Combine(executableDirectory, "release_info.json");
        if (File.Exists(releaseInfo))
            AddFile(archive, releaseInfo, "release_info.json");

        AddText(archive, "combat-solver/combat-state.json", combatJson);
        AddText(archive, "combat-solver/current-route.txt", routeText);
        AddText(archive, "combat-solver/replan-audit.txt", replanAudit);
        AddText(archive, "combat-solver/settings.json", settingsJson);
        AddText(archive, "combat-solver/export-context.json", exportContextJson);
        AddText(archive, "combat-solver/environment.json", environmentJson);
        AddText(archive, "combat-solver/forensics/manifest.json", forensics.ManifestJson);
        AddText(archive, "combat-solver/checkpoint.json", forensics.CheckpointJson);
        WriteForensicSession(archive, "current", forensics.Current);
        WriteForensicSession(archive, "recent", forensics.Recent);
        AddText(
            archive,
            "combat-solver/README.txt",
            "此问题包由 CombatSolver 设置页导出。\n" +
            "问题包只保存当前战斗；离开战斗后提交时则只保存最近结束的一场，不会重复附带更早楼层。归档保留最近关键的 6 个检查点。\n" +
            "replay-state 是可机器读取的完整中途战斗夹具，含有序牌堆、逐牌存档/动态状态、Power/遗物/怪物字段、行动历史、阵容和全部 RNG；native-state 是游戏原生 NetFullCombatState；run-state 是该检查点时刻的内存跑局存档。\n" +
            "checkpoint.json 是唯一入口索引，直接指向本包当前可还原检查点的四个文件；导入器应按索引读取，不要扫描或猜测检查点文件名。\n" +
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
        string root = $"combat-solver/forensics/{slot}";
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
        AddText(archive, $"{root}/last-route.txt", session.LastRoute);
        AddText(archive, $"{root}/replan-audit.txt", session.ReplanAudit);
        foreach (ForensicLogRange log in session.Logs)
        {
            AddFileRange(
                archive,
                log.Path,
                $"{root}/logs/{log.EntryName}",
                log.Start,
                log.End,
                MaximumCombatLogBytes);
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
        CombatState state,
        string label,
        SolverResult? result,
        string replanAudit)
    {
        ForensicSession session = _currentSession
            ?? throw new InvalidOperationException("记录战斗取证检查点时没有活动会话。");
        DateTimeOffset capturedAt = DateTimeOffset.Now;
        SolverSettingsSnapshot profiles = SolverSettings.Capture();
        SolverSettingsData settings = SolverSettings.Current;
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
            profiles.ShortProfile,
            profiles.DeepProfile,
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
            label,
            capturedAt,
            stateText,
            combatCapture,
            settings,
            searchProfiles,
            metadataResult,
            replayResult,
            result != null,
            route,
            replanAudit,
            controlMode,
            lastSolverDeployedTurn);
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
                profiles.ShortProfile,
                profiles.DeepProfile),
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
                    capture.Label,
                    SerializeSnapshotToUtf8Bytes(BuildCheckpointMetadata(session, capture)),
                    SerializeSnapshotToUtf8Bytes(BuildReplayState(capture)),
                    SerializeNativeCombatState(capture.Combat.NativeCombatState),
                    runSave);
                if (session.Checkpoints.Count >= MaximumCheckpoints)
                    session.Checkpoints.RemoveAt(1);
                session.Checkpoints.Add(checkpoint);
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
            restorableScope = "mid_combat_checkpoint",
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
            searchProfiles = capture.SearchProfiles,
            actualPotionsUsedThisCombat = state.ActualPotionsUsedThisCombat,
            resultSummary = capture.ReplayResult,
            players = state.Players,
            creatures = state.Creatures,
            history = state.History,
            route = capture.Route,
        };
    }

    private static Task QueueSessionCompletion(
        ForensicSession session,
        string reason,
        SolverResult? result,
        string replanAudit,
        DateTimeOffset endedAt,
        IReadOnlyDictionary<string, long> logEndOffsets)
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
                foreach ((string path, long end) in logEndOffsets)
                    session.LogEndOffsets[path] = end;
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
        session.BackgroundErrors.Add(exception);
        Entry.Logger.Error(
            $"[CombatSolver/Test] BUG_REPORT_CHECKPOINT_FAILURE label={label} exception={exception}");
    }

    private static string DescribeRoute(SolverResult? result)
        => result == null
            ? "当前没有已完成的求解路线。"
            : SolverDiagnostics.DescribeResult(result) + System.Environment.NewLine + result.Format();

    private static ForensicArchiveBundle CaptureForensicBundle(
        ForensicSession? currentSession,
        ForensicSession? recentSession)
    {
        ForensicSession? selectedSession = currentSession ?? recentSession;
        List<Exception> backgroundErrors = selectedSession?.BackgroundErrors.ToList() ?? [];
        if (backgroundErrors.Count > 0)
        {
            throw new AggregateException(
                "战斗取证后台写出未能完整完成，问题包未导出。",
                backgroundErrors);
        }

        ForensicArchiveSession? current = currentSession == null
            ? null
            : CaptureForensicSession(currentSession);
        ForensicArchiveSession? recent = currentSession != null
            ? null
            : CaptureForensicSession(recentSession);
        string manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            capturedAt = DateTimeOffset.Now,
            currentSessionId = current?.SessionId,
            recentSessionId = recent?.SessionId,
            currentEncounterId = current?.EncounterId,
            recentEncounterId = recent?.EncounterId,
            checkpointLimitPerCombat = MaximumCheckpoints,
            archivedCheckpointLimit = MaximumArchivedCheckpoints,
            checkpointArtifacts = new[] { "metadata", "replay-state", "native-state", "run-state" },
            checkpointIndexPath = "combat-solver/checkpoint.json",
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
        ForensicArchiveCheckpoint? selectedCheckpoint = selected?.Checkpoints.LastOrDefault();
        string? checkpointName = selectedCheckpoint?.Name;
        string checkpointJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            available = selectedCheckpoint != null,
            slot = selectedSlot,
            checkpoint = checkpointName,
            metadataPath = selectedSlot == null || checkpointName == null
                ? null
                : $"combat-solver/forensics/{selectedSlot}/checkpoints/{checkpointName}",
            replayStatePath = selectedSlot == null || checkpointName == null
                ? null
                : $"combat-solver/forensics/{selectedSlot}/replay-state/{checkpointName}",
            nativeStatePath = selectedSlot == null || checkpointName == null
                ? null
                : $"combat-solver/forensics/{selectedSlot}/native-state/" +
                  $"{Path.GetFileNameWithoutExtension(checkpointName)}.bin",
            runStatePath = selectedSlot == null || checkpointName == null
                ? null
                : $"combat-solver/forensics/{selectedSlot}/run-state/" +
                  $"{Path.GetFileNameWithoutExtension(checkpointName)}.save",
        }, JsonOptions);
        return new ForensicArchiveBundle(manifest, checkpointJson, current, recent);
    }

    private static ForensicArchiveSession? CaptureForensicSession(ForensicSession? session)
    {
        if (session == null)
            return null;
        IReadOnlyList<ForensicLogRange> logs = session.LogStartOffsets
            .Select(item =>
            {
                long end = session.LogEndOffsets.GetValueOrDefault(
                    item.Key,
                    File.Exists(item.Key) ? new FileInfo(item.Key).Length : item.Value);
                return new ForensicLogRange(
                    item.Key,
                    NormalizeEntryPath(Path.GetFileName(item.Key)),
                    item.Value,
                    Math.Max(item.Value, end));
            })
            .ToArray();
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
            logRanges = logs.Select(log => new
            {
                log.EntryName,
                log.Start,
                log.End,
                bytes = log.End - log.Start,
            }),
        }, JsonOptions);
        IReadOnlyList<ForensicArchiveCheckpoint> checkpoints = selectedCheckpoints
            .Select(item => new ForensicArchiveCheckpoint(
                $"{item.Index:D3}-{SanitizeFileName(item.Checkpoint.Label)}.json",
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
            logs,
            session.LastRoute,
            session.ReplanAudit);
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

    private static string CaptureSettingsForBugReport()
    {
        JsonObject settings = JsonSerializer.SerializeToNode(SolverSettings.Current, JsonOptions)?.AsObject()
            ?? throw new InvalidDataException("求解器设置无法序列化为问题包。");
        settings.Remove(JsonNamingPolicy.CamelCase.ConvertName(nameof(SolverSettingsData.ReporterContactQq)));
        return settings.ToJsonString(JsonOptions);
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
            profiles.ShortProfile,
            profiles.DeepProfile);
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

        object[] history = CombatManager.Instance.History.Entries
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
        if (value is IDictionary dictionary)
        {
            List<object?> entries = [];
            int count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= 256)
                    break;
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
                    break;
                items.Add(SnapshotFieldValue(item, depth + 1, visited));
            }
            return items;
        }
        if (depth >= 2 || !visited.Add(value))
            return value.ToString();
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

    private static void CaptureLogStarts(ForensicSession session)
    {
        string logsDirectory = Path.Combine(session.UserDataDirectory, "logs");
        if (!Directory.Exists(logsDirectory))
            return;
        foreach (string log in EnumerateFilesSafely(logsDirectory, "*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(MaximumGeneralLogFiles))
            session.LogStartOffsets[log] = new FileInfo(log).Length;
    }

    private static IReadOnlyDictionary<string, long> CaptureLogEndOffsets(ForensicSession session)
    {
        Dictionary<string, long> offsets = new(StringComparer.OrdinalIgnoreCase);
        foreach (string log in session.LogStartOffsets.Keys)
        {
            if (File.Exists(log))
                offsets[log] = new FileInfo(log).Length;
        }
        return offsets;
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

    private static void AddFileTail(
        ZipArchive archive,
        string path,
        string entryName,
        long maximumBytes)
    {
        using FileStream input = new(
            path,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > maximumBytes)
            input.Seek(-maximumBytes, SeekOrigin.End);
        AddStream(archive, entryName, input);
    }

    private static void AddFileRange(
        ZipArchive archive,
        string path,
        string entryName,
        long start,
        long end,
        long maximumBytes)
    {
        if (!File.Exists(path) || end <= start)
            return;
        using FileStream input = new(
            path,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        end = Math.Min(end, input.Length);
        start = Math.Clamp(start, 0, end);
        if (end - start > maximumBytes)
            start = end - maximumBytes;
        input.Seek(start, SeekOrigin.Begin);
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using Stream output = entry.Open();
        byte[] buffer = new byte[81920];
        long remaining = end - start;
        while (remaining > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
                break;
            output.Write(buffer, 0, read);
            remaining -= read;
        }
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

    private static IEnumerable<string> EnumerateFilesSafely(string root, string pattern)
        => Directory.EnumerateFiles(root, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        });

    private static string NormalizeEntryPath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    private static string SanitizeFileName(string value)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string sanitized = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}
