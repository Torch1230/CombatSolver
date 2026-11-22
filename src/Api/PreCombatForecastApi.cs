using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CombatSolver.Api;

/// <summary>
/// Public, side-effect-checked entry point for forecasting a known encounter before entering combat.
/// Eligible Standard single-player forecasts run in-process via the Sts2Sim bridge; Dread mode and
/// other unsupported configurations route to the legacy isolated headless game process.
/// </summary>
public static class PreCombatForecastApi
{
    public const int ApiVersion = 7;
    public const int DefaultWorkerIdleTimeoutMilliseconds = 120_000;
    public const int MinimumWorkerIdleTimeoutMilliseconds = 1_000;
    public const int MaximumWorkerIdleTimeoutMilliseconds = 86_400_000;

    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, ActiveProducer> Active = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CachedForecastEntry> Completed = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> LatestCompletedTicket = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> ActiveProducerCount = new(StringComparer.Ordinal);
    private static long _nextCompletionTicket;

    private sealed record ActiveProducer(long Ticket, Task<PreCombatForecastResult> Work);
    private sealed record CachedForecastEntry(PreCombatForecastResult Result, PreCombatBackendKind Backend);

    public static bool IsAvailable => Entry.Enabled
                                      && !Entry.IsPreCombatWorker
                                      && (OperatingSystem.IsWindows()
                                          || PreCombatForecastWorker.HasInstalledInProcessBackend);

    /// <summary>Returns an opaque token suitable for caller-side caching while the run remains unchanged.</summary>
    public static string CaptureLiveStateToken(RunState run) => PreCombatLiveStateSnapshot.CaptureToken(run);

    /// <summary>
    /// Stops the reusable pre-combat session, if one is active. Callers may use this when their forecast UI closes.
    /// </summary>
    public static Task StopWorkerAsync() => PreCombatForecastWorker.StopSessionAsync();

    /// <summary>Returns the current pre-combat worker session and memory state without starting it.</summary>
    public static PreCombatWorkerStatus GetWorkerStatus() => PreCombatForecastWorker.GetStatus();

    /// <summary>Returns an immutable snapshot of active, recent, and eligible pre-combat forecast backends for UI display.</summary>
    internal static PreCombatBackendDisplaySnapshot CaptureBackendDisplaySnapshot()
    {
        if (!Entry.Enabled || Entry.IsPreCombatWorker)
        {
            return new PreCombatBackendDisplaySnapshot(
                PreCombatBackendKind.None,
                IsActiveRunning: false,
                IsBusy: false,
                PreCombatBackendKind.None,
                LastEffectiveParallelism: null,
                LastFallbackCode: null,
                HasInstalledInProcessBackend: false,
                IsBridgeCorrupt: false,
                HostEligibleForInProcess: false,
                HostExclusionCode: null,
                IsWindowsHost: OperatingSystem.IsWindows());
        }
        RunState? liveRun = NGame.IsMainThread() ? RunManager.Instance?.DebugOnlyGetState() : null;
        return PreCombatForecastWorker.CaptureBackendDisplaySnapshot(liveRun);
    }

    /// <summary>
    /// Changes the idle lifetime for the current and future retained worker. Null disables automatic idle shutdown.
    /// If a worker is already idle, its countdown is restarted with the new value.
    /// </summary>
    public static Task SetWorkerIdleTimeoutAsync(int? idleTimeoutMilliseconds)
    {
        ValidateWorkerIdleTimeoutOrThrow(idleTimeoutMilliseconds);
        return PreCombatForecastWorker.ConfigureIdleTimeoutAsync(idleTimeoutMilliseconds);
    }

    /// <summary>
    /// Stops any existing pre-combat session and prewarms the appropriate backend for the supplied run.
    /// </summary>
    public static Task<PreCombatWorkerStatus> RestartWorkerAsync(
        RunState run,
        CancellationToken cancellationToken = default) =>
        RestartWorkerAsync(run, DefaultWorkerIdleTimeoutMilliseconds, cancellationToken);

