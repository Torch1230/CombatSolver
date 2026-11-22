using System.Text;
using System.Text.Json;
using CombatSolver.Api;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

int passed = 0;

static RunState NewRun() => RunManager.Instance.Run = new();
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
static byte[] JsonBytes(string json) => Encoding.UTF8.GetBytes(json);

static string SucceededWireJson(int loss = 4, int finalHp = 76) =>
    $"{{\"abiVersion\":1,\"outcome\":\"Succeeded\",\"failureCode\":null,\"fieldPath\":null,\"error\":null," +
    $"\"projectedHpLoss\":{loss},\"finalHp\":{finalHp},\"combatEndedTurn\":2,\"onlyDeathRoutes\":false," +
    $"\"confidence\":\"Complete\",\"searchBoundary\":\"None\",\"potionUses\":[],\"expandedNodes\":40," +
    $"\"effectiveDegreeOfParallelism\":1,\"searchElapsedMilliseconds\":5.0,\"totalElapsedMilliseconds\":6.0}}";

static string CapabilityMissWireJson(string code = "encounter_unmapped") =>
    $"{{\"abiVersion\":1,\"outcome\":\"CapabilityMiss\",\"failureCode\":\"{code}\"," +
    $"\"fieldPath\":\"encounterId\",\"error\":\"Missing encounter.\",\"effectiveDegreeOfParallelism\":1," +
    $"\"totalElapsedMilliseconds\":1.5}}";

