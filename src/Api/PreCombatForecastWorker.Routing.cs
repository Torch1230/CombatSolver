using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Api;

internal static partial class PreCombatForecastWorker
{
    private static long _stopEpoch;
    private static int _inProcessStartCount;
    private static int _inProcessReuseCount;
    private static int _fallbackCount;
    private static int _lastUsedBackend;
    private static int _lastEffectiveParallelism;
    private static string? _lastFallbackCode;
    private static string? _lastFallbackDetail;

    internal static int WorkerStartCountForTesting => Volatile.Read(ref _workerStartCount);

    internal static int WorkerReuseCountForTesting => Volatile.Read(ref _workerReuseCount);

    internal static int InProcessStartCountForTesting => Volatile.Read(ref _inProcessStartCount);

    internal static int InProcessReuseCountForTesting => Volatile.Read(ref _inProcessReuseCount);

    internal static int FallbackCountForTesting => Volatile.Read(ref _fallbackCount);

    internal static PreCombatBackendKind LastUsedBackendForTesting =>
        (PreCombatBackendKind)Volatile.Read(ref _lastUsedBackend);

    internal static int? LastEffectiveParallelismForTesting
    {
        get
        {
            int value = Volatile.Read(ref _lastEffectiveParallelism);
            return value > 0 ? value : null;
        }
    }

    internal static string? LastFallbackCodeForTesting => Volatile.Read(ref _lastFallbackCode);

    internal static string? LastFallbackDetailForTesting => Volatile.Read(ref _lastFallbackDetail);

    internal static bool HasInstalledInProcessBackend => PreCombatSts2SimBridge.HasInstalledBackendArtifact();

    internal static void ResetBackendDisplayState()
    {
        Volatile.Write(ref _lastUsedBackend, (int)PreCombatBackendKind.None);
        Volatile.Write(ref _lastEffectiveParallelism, 0);
        Volatile.Write(ref _lastFallbackCode, null);
        Volatile.Write(ref _lastFallbackDetail, null);
    }

    internal static PreCombatBackendDisplaySnapshot CaptureBackendDisplaySnapshot(RunState? liveRun)
    {
        PreCombatWorkerStatus status = GetStatus();
        bool hasInstalled = HasInstalledInProcessBackend;
        bool isCorrupt = PreCombatSts2SimBridge.IsCorrupt;
        bool hostEligible = false;
        string? hostExclusionCode = null;
        string? hostExclusionDetail = null;
        if (liveRun is not null)
        {
            PreCombatEligibilityResult eligibility = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(liveRun);
            hostEligible = eligibility.IsEligible;
            hostExclusionCode = eligibility.FailureCode;
            hostExclusionDetail = eligibility.Detail;
        }

        return new PreCombatBackendDisplaySnapshot(
            ActiveBackend: status.Backend,
            IsActiveRunning: status.IsRunning,
            IsBusy: status.IsBusy,
            LastUsedBackend: LastUsedBackendForTesting,
            LastEffectiveParallelism: LastEffectiveParallelismForTesting,
            LastFallbackCode: LastFallbackCodeForTesting,
            HasInstalledInProcessBackend: hasInstalled,
            IsBridgeCorrupt: isCorrupt,
            HostEligibleForInProcess: hostEligible,
            HostExclusionCode: hostExclusionCode,
            IsWindowsHost: OperatingSystem.IsWindows(),
            HostExclusionDetail: hostExclusionDetail,
            LastFallbackDetail: Volatile.Read(ref _lastFallbackDetail));
    }

    internal static PreCombatForecastResult LocalizeResultForDelivery(PreCombatForecastResult result) =>
        PreCombatSts2SimPolicyMapper.LocalizeResultOnDelivery(result);

    internal static long CaptureAdmissionEpoch()
    {
        lock (RequestCancellationSync)
            return Volatile.Read(ref _stopEpoch);
    }

