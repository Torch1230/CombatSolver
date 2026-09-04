using System.Collections.Concurrent;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Api;

/// <summary>
/// Public, side-effect-checked entry point for forecasting a known encounter before entering combat.
/// The combat is initialized and solved in a separately owned headless game process.
/// </summary>
public static class PreCombatForecastApi
{
    public const int ApiVersion = 2;

    private static readonly ConcurrentDictionary<string, Task<PreCombatForecastResult>> Active = new();
    private static readonly ConcurrentDictionary<string, PreCombatForecastResult> Completed = new();

    public static bool IsAvailable => OperatingSystem.IsWindows()
                                      && Entry.Enabled
                                      && !string.Equals(
                                          Environment.GetEnvironmentVariable("COMBATSOLVER_PRECOMBAT_WORKER"),
                                          "1",
                                          StringComparison.Ordinal);

    /// <summary>Returns an opaque token suitable for caller-side caching while the run remains unchanged.</summary>
    public static string CaptureLiveStateToken(RunState run) => PreCombatLiveStateSnapshot.CaptureToken(run);

    public static Task<PreCombatForecastResult> ForecastAsync(
        RunState run,
        EncounterModel encounter,
        int targetActFloor,
        int targetMapColumn,
        PreCombatRoomKind roomKind,
        PreCombatMapPointKind mapPointKind,
        bool isSecondBoss = false,
        PreCombatForecastOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string requestId = Guid.NewGuid().ToString("N");
        if (!IsAvailable)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                "The isolated pre-combat worker is currently available on Windows only."));
        }

        if (!NGame.IsMainThread())
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                "ForecastAsync must be called on the game main thread."));
        }

        PreCombatForecastOptions effectiveOptions = options ?? PreCombatForecastOptions.Default;
        string? optionError = ValidateOptions(
            effectiveOptions,
            targetActFloor,
            targetMapColumn,
            roomKind,
            mapPointKind);
        if (optionError != null)
            return Task.FromResult(Failure(PreCombatForecastStatus.Unsupported, requestId, optionError));

        PreCombatLiveStateSnapshot snapshot;
        try
        {
            snapshot = PreCombatLiveStateSnapshot.Capture(run);
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult(Failure(PreCombatForecastStatus.Unsupported, requestId, ex.Message));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Failure(PreCombatForecastStatus.Failed, requestId, ex.Message));
        }
        if (effectiveOptions.PlayerCurrentHpOverride is { } playerHp
            && (playerHp < 1 || playerHp > run.Players[0].Creature.MaxHp))
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                $"PlayerCurrentHpOverride must be between 1 and {run.Players[0].Creature.MaxHp}."));
        }
        var targetMapCoord = new MapCoord(targetMapColumn, targetActFloor - 1);
        if (run.Map.GetPoint(targetMapCoord) is null)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                $"The target map coordinate does not exist: {targetMapCoord}."));
        }
        string? routeError = ValidateInterveningMapPoints(
            run,
            effectiveOptions.InterveningMapPoints,
            targetMapCoord);
        if (routeError is not null)
            return Task.FromResult(Failure(PreCombatForecastStatus.Unsupported, requestId, routeError));

        string routeKey = string.Join(
            ';',
            effectiveOptions.InterveningMapPoints.Select(static step =>
                $"{step.Coordinate.col},{step.Coordinate.row},{step.MapPointType},{step.RoomType}"));

        string key = string.Join(
            '|',
            snapshot.StateToken,
            encounter.Id,
            targetActFloor,
            targetMapColumn,
            roomKind,
            mapPointKind,
            isSecondBoss,
            effectiveOptions.SearchBudgetMilliseconds,
            effectiveOptions.OverallTimeoutMilliseconds,
            effectiveOptions.MaxDegreeOfParallelism?.ToString() ?? "configured",
            effectiveOptions.PlayerCurrentHpOverride?.ToString() ?? "live-hp",
            routeKey);

        if (!effectiveOptions.ForceRefresh
            && Completed.TryGetValue(key, out PreCombatForecastResult? completed))
            return CompleteAfterLiveValidation(snapshot, completed, cancellationToken);

        Task<PreCombatForecastResult> worker;
        if (effectiveOptions.CancelWorkerWhenCallerCancels)
        {
            worker = Task.Run(
                () => PreCombatForecastWorker.RunAsync(
                    snapshot,
                    encounter.Id.Entry,
                    targetActFloor,
                    targetMapColumn,
                    roomKind,
                    mapPointKind,
                    isSecondBoss,
                    effectiveOptions,
                    cancellationToken),
                CancellationToken.None);
        }
        else
        {
            worker = Active.GetOrAdd(
                key,
                _ => Task.Run(
                    () => PreCombatForecastWorker.RunAsync(
                        snapshot,
                        encounter.Id.Entry,
                        targetActFloor,
                        targetMapColumn,
                        roomKind,
                        mapPointKind,
                        isSecondBoss,
                        effectiveOptions,
                        CancellationToken.None),
                    CancellationToken.None));
        }
        _ = CacheCompletionAsync(key, worker);
        return CompleteAfterLiveValidation(
            snapshot,
            worker,
            cancellationToken,
            effectiveOptions.CancelWorkerWhenCallerCancels);
    }

    private static async Task CacheCompletionAsync(string key, Task<PreCombatForecastResult> worker)
    {
        try
        {
            PreCombatForecastResult result = await worker.ConfigureAwait(false);
            if (result.Status == PreCombatForecastStatus.Succeeded)
            {
                if (Completed.Count >= 32)
                    Completed.Clear();
                Completed[key] = result;
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Error($"[CombatSolver/PreCombatApi] CACHE_COMPLETION_FAILED exception={ex}");
        }
        finally
        {
            Active.TryRemove(new KeyValuePair<string, Task<PreCombatForecastResult>>(key, worker));
        }
    }

    private static Task<PreCombatForecastResult> CompleteAfterLiveValidation(
        PreCombatLiveStateSnapshot snapshot,
        PreCombatForecastResult result,
        CancellationToken cancellationToken)
        => CompleteAfterLiveValidation(snapshot, Task.FromResult(result), cancellationToken);

    private static Task<PreCombatForecastResult> CompleteAfterLiveValidation(
        PreCombatLiveStateSnapshot snapshot,
        Task<PreCombatForecastResult> worker,
        CancellationToken cancellationToken,
        bool awaitOwnedWorkerCancellation = false)
    {
        TaskCompletionSource<PreCombatForecastResult> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = AwaitWorkerAsync();
        return completion.Task;

        async Task AwaitWorkerAsync()
        {
            PreCombatForecastResult result;
            try
            {
                result = awaitOwnedWorkerCancellation
                    ? await worker.ConfigureAwait(false)
                    : await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = Failure(
                    PreCombatForecastStatus.Cancelled,
                    Guid.NewGuid().ToString("N"),
                    "The pre-combat forecast was cancelled.");
            }
            catch (Exception ex)
            {
                result = Failure(
                    PreCombatForecastStatus.Failed,
                    Guid.NewGuid().ToString("N"),
                    ex.GetBaseException().Message);
            }

            SolverDispatcher.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetResult(result with
                    {
                        Status = PreCombatForecastStatus.Cancelled,
                        Error = "The pre-combat forecast was cancelled."
                    });
                    return;
                }

                if (!ReferenceEquals(RunManager.Instance.DebugOnlyGetState(), snapshot.LiveRun)
                    || CombatManager.Instance.IsInProgress)
                {
                    completion.TrySetResult(result with
                    {
                        Status = PreCombatForecastStatus.LiveStateChanged,
                        Error = "The active run or combat state changed while the worker was running."
                    });
                    return;
                }

                try
                {
                    string after = PreCombatLiveStateSnapshot.CaptureToken(snapshot.LiveRun);
                    if (!after.Equals(snapshot.StateToken, StringComparison.Ordinal))
                    {
                        completion.TrySetResult(result with
                        {
                            Status = PreCombatForecastStatus.LiveStateChanged,
                            Error = "The live run state or RNG changed while the worker was running."
                        });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(result with
                    {
                        Status = PreCombatForecastStatus.LiveStateChanged,
                        Error = $"The live run could not be revalidated: {ex.Message}"
                    });
                    return;
                }

                completion.TrySetResult(result);
            });
        }
    }

    private static string? ValidateOptions(
        PreCombatForecastOptions options,
        int targetActFloor,
        int targetMapColumn,
        PreCombatRoomKind roomKind,
        PreCombatMapPointKind mapPointKind)
    {
        if (targetActFloor < 1)
            return "Target floor must be at least 1.";
        if (targetMapColumn < 0)
            return "Target map column must be non-negative.";
        if (options.SearchBudgetMilliseconds is < 1_000 or > 30_000)
            return "SearchBudgetMilliseconds must be between 1000 and 30000.";
        if (options.OverallTimeoutMilliseconds < options.SearchBudgetMilliseconds + 10_000
            || options.OverallTimeoutMilliseconds > 180_000)
        {
            return "OverallTimeoutMilliseconds must allow at least 10 seconds of startup overhead and be no more than 180000.";
        }
        if (options.MaxDegreeOfParallelism is < 1 or > 16)
            return "MaxDegreeOfParallelism must be between 1 and 16.";
        bool roomMatchesMapPoint = (mapPointKind, roomKind) switch
        {
            (PreCombatMapPointKind.Unknown, _) => true,
            (PreCombatMapPointKind.Normal, PreCombatRoomKind.Normal) => true,
            (PreCombatMapPointKind.Elite, PreCombatRoomKind.Elite) => true,
            (PreCombatMapPointKind.Boss, PreCombatRoomKind.Boss) => true,
            _ => false,
        };
        if (!roomMatchesMapPoint)
        {
            return $"Map point kind {mapPointKind} does not match combat room kind {roomKind}.";
        }
        return null;
    }

    private static string? ValidateInterveningMapPoints(
        RunState run,
        IReadOnlyList<PreCombatMapStep>? steps,
        MapCoord target)
    {
        if (steps is null)
            return "InterveningMapPoints cannot be null.";

        int previousRow = run.CurrentMapCoord?.row ?? run.ActFloor - 1;
        foreach (PreCombatMapStep step in steps)
        {
            if (step.Coordinate.row <= previousRow || step.Coordinate.row >= target.row)
                return $"Intervening map rows must increase and remain before the target: {step.Coordinate}.";
            if (step.Coordinate.row != previousRow + 1)
                return $"Intervening map points must describe every row before the target: {step.Coordinate}.";
            MapPoint? point = run.Map.GetPoint(step.Coordinate);
            if (point is null)
                return $"An intervening map coordinate does not exist: {step.Coordinate}.";
            if (point.PointType != step.MapPointType)
            {
                return $"Intervening map-point kind does not match the map at {step.Coordinate}: " +
                       $"expected {point.PointType}, received {step.MapPointType}.";
            }
            if (step.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss or RoomType.Event)
            {
                return $"Intervening room {step.RoomType} cannot be skipped by a deterministic pre-combat request.";
            }
            previousRow = step.Coordinate.row;
        }

        if (previousRow + 1 != target.row)
            return "InterveningMapPoints must include every map row between the live position and the target.";
        return null;
    }

    internal static PreCombatForecastResult Failure(
        PreCombatForecastStatus status,
        string requestId,
        string error,
        string? diagnosticLogPath = null)
        => new()
        {
            Status = status,
            RequestId = requestId,
            Error = error,
            DiagnosticLogPath = diagnosticLogPath,
        };
}