    /// <summary>
    /// Stops any existing session and prewarms the appropriate backend with the requested idle lifetime. Null keeps
    /// readiness until explicitly stopped.
    /// </summary>
    public static Task<PreCombatWorkerStatus> RestartWorkerAsync(
        RunState run,
        int? idleTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (!Entry.Enabled || Entry.IsPreCombatWorker)
            throw new PlatformNotSupportedException("Pre-combat forecasting is disabled or running inside an isolated worker.");
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("RestartWorkerAsync must be called on the game main thread.");
        if (!IsAvailable)
        {
            string? reason = PreCombatLiveStateSnapshot.TryDescribeDirectIneligibility(run);
            throw new PlatformNotSupportedException(reason is null
                ? "The pre-combat simulator backend is unavailable on this platform."
                : $"The isolated pre-combat worker is required on this platform ({reason}).");
        }
        ValidateWorkerIdleTimeoutOrThrow(idleTimeoutMilliseconds);
        long admissionEpoch = PreCombatForecastWorker.CaptureAdmissionEpoch();
        PreCombatLiveStateSnapshot snapshot = PreCombatLiveStateSnapshot.Capture(run);
        return Task.Run(
            () => PreCombatForecastWorker.RestartSessionAsync(
                snapshot,
                idleTimeoutMilliseconds,
                admissionEpoch,
                cancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// Runs one explicitly hypothetical combat sample at the next map row using the supplied current-act encounter.
    /// The caller chooses the sample seed; the live run and its RNG streams are captured and revalidated but never
    /// mutated.
    /// </summary>
    public static Task<PreCombatForecastResult> SimulateAsync(
        RunState run,
        EncounterModel encounter,
        PreCombatRoomKind roomKind,
        PreCombatSimulationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string requestId = Guid.NewGuid().ToString("N");
        if (!Entry.Enabled || Entry.IsPreCombatWorker)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                "Pre-combat forecasting is disabled or running inside an isolated worker."));
        }
        if (!NGame.IsMainThread())
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                "SimulateAsync must be called on the game main thread."));
        }
        if (!IsAvailable)
        {
            string? reason = PreCombatLiveStateSnapshot.TryDescribeDirectIneligibility(run);
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                reason is null
                    ? "The pre-combat simulator backend is unavailable on this platform."
                    : $"The isolated pre-combat worker is required on this platform ({reason})."));
        }
        if (ModelDb.GetByIdOrNull<EncounterModel>(encounter.Id) is null)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                $"Encounter {encounter.Id} is not registered in the current game build."));
        }

        PreCombatSimulationOptions simulationOptions = options ?? PreCombatSimulationOptions.Default;
        PreCombatForecastOptions workerOptions = new()
        {
            SearchBudgetMilliseconds = simulationOptions.SearchBudgetMilliseconds,
            OverallTimeoutMilliseconds = simulationOptions.OverallTimeoutMilliseconds,
            MaxDegreeOfParallelism = simulationOptions.MaxDegreeOfParallelism,
            CancelWorkerWhenCallerCancels = true,
            ForceRefresh = true,
            CloseWorkerAfterRequest = simulationOptions.CloseWorkerAfterRequest,
            WorkerIdleTimeoutMilliseconds = simulationOptions.WorkerIdleTimeoutMilliseconds,
            SimulationSeed = simulationOptions.SampleSeed,
        };
        MapCoord target = ResolveImmediateSimulationTarget(run);
        if (run.Map.GetPoint(target) is null)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                $"The current map has no point at the hypothetical combat coordinate {target}."));
        }
        PreCombatMapPointKind simulatedMapPointKind = roomKind switch
        {
            PreCombatRoomKind.Normal => PreCombatMapPointKind.Normal,
            PreCombatRoomKind.Elite => PreCombatMapPointKind.Elite,
            PreCombatRoomKind.Boss => PreCombatMapPointKind.Boss,
            _ => throw new ArgumentOutOfRangeException(nameof(roomKind), roomKind, null),
        };
        string? optionError = ValidateOptions(
            workerOptions,
            target.row + 1,
            target.col,
            roomKind,
            simulatedMapPointKind);
        if (optionError is not null)
            return Task.FromResult(Failure(PreCombatForecastStatus.Unsupported, requestId, optionError));

        PreCombatRoomKind encounterRoomKind = encounter.RoomType switch
        {
            RoomType.Monster => PreCombatRoomKind.Normal,
            RoomType.Elite => PreCombatRoomKind.Elite,
            RoomType.Boss => PreCombatRoomKind.Boss,
            _ => roomKind,
        };
        if (encounterRoomKind != roomKind)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                $"Encounter room kind {encounterRoomKind} does not match requested kind {roomKind}."));
        }

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

        long admissionEpoch = PreCombatForecastWorker.CaptureAdmissionEpoch();
        Task<PreCombatForecastResult> worker = Task.Run(
            () => PreCombatForecastWorker.RunAsync(
                snapshot,
                encounter.Id.Entry,
                target.row + 1,
                target.col,
                roomKind,
                simulatedMapPointKind,
                isSecondBoss: false,
                workerOptions,
                admissionEpoch,
                cancellationToken),
            CancellationToken.None);
        return CompleteAfterLiveValidation(
            snapshot,
            worker,
            cancellationToken,
            awaitOwnedWorkerCancellation: true);
    }

    /// <summary>
    /// Runs one hypothetical combat from a caller-owned planning snapshot.
    /// The active run is captured only for the host environment and is revalidated after the solver returns;
    /// the solver evaluates <paramref name="plannedRun" /> independently as its effective run state.
    /// </summary>
    public static Task<PreCombatForecastResult> SimulatePlanningAsync(
        RunState liveRun,
        SerializableRun plannedRun,
        EncounterModel encounter,
        int targetActFloor,
        int targetMapColumn,
        PreCombatRoomKind roomKind,
        PreCombatMapPointKind mapPointKind,
        PreCombatSimulationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string requestId = Guid.NewGuid().ToString("N");
        if (!Entry.Enabled || Entry.IsPreCombatWorker)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                "Pre-combat forecasting is disabled or running inside an isolated worker."));
        }
        if (!NGame.IsMainThread())
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                "SimulatePlanningAsync must be called on the game main thread."));
        }
        if (!IsAvailable)
        {
            string? reason = PreCombatLiveStateSnapshot.TryDescribeDirectIneligibility(liveRun);
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                reason is null
                    ? "The pre-combat simulator backend is unavailable on this platform."
                    : $"The isolated pre-combat worker is required on this platform ({reason})."));
        }
        if (ModelDb.GetByIdOrNull<EncounterModel>(encounter.Id) is null)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                $"Encounter {encounter.Id} is not registered in the current game build."));
        }

        PreCombatSimulationOptions simulationOptions = options ?? PreCombatSimulationOptions.Default;
        string? optionError = ValidateOptions(
            new PreCombatForecastOptions
            {
                SearchBudgetMilliseconds = simulationOptions.SearchBudgetMilliseconds,
                OverallTimeoutMilliseconds = simulationOptions.OverallTimeoutMilliseconds,
                MaxDegreeOfParallelism = simulationOptions.MaxDegreeOfParallelism,
                WorkerIdleTimeoutMilliseconds = simulationOptions.WorkerIdleTimeoutMilliseconds,
            },
            targetActFloor,
            targetMapColumn,
            roomKind,
            mapPointKind);
        if (optionError is not null)
            return Task.FromResult(Failure(PreCombatForecastStatus.Unsupported, requestId, optionError));

        PreCombatEligibilityResult mandatoryHost = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(liveRun);
        if (!mandatoryHost.IsEligible && !OperatingSystem.IsWindows())
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                $"{mandatoryHost.Detail} ({mandatoryHost.FailureCode})"));
        }

        RunState plannedState;
        try
        {
            plannedState = RunState.FromSerializable(plannedRun);
        }
        catch (Exception exception)
        {
            if (!mandatoryHost.IsEligible)
            {
                return Task.FromResult(Failure(
                    PreCombatForecastStatus.Unsupported,
                    requestId,
                    $"{mandatoryHost.Detail} ({mandatoryHost.FailureCode})"));
            }
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                $"The planning run snapshot could not be restored: {exception.GetBaseException().Message}"));
        }
        MapCoord target = new(targetMapColumn, targetActFloor - 1);
        if (plannedState.CurrentActIndex != liveRun.CurrentActIndex
            || plannedRun.SerializableRng.Seed != liveRun.Rng.StringSeed
            || liveRun.Players.Count != 1
            || plannedState.Players.Count != 1
            || plannedState.Players[0].Character.Id != liveRun.Players[0].Character.Id
            || plannedState.Players[0].NetId != liveRun.Players[0].NetId
            || liveRun.Map.GetPoint(target) is null)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                $"The planning snapshot or target {target} does not belong to the active run."));
        }
        PreCombatRoomKind encounterRoomKind = encounter.RoomType switch
        {
            RoomType.Monster => PreCombatRoomKind.Normal,
            RoomType.Elite => PreCombatRoomKind.Elite,
            RoomType.Boss => PreCombatRoomKind.Boss,
            _ => roomKind,
        };
        if (encounterRoomKind != roomKind)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                $"Encounter room kind {encounterRoomKind} does not match requested kind {roomKind}."));
        }

        bool isSecondBoss = liveRun.Map.SecondBossMapPoint?.coord == target;
        PreCombatLiveStateSnapshot snapshot;
        try
        {
            snapshot = PreCombatLiveStateSnapshot.Capture(liveRun)
                .WithPlanningRun(plannedRun, plannedState, targetActFloor, targetMapColumn, isSecondBoss);
        }
        catch (NotSupportedException exception)
        {
            return Task.FromResult(Failure(PreCombatForecastStatus.Unsupported, requestId, exception.Message));
        }
        catch (Exception exception)
        {
            return Task.FromResult(Failure(PreCombatForecastStatus.Failed, requestId, exception.Message));
        }

        PreCombatForecastOptions workerOptions = new()
        {
            SearchBudgetMilliseconds = simulationOptions.SearchBudgetMilliseconds,
            OverallTimeoutMilliseconds = simulationOptions.OverallTimeoutMilliseconds,
            MaxDegreeOfParallelism = simulationOptions.MaxDegreeOfParallelism,
            CancelWorkerWhenCallerCancels = true,
            ForceRefresh = true,
            CloseWorkerAfterRequest = simulationOptions.CloseWorkerAfterRequest,
            WorkerIdleTimeoutMilliseconds = simulationOptions.WorkerIdleTimeoutMilliseconds,
            SimulationSeed = simulationOptions.SampleSeed,
        };
        string encounterId = encounter.Id.Entry;
        long admissionEpoch = PreCombatForecastWorker.CaptureAdmissionEpoch();
        Task<PreCombatForecastResult> worker = Task.Run(
            () => PreCombatForecastWorker.RunAsync(
                snapshot,
                encounterId,
                targetActFloor,
                targetMapColumn,
                roomKind,
                mapPointKind,
                isSecondBoss,
                workerOptions,
                admissionEpoch,
                cancellationToken),
            CancellationToken.None);
        return CompleteAfterLiveValidation(
            snapshot,
            worker,
            cancellationToken,
            awaitOwnedWorkerCancellation: true);
    }

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
        if (!Entry.Enabled || Entry.IsPreCombatWorker)
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                "Pre-combat forecasting is disabled or running inside an isolated worker."));
        }
        if (!NGame.IsMainThread())
        {
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                "ForecastAsync must be called on the game main thread."));
        }
        if (!IsAvailable)
        {
            string? reason = PreCombatLiveStateSnapshot.TryDescribeDirectIneligibility(run);
            return Task.FromResult(Failure(
                PreCombatForecastStatus.Unsupported,
                requestId,
                reason is null
                    ? "The pre-combat simulator backend is unavailable on this platform."
                    : $"The isolated pre-combat worker is required on this platform ({reason})."));
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
        effectiveOptions = effectiveOptions with
        {
            InterveningMapPoints = effectiveOptions.InterveningMapPoints?.ToArray()!,
        };

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

        long admissionEpoch = PreCombatForecastWorker.CaptureAdmissionEpoch();
        string activeKey = string.Join(
            '|',
            key,
            admissionEpoch,
            effectiveOptions.CloseWorkerAfterRequest,
            effectiveOptions.WorkerIdleTimeoutMilliseconds?.ToString() ?? "keep-alive");

        Task<PreCombatForecastResult> worker;
        bool shouldObserveCompletion = false;
        long producerTicket = 0;

        lock (CacheSync)
        {
            if (!effectiveOptions.ForceRefresh
                && Completed.TryGetValue(key, out CachedForecastEntry? completed))
            {
                return CompleteAfterLiveValidation(
                    snapshot,
                    ApplyCachedRequestLifetimeAsync(completed, effectiveOptions, cancellationToken),
                    cancellationToken);
            }

            if (!effectiveOptions.ForceRefresh
                && !effectiveOptions.CancelWorkerWhenCallerCancels
                && Active.TryGetValue(activeKey, out ActiveProducer? existing))
            {
                worker = existing.Work;
            }
            else
            {
                producerTicket = ++_nextCompletionTicket;
                ActiveProducerCount[key] = ActiveProducerCount.GetValueOrDefault(key) + 1;
                CancellationToken workerToken = effectiveOptions.CancelWorkerWhenCallerCancels
                    ? cancellationToken
                    : CancellationToken.None;
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
                        admissionEpoch,
                        workerToken),
                    CancellationToken.None);
                shouldObserveCompletion = true;
                if (!effectiveOptions.ForceRefresh && !effectiveOptions.CancelWorkerWhenCallerCancels)
                    Active[activeKey] = new ActiveProducer(producerTicket, worker);
            }
        }

        if (shouldObserveCompletion)
            _ = CacheCompletionAsync(key, activeKey, producerTicket, worker);

        return CompleteAfterLiveValidation(
            snapshot,
            worker,
            cancellationToken,
            effectiveOptions.CancelWorkerWhenCallerCancels);
    }

    private static async Task<PreCombatForecastResult> ApplyCachedRequestLifetimeAsync(
        CachedForecastEntry cachedEntry,
        PreCombatForecastOptions options,
        CancellationToken cancellationToken)
    {
        await PreCombatForecastWorker.ApplyCachedRequestLifetimeAsync(
            cachedEntry.Backend,
            options.CloseWorkerAfterRequest,
            options.WorkerIdleTimeoutMilliseconds,
            cancellationToken).ConfigureAwait(false);
        return cachedEntry.Result;
    }

    private static async Task CacheCompletionAsync(
        string key,
        string activeKey,
        long producerTicket,
        Task<PreCombatForecastResult> worker)
    {
        try
        {
            PreCombatForecastResult result = await worker.ConfigureAwait(false);
            lock (CacheSync)
            {
                if (result.Status == PreCombatForecastStatus.Succeeded)
                {
                    long latestTicket = LatestCompletedTicket.GetValueOrDefault(key);
                    if (producerTicket >= latestTicket)
                    {
                        if (Completed.Count >= 32 && !Completed.ContainsKey(key))
                        {
                            Completed.Clear();
                            // Keep LatestCompletedTicket entries for any keys still with active producers
                            // so a stale pre-eviction producer cannot overwrite a refresh.
                            foreach (string trackedKey in LatestCompletedTicket.Keys.ToArray())
                            {
                                if (ActiveProducerCount.GetValueOrDefault(trackedKey) <= 0)
                                    LatestCompletedTicket.Remove(trackedKey);
                            }
                        }
                        Completed[key] = new CachedForecastEntry(result, result.ProducingBackend);
                        LatestCompletedTicket[key] = producerTicket;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Error($"[CombatSolver/PreCombatApi] CACHE_COMPLETION_FAILED exception={ex}");
        }
        finally
        {
            lock (CacheSync)
            {
                if (Active.TryGetValue(activeKey, out ActiveProducer? active)
                    && ReferenceEquals(active.Work, worker))
                {
                    Active.Remove(activeKey);
                }
                if (ActiveProducerCount.TryGetValue(key, out int count))
                {
                    if (count <= 1)
                    {
                        ActiveProducerCount.Remove(key);
                        if (!Completed.ContainsKey(key))
                            LatestCompletedTicket.Remove(key);
                    }
                    else
                    {
                        ActiveProducerCount[key] = count - 1;
                    }
                }
            }
        }
    }

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

                completion.TrySetResult(PreCombatForecastWorker.LocalizeResultForDelivery(result));
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
        if (WorkerIdleTimeoutError(options.WorkerIdleTimeoutMilliseconds) is { } idleTimeoutError)
            return idleTimeoutError;
        bool roomMatchesMapPoint = (mapPointKind, roomKind) switch
        {
            (PreCombatMapPointKind.Unknown, _) => true,
            (PreCombatMapPointKind.Normal, PreCombatRoomKind.Normal) => true,
            (PreCombatMapPointKind.Elite, PreCombatRoomKind.Elite) => true,
            (PreCombatMapPointKind.Boss, PreCombatRoomKind.Boss) => true,
            (PreCombatMapPointKind.Event, _) => true,
            _ => false,
        };
        if (!roomMatchesMapPoint)
        {
            return $"Map point kind {mapPointKind} does not match combat room kind {roomKind}.";
        }
        return null;
    }

    private static void ValidateWorkerIdleTimeoutOrThrow(int? idleTimeoutMilliseconds)
    {
        if (WorkerIdleTimeoutError(idleTimeoutMilliseconds) is { } error)
            throw new ArgumentOutOfRangeException(nameof(idleTimeoutMilliseconds), error);
    }

    private static string? WorkerIdleTimeoutError(int? idleTimeoutMilliseconds) =>
        idleTimeoutMilliseconds is < MinimumWorkerIdleTimeoutMilliseconds or > MaximumWorkerIdleTimeoutMilliseconds
            ? $"Worker idle timeout must be null or between {MinimumWorkerIdleTimeoutMilliseconds} and {MaximumWorkerIdleTimeoutMilliseconds} milliseconds."
            : null;

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

    private static MapCoord ResolveImmediateSimulationTarget(RunState run)
    {
        int targetRow = Math.Max(0, run.ActFloor);
        int targetColumn = run.CurrentMapPoint?.Children
                               .OrderBy(static point => point.coord.col)
                               .FirstOrDefault()?.coord.col
                           ?? run.Map.GetPointsInRow(targetRow)
                               .OrderBy(static point => point.coord.col)
                               .FirstOrDefault()?.coord.col
                           ?? run.Map.BossMapPoint.coord.col;
        return new MapCoord(targetColumn, targetRow);
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
