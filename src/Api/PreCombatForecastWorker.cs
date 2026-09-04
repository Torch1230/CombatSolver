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
    private static readonly SemaphoreSlim Gate = new(1, 1);

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

            string runtimeRoot = Path.Combine(snapshot.GameRoot, RuntimeDirectoryName);
            string workerGameRoot = Path.Combine(runtimeRoot, "game");
            string requestRoot = Path.Combine(runtimeRoot, "requests", requestId);
            string roamingRoot = Path.Combine(requestRoot, "Roaming");
            string localRoot = Path.Combine(requestRoot, "Local");
            string workerDataRoot = Path.Combine(roamingRoot, "SlayTheSpire2");
            string snapshotPath = Path.Combine(requestRoot, "run.save.json");
            string logPath = Path.Combine(requestRoot, "worker.log");
            diagnosticLogPath = logPath;
            string requestPath = Path.Combine(workerDataRoot, "combat_solver_test_request.json");
            string resultPath = Path.Combine(workerDataRoot, "combat_solver_test_result.json");

            EnsureRuntimeRoot(runtimeRoot, snapshot.GameRoot);
            EnsureGameMirror(runtimeRoot, snapshot.GameRoot, workerGameRoot);
            MirrorLoadedMods(runtimeRoot, workerGameRoot, snapshot.Mods);
            PrepareWorkerUserData(snapshot.UserDataRoot, workerDataRoot);
            Directory.CreateDirectory(localRoot);
            Directory.CreateDirectory(requestRoot);
            await File.WriteAllBytesAsync(snapshotPath, snapshot.SerializedRun, cancellationToken)
                .ConfigureAwait(false);

            string[] expectedMods = snapshot.Mods
                .Select(static mod => $"{mod.Id}@{mod.Version}")
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            UnattendedTestRequest request = new()
            {
                RunId = requestId,
                ScenarioId = "PRECOMBAT-API-V1",
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
                ExitOnComplete = true,
                TimeoutSeconds = Math.Max(
                    15,
                    (options.OverallTimeoutMilliseconds - 5_000) / 1_000d),
            };
            await WriteJsonAtomicallyAsync(requestPath, request, cancellationToken).ConfigureAwait(false);

            string workerExe = Path.Combine(
                workerGameRoot,
                Path.GetFileName(Path.Combine(snapshot.GameRoot, "SlayTheSpire2.exe")));
            if (!File.Exists(workerExe))
                throw new FileNotFoundException("The mirrored game executable is missing.", workerExe);

            using Process process = StartWorker(workerExe, workerGameRoot, roamingRoot, localRoot, logPath);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.OverallTimeoutMilliseconds);
            UnattendedTestResult? workerResult;
            try
            {
                workerResult = await WaitForResultAsync(
                    process,
                    resultPath,
                    requestId,
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                StopOwnedProcess(process);
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.TimedOut,
                    requestId,
                    $"The isolated worker exceeded {options.OverallTimeoutMilliseconds} ms.",
                    logPath);
            }
            catch (OperationCanceledException)
            {
                StopOwnedProcess(process);
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Cancelled,
                    requestId,
                    "The isolated pre-combat request was cancelled.",
                    logPath);
            }

            await StopAfterResultAsync(process).ConfigureAwait(false);
            if (!workerResult.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase)
                || workerResult.SolverMetrics == null)
            {
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Failed,
                    requestId,
                    workerResult.Error ?? $"Worker stopped at stage {workerResult.Stage}.",
                    logPath);
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
                Gate.Release();
        }
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

    private static void PrepareWorkerUserData(string sourceRoot, string workerRoot)
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
        File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

    private static async Task StopAfterResultAsync(Process process)
    {
        if (process.HasExited)
            return;
        using CancellationTokenSource exitDeadline = new(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            StopOwnedProcess(process);
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
}