async Task Check(string name, Func<Task> body)
{
    PreCombatForecastWorker.ResetForRoutingTests();
    CombatSolver.Api.OperatingSystem.WindowsOverride = true;
    await PreCombatForecastApi.StopWorkerAsync();
    try
    {
        await body().WaitAsync(TimeSpan.FromSeconds(20));
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    finally
    {
        await PreCombatForecastApi.StopWorkerAsync();
        PreCombatForecastWorker.ResetForRoutingTests();
        CombatSolver.Api.OperatingSystem.WindowsOverride = true;
    }
}

await Check("Windows CapabilityMiss hands off to legacy under continuous busy lease and shared deadline", async () =>
{
    PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.Ready;
    PreCombatSts2SimBridge.RunRawJsonHookForTesting = (_, _) => Task.FromResult(JsonBytes(CapabilityMissWireJson()));

    bool sawBusyMidLegacy = false;
    PreCombatForecastWorker.LegacyHandler = call =>
    {
        Equal(true, call.BusyUponEntry);
        Equal(true, call.InProcessClearedBeforeLegacy);
        Equal(false, call.DeadlineToken.IsCancellationRequested);
        sawBusyMidLegacy = PreCombatForecastApi.GetWorkerStatus().IsBusy;
        return Task.FromResult(new PreCombatForecastResult
        {
            Status = PreCombatForecastStatus.Succeeded,
            RequestId = call.RequestId,
            ProjectedHpLoss = 7,
            FinalHp = 73,
            CombatEndedTurn = 3,
            Confidence = PreCombatForecastConfidence.Complete,
            SearchBoundary = "None",
            ProducingBackend = PreCombatBackendKind.LegacyProcess,
        });
    };

    PreCombatForecastResult result = await PreCombatForecastApi.ForecastAsync(
        NewRun(), new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: new() { ForceRefresh = true, SearchBudgetMilliseconds = 2_000, OverallTimeoutMilliseconds = 15_000 });

    Equal(PreCombatForecastStatus.Succeeded, result.Status);
    Equal(PreCombatBackendKind.LegacyProcess, result.ProducingBackend);
    Equal(7, result.ProjectedHpLoss);
    Equal(true, sawBusyMidLegacy);
    Equal(false, PreCombatForecastApi.GetWorkerStatus().IsBusy);
    Equal(1, PreCombatForecastWorker.FallbackCountForTesting);
    Equal(1, PreCombatForecastWorker.LegacyCalls.Count);
    PreCombatBackendDisplaySnapshot missSnapshot = PreCombatForecastApi.CaptureBackendDisplaySnapshot();
    Equal(PreCombatBackendKind.LegacyProcess, missSnapshot.LastUsedBackend);
    Equal("encounter_unmapped", missSnapshot.LastFallbackCode);
    Equal("Missing encounter.", missSnapshot.LastFallbackDetail);
});

await Check("Malformed negative ABI fixtures fail closed without fallback or cache publication", async () =>
{
    string fixturePath = Path.Combine(AppContext.BaseDirectory, "abi-v1-fixtures.json");
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
    PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.Ready;
    RunState run = NewRun();

    foreach (JsonProperty entry in doc.RootElement.GetProperty("invalidResponses").EnumerateObject())
    {
        string rawJson = entry.Value.GetRawText();
        PreCombatSts2SimBridge.RunRawJsonHookForTesting = (_, _) => Task.FromResult(JsonBytes(rawJson));
        PreCombatForecastResult bad = await PreCombatForecastApi.ForecastAsync(
            run, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
            options: new() { SearchBudgetMilliseconds = 2_000, OverallTimeoutMilliseconds = 15_000 });
        Equal(PreCombatForecastStatus.Failed, bad.Status);
        Equal(0, PreCombatForecastWorker.FallbackCountForTesting);
        Equal(0, PreCombatForecastWorker.LegacyCalls.Count);
    }

    // Confirm no failed result poisoned the cache: a valid response now executes and succeeds.
    PreCombatSts2SimBridge.RunRawJsonHookForTesting = (_, _) => Task.FromResult(JsonBytes(SucceededWireJson(5, 75)));
    PreCombatForecastResult recovered = await PreCombatForecastApi.ForecastAsync(
        run, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: new() { SearchBudgetMilliseconds = 2_000, OverallTimeoutMilliseconds = 15_000 });
    Equal(PreCombatForecastStatus.Succeeded, recovered.Status);
    Equal(5, recovered.ProjectedHpLoss);
    Equal(0, PreCombatForecastWorker.FallbackCountForTesting);
});

await Check("Parallel EffectiveDegreeOfParallelism=4 succeeds within requested cap and rejects overshoot", async () =>
{
    string fixturePath = Path.Combine(AppContext.BaseDirectory, "abi-v1-fixtures.json");
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
    string parallelJson = doc.RootElement.GetProperty("responses")
        .GetProperty("succeededParallelVictory")
        .GetRawText();

    PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.Ready;
    PreCombatSts2SimBridge.RunRawJsonHookForTesting = (_, _) => Task.FromResult(JsonBytes(parallelJson));

    PreCombatForecastResult ok = await PreCombatForecastApi.ForecastAsync(
        NewRun(), new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: new()
        {
            ForceRefresh = true,
            SearchBudgetMilliseconds = 2_000,
            OverallTimeoutMilliseconds = 15_000,
            MaxDegreeOfParallelism = 4,
        });
    Equal(PreCombatForecastStatus.Succeeded, ok.Status);
    Equal(PreCombatBackendKind.InProcess, ok.ProducingBackend);
    Equal(4, ok.EffectiveDegreeOfParallelism);
    Equal(PreCombatBackendKind.InProcess, PreCombatForecastWorker.LastUsedBackendForTesting);
    Equal(4, PreCombatForecastWorker.LastEffectiveParallelismForTesting);
    Equal<string?>(null, PreCombatForecastWorker.LastFallbackCodeForTesting);
    Equal(0, PreCombatForecastWorker.FallbackCountForTesting);

    PreCombatForecastResult overshoot = await PreCombatForecastApi.ForecastAsync(
        NewRun(), new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: new()
        {
            ForceRefresh = true,
            SearchBudgetMilliseconds = 2_000,
            OverallTimeoutMilliseconds = 15_000,
            MaxDegreeOfParallelism = 2,
        });
    Equal(PreCombatForecastStatus.Failed, overshoot.Status);
    Equal(0, PreCombatForecastWorker.FallbackCountForTesting);
    Equal(0, PreCombatForecastWorker.LegacyCalls.Count);
});

await Check("Exhaustive mixed-pool selected defeat under forced Foul Potion succeeds without fallback", async () =>
{
    string fixturePath = Path.Combine(AppContext.BaseDirectory, "abi-v1-fixtures.json");
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
    string mixedPoolJson = doc.RootElement.GetProperty("responses")
        .GetProperty("succeededMixedPoolForcedPotionLoss")
        .GetRawText();

    PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.Ready;
    PreCombatSts2SimBridge.RunRawJsonHookForTesting = (_, _) => Task.FromResult(JsonBytes(mixedPoolJson));
    PreCombatForecastResult result = await PreCombatForecastApi.ForecastAsync(
        NewRun(), new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: new() { ForceRefresh = true, SearchBudgetMilliseconds = 2_000, OverallTimeoutMilliseconds = 15_000 });

    Equal(PreCombatForecastStatus.Succeeded, result.Status);
    Equal(0, result.FinalHp);
    Equal(1, result.ProjectedHpLoss);
    Equal<int?>(null, result.CombatEndedTurn);
    Equal(PreCombatForecastConfidence.Bounded, result.Confidence);
    Equal("None", result.SearchBoundary);
    Equal(1, result.PotionUses.Count);
    Equal("FOUL_POTION", result.PotionUses[0].Id);
    Equal(0, result.PotionUses[0].Slot);
    Equal(0, PreCombatForecastWorker.FallbackCountForTesting);
    Equal(0, PreCombatForecastWorker.LegacyCalls.Count);
});

await Check("Caller cancellation, timeout expiry, and unrelated cancellation around handoff", async () =>
{
    PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.Ready;

    // 1. Caller cancellation during CapabilityMiss before legacy handoff.
    using var callerCts = new CancellationTokenSource();
    PreCombatSts2SimBridge.RunRawJsonHookForTesting = (_, _) =>
    {
        callerCts.Cancel();
        return Task.FromResult(JsonBytes(CapabilityMissWireJson()));
    };
    PreCombatForecastResult cancelled = await PreCombatForecastApi.ForecastAsync(
        NewRun(), new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: new() { ForceRefresh = true, CancelWorkerWhenCallerCancels = true, SearchBudgetMilliseconds = 1_000, OverallTimeoutMilliseconds = 15_000 },
        cancellationToken: callerCts.Token);
    Equal(PreCombatForecastStatus.Cancelled, cancelled.Status);
    Equal(0, PreCombatForecastWorker.LegacyCalls.Count);
    Equal(0, PreCombatForecastWorker.FallbackCountForTesting);

    // 2. Unrelated OperationCanceledException under live deadline in both InProcess and Legacy fails closed.
    PreCombatSts2SimBridge.RunRawJsonHookForTesting = (_, _) =>
        Task.FromException<byte[]>(new OperationCanceledException("unrelated-inprocess"));
    PreCombatForecastResult inprocOce = await PreCombatForecastApi.ForecastAsync(
        NewRun(), new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: new() { ForceRefresh = true, SearchBudgetMilliseconds = 1_000, OverallTimeoutMilliseconds = 30_000 });
    Equal(PreCombatForecastStatus.Failed, inprocOce.Status);
    Equal(0, PreCombatForecastWorker.LegacyCalls.Count);

    PreCombatSts2SimBridge.RunRawJsonHookForTesting = (_, _) => Task.FromResult(JsonBytes(CapabilityMissWireJson()));
    PreCombatForecastWorker.LegacyHandler = _ =>
        Task.FromException<PreCombatForecastResult>(new OperationCanceledException("unrelated-legacy"));
    PreCombatForecastResult legacyOce = await PreCombatForecastApi.ForecastAsync(
        NewRun(), new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: new() { ForceRefresh = true, SearchBudgetMilliseconds = 1_000, OverallTimeoutMilliseconds = 30_000 });
    Equal(PreCombatForecastStatus.Failed, legacyOce.Status);
    Equal(1, PreCombatForecastWorker.LegacyCalls.Count);

    // 3. Deadline expiry during fallback yields TimedOut.
    PreCombatForecastWorker.LegacyHandler = async call =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, call.DeadlineToken);
        throw new InvalidOperationException("Unreachable");
    };
    PreCombatForecastResult timedOut = await PreCombatForecastApi.ForecastAsync(
        NewRun(), new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: new() { ForceRefresh = true, SearchBudgetMilliseconds = 1_000, OverallTimeoutMilliseconds = 11_000 });
    // Note: overallTimeout minimum is 11000ms, so trigger timeout quickly via pre-delayed hook instead:
    Equal(PreCombatForecastStatus.TimedOut, timedOut.Status);
});

