using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver.Api;

internal static class PreCombatForecastWorker
{
    private const string RuntimeDirectoryName = ".combatsolver-precombat";
    private const string RuntimeMarkerName = ".combatsolver-precombat-owner";
    private const string RuntimeMarkerContents = "CombatSolver PreCombat API v1";
    private const int KeepAliveIndefinitely = -1;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static WorkerSession? _session;
    private static CancellationTokenSource? _idleShutdown;
    private static int _idleWorkerLifetimeMilliseconds = PreCombatForecastApi.DefaultWorkerIdleTimeoutMilliseconds;
    private static int _workerStartCount;
    private static int _workerReuseCount;
    private static int _workerBusy;

    static PreCombatForecastWorker()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => StopSessionAtProcessExit();
    }

    internal static int WorkerStartCountForTesting => Volatile.Read(ref _workerStartCount);

    internal static int WorkerReuseCountForTesting => Volatile.Read(ref _workerReuseCount);

    internal static PreCombatWorkerStatus GetStatus()
    {
        bool busy = Volatile.Read(ref _workerBusy) != 0;
        WorkerSession? session = Volatile.Read(ref _session);
        if (session is null)
            return StoppedStatus(busy);
        try
        {
            if (session.Process.HasExited)
                return StoppedStatus(busy);
            session.Process.Refresh();
            return new PreCombatWorkerStatus
            {
                IsRunning = true,
                IsBusy = busy,
                ProcessId = session.Process.Id,
                WorkingSetBytes = session.Process.WorkingSet64,
                PrivateMemoryBytes = session.Process.PrivateMemorySize64,
                PeakWorkingSetBytes = session.Process.PeakWorkingSet64,
                AudioMuted = session.AudioMuted,
                IdleTimeoutMilliseconds = GetIdleTimeoutMilliseconds(),
            };
        }
        catch (InvalidOperationException)
        {
            return StoppedStatus(busy);
        }
        catch (Win32Exception)
        {
            return StoppedStatus(busy);
        }
        catch (NotSupportedException)
        {
            return StoppedStatus(busy);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        nint securityAttributes);

    public static async Task<PreCombatForecastResult> RunAsync(
        PreCombatLiveStateSnapshot snapshot,
        string encounterId,
        int targetActFloor,
        int targetMapColumn,
        PreCombatRoomKind roomKind,
        PreCombatMapPointKind mapPointKind,
        bool isSecondBoss,
        PreCombatForecastOptions options,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        string? diagnosticLogPath = null;
        bool gateEntered = false;
        try
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            Interlocked.Exchange(ref _workerBusy, 1);
            CancelIdleShutdown();
            SetIdleWorkerLifetime(options.WorkerIdleTimeoutMilliseconds);

            string runtimeRoot = Path.Combine(snapshot.GameRoot, RuntimeDirectoryName);
            string requestRoot = Path.Combine(runtimeRoot, "requests", requestId);
            string snapshotPath = Path.Combine(requestRoot, "run.save.json");
            EnsureRuntimeRoot(runtimeRoot, snapshot.GameRoot);
            Directory.CreateDirectory(requestRoot);
            await File.WriteAllBytesAsync(snapshotPath, snapshot.SerializedRun, cancellationToken)
                .ConfigureAwait(false);

            string sessionSignature = BuildSessionSignature(snapshot);
            WorkerSession? session = TryGetReusableSession(sessionSignature);
            bool reusedWorker = session is not null;
            if (session is null)
            {
                StopCurrentSession();
                session = CreateSession(snapshot, runtimeRoot, sessionSignature);
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
                TargetActFloor = targetActFloor,
                TargetMapColumn = targetMapColumn,
                TargetRoomType = ToRoomType(roomKind),
                TargetMapPointType = ToMapPointType(mapPointKind),
                ExpectedLoadedMods = expectedMods,
                EnemyCurrentHp = int.MaxValue,
                Cards = [],
                ForceShortSearchOnly = true,
                ShortSearchBudgetOverrideMilliseconds = options.SearchBudgetMilliseconds,
                DeepSearchBudgetOverrideMilliseconds = options.SearchBudgetMilliseconds,
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
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.OverallTimeoutMilliseconds);
            UnattendedTestResult workerResult;
            try
            {
                workerResult = await WaitForResultAsync(
                    session.Process,
                    session.ResultPath,
                    requestId,
                    deadline.Token).ConfigureAwait(false);
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
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                InvalidateSession(session);
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.TimedOut,
                    requestId,
                    $"The isolated worker exceeded {options.OverallTimeoutMilliseconds} ms.",
                    session.LogPath);
            }
            catch (OperationCanceledException)
            {
                InvalidateSession(session);
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Cancelled,
                    requestId,
                    "The isolated pre-combat request was cancelled.",
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
            };
            TryDeleteOwnedRequestDirectory(runtimeRoot, requestRoot);
            if (options.CloseWorkerAfterRequest)
                StopCurrentSession();
            else
                ScheduleIdleShutdown(session);
            return result;
        }
        catch (OperationCanceledException)
        {
            return PreCombatForecastApi.Failure(
                PreCombatForecastStatus.Cancelled,
                requestId,
                "The isolated pre-combat request was cancelled.");
        }
        catch (Exception ex)
        {
            Entry.Logger.Error(
                $"[CombatSolver/PreCombatApi] WORKER_FAILED request_id={requestId} exception={ex}");
            return PreCombatForecastApi.Failure(
                PreCombatForecastStatus.Failed,
                requestId,
                ex.GetBaseException().Message,
                diagnosticLogPath);
        }
        finally
        {
            if (gateEntered)
            {
                Interlocked.Exchange(ref _workerBusy, 0);
                Gate.Release();
            }
        }
    }

    public static async Task<PreCombatWorkerStatus> RestartSessionAsync(
        PreCombatLiveStateSnapshot snapshot,
        int? idleTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _workerBusy, 1);
            CancelIdleShutdown();
            SetIdleWorkerLifetime(idleTimeoutMilliseconds);
            StopCurrentSession();
            string runtimeRoot = Path.Combine(snapshot.GameRoot, RuntimeDirectoryName);
            EnsureRuntimeRoot(runtimeRoot, snapshot.GameRoot);
            WorkerSession session = CreateSession(
                snapshot,
                runtimeRoot,
                BuildSessionSignature(snapshot));
            ScheduleIdleShutdown(session);
        }
        finally
        {
            Interlocked.Exchange(ref _workerBusy, 0);
            Gate.Release();
        }
        return GetStatus();
    }

    public static async Task ConfigureIdleTimeoutAsync(int? idleTimeoutMilliseconds)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            SetIdleWorkerLifetime(idleTimeoutMilliseconds);
            WorkerSession? session = Volatile.Read(ref _session);
            if (session is not null && !session.Process.HasExited)
                ScheduleIdleShutdown(session);
            else
                CancelIdleShutdown();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopSessionAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelIdleShutdown();
            StopCurrentSession();
        }
        finally
        {
            Gate.Release();
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
        string sessionSignature)
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
        bool audioMuted = PrepareWorkerUserData(snapshot.UserDataRoot, workerDataRoot);
        Directory.CreateDirectory(localRoot);

        string workerExe = Path.Combine(workerGameRoot, "SlayTheSpire2.exe");
        if (!File.Exists(workerExe))
            throw new FileNotFoundException("The mirrored game executable is missing.", workerExe);
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

    private static string BuildSessionSignature(PreCombatLiveStateSnapshot snapshot) => string.Join(
        '|',
        Path.GetFullPath(snapshot.GameRoot),
        Path.GetFullPath(snapshot.UserDataRoot),
        string.Join(';', snapshot.Mods.Select(static mod =>
            $"{mod.Id}@{mod.Version}@{Path.GetFullPath(mod.SourcePath)}")));

    private static PreCombatWorkerStatus StoppedStatus(bool busy) => new()
    {
        IsRunning = false,
        IsBusy = busy,
        AudioMuted = false,
        IdleTimeoutMilliseconds = GetIdleTimeoutMilliseconds(),
    };

    private static void ScheduleIdleShutdown(WorkerSession session)
    {
        CancelIdleShutdown();
        int idleTimeoutMilliseconds = Volatile.Read(ref _idleWorkerLifetimeMilliseconds);
        if (idleTimeoutMilliseconds == KeepAliveIndefinitely)
            return;
        CancellationTokenSource cancellation = new();
        _idleShutdown = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(idleTimeoutMilliseconds, cancellation.Token).ConfigureAwait(false);
                await Gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_session, session))
                    {
                        Entry.Logger.Info(
                            $"[CombatSolver/PreCombatApi] WORKER_IDLE_STOP pid={session.Process.Id} " +
                            $"idle_ms={idleTimeoutMilliseconds}");
                        StopCurrentSession();
                    }
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // A new request or explicit panel close owns the next session transition.
            }
            catch (Exception ex)
            {
                Entry.Logger.Error($"[CombatSolver/PreCombatApi] WORKER_IDLE_STOP_FAILED exception={ex}");
            }
            finally
            {
                Interlocked.CompareExchange(ref _idleShutdown, null, cancellation);
                cancellation.Dispose();
            }
        });
    }

    private static int? GetIdleTimeoutMilliseconds()
    {
        int value = Volatile.Read(ref _idleWorkerLifetimeMilliseconds);
        return value == KeepAliveIndefinitely ? null : value;
    }

    private static void SetIdleWorkerLifetime(int? idleTimeoutMilliseconds) =>
        Interlocked.Exchange(
            ref _idleWorkerLifetimeMilliseconds,
            idleTimeoutMilliseconds ?? KeepAliveIndefinitely);

    private static void CancelIdleShutdown()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _idleShutdown, null);
        if (cancellation is null)
            return;
        cancellation.Cancel();
    }

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

    private static void StopSessionAtProcessExit()
    {
        try
        {
            CancelIdleShutdown();
            WorkerSession? session = Interlocked.Exchange(ref _session, null);
            if (session is not null && !session.Process.HasExited)
                session.Process.Kill(entireProcessTree: true);
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
            return;
        }

        if (Directory.EnumerateFileSystemEntries(runtimeRoot).Any())
            throw new InvalidDataException($"Refusing to adopt a non-empty unowned runtime directory: {runtimeRoot}");
        File.WriteAllText(markerPath, expected);
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
                     static mod => mod.SourcePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            string label = string.Join("_", sourceGroup.Select(static mod => Sanitize(mod.Id)));
            string target = Path.Combine(modsRoot, $"{index++:D2}_{label}");
            MirrorDirectory(sourceGroup.Key, target);
        }
    }

    private static bool PrepareWorkerUserData(string sourceRoot, string workerRoot)
    {
        Directory.CreateDirectory(workerRoot);
        foreach (string directoryName in new[] { "default", "ModConfig", "mod_configs" })
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

    private sealed class WorkerReady
    {
        public int SchemaVersion { get; init; }
        public string RunId { get; init; } = string.Empty;
        public bool Held { get; init; }
    }
}