    internal static PreCombatWorkerStatus GetStatus()
    {
        bool busy = Volatile.Read(ref _workerBusy) != 0;
        WorkerSession? session = Volatile.Read(ref _session);
        if (session is not null)
        {
            try
            {
                if (!session.Process.HasExited)
                {
                    session.Process.Refresh();
                    return new PreCombatWorkerStatus
                    {
                        IsRunning = true,
                        IsBusy = busy,
                        Backend = PreCombatBackendKind.LegacyProcess,
                        ProcessId = session.Process.Id,
                        WorkingSetBytes = session.Process.WorkingSet64,
                        PrivateMemoryBytes = session.Process.PrivateMemorySize64,
                        PeakWorkingSetBytes = session.Process.PeakWorkingSet64,
                        AudioMuted = session.AudioMuted,
                        IdleTimeoutMilliseconds = GetIdleTimeoutMilliseconds(),
                    };
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        if (Volatile.Read(ref _inProcessSession) is not null)
        {
            return new PreCombatWorkerStatus
            {
                IsRunning = true,
                IsBusy = busy,
                Backend = PreCombatBackendKind.InProcess,
                ProcessId = null,
                WorkingSetBytes = null,
                PrivateMemoryBytes = null,
                PeakWorkingSetBytes = null,
                AudioMuted = true,
                IdleTimeoutMilliseconds = GetIdleTimeoutMilliseconds(),
            };
        }

        return StoppedStatus(busy);
    }

    public static async Task<PreCombatForecastResult> RunAsync(
        PreCombatLiveStateSnapshot snapshot,
        string encounterId,
        int targetActFloor,
        int targetMapColumn,
        PreCombatRoomKind roomKind,
        PreCombatMapPointKind mapPointKind,
        bool isSecondBoss,
        PreCombatForecastOptions options,
        long admissionEpoch,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        bool gateEntered = false;
        CancellationToken callerToken = cancellationToken;
        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        using CancellationTokenSource timeoutCancellation = new();
        timeoutCancellation.CancelAfter(options.OverallTimeoutMilliseconds);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellation.Token,
            timeoutCancellation.Token);
        try
        {
            await Gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            gateEntered = true;
            lock (RequestCancellationSync)
            {
                if (Volatile.Read(ref _stopEpoch) != admissionEpoch || _shuttingDown)
                {
                    return PreCombatForecastApi.Failure(
                        PreCombatForecastStatus.Cancelled,
                        requestId,
                        "The pre-combat request was stopped before starting.");
                }
                _activeRequestCancellation = requestCancellation;
            }

            Interlocked.Exchange(ref _workerBusy, 1);
            CancelIdleShutdown();
            SetIdleWorkerLifetime(options.WorkerIdleTimeoutMilliseconds);

            string? fallbackCode = snapshot.Eligibility.IsEligible ? null : snapshot.Eligibility.FailureCode;
            string? fallbackDetail = snapshot.Eligibility.IsEligible ? null : snapshot.Eligibility.Detail;
            if (snapshot.Eligibility.IsEligible)
            {
                if (!PreCombatSts2SimPolicyMapper.TryBuildPolicy(
                        snapshot.SolverSettingsData,
                        snapshot.SolverSettingsSnapshot,
                        options.SearchBudgetMilliseconds,
                        snapshot.EffectivePotions,
                        out Sts2SimPolicyDto? bridgePolicy,
                        out string? policyReason))
                {
                    fallbackCode = "policy_unsupported";
                    fallbackDetail = policyReason;
                    Volatile.Write(ref _lastFallbackCode, fallbackCode);
                    Volatile.Write(ref _lastFallbackDetail, fallbackDetail);
                    Entry.Logger.Info(
                        $"[CombatSolver/PreCombatApi] STS2SIM_FALLBACK request_id={requestId} " +
                        $"code={fallbackCode} detail={fallbackDetail}");
                }
                else
                {
                    PreCombatBridgeAvailability bridgeState = PreCombatSts2SimBridge.EnsureActivated(out string? bridgeError);
                    if (bridgeState == PreCombatBridgeAvailability.CorruptOrIncompatible)
                    {
                        return PreCombatForecastApi.Failure(
                            PreCombatForecastStatus.Failed,
                            requestId,
                            bridgeError ?? "The installed sts2sim backend is corrupt or incompatible.");
                    }

                    if (bridgeState == PreCombatBridgeAvailability.Ready)
                    {
                        PreCombatForecastResult? bridgeResult = await TryRunInProcessLockedAsync(
                            requestId,
                            snapshot,
                            encounterId,
                            targetActFloor,
                            targetMapColumn,
                            roomKind,
                            mapPointKind,
                            isSecondBoss,
                            options,
                            bridgePolicy!,
                            requestCancellation,
                            timeoutCancellation.Token,
                            deadline.Token).ConfigureAwait(false);
                        if (bridgeResult is not null)
                            return bridgeResult;
                    }
                    else
                    {
                        fallbackCode = "bridge_absent";
                        fallbackDetail = bridgeError ?? "The optional sts2sim backend is not installed.";
                        Volatile.Write(ref _lastFallbackCode, fallbackCode);
                        Volatile.Write(ref _lastFallbackDetail, fallbackDetail);
                        Entry.Logger.Info(
                            $"[CombatSolver/PreCombatApi] STS2SIM_FALLBACK request_id={requestId} code=bridge_absent field=sts2sim");
                    }
                }
            }
            else
            {
                Entry.Logger.Info(
                    $"[CombatSolver/PreCombatApi] STS2SIM_DIRECT_LEGACY request_id={requestId} " +
                    $"code={snapshot.Eligibility.FailureCode} detail={snapshot.Eligibility.Detail}");
            }

            // Re-check cancellation & overall deadline before falling back to legacy worker.
            deadline.Token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                string code = fallbackCode ?? "unsupported";
                string reason = fallbackDetail
                    ?? $"Pre-combat forecast requires legacy Windows worker ({code}).";
                Volatile.Write(ref _lastFallbackCode, code);
                Volatile.Write(ref _lastFallbackDetail, reason);
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Unsupported,
                    requestId,
                    $"{reason} ({code})");
            }

            Interlocked.Increment(ref _fallbackCount);
            StopCurrentInProcessSession();
            PreCombatForecastResult legacyResult = await RunLegacyLockedAsync(
                requestId,
                snapshot,
                encounterId,
                targetActFloor,
                targetMapColumn,
                roomKind,
                mapPointKind,
                isSecondBoss,
                options,
                requestCancellation,
                timeoutCancellation.Token,
                deadline.Token).ConfigureAwait(false);
            if (legacyResult.IsSuccess)
            {
                Volatile.Write(ref _lastUsedBackend, (int)PreCombatBackendKind.LegacyProcess);
                Volatile.Write(ref _lastEffectiveParallelism, 0);
                if (fallbackCode is not null)
                {
                    Volatile.Write(ref _lastFallbackCode, fallbackCode);
                    Volatile.Write(ref _lastFallbackDetail, fallbackDetail);
                }
            }
            return legacyResult;
        }
        catch (OperationCanceledException ex)
        {
            if (gateEntered)
            {
                StopCurrentSession();
                StopCurrentInProcessSession();
            }
            if (requestCancellation.IsCancellationRequested)
            {
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Cancelled,
                    requestId,
                    "The pre-combat request was cancelled.");
            }
            if (timeoutCancellation.IsCancellationRequested)
            {
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.TimedOut,
                    requestId,
                    $"The pre-combat request exceeded {options.OverallTimeoutMilliseconds} ms.");
            }
            Entry.Logger.Error(
                $"[CombatSolver/PreCombatApi] WORKER_UNEXPECTED_CANCELLATION request_id={requestId} exception={ex}");
            return PreCombatForecastApi.Failure(
                PreCombatForecastStatus.Failed,
                requestId,
                ex.GetBaseException().Message);
        }
        catch (Exception ex)
        {
            if (gateEntered)
            {
                StopCurrentSession();
                StopCurrentInProcessSession();
            }
            Entry.Logger.Error(
                $"[CombatSolver/PreCombatApi] WORKER_FAILED request_id={requestId} exception={ex}");
            return PreCombatForecastApi.Failure(
                PreCombatForecastStatus.Failed,
                requestId,
                ex.GetBaseException().Message);
        }
        finally
        {
            if (gateEntered)
            {
                lock (RequestCancellationSync)
                    _activeRequestCancellation = null;
                Interlocked.Exchange(ref _workerBusy, 0);
                Gate.Release();
            }
        }
    }

    private static async Task<PreCombatForecastResult?> TryRunInProcessLockedAsync(
        string requestId,
        PreCombatLiveStateSnapshot snapshot,
        string encounterId,
        int targetActFloor,
        int targetMapColumn,
        PreCombatRoomKind roomKind,
        PreCombatMapPointKind mapPointKind,
        bool isSecondBoss,
        PreCombatForecastOptions options,
        Sts2SimPolicyDto policy,
        CancellationTokenSource requestCancellation,
        CancellationToken timeoutToken,
        CancellationToken deadlineToken)
    {
        StopCurrentSession();
        bool reusedSession = Volatile.Read(ref _inProcessSession) is not null;
        object inProcessSession = Volatile.Read(ref _inProcessSession) ?? new object();
        if (!reusedSession)
        {
            Volatile.Write(ref _inProcessSession, inProcessSession);
            Interlocked.Increment(ref _inProcessStartCount);
        }
        else
        {
            Interlocked.Increment(ref _inProcessReuseCount);
        }

        string saveJson = Encoding.UTF8.GetString(snapshot.SerializedPlanningRun ?? snapshot.SerializedRun);
        var request = new Sts2SimBridgeRequestDto(
            PreCombatSts2SimAbi.Version,
            requestId,
            saveJson,
            snapshot.HasLiveSidecar,
            snapshot.HasLiveSidecar ? snapshot.LiveActFloor : null,
            snapshot.HasLiveSidecar ? snapshot.NextRoomId : null,
            snapshot.HasLiveSidecar ? snapshot.SpoilsMapVerification : null,
            encounterId,
            targetActFloor,
            targetMapColumn,
            roomKind.ToString(),
            mapPointKind.ToString(),
            isSecondBoss,
            options.InterveningMapPoints
                .Select(static step => new Sts2SimMapStepDto(
                    new Sts2SimMapCoordDto(step.Coordinate.col, step.Coordinate.row),
                    step.RoomType.ToString(),
                    step.MapPointType.ToString()))
                .ToArray(),
            options.PlayerCurrentHpOverride,
            options.SimulationSeed,
            options.SearchBudgetMilliseconds,
            options.OverallTimeoutMilliseconds,
            options.MaxDegreeOfParallelism ?? snapshot.SolverSettingsSnapshot.SearchMaxDegreeOfParallelism,
            policy);

        int requestedDop = Math.Max(1, request.MaxDegreeOfParallelism);
        Sts2SimBridgeResponseDto response;
        try
        {
            deadlineToken.ThrowIfCancellationRequested();
            PreCombatSts2SimBridge.Prewarm();
            deadlineToken.ThrowIfCancellationRequested();
            Entry.Logger.Info(
                $"[CombatSolver/PreCombatApi] INPROCESS_REQUEST request_id={requestId} reused={reusedSession} encounter={encounterId}");
            response = await PreCombatSts2SimBridge.RunAsync(request, deadlineToken).ConfigureAwait(false);
            deadlineToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            StopCurrentInProcessSession();
            return PreCombatForecastApi.Failure(
                PreCombatForecastStatus.Cancelled,
                requestId,
                "The in-process pre-combat request was cancelled.");
        }
        catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
        {
            StopCurrentInProcessSession();
            return PreCombatForecastApi.Failure(
                PreCombatForecastStatus.TimedOut,
                requestId,
                $"The in-process simulator exceeded {options.OverallTimeoutMilliseconds} ms.");
        }
        catch (OperationCanceledException ex)
        {
            StopCurrentInProcessSession();
            Entry.Logger.Error(
                $"[CombatSolver/PreCombatApi] INPROCESS_UNEXPECTED_CANCELLATION request_id={requestId} exception={ex}");
            return PreCombatForecastApi.Failure(
                PreCombatForecastStatus.Failed,
                requestId,
                ex.GetBaseException().Message);
        }

        switch (response.Outcome)
        {
            case "Succeeded":
                if (response.EffectiveDegreeOfParallelism is not { } effectiveDop
                    || effectiveDop < 1
                    || effectiveDop > requestedDop)
                {
                    StopCurrentInProcessSession();
                    return PreCombatForecastApi.Failure(
                        PreCombatForecastStatus.Failed,
                        requestId,
                        $"The in-process simulator reported out-of-range EffectiveDegreeOfParallelism={response.EffectiveDegreeOfParallelism} (requested={requestedDop}).");
                }
                PreCombatForecastConfidence confidence = Enum.Parse<PreCombatForecastConfidence>(response.Confidence!, ignoreCase: false);
                PreCombatPotionUse[] potionUses = response.PotionUses!
                    .Select(static use => new PreCombatPotionUse(use.Id, use.Id, use.Turn!.Value, use.Slot!.Value))
                    .ToArray();
                if (options.CloseWorkerAfterRequest)
                    StopCurrentInProcessSession();
                else
                    ScheduleInProcessIdleShutdown(inProcessSession);
                Volatile.Write(ref _lastUsedBackend, (int)PreCombatBackendKind.InProcess);
                Volatile.Write(ref _lastEffectiveParallelism, effectiveDop);
                Volatile.Write(ref _lastFallbackCode, null);
                Volatile.Write(ref _lastFallbackDetail, null);
                return new PreCombatForecastResult
                {
                    Status = PreCombatForecastStatus.Succeeded,
                    RequestId = requestId,
                    ProjectedHpLoss = response.ProjectedHpLoss,
                    PotionUses = potionUses,
                    SearchBoundary = response.SearchBoundary,
                    Confidence = confidence,
                    FinalHp = response.FinalHp,
                    CombatEndedTurn = response.CombatEndedTurn,
                    SearchElapsedMilliseconds = response.SearchElapsedMilliseconds,
                    TotalElapsedMilliseconds = response.TotalElapsedMilliseconds,
                    ProducingBackend = PreCombatBackendKind.InProcess,
                    EffectiveDegreeOfParallelism = effectiveDop,
                };

            case "CapabilityMiss":
                Volatile.Write(ref _lastFallbackCode, response.FailureCode);
                Volatile.Write(ref _lastFallbackDetail, response.Error);
                Entry.Logger.Info(
                    $"[CombatSolver/PreCombatApi] STS2SIM_FALLBACK request_id={requestId} " +
                    $"code={response.FailureCode} field={response.FieldPath ?? "-"} error={response.Error}");
                StopCurrentInProcessSession();
                if (!OperatingSystem.IsWindows())
                {
                    return PreCombatForecastApi.Failure(
                        PreCombatForecastStatus.Unsupported,
                        requestId,
                        $"{response.Error} ({response.FailureCode})");
                }
                return null;

            case "Cancelled":
                StopCurrentInProcessSession();
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Cancelled,
                    requestId,
                    response.Error ?? "The in-process pre-combat request was cancelled.");

            case "TimedOut":
                StopCurrentInProcessSession();
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.TimedOut,
                    requestId,
                    response.Error ?? $"The in-process simulator exceeded {options.OverallTimeoutMilliseconds} ms.");

            default:
                StopCurrentInProcessSession();
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Failed,
                    requestId,
                    response.Error ?? "The in-process pre-combat request failed.");
        }
    }

    public static async Task<PreCombatWorkerStatus> RestartSessionAsync(
        PreCombatLiveStateSnapshot snapshot,
        int? idleTimeoutMilliseconds,
        long admissionEpoch,
        CancellationToken cancellationToken)
    {
        bool gateEntered = false;
        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Gate.WaitAsync(requestCancellation.Token).ConfigureAwait(false);
            gateEntered = true;
            lock (RequestCancellationSync)
            {
                if (Volatile.Read(ref _stopEpoch) != admissionEpoch || _shuttingDown)
                    throw new OperationCanceledException("The pre-combat worker restart was stopped before admission.");
                _activeRequestCancellation = requestCancellation;
            }

            Interlocked.Exchange(ref _workerBusy, 1);
            CancelIdleShutdown();
            SetIdleWorkerLifetime(idleTimeoutMilliseconds);
            StopCurrentSession();
            StopCurrentInProcessSession();

            string? fallbackCode = snapshot.Eligibility.IsEligible ? null : snapshot.Eligibility.FailureCode;
            string? fallbackDetail = snapshot.Eligibility.IsEligible ? null : snapshot.Eligibility.Detail;
            bool activatedInProcess = false;
            if (snapshot.Eligibility.IsEligible)
            {
                if (!PreCombatSts2SimPolicyMapper.TryBuildPolicy(
                        snapshot.SolverSettingsData,
                        snapshot.SolverSettingsSnapshot,
                        1_000,
                        snapshot.EffectivePotions,
                        out _,
                        out string? policyReason))
                {
                    fallbackCode = "policy_unsupported";
                    fallbackDetail = policyReason;
                }
                else
                {
                    PreCombatBridgeAvailability bridgeState = PreCombatSts2SimBridge.EnsureActivated(out string? bridgeError);
                    if (bridgeState == PreCombatBridgeAvailability.CorruptOrIncompatible)
                        throw new InvalidOperationException(bridgeError ?? "The installed sts2sim backend is corrupt or incompatible.");
                    if (bridgeState == PreCombatBridgeAvailability.Ready)
                    {
                        requestCancellation.Token.ThrowIfCancellationRequested();
                        PreCombatSts2SimBridge.Prewarm();
                        requestCancellation.Token.ThrowIfCancellationRequested();
                        object inProcessSession = new();
                        Volatile.Write(ref _inProcessSession, inProcessSession);
                        Interlocked.Increment(ref _inProcessStartCount);
                        ScheduleInProcessIdleShutdown(inProcessSession);
                        activatedInProcess = true;
                    }
                    else
                    {
                        fallbackCode = "bridge_absent";
                        fallbackDetail = bridgeError;
                    }
                }
            }

            if (!activatedInProcess)
            {
                if (!OperatingSystem.IsWindows())
                {
                    string code = fallbackCode ?? "unsupported";
                    string detail = fallbackDetail
                        ?? $"Pre-combat forecast worker requires Windows for fallback ({code}).";
                    throw new PlatformNotSupportedException($"{detail} ({code})");
                }

                requestCancellation.Token.ThrowIfCancellationRequested();
                string runtimeRoot = RuntimeRoot(snapshot.GameRoot);
                EnsureRuntimeRoot(runtimeRoot, snapshot.GameRoot);
                WorkerSession session = CreateSession(
                    snapshot,
                    runtimeRoot,
                    BuildSessionSignature(snapshot),
                    requestCancellation.Token);
                ScheduleIdleShutdown(session);
            }
        }
        finally
        {
            if (gateEntered)
            {
                lock (RequestCancellationSync)
                    _activeRequestCancellation = null;
                Interlocked.Exchange(ref _workerBusy, 0);
                Gate.Release();
            }
        }
        return GetStatus();
    }

    public static Task ConfigureIdleTimeoutAsync(int? idleTimeoutMilliseconds) =>
        ApplyCachedRequestLifetimeAsync(
            PreCombatBackendKind.None,
            false,
            idleTimeoutMilliseconds,
            CancellationToken.None);

    // A cache hit owns no running request: wait for the reusable barrier rather than
    // using StopSessionAsync, which would cancel another caller's active search.
    // Cache hits apply lifetime changes only to sessions belonging to the cached backend.
    internal static async Task ApplyCachedRequestLifetimeAsync(
        PreCombatBackendKind targetBackend,
        bool closeWorker,
        int? idleTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkerSession? session = Volatile.Read(ref _session);
            bool legacyRunning = session is not null && !session.Process.HasExited;
            object? inProcessSession = Volatile.Read(ref _inProcessSession);
            bool inProcessRunning = inProcessSession is not null;

            if (targetBackend == PreCombatBackendKind.InProcess)
            {
                if (legacyRunning)
                    return;
                SetIdleWorkerLifetime(idleTimeoutMilliseconds);
                if (!inProcessRunning)
                {
                    CancelIdleShutdown();
                    return;
                }
                if (closeWorker)
                {
                    CancelIdleShutdown();
                    StopCurrentInProcessSession();
                }
                else
                {
                    ScheduleInProcessIdleShutdown(inProcessSession!);
                }
                return;
            }

            if (targetBackend == PreCombatBackendKind.LegacyProcess)
            {
                if (inProcessRunning)
                    return;
                SetIdleWorkerLifetime(idleTimeoutMilliseconds);
                if (!legacyRunning)
                {
                    CancelIdleShutdown();
                    return;
                }
                if (closeWorker)
                {
                    CancelIdleShutdown();
                    StopCurrentSession();
                }
                else
                {
                    ScheduleIdleShutdown(session!);
                }
                return;
            }

            SetIdleWorkerLifetime(idleTimeoutMilliseconds);
            if (closeWorker)
            {
                CancelIdleShutdown();
                StopCurrentSession();
                StopCurrentInProcessSession();
                return;
            }
            if (legacyRunning)
            {
                ScheduleIdleShutdown(session!);
                return;
            }
            if (inProcessRunning)
            {
                ScheduleInProcessIdleShutdown(inProcessSession!);
                return;
            }
            CancelIdleShutdown();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopSessionAsync()
    {
        lock (RequestCancellationSync)
        {
            Interlocked.Increment(ref _stopEpoch);
            _activeRequestCancellation?.Cancel();
        }
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelIdleShutdown();
            StopCurrentSession();
            StopCurrentInProcessSession();
        }
        finally
        {
            Gate.Release();
        }
    }
}