await Check("Cached InProcess hit preserves active LegacyProcess session and closes only InProcess session", async () =>
{
    PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.Ready;
    PreCombatSts2SimBridge.RunRawJsonHookForTesting = (_, _) => Task.FromResult(JsonBytes(SucceededWireJson(3, 77)));

    RunState inProcRun = NewRun();
    PreCombatForecastOptions sharedOpts = new()
    {
        SearchBudgetMilliseconds = 2_000,
        OverallTimeoutMilliseconds = 15_000,
        WorkerIdleTimeoutMilliseconds = 120_000,
    };
    PreCombatForecastResult fill = await PreCombatForecastApi.ForecastAsync(
        inProcRun, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal, options: sharedOpts);
    Equal(PreCombatForecastStatus.Succeeded, fill.Status);
    Equal(PreCombatBackendKind.InProcess, fill.ProducingBackend);
    Equal(PreCombatBackendKind.InProcess, PreCombatForecastApi.GetWorkerStatus().Backend);

    // Now switch active backend to LegacyProcess on a different run.
    RunState dreadRun = NewRun();
    dreadRun.MandatoryExclusionCode = "dread_mode";
    PreCombatForecastResult legacyRun = await PreCombatForecastApi.ForecastAsync(
        dreadRun, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal, options: sharedOpts);
    Equal(PreCombatForecastStatus.Succeeded, legacyRun.Status);
    Equal(PreCombatBackendKind.LegacyProcess, legacyRun.ProducingBackend);
    PreCombatWorkerStatus beforeHit = PreCombatForecastApi.GetWorkerStatus();
    Equal(PreCombatBackendKind.LegacyProcess, beforeHit.Backend);
    Equal(true, beforeHit.IsRunning);
    int? legacyPid = beforeHit.ProcessId;

    // Cache hit for InProcess entry with CloseWorkerAfterRequest=true MUST NOT stop or change LegacyProcess.
    RunManager.Instance.Run = inProcRun;
    PreCombatForecastResult hitWhileLegacyActive = await PreCombatForecastApi.ForecastAsync(
        inProcRun, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: sharedOpts with { CloseWorkerAfterRequest = true, WorkerIdleTimeoutMilliseconds = 1_500 });
    Equal(PreCombatForecastStatus.Succeeded, hitWhileLegacyActive.Status);
    PreCombatWorkerStatus afterHit = PreCombatForecastApi.GetWorkerStatus();
    Equal(PreCombatBackendKind.LegacyProcess, afterHit.Backend);
    Equal(true, afterHit.IsRunning);
    Equal(legacyPid, afterHit.ProcessId);
    Equal(120_000, afterHit.IdleTimeoutMilliseconds);
    Equal(0, PreCombatForecastWorker.StoppedLegacyPids.Count);

    // Switch back to InProcess active, then hit InProcess cache with CloseWorkerAfterRequest=true -> closes InProcess.
    RunManager.Instance.Run = inProcRun;
    PreCombatWorkerStatus backToInProcess = await PreCombatForecastApi.RestartWorkerAsync(inProcRun, 120_000);
    Equal(PreCombatBackendKind.InProcess, backToInProcess.Backend);
    await PreCombatForecastApi.ForecastAsync(
        inProcRun, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: sharedOpts with { CloseWorkerAfterRequest = true });
    Equal(PreCombatBackendKind.None, PreCombatForecastApi.GetWorkerStatus().Backend);
});

