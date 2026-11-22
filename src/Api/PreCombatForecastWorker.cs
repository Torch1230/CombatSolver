using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver.Api;

internal static partial class PreCombatForecastWorker
{
    private const string RuntimeDirectoryName = ".combatsolver-precombat";
    private const string RuntimeMarkerName = ".combatsolver-precombat-owner";
    private const string RuntimeMarkerContents = "CombatSolver PreCombat API v1";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object RequestCancellationSync = new();
    private static CancellationTokenSource? _activeRequestCancellation;
    private static bool _shuttingDown;
    private static string RuntimeRoot(string gameRoot) =>
        Path.Combine(gameRoot, RuntimeDirectoryName, $"process-{Environment.ProcessId}");
    private static readonly object PinnedModSourcesSync = new();
    private static readonly Dictionary<string, PinnedModSource> PinnedModSources =
        new(StringComparer.Ordinal);
    private static WorkerSession? _session;
    private static string? _ownedRuntimeRoot;
    private static int _workerStartCount;
    private static int _workerReuseCount;
    private static int _workerBusy;

    static PreCombatForecastWorker()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => StopSessionAtProcessExit();
    }

    internal static void PinMainProcessModSources()
    {
        if (!OperatingSystem.IsWindows() || Entry.IsPreCombatWorker)
        {
            return;
        }

        lock (PinnedModSourcesSync)
        {
            if (PinnedModSources.Count > 0)
                return;

            string executablePath = Godot.OS.GetExecutablePath();
            string gameRoot = Path.GetDirectoryName(executablePath)
                ?? throw new InvalidOperationException("The game executable has no parent directory.");
            string runtimeRoot = RuntimeRoot(gameRoot);
            string pinnedRoot = Path.Combine(runtimeRoot, "startup-mods");
            EnsureRuntimeRoot(runtimeRoot, gameRoot);
            ResetOwnedDirectory(runtimeRoot, pinnedRoot);

            Mod[] candidates = ModManager.Mods
                .Where(static mod => mod.manifest?.id != null
                                     && mod.state is ModLoadState.None or ModLoadState.Loaded)
                .ToArray();
            int sourceIndex = 0;
            foreach (IGrouping<string, Mod> sourceGroup in candidates.GroupBy(
                         static mod => Path.GetFullPath(mod.path),
                         StringComparer.OrdinalIgnoreCase))
            {
                Mod[] sourceMods = sourceGroup.ToArray();
                string label = string.Join("_", sourceMods.Select(static mod => Sanitize(mod.manifest!.id!)));
                string target = Path.Combine(pinnedRoot, $"{sourceIndex++:D2}_{label}");
                try
                {
                    CopyDirectory(sourceGroup.Key, target);
                    foreach (Mod mod in sourceMods)
                    {
                        string id = mod.manifest!.id!;
                        PinnedModSources[id] = new PinnedModSource(
                            mod.manifest.version ?? "unknown",
                            sourceGroup.Key,
                            target);
                    }
                }
                catch (Exception ex)
                {
                    Entry.Logger.Warn(
                        $"[CombatSolver/PreCombatApi] MOD_SOURCE_PIN_FAILED " +
                        $"source={sourceGroup.Key} error={ex.Message}");
                }
            }

            Entry.Logger.Info(
                $"[CombatSolver/PreCombatApi] MOD_SOURCES_PINNED " +
                $"mods={PinnedModSources.Count} sources={sourceIndex} root={pinnedRoot}");
        }
    }

    internal static string ResolvePinnedModSource(string id, string version, string sourcePath)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        lock (PinnedModSourcesSync)
        {
            if (PinnedModSources.TryGetValue(id, out PinnedModSource? pinned)
                && pinned.Version.Equals(version, StringComparison.Ordinal)
                && pinned.OriginalSourcePath.Equals(fullSourcePath, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(pinned.PinnedSourcePath))
            {
                return pinned.PinnedSourcePath;
            }
        }
        throw new InvalidOperationException(
            $"No startup snapshot is available for loaded mod {id}@{version}: {fullSourcePath}");
    }

    internal static void MirrorDirectoryForTesting(string sourceRoot, string targetRoot) =>
        CopyDirectory(sourceRoot, targetRoot);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        nint securityAttributes);

    private static async Task<PreCombatForecastResult> RunLegacyLockedAsync(
        string requestId,
        PreCombatLiveStateSnapshot snapshot,
        string encounterId,
        int targetActFloor,
        int targetMapColumn,
        PreCombatRoomKind roomKind,
        PreCombatMapPointKind mapPointKind,
        bool isSecondBoss,
        PreCombatForecastOptions options,
        CancellationTokenSource requestCancellation,
        CancellationToken timeoutToken,
        CancellationToken cancellationToken)
    {
        string? diagnosticLogPath = null;
        try
        {
            string runtimeRoot = RuntimeRoot(snapshot.GameRoot);
            string requestRoot = Path.Combine(runtimeRoot, "requests", requestId);
            string snapshotPath = Path.Combine(requestRoot, "run.save.json");
            EnsureRuntimeRoot(runtimeRoot, snapshot.GameRoot);
            Directory.CreateDirectory(requestRoot);
            await File.WriteAllBytesAsync(
                    snapshotPath,
                    snapshot.SerializedPlanningRun ?? snapshot.SerializedRun,
                    cancellationToken)
                .ConfigureAwait(false);

            string sessionSignature = BuildSessionSignature(snapshot);
            WorkerSession? session = TryGetReusableSession(sessionSignature);
            bool reusedWorker = session is not null;
            if (session is null)
            {
                StopCurrentSession();
                session = CreateSession(snapshot, runtimeRoot, sessionSignature, cancellationToken);
            }
            else
            {
                Interlocked.Increment(ref _workerReuseCount);
            }
            diagnosticLogPath = session.LogPath;
            DeleteIfExists(session.ResultPath);
            DeleteIfExists(session.ReadyPath);

            string[] expectedMods = snapshot.Mods
                .Select(static mod => $"{mod.Id}@{mod.Version}")
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            UnattendedTestRequest request = new()
            {
                RunId = requestId,
                ScenarioId = options.SimulationSeed.HasValue
                    ? "PRECOMBAT-SIMULATION-V1"
                    : "PRECOMBAT-API-V1",
                CharacterId = snapshot.CharacterId,
                EncounterId = encounterId,
                Seed = snapshot.Seed,
                RunSnapshotPath = snapshotPath,
                ActIndexForTest = snapshot.ActIndex,
                MarkEncounterAsSecondBossForTest = isSecondBoss,
                LoadRunSnapshotDirectly = true,
                PreserveNativeCombatStateForTest = true,
                TargetActFloor = targetActFloor,
                TargetMapColumn = targetMapColumn,
                TargetRoomType = ToRoomType(roomKind),
                TargetMapPointType = ToMapPointType(mapPointKind),
                ExpectedLoadedMods = expectedMods,
                EnemyCurrentHp = int.MaxValue,
                Cards = [],
                FixedSearchBudget = true,
                SearchBudgetOverrideMilliseconds = options.SearchBudgetMilliseconds,
                SearchMaxDegreeOfParallelismForTest = options.MaxDegreeOfParallelism,
                PreCombatPlayerCurrentHpOverride = options.PlayerCurrentHpOverride,
                PreCombatSimulationSeed = options.SimulationSeed,
                PreCombatInterveningMapPoints = options.InterveningMapPoints
                    .Select(static step => new UnattendedPreCombatMapStep
                    {
                        Coordinate = step.Coordinate,
                        RoomType = step.RoomType,
                        MapPointType = step.MapPointType,
                    })
                    .ToArray(),
                EnableNoGcRegionForTest = false,
                HeadlessFastModeForTest = SolverDeploymentFastMode.Instant,
                StopAfterInitialSolverResultAssertion = true,
                ExitOnComplete = false,
                TimeoutSeconds = Math.Max(
                    15,
                    (options.OverallTimeoutMilliseconds - 5_000) / 1_000d),
            };
            await WriteJsonAtomicallyAsync(session.RequestPath, request, cancellationToken).ConfigureAwait(false);
            Entry.Logger.Info(
                $"[CombatSolver/PreCombatApi] WORKER_REQUEST request_id={requestId} " +
                $"pid={session.Process.Id} reused={reusedWorker} audio_muted={session.AudioMuted}");
            UnattendedTestResult workerResult;
            try
            {
                workerResult = await WaitForResultAsync(
                    session.Process,
                    session.ResultPath,
                    requestId,
                    cancellationToken).ConfigureAwait(false);
                if (!workerResult.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase)
                    || workerResult.SolverMetrics == null)
                {
                    InvalidateSession(session);
                    return PreCombatForecastApi.Failure(
                        PreCombatForecastStatus.Failed,
                        requestId,
                        workerResult.Error ?? $"Worker stopped at stage {workerResult.Stage}.",
                        session.LogPath);
                }
                if (options.SimulationSeed is { } simulationSeed
                    && !workerResult.CompletedChecks.Contains(
                        $"PreCombatSimulationRng:{simulationSeed}",
                        StringComparer.Ordinal))
                {
                    InvalidateSession(session);
                    return PreCombatForecastApi.Failure(
                        PreCombatForecastStatus.Failed,
                        requestId,
                        "The isolated worker did not confirm the requested hypothetical combat RNG sample.",
                        session.LogPath);
                }
                await WaitForReadyAsync(
                    session.Process,
                    session.ReadyPath,
                    requestId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                InvalidateSession(session);
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Cancelled,
                    requestId,
                    "The isolated pre-combat request was cancelled.",
                    session.LogPath);
            }
            catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
            {
                InvalidateSession(session);
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.TimedOut,
                    requestId,
                    $"The isolated worker exceeded {options.OverallTimeoutMilliseconds} ms.",
                    session.LogPath);
            }
            catch (OperationCanceledException ex)
            {
                InvalidateSession(session);
                Entry.Logger.Error(
                    $"[CombatSolver/PreCombatApi] LEGACY_UNEXPECTED_CANCELLATION request_id={requestId} exception={ex}");
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Failed,
                    requestId,
                    ex.GetBaseException().Message,
                    session.LogPath);
            }
            catch
            {
                InvalidateSession(session);
                throw;
            }

            UnattendedSolverMetrics metrics = workerResult.SolverMetrics;
            PreCombatForecastConfidence confidence = metrics.OnlyDeathRoutes
                ? PreCombatForecastConfidence.DeathOnly
                : metrics.CombatEndedTurn.HasValue
                    ? PreCombatForecastConfidence.Complete
                    : PreCombatForecastConfidence.Bounded;
            PreCombatForecastResult result = new()
            {
                Status = PreCombatForecastStatus.Succeeded,
                RequestId = requestId,
                ProjectedHpLoss = metrics.ProjectedBattleHpLost,
                PotionUses = metrics.PotionUses
                    .Select(static use => new PreCombatPotionUse(
                        use.Id,
                        use.Title,
                        use.Turn,
                        use.Slot))
                    .ToArray(),
                SearchBoundary = metrics.Boundary.ToString(),
                Confidence = confidence,
                FinalHp = metrics.FinalHp,
                CombatEndedTurn = metrics.CombatEndedTurn,
                SearchElapsedMilliseconds = metrics.TotalElapsedMilliseconds,
                TotalElapsedMilliseconds = workerResult.ElapsedMilliseconds,
                ProducingBackend = PreCombatBackendKind.LegacyProcess,
            };
            TryDeleteOwnedRequestDirectory(runtimeRoot, requestRoot);
            if (options.CloseWorkerAfterRequest)
                StopCurrentSession();
            else
                ScheduleIdleShutdown(session);
            return result;
        }
        catch (OperationCanceledException ex)
        {
            StopCurrentSession();
            if (requestCancellation.IsCancellationRequested)
            {
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Cancelled,
                    requestId,
                    "The isolated pre-combat request was cancelled.",
                    diagnosticLogPath);
            }
            if (timeoutToken.IsCancellationRequested)
            {
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.TimedOut,
                    requestId,
                    $"The isolated worker exceeded {options.OverallTimeoutMilliseconds} ms.",
                    diagnosticLogPath);
            }
            Entry.Logger.Error(
                $"[CombatSolver/PreCombatApi] LEGACY_WORKER_UNEXPECTED_CANCELLATION request_id={requestId} exception={ex}");
            return PreCombatForecastApi.Failure(
                PreCombatForecastStatus.Failed,
                requestId,
                ex.GetBaseException().Message,
                diagnosticLogPath);
        }
        catch (Exception ex)
        {
            StopCurrentSession();
            Entry.Logger.Error(
                $"[CombatSolver/PreCombatApi] WORKER_FAILED request_id={requestId} exception={ex}");
            return PreCombatForecastApi.Failure(
                PreCombatForecastStatus.Failed,
                requestId,
                ex.GetBaseException().Message,
                diagnosticLogPath);
        }
    }

    private static WorkerSession? TryGetReusableSession(string signature)
    {
        WorkerSession? session = Volatile.Read(ref _session);
        if (session is null)
            return null;
        if (!signature.Equals(session.Signature, StringComparison.Ordinal)
            || session.Process.HasExited)
        {
            InvalidateSession(session);
            return null;
        }
        return session;
    }

    private static WorkerSession CreateSession(
        PreCombatLiveStateSnapshot snapshot,
        string runtimeRoot,
        string sessionSignature,
        CancellationToken cancellationToken)
    {
        string workerGameRoot = Path.Combine(runtimeRoot, "game");
        string sessionRoot = Path.Combine(runtimeRoot, "session");
        string roamingRoot = Path.Combine(sessionRoot, "Roaming");
        string localRoot = Path.Combine(sessionRoot, "Local");
        string workerDataRoot = Path.Combine(roamingRoot, "SlayTheSpire2");
        string logPath = Path.Combine(sessionRoot, "worker.log");
        ResetOwnedDirectory(runtimeRoot, sessionRoot);
        EnsureGameMirror(runtimeRoot, snapshot.GameRoot, workerGameRoot);
        MirrorLoadedMods(runtimeRoot, workerGameRoot, snapshot.Mods);
        bool audioMuted = PrepareWorkerUserData(snapshot.UserDataRoot, snapshot.AccountDataRoot, workerDataRoot);
        File.WriteAllBytes(Path.Combine(workerDataRoot, "combat_solver_settings.json"), snapshot.SolverSettings);
        Directory.CreateDirectory(localRoot);

        string workerExe = Path.Combine(workerGameRoot, "SlayTheSpire2.exe");
        if (!File.Exists(workerExe))
            throw new FileNotFoundException("The mirrored game executable is missing.", workerExe);
        lock (RequestCancellationSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_shuttingDown)
                throw new OperationCanceledException("The main game is exiting.");
            WorkerSession session = new(
                sessionSignature,
                StartWorker(workerExe, workerGameRoot, roamingRoot, localRoot, logPath),
                Path.Combine(workerDataRoot, "combat_solver_test_request.json"),
                Path.Combine(workerDataRoot, "combat_solver_test_result.json"),
                Path.Combine(workerDataRoot, "combat_solver_test_ready.json"),
                logPath,
                audioMuted);
            Volatile.Write(ref _session, session);
            Interlocked.Increment(ref _workerStartCount);
            return session;
        }
    }

    private static string BuildSessionSignature(PreCombatLiveStateSnapshot snapshot) => string.Join(
        '|',
        Path.GetFullPath(snapshot.GameRoot),
        Path.GetFullPath(snapshot.UserDataRoot),
        Path.GetFullPath(snapshot.AccountDataRoot),
        Convert.ToHexString(SHA256.HashData(snapshot.SolverSettings)),
        string.Join(';', snapshot.Mods.Select(static mod =>
            $"{mod.Id}@{mod.Version}@{Path.GetFullPath(ResolvePinnedModSource(mod.Id, mod.Version, mod.SourcePath))}")));

    private static PreCombatWorkerStatus StoppedStatus(bool busy) => new()
    {
        IsRunning = false,
        IsBusy = busy,
        Backend = PreCombatBackendKind.None,
        AudioMuted = false,
        IdleTimeoutMilliseconds = GetIdleTimeoutMilliseconds(),
    };

    private static void InvalidateSession(WorkerSession session)
    {
        Interlocked.CompareExchange(ref _session, null, session);
        StopOwnedProcess(session.Process);
    }

    private static void StopCurrentSession()
    {
        WorkerSession? session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
            StopOwnedProcess(session.Process);
    }

    internal static void StopSessionAtProcessExit()
    {
        try
        {
            lock (RequestCancellationSync)
            {
                _shuttingDown = true;
                Interlocked.Increment(ref _stopEpoch);
                _activeRequestCancellation?.Cancel();
            }
            CancelIdleShutdown();
            StopCurrentInProcessSession();
            ResetBackendDisplayState();
            WorkerSession? session = Interlocked.Exchange(ref _session, null);
            if (session is not null && !session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
                if (!session.Process.WaitForExit(5_000))
                    return;
            }
            if (_ownedRuntimeRoot is { } root)
            {
                foreach (string name in new[] { "game", "startup-mods" })
                {
                    string directory = Path.Combine(root, name);
                    AssertOwnedPath(root, directory);
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch
        {
            // Process shutdown is already in progress; there is no caller to report to.
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static RoomType ToRoomType(PreCombatRoomKind kind) => kind switch
    {
        PreCombatRoomKind.Normal => RoomType.Monster,
        PreCombatRoomKind.Elite => RoomType.Elite,
        PreCombatRoomKind.Boss => RoomType.Boss,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static MegaCrit.Sts2.Core.Map.MapPointType ToMapPointType(PreCombatMapPointKind kind) => kind switch
    {
        PreCombatMapPointKind.Normal => MegaCrit.Sts2.Core.Map.MapPointType.Monster,
        PreCombatMapPointKind.Elite => MegaCrit.Sts2.Core.Map.MapPointType.Elite,
        PreCombatMapPointKind.Boss => MegaCrit.Sts2.Core.Map.MapPointType.Boss,
        PreCombatMapPointKind.Event => MegaCrit.Sts2.Core.Map.MapPointType.Ancient,
        PreCombatMapPointKind.Unknown => MegaCrit.Sts2.Core.Map.MapPointType.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static void EnsureRuntimeRoot(string runtimeRoot, string sourceGameRoot)
    {
        Directory.CreateDirectory(runtimeRoot);
        string markerPath = Path.Combine(runtimeRoot, RuntimeMarkerName);
        string expected = $"{RuntimeMarkerContents}{Environment.NewLine}{Path.GetFullPath(sourceGameRoot)}";
        if (File.Exists(markerPath))
        {
            string actual = File.ReadAllText(markerPath);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The pre-combat runtime marker is invalid: {markerPath}");
            _ownedRuntimeRoot = runtimeRoot;
            return;
        }

        if (Directory.EnumerateFileSystemEntries(runtimeRoot).Any())
            throw new InvalidDataException($"Refusing to adopt a non-empty unowned runtime directory: {runtimeRoot}");
        File.WriteAllText(markerPath, expected);
        _ownedRuntimeRoot = runtimeRoot;
    }

    private static void EnsureGameMirror(string runtimeRoot, string sourceRoot, string targetRoot)
    {
        ResetOwnedDirectory(runtimeRoot, targetRoot);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.TopDirectoryOnly))
            LinkFile(sourceFile, Path.Combine(targetRoot, Path.GetFileName(sourceFile)));

        foreach (string directoryName in new[] { "data_sts2_windows_x86_64", "controller_config" })
        {
            string sourceDirectory = Path.Combine(sourceRoot, directoryName);
            if (Directory.Exists(sourceDirectory))
                MirrorDirectory(sourceDirectory, Path.Combine(targetRoot, directoryName));
        }
    }

    private static void MirrorLoadedMods(
        string runtimeRoot,
        string workerGameRoot,
        IReadOnlyList<PreCombatModSnapshot> mods)
    {
        string modsRoot = Path.Combine(workerGameRoot, "mods");
        ResetOwnedDirectory(runtimeRoot, modsRoot);
        int index = 0;
        foreach (IGrouping<string, PreCombatModSnapshot> sourceGroup in mods.GroupBy(
                     static mod => ResolvePinnedModSource(mod.Id, mod.Version, mod.SourcePath),
                     StringComparer.OrdinalIgnoreCase))
        {
            string label = string.Join("_", sourceGroup.Select(static mod => Sanitize(mod.Id)));
            string target = Path.Combine(modsRoot, $"{index++:D2}_{label}");
            CopyDirectory(sourceGroup.Key, target);
        }
    }

    private static bool PrepareWorkerUserData(string sourceRoot, string sourceAccountRoot, string workerRoot)
    {
        Directory.CreateDirectory(workerRoot);
        CopyDirectory(sourceAccountRoot, Path.Combine(workerRoot, "default", "1"));
        foreach (string directoryName in new[] { "ModConfig", "mod_configs" })
        {
            string source = Path.Combine(sourceRoot, directoryName);
            if (Directory.Exists(source))
                CopyDirectory(source, Path.Combine(workerRoot, directoryName));
        }

        string sourceNestedConfig = Path.Combine(sourceRoot, "mods", "config");
        if (Directory.Exists(sourceNestedConfig))
            CopyDirectory(sourceNestedConfig, Path.Combine(workerRoot, "mods", "config"));
        string sourceSolverSettings = Path.Combine(sourceRoot, "combat_solver_settings.json");
        if (File.Exists(sourceSolverSettings))
            File.Copy(sourceSolverSettings, Path.Combine(workerRoot, "combat_solver_settings.json"), true);

        string settingsPath = Path.Combine(workerRoot, "default", "1", "settings.save");
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException("The worker profile settings template is missing.", settingsPath);
        JsonObject settings = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject()
            ?? throw new InvalidDataException("The worker settings template is empty.");
        settings["mod_settings"] = new JsonObject
        {
            ["mods_enabled"] = true,
            ["mod_list"] = new JsonArray(),
        };
        string[] volumeKeys =
        [
            "volume_master",
            "volume_bgm",
            "volume_sfx",
            "volume_ambience",
        ];
        foreach (string key in volumeKeys)
            settings[key] = 0f;
        File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        JsonObject savedSettings = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject()
            ?? throw new InvalidDataException("The muted worker settings file is empty.");
        if (!volumeKeys.All(key => savedSettings[key]?.GetValue<float>() == 0f))
            throw new InvalidDataException("The isolated worker audio settings could not be muted.");
        return true;
    }

    private static Process StartWorker(
        string executable,
        string workingDirectory,
        string roamingRoot,
        string localRoot,
        string logPath)
    {
        ProcessStartInfo start = new(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--disable-vsync");
        start.ArgumentList.Add("--max-fps");
        start.ArgumentList.Add("0");
        start.ArgumentList.Add("--force-steam=off");
        start.ArgumentList.Add("--log-file");
        start.ArgumentList.Add(logPath);
        start.Environment["APPDATA"] = roamingRoot;
        start.Environment["LOCALAPPDATA"] = localRoot;
        start.Environment["COMBATSOLVER_HEADLESS"] = "1";
        start.Environment["COMBATSOLVER_PRECOMBAT_WORKER"] = "1";
        return Process.Start(start)
            ?? throw new InvalidOperationException("The isolated game process did not start.");
    }

    private static async Task<UnattendedTestResult> WaitForResultAsync(
        Process process,
        string resultPath,
        string requestId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(resultPath))
            {
                string json = await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false);
                UnattendedTestResult? result = JsonSerializer.Deserialize<UnattendedTestResult>(
                    json,
                    UnattendedTestFiles.JsonOptions);
                if (result?.RunId == requestId)
                    return result;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The isolated game process exited with code {process.ExitCode} before returning a result.");
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitForReadyAsync(
        Process process,
        string readyPath,
        string requestId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(readyPath))
            {
                string json = await File.ReadAllTextAsync(readyPath, cancellationToken).ConfigureAwait(false);
                WorkerReady? ready = JsonSerializer.Deserialize<WorkerReady>(
                    json,
                    UnattendedTestFiles.JsonOptions);
                if (ready?.RunId == requestId)
                {
                    if (ready.Held)
                        throw new InvalidOperationException("The pre-combat worker unexpectedly retained a live search.");
                    return;
                }
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The isolated game process exited with code {process.ExitCode} before becoming reusable.");
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void StopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
            // The exact Process object returned by Process.Start is the only process this method can touch.
        }
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = path + ".tmp";
        string json = JsonSerializer.Serialize(value, UnattendedTestFiles.JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, true);
    }

    private static void MirrorDirectory(string sourceRoot, string targetRoot)
    {
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException(sourceRoot);
        Directory.CreateDirectory(targetRoot);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, sourceFile);
            LinkFile(sourceFile, Path.Combine(targetRoot, relative));
        }
    }

    private static void LinkFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        FileInfo sourceInfo = new(source);
        if (File.Exists(target))
        {
            FileInfo targetInfo = new(target);
            if (sourceInfo.Length == targetInfo.Length
                && sourceInfo.LastWriteTimeUtc == targetInfo.LastWriteTimeUtc)
            {
                return;
            }
            File.Delete(target);
        }
        if (!CreateHardLinkW(target, source, 0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not hard-link '{source}' to '{target}'.");
        }
    }

    private static void CopyDirectory(string sourceRoot, string targetRoot)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, sourceFile);
            string target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourceFile, target, true);
        }
    }

    private static void ResetOwnedDirectory(string runtimeRoot, string directory)
    {
        AssertOwnedPath(runtimeRoot, directory);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
    }

    private static void TryDeleteOwnedRequestDirectory(string runtimeRoot, string requestRoot)
    {
        try
        {
            AssertOwnedPath(runtimeRoot, requestRoot);
            if (Directory.Exists(requestRoot))
                Directory.Delete(requestRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn(
                $"[CombatSolver/PreCombatApi] REQUEST_CLEANUP_FAILED path={requestRoot} error={ex.Message}");
        }
    }

    private static void AssertOwnedPath(string runtimeRoot, string path)
    {
        string root = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to modify a path outside the owned runtime: {candidate}");
        string marker = Path.Combine(runtimeRoot, RuntimeMarkerName);
        if (!File.Exists(marker)
            || !File.ReadAllText(marker).StartsWith(RuntimeMarkerContents, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The pre-combat runtime is not owned by Combat Solver: {runtimeRoot}");
        }
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private sealed record WorkerSession(
        string Signature,
        Process Process,
        string RequestPath,
        string ResultPath,
        string ReadyPath,
        string LogPath,
        bool AudioMuted);

    private sealed record PinnedModSource(
        string Version,
        string OriginalSourcePath,
        string PinnedSourcePath);

    private sealed class WorkerReady
    {
        public int SchemaVersion { get; init; }
        public string RunId { get; init; } = string.Empty;
        public bool Held { get; init; }
    }
}