await Check("Dread exclusion with corrupt bundle never activates bridge on Windows or Linux", async () =>
{
    string repoRoot = FindRepoRoot();
    string corruptDir = Path.Combine(repoRoot, ".local", "tool-tasks", "sts2sim-bridge", "routing-corrupt-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(corruptDir, "sts2sim"));
    File.WriteAllText(Path.Combine(corruptDir, "sts2sim", "Sts2Sim.PreCombatBridge.dll"), "corrupt");
    try
    {
        PreCombatSts2SimBridge.ModDirectoryOverrideForTesting = corruptDir;
        RunState dreadRun = NewRun();
        dreadRun.MandatoryExclusionCode = "dread_mode";
        dreadRun.DirectIneligibility = "dread_mode";
        RunManager.Instance.Run = dreadRun;
        PreCombatBackendDisplaySnapshot dreadDisplay = PreCombatForecastApi.CaptureBackendDisplaySnapshot();
        Equal("dread_mode", dreadDisplay.HostExclusionCode);
        Equal(true, !string.IsNullOrEmpty(dreadDisplay.HostExclusionDetail));

        PreCombatForecastResult winResult = await PreCombatForecastApi.ForecastAsync(
            dreadRun, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
            options: new() { SearchBudgetMilliseconds = 1_000, OverallTimeoutMilliseconds = 15_000 });
        Equal(PreCombatForecastStatus.Succeeded, winResult.Status);
        Equal(PreCombatBackendKind.LegacyProcess, winResult.ProducingBackend);
        Equal(0, PreCombatSts2SimBridge.ActivationAttemptCountForTesting);

        await PreCombatForecastApi.StopWorkerAsync();
        CombatSolver.Api.OperatingSystem.WindowsOverride = false;
        PreCombatForecastResult linuxResult = await PreCombatForecastApi.ForecastAsync(
            dreadRun, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
            options: new() { ForceRefresh = true, SearchBudgetMilliseconds = 1_000, OverallTimeoutMilliseconds = 15_000 });
        Equal(PreCombatForecastStatus.Unsupported, linuxResult.Status);
        Equal(true, linuxResult.Error?.Contains("dread_mode", StringComparison.Ordinal) == true);
        Equal(0, PreCombatSts2SimBridge.ActivationAttemptCountForTesting);
    }
    finally
    {
        if (Directory.Exists(corruptDir))
            Directory.Delete(corruptDir, recursive: true);
    }
});

await Check("RestartWorkerAsync releases busy lease before status and cancels cleanly during blocked prewarm", async () =>
{
    PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.Ready;
    PreCombatWorkerStatus ready = await PreCombatForecastApi.RestartWorkerAsync(NewRun(), 120_000);
    Equal(true, ready.IsRunning);
    Equal(false, ready.IsBusy);
    Equal(PreCombatBackendKind.InProcess, ready.Backend);
    await PreCombatForecastApi.StopWorkerAsync();

    using var entered = new ManualResetEventSlim(false);
    using var release = new ManualResetEventSlim(false);
    PreCombatSts2SimBridge.PrewarmHookForTesting = () =>
    {
        entered.Set();
        release.Wait(TimeSpan.FromSeconds(5));
    };
    using var cts = new CancellationTokenSource();
    Task<PreCombatWorkerStatus> restartTask = PreCombatForecastApi.RestartWorkerAsync(NewRun(), null, cts.Token);
    Equal(true, entered.Wait(TimeSpan.FromSeconds(2)));
    cts.Cancel();
    release.Set();
    bool threw = false;
    try { await restartTask; }
    catch (OperationCanceledException) { threw = true; }
    Equal(true, threw);
    Equal(false, PreCombatForecastApi.GetWorkerStatus().IsRunning);
    Equal(false, PreCombatForecastApi.GetWorkerStatus().IsBusy);
    Equal(PreCombatBackendKind.None, PreCombatForecastApi.GetWorkerStatus().Backend);
});

await Check("Cosmetic TestSubjectKills routes to InProcess while unsupported extra fields fall back cleanly", async () =>
{
    var baselineExtraFields = new PreCombatExtraFieldsState(
        StartedWithNeow: true,
        FreedRepy: false,
        TestSubjectKills: 0,
        UsedDebugCommand: false,
        WongoPoints: 0,
        IsPlayingRotatingCharacter: false);
    int bridgeInvocations = 0;
    PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.Ready;
    PreCombatSts2SimBridge.RunRawJsonHookForTesting = (_, _) =>
    {
        Interlocked.Increment(ref bridgeInvocations);
        return Task.FromResult(JsonBytes(SucceededWireJson()));
    };

    var cosmeticRun = new RunState
    {
        ExtraFields = baselineExtraFields with { TestSubjectKills = 3 },
    };
    RunManager.Instance.Run = cosmeticRun;
    PreCombatForecastResult cosmeticResult = await PreCombatForecastApi.ForecastAsync(
        cosmeticRun, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: new() { SearchBudgetMilliseconds = 1_000, OverallTimeoutMilliseconds = 15_000 });
    Equal(PreCombatForecastStatus.Succeeded, cosmeticResult.Status);
    Equal(PreCombatBackendKind.InProcess, cosmeticResult.ProducingBackend);
    Equal(1, bridgeInvocations);
    Equal(0, PreCombatForecastWorker.FallbackCountForTesting);
    Equal(0, PreCombatForecastWorker.LegacyCalls.Count);

    await PreCombatForecastApi.StopWorkerAsync();

    PreCombatExtraFieldsState[] excludedCases =
    [
        baselineExtraFields with { TestSubjectKills = 2, WongoPoints = 1 },
        baselineExtraFields with { TestSubjectKills = 2, IsPlayingRotatingCharacter = true },
        baselineExtraFields with { TestSubjectKills = 2, FreedRepy = true },
        baselineExtraFields with { TestSubjectKills = 2, UsedDebugCommand = true },
        baselineExtraFields with { TestSubjectKills = 2, StartedWithNeow = false },
    ];
    int expectedFallbacks = 0;
    int expectedLegacyCalls = 0;
    foreach (PreCombatExtraFieldsState excludedFields in excludedCases)
    {
        var excludedRun = new RunState { ExtraFields = excludedFields };
        RunManager.Instance.Run = excludedRun;
        CombatSolver.Api.OperatingSystem.WindowsOverride = true;
        PreCombatForecastResult winResult = await PreCombatForecastApi.ForecastAsync(
            excludedRun, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
            options: new() { ForceRefresh = true, SearchBudgetMilliseconds = 1_000, OverallTimeoutMilliseconds = 15_000 });
        expectedFallbacks++;
        expectedLegacyCalls++;
        Equal(PreCombatForecastStatus.Succeeded, winResult.Status);
        Equal(PreCombatBackendKind.LegacyProcess, winResult.ProducingBackend);
        Equal(expectedFallbacks, PreCombatForecastWorker.FallbackCountForTesting);
        Equal(expectedLegacyCalls, PreCombatForecastWorker.LegacyCalls.Count);
        Equal("extra_field_unsupported", PreCombatForecastWorker.LastFallbackCodeForTesting);
        Equal(1, bridgeInvocations);

        await PreCombatForecastApi.StopWorkerAsync();

        CombatSolver.Api.OperatingSystem.WindowsOverride = false;
        PreCombatForecastResult linuxResult = await PreCombatForecastApi.ForecastAsync(
            excludedRun, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
            options: new() { ForceRefresh = true, SearchBudgetMilliseconds = 1_000, OverallTimeoutMilliseconds = 15_000 });
        Equal(PreCombatForecastStatus.Unsupported, linuxResult.Status);
        Equal(true, linuxResult.Error?.Contains("extra_field_unsupported", StringComparison.Ordinal) == true);
        Equal(expectedFallbacks, PreCombatForecastWorker.FallbackCountForTesting);
        Equal(expectedLegacyCalls, PreCombatForecastWorker.LegacyCalls.Count);
        Equal(1, bridgeInvocations);
    }
});

Console.WriteLine($"Passed {passed} portable production routing checks.");

static string FindRepoRoot()
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "CombatSolver.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate CombatSolver.csproj.");
}
