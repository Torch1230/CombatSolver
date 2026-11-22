using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using CombatSolver.Api;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;

int passed = 0;
await Check("worker and disabled guards reject all entry points", async () =>
{
    CombatSolver.Entry.IsPreCombatWorker = true;
    try
    {
        Equal(false, PreCombatForecastApi.IsAvailable);
        Equal(PreCombatForecastStatus.Unsupported, (await Forecast(NewRun())).Status);
        Equal(PreCombatForecastStatus.Unsupported, (await Simulate(NewRun())).Status);
        Equal(PreCombatForecastStatus.Unsupported, (await SimulatePlanning(NewRun())).Status);
        await ExpectThrowsAsync<PlatformNotSupportedException>(() => PreCombatForecastApi.RestartWorkerAsync(NewRun()));
    }
    finally { CombatSolver.Entry.IsPreCombatWorker = false; }

    CombatSolver.Entry.Enabled = false;
    try
    {
        Equal(false, PreCombatForecastApi.IsAvailable);
        Equal(PreCombatForecastStatus.Unsupported, (await Forecast(NewRun())).Status);
        await ExpectThrowsAsync<PlatformNotSupportedException>(() => PreCombatForecastApi.RestartWorkerAsync(NewRun()));
    }
    finally { CombatSolver.Entry.Enabled = true; }

    Equal(CombatSolver.Api.OperatingSystem.IsWindows(), PreCombatForecastApi.IsAvailable);
});
await Check("linux non-windows availability preserves dread reason without bridge", async () =>
{
    CombatSolver.Api.OperatingSystem.WindowsOverride = false;
    PreCombatForecastWorker.InstalledInProcessBackend = false;
    try
    {
        Equal(false, PreCombatForecastApi.IsAvailable);
        RunState dreadRun = NewRun();
        dreadRun.DirectIneligibility = "dread_mode";
        PreCombatForecastResult result = await Forecast(dreadRun);
        Equal(PreCombatForecastStatus.Unsupported, result.Status);
        Equal(true, result.Error?.Contains("dread_mode", StringComparison.Ordinal) == true);
        PreCombatForecastResult planResult = await SimulatePlanning(dreadRun);
        Equal(PreCombatForecastStatus.Unsupported, planResult.Status);
        Equal(true, planResult.Error?.Contains("dread_mode", StringComparison.Ordinal) == true);
        await ExpectThrowsAsync<PlatformNotSupportedException>(
            () => PreCombatForecastApi.RestartWorkerAsync(dreadRun),
            "dread_mode");

        PreCombatForecastWorker.InstalledInProcessBackend = true;
        Equal(true, PreCombatForecastApi.IsAvailable);
    }
    finally
    {
        PreCombatForecastWorker.InstalledInProcessBackend = false;
        CombatSolver.Api.OperatingSystem.WindowsOverride = true;
    }
});
await Check("force refresh bypasses an active request", async () =>
{
    RunState run = NewRun();
    Task<PreCombatForecastResult> first = Forecast(run);
    var oldWork = await NextWork();
    Task<PreCombatForecastResult> refreshed = Forecast(run, new() { ForceRefresh = true });
    var newWork = await NextWork();
    oldWork.Succeed("old");
    newWork.Succeed("new");
    Equal("old", (await first).RequestId);
    Equal("new", (await refreshed).RequestId);
});
await Check("matching requests still share active work", async () =>
{
    RunState run = NewRun();
    var first = Forecast(run);
    var work = await NextWork();
    var second = Forecast(run);
    work.Succeed("shared");
    Equal("shared", (await first).RequestId);
    Equal("shared", (await second).RequestId);
});
await Check("different close requirements do not share active work", async () =>
{
    RunState run = NewRun();
    var first = Forecast(run);
    var retained = await NextWork();
    var second = Forecast(run, new() { CloseWorkerAfterRequest = true });
    var closing = await NextWork();
    Equal(true, closing.Options.CloseWorkerAfterRequest);
    retained.Succeed("retained");
    closing.Succeed("closing");
    await Task.WhenAll(first, second);
});
await Check("different idle requirements do not share active work", async () =>
{
    RunState run = NewRun();
    var first = Forecast(run);
    var timed = await NextWork();
    var second = Forecast(run, new() { WorkerIdleTimeoutMilliseconds = null });
    var indefinite = await NextWork();
    Equal<int?>(null, indefinite.Options.WorkerIdleTimeoutMilliseconds);
    timed.Succeed("timed");
    indefinite.Succeed("indefinite");
    await Task.WhenAll(first, second);
});
await Check("cache reuse honors closing and idle settings without searching", async () =>
{
    RunState run = NewRun();
    var first = Forecast(run, new() { WorkerIdleTimeoutMilliseconds = null });
    (await NextWork()).SucceedWithBackend("cached", PreCombatBackendKind.InProcess);
    await first;
    var second = await Forecast(run, new() { CloseWorkerAfterRequest = true });
    Equal("cached", second.RequestId);
    Equal((PreCombatBackendKind.InProcess, true, (int?)120_000), PreCombatForecastWorker.AppliedLifetimes[^1]);
    await Forecast(run, new() { WorkerIdleTimeoutMilliseconds = 1_000 });
    Equal((PreCombatBackendKind.InProcess, false, (int?)1_000), PreCombatForecastWorker.AppliedLifetimes[^1]);
    await Forecast(run, new() { WorkerIdleTimeoutMilliseconds = null });
    Equal((PreCombatBackendKind.InProcess, false, (int?)null), PreCombatForecastWorker.AppliedLifetimes[^1]);
});
await Check("force refresh preserves detached cancellation", async () =>
{
    RunState run = NewRun();
    using var cancellation = new CancellationTokenSource();
    var request = Forecast(run, new() { ForceRefresh = true }, cancellation.Token);
    var work = await NextWork();
    cancellation.Cancel();
    Equal(PreCombatForecastStatus.Cancelled, (await request).Status);
    Equal(false, work.Cancellation.CanBeCanceled);
    work.Succeed("detached");
});
await Check("owned force refresh forwards cancellation and awaits worker cleanup", async () =>
{
    RunState run = NewRun();
    using var cancellation = new CancellationTokenSource();
    var request = Forecast(run, new() { ForceRefresh = true, CancelWorkerWhenCallerCancels = true }, cancellation.Token);
    var work = await NextWork();
    cancellation.Cancel();
    Equal(true, work.Cancellation.IsCancellationRequested);
    Equal(false, request.IsCompleted);
    work.Succeed("owned");
    Equal(PreCombatForecastStatus.Cancelled, (await request).Status);
});
await Check("live state changes invalidate worker results", async () =>
{
    RunState run = NewRun();
    var first = Forecast(run);
    var work = await NextWork();
    run.Token = "changed";
    work.Succeed("stale");
    Equal(PreCombatForecastStatus.LiveStateChanged, (await first).Status);
});
await Check("force refresh bypasses a completed result", async () =>
{
    RunState run = NewRun();
    var first = Forecast(run);
    (await NextWork()).Succeed("cached");
    await first;
    var refreshed = Forecast(run, new() { ForceRefresh = true });
    (await NextWork()).Succeed("fresh");
    Equal("fresh", (await refreshed).RequestId);
});
await Check("cancelled cache hits do not change worker lifetime", async () =>
{
    RunState run = NewRun();
    var first = Forecast(run);
    (await NextWork()).Succeed("cached");
    await first;
    int applied = PreCombatForecastWorker.AppliedLifetimes.Count;
    var result = await Forecast(run, new() { CloseWorkerAfterRequest = true }, new CancellationToken(true));
    Equal(PreCombatForecastStatus.Cancelled, result.Status);
    Equal(applied, PreCombatForecastWorker.AppliedLifetimes.Count);
});
await Check("producer-owned ticket keeps refresh result even with late joiner on old work", async () =>
{
    RunState run = NewRun();
    Task<PreCombatForecastResult> requestA = Forecast(run);
    var workA = await NextWork();
    Task<PreCombatForecastResult> refreshB = Forecast(run, new() { ForceRefresh = true });
    var workB = await NextWork();
    Task<PreCombatForecastResult> joinerC = Forecast(run);

    workB.Succeed("refresh-b");
    Equal("refresh-b", (await refreshB).RequestId);

    workA.Succeed("stale-a");
    Equal("stale-a", (await requestA).RequestId);
    Equal("stale-a", (await joinerC).RequestId);

    PreCombatForecastResult cached = await Forecast(run);
    Equal("refresh-b", cached.RequestId);
});
await Check("stale producer does not overwrite refresh after 32-entry cache eviction", async () =>
{
    RunState staleRun = NewRun();
    Task<PreCombatForecastResult> oldRequest = Forecast(staleRun);
    var oldWork = await NextWork();

    for (int i = 0; i < 34; i++)
    {
        RunState filler = NewRun();
        Task<PreCombatForecastResult> task = Forecast(filler);
        (await NextWork()).Succeed($"fill-{i}");
        await task;
    }

    Task<PreCombatForecastResult> refresh = Forecast(staleRun, new() { ForceRefresh = true });
    (await NextWork()).Succeed("new-owner");
    Equal("new-owner", (await refresh).RequestId);

    oldWork.Succeed("very-late-old");
    await oldRequest;
    Equal("new-owner", (await Forecast(staleRun)).RequestId);
});
await Check("stop epoch fences pre-stop active dedup and queued restart", async () =>
{
    RunState run = NewRun();
    Task<PreCombatForecastResult> preStop = Forecast(run);
    var workBeforeStop = await NextWork();
    await PreCombatForecastApi.StopWorkerAsync();
    Task<PreCombatForecastResult> postStop = Forecast(run);
    var workAfterStop = await NextWork();
    Equal(true, workAfterStop.AdmissionEpoch > workBeforeStop.AdmissionEpoch);
    workBeforeStop.Succeed("pre");
    workAfterStop.Succeed("post");
    Equal("pre", (await preStop).RequestId);
    Equal("post", (await postStop).RequestId);

        var restartTask = PreCombatForecastApi.RestartWorkerAsync(run);
    long restartEpoch = await PreCombatForecastWorker.RestartEpochs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    await PreCombatForecastApi.StopWorkerAsync();
    Equal(true, restartEpoch < PreCombatForecastWorker.CaptureAdmissionEpoch());
    await restartTask;
});
await Check("stop before queued restart task first runs prevents starting in new epoch", async () =>
{
    RunState run = NewRun();
    var delayGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    PreCombatForecastWorker.DelayStopAfterEpochBump = delayGate;
    try
    {
        Task stopTask = PreCombatForecastApi.StopWorkerAsync();
        Task<PreCombatWorkerStatus> queuedRestart = PreCombatForecastApi.RestartWorkerAsync(run);
        long queuedEpoch = await PreCombatForecastWorker.RestartEpochs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        delayGate.SetResult(true);
        await stopTask;
        await queuedRestart;
        Equal(true, queuedEpoch == PreCombatForecastWorker.CaptureAdmissionEpoch());
    }
    finally
    {
        PreCombatForecastWorker.DelayStopAfterEpochBump = null;
    }

    RunState run2 = NewRun();
    PreCombatForecastWorker.StopEpochValue = 10;
    long preStopEpoch = PreCombatForecastWorker.CaptureAdmissionEpoch();
    await PreCombatForecastApi.StopWorkerAsync();
    PreCombatForecastResult lateResult = await PreCombatForecastWorker.RunAsync(
        PreCombatLiveStateSnapshot.Capture(run2),
        "TEST", 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal, false,
        PreCombatForecastOptions.Default, preStopEpoch, CancellationToken.None);
    Equal(PreCombatForecastStatus.Cancelled, lateResult.Status);
});
await Check("cached potion titles localize on main-thread delivery", async () =>
{
    RunState run = NewRun();
    PreCombatForecastWorker.LocalePrefix = "ZH";
    Task<PreCombatForecastResult> first = Forecast(run);
    (await NextWork()).Succeed("loc", new PreCombatPotionUse("FIRE_POTION", "FIRE_POTION", 1, 0));
    PreCombatForecastResult firstResult = await first;
    Equal("ZH:FIRE_POTION", firstResult.PotionUses[0].Title);

    PreCombatForecastWorker.LocalePrefix = "EN";
    PreCombatForecastResult cachedResult = await Forecast(run);
    Equal("EN:FIRE_POTION", cachedResult.PotionUses[0].Title);
});
await Check("shared ABI v1 wire validator enforces schema and nullability", () =>
{
    var validVictory = new Sts2SimBridgeResponseDto(
        1, "Succeeded", null, null, null,
        6, 74, 2, false, "Complete", "None",
        [new Sts2SimPotionUseDto("FIRE_POTION", 1, 0)],
        120, 1, 15.5, 18.2);
    PreCombatSts2SimWireValidator.ValidateResponse(validVictory);

    var validDefeat = new Sts2SimBridgeResponseDto(
        1, "Succeeded", null, null, null,
        80, 0, null, true, "DeathOnly", "None",
        [], 42, 1, 4.0, 5.1);
    PreCombatSts2SimWireValidator.ValidateResponse(validDefeat);

    var validMiss = new Sts2SimBridgeResponseDto(
        1, "CapabilityMiss", "encounter_unmapped", "encounterId", "Encounter absent.",
        null, null, null, null, null, null, null, null, 1, null, 1.0);
    PreCombatSts2SimWireValidator.ValidateResponse(validMiss);

    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidateResponse(
        validVictory with { Confidence = "DeathOnly" }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidateResponse(
        validVictory with { CombatEndedTurn = 0 }));
    PreCombatSts2SimWireValidator.ValidateResponse(validVictory with { EffectiveDegreeOfParallelism = 4 });
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidateResponse(
        validVictory with { EffectiveDegreeOfParallelism = 0 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidateResponse(
        validVictory with { EffectiveDegreeOfParallelism = 65 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidateResponse(
        validMiss with { FailureCode = "made_up_code" }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidateResponse(
        validMiss with { ProjectedHpLoss = 0 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidateResponse(
        validMiss with { ExpandedNodes = 12 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidateResponse(
        validMiss with { SearchElapsedMilliseconds = 1.5 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidateResponse(
        validVictory with { TotalElapsedMilliseconds = double.NaN }));
    InvalidDataException malformedPreCombatEx = ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(
        System.Text.Encoding.UTF8.GetBytes("{\"abiVersion\":1,\"outcome\":\"Succeeded\",\"effectiveDegreeOfParallelism\":1,\"totalElapsedMilliseconds\":\"NaN\"}")));
    Equal(true, malformedPreCombatEx.InnerException is JsonException);
    InvalidDataException truncatedPreCombatEx = ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(
        System.Text.Encoding.UTF8.GetBytes("{")));
    Equal(true, truncatedPreCombatEx.InnerException is JsonException);
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(
        System.Text.Encoding.UTF8.GetBytes("{\"abiVersion\":1,\"outcome\":\"Succeeded\",\"effectiveDegreeOfParallelism\":1,\"totalElapsedMilliseconds\":1.0}")));
    return Task.CompletedTask;
});
await Check("shared abi-v1-fixtures.json schema and samples match contract", () =>
{
    string fixturePath = Path.Combine(AppContext.BaseDirectory, "abi-v1-fixtures.json");
    byte[] fixtureBytes = File.ReadAllBytes(fixturePath);
    using JsonDocument doc = JsonDocument.Parse(fixtureBytes);
    JsonElement root = doc.RootElement;
    Equal(PreCombatSts2SimAbi.Version, root.GetProperty("abiVersion").GetInt32());
    Equal(PreCombatSts2SimAbi.SupportedGameVersion, root.GetProperty("supportedGameVersion").GetString());
    Equal(PreCombatSts2SimAbi.SupportedGameCommit, root.GetProperty("supportedGameCommit").GetString());

    HashSet<string> fixtureCodes = root.GetProperty("allowlistedFailureCodes").EnumerateArray()
        .Select(static e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
    Equal(true, fixtureCodes.SetEquals(PreCombatSts2SimAbi.AllowlistedCapabilityMissCodes));

    HashSet<string> fixtureBoundaries = root.GetProperty("allowedSearchBoundaries").EnumerateArray()
        .Select(static e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
    Equal(true, fixtureBoundaries.SetEquals(PreCombatSts2SimAbi.AllowedSearchBoundaries));

    Sts2SimBridgeRequestDto? sampleRequest = JsonSerializer.Deserialize<Sts2SimBridgeRequestDto>(
        root.GetProperty("sampleRequest").GetRawText(),
        PreCombatSts2SimAbi.JsonOptions);
    Equal(true, sampleRequest is not null);
    Equal(0UL, sampleRequest!.SimulationSeed);
    Equal(1, sampleRequest.Policy.RelicCounterRules.Count);
    Equal("NIBBITS_WEAK", sampleRequest.EncounterId);

    foreach (JsonProperty entry in root.GetProperty("responses").EnumerateObject())
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(entry.Value.GetRawText());
        Sts2SimBridgeResponseDto validated = PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(raw);
        Equal(PreCombatSts2SimAbi.Version, validated.AbiVersion);
    }

    foreach (JsonProperty invalidEntry in root.GetProperty("invalidResponses").EnumerateObject())
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(invalidEntry.Value.GetRawText());
        ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(raw));
    }
    return Task.CompletedTask;
});
await Check("in-combat ABI v1 wire validator rejects malformed nested traces and missing/duplicate turn outcomes", () =>
{
    var validChoiceHand = new InCombatChoiceTraceDto(
        "Action", "SURVIVOR", "Hand",
        [new InCombatChoiceCardTokenDto("DEFEND_IRONCLAD", 0, 0, 1)]);
    var validChoiceGenerated = new InCombatChoiceTraceDto(
        "Action", "DISCOVERY", "None",
        [new InCombatChoiceCardTokenDto("BASH", 0, 0, -1)]);
    var validResult = new InCombatSts2SimResultDto(
        Actions:
        [
            new InCombatActionTraceDto(
                Turn: 1,
                Kind: "PlayCard",
                CardId: "SURVIVOR",
                CardOccurrence: 0,
                CardUpgradeLevel: 0,
                CardEnchantmentId: string.Empty,
                TargetIndex: -1,
                TargetCombatId: null,
                PotionId: string.Empty,
                PotionSlot: -1,
                Choices: [validChoiceHand, validChoiceGenerated],
                FlashedRelicIds: ["PEN_NIB"],
                KilledEnemyCombatIds: []),
            new InCombatActionTraceDto(
                Turn: 1,
                Kind: "EndTurn",
                CardId: string.Empty,
                CardOccurrence: 0,
                CardUpgradeLevel: -1,
                CardEnchantmentId: string.Empty,
                TargetIndex: -1,
                TargetCombatId: null,
                PotionId: string.Empty,
                PotionSlot: -1,
                Choices: [],
                FlashedRelicIds: [],
                KilledEnemyCombatIds: []),
        ],
        TurnOutcomes:
        [
            new InCombatTurnOutcomeDto(1, 0, 0, 6, 0, 8, 8, 1, 0, 0, true),
        ],
        Terminal: new InCombatTerminalSnapshotDto(
            false, true, 75, 80, 5, 0, 75, 8, 0, 0, 1, 0, 3, 0, 1, 0, 0, 5, 1, null, false, "Complete", "None", false),
        PotionUses: [],
        ExpandedNodes: 12,
        SearchElapsedMilliseconds: 3.5);
    var validResponse = new InCombatSts2SimResponseDto(
        InCombatSts2SimAbi.Version, "Succeeded", null, null, null, validResult, null, 1, 4.0);
    InCombatSts2SimWireValidator.ValidateResponse(validResponse);

    // One-action damaging turn with empty TurnOutcomes list must fail.
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [validResult.Actions[0]],
                TurnOutcomes = [],
            },
        }));

    // Missing TurnOutcome for turn 2 action and duplicate TurnOutcome for turn 1 must fail.
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions =
                [
                    validResult.Actions[0],
                    validResult.Actions[1] with { Turn = 2 },
                ],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                TurnOutcomes =
                [
                    new InCombatTurnOutcomeDto(1, 0, 0, 6, 0, 8, 8, 1, 0, 0, false),
                    new InCombatTurnOutcomeDto(1, 0, 0, 6, 0, 8, 8, 1, 0, 0, true),
                ],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                TurnOutcomes = [new InCombatTurnOutcomeDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false)],
            },
        }));

    // Extra fabricated TurnOutcome for an unreferenced turn must fail (strict 1:1 turn set equality).
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                TurnOutcomes =
                [
                    validResult.TurnOutcomes[0],
                    new InCombatTurnOutcomeDto(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, false),
                ],
            },
        }));

    // Empty action list must have exactly one TurnOutcome matching Terminal.Turn.
    InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [],
                TurnOutcomes = [new InCombatTurnOutcomeDto(1, 0, 0, 0, 0, 0, 0, 3, 0, 0, false)],
            },
        });
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [],
                TurnOutcomes =
                [
                    new InCombatTurnOutcomeDto(1, 0, 0, 0, 0, 0, 0, 3, 0, 0, false),
                    new InCombatTurnOutcomeDto(2, 0, 0, 0, 0, 0, 0, 3, 0, 0, false),
                ],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [],
                TurnOutcomes = [new InCombatTurnOutcomeDto(2, 0, 0, 0, 0, 0, 0, 3, 0, 0, false)],
            },
        }));

    // Unknown action kind and malformed nested collections must fail.
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [validResult.Actions[0] with { Kind = "BogusAction" }],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [validResult.Actions[0] with { FlashedRelicIds = null! }],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [validResult.Actions[0] with { FlashedRelicIds = [" "] }],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [validResult.Actions[0] with { KilledEnemyCombatIds = null! }],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [validResult.Actions[0] with { Choices = [validChoiceHand with { Timing = "MidCombat" }] }],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [validResult.Actions[0] with { Choices = [validChoiceHand with { SourcePile = "Nowhere" }] }],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions = [validResult.Actions[0] with { Choices = [validChoiceHand with { Cards = null! }] }],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions =
                [
                    validResult.Actions[0] with
                    {
                        Choices =
                        [
                            validChoiceHand with
                            {
                                Cards = [new InCombatChoiceCardTokenDto("STRIKE_IRONCLAD", 0, 0, -1)],
                            },
                        ],
                    },
                ],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with
            {
                Actions =
                [
                    validResult.Actions[0] with
                    {
                        Choices =
                        [
                            validChoiceGenerated with
                            {
                                Cards = [new InCombatChoiceCardTokenDto("BASH", 0, 0, -2)],
                            },
                        ],
                    },
                ],
            },
        }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidateResponse(
        validResponse with
        {
            Result = validResult with { PotionUses = [null!] },
        }));

    byte[] malformedProgressBytes = JsonSerializer.SerializeToUtf8Bytes(
        new InCombatSts2SimProgressDto(
            10, 1, 2.0, 0, true, 1, 0,
            validResult with { Actions = [validResult.Actions[0]], TurnOutcomes = [] }),
        PreCombatSts2SimAbi.JsonOptions);
    ExpectThrows<InvalidDataException>(() =>
        InCombatSts2SimWireValidator.DeserializeAndValidateProgress(malformedProgressBytes));

    // Malformed JSON at both in-combat external boundaries must normalize to InvalidDataException.
    InvalidDataException inCombatRespJsonEx = ExpectThrows<InvalidDataException>(() =>
        InCombatSts2SimWireValidator.DeserializeAndValidateResponse(
            System.Text.Encoding.UTF8.GetBytes("{\"abiVersion\":1,\"outcome\":")));
    Equal(true, inCombatRespJsonEx.InnerException is JsonException);
    InvalidDataException inCombatProgJsonEx = ExpectThrows<InvalidDataException>(() =>
        InCombatSts2SimWireValidator.DeserializeAndValidateProgress(
            System.Text.Encoding.UTF8.GetBytes("{\"expandedNodes\":\"not-a-number\"}")));
    Equal(true, inCombatProgJsonEx.InnerException is JsonException);
    return Task.CompletedTask;
});
await Check("isolated bridge loader activates C2-only pre-combat bundle without breaking pre-combat", async () =>
{
    string repoRoot = FindRepoRoot();
    string scratchRoot = Path.Combine(repoRoot, ".local", "tool-tasks", "sts2sim-bridge", "c2-only-check-" + Guid.NewGuid().ToString("N"));
    string sts2simDir = Path.Combine(scratchRoot, "sts2sim");
    Directory.CreateDirectory(sts2simDir);
    try
    {
        string bridgeMvid = Guid.NewGuid().ToString("D");
        string solverMvid = Guid.NewGuid().ToString("D");
        string coreMvid = Guid.NewGuid().ToString("D");
        string identityJson = JsonSerializer.Serialize(
            new Sts2SimBundleIdentityDto(
                PreCombatSts2SimAbi.Version,
                PreCombatSts2SimAbi.SupportedGameVersion,
                PreCombatSts2SimAbi.SupportedGameCommit,
                bridgeMvid,
                solverMvid,
                coreMvid),
            PreCombatSts2SimAbi.JsonOptions);
        string preCombatResponseJson = JsonSerializer.Serialize(
            new Sts2SimBridgeResponseDto(
                PreCombatSts2SimAbi.Version,
                "Succeeded",
                null,
                null,
                null,
                3,
                77,
                2,
                false,
                "Complete",
                "None",
                [],
                25,
                1,
                4.0,
                5.0),
            PreCombatSts2SimAbi.JsonOptions);

        string bridgeDllPath = Path.Combine(sts2simDir, "Sts2Sim.PreCombatBridge.dll");
        var asmBuilder = new PersistedAssemblyBuilder(
            new AssemblyName("Sts2Sim.PreCombatBridge"),
            typeof(object).Assembly);
        ModuleBuilder modBuilder = asmBuilder.DefineDynamicModule("Sts2Sim.PreCombatBridge.dll");
        TypeBuilder typeBuilder = modBuilder.DefineType(
            "Sts2Sim.PreCombatBridge.BridgeEntry",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        ILGenerator getAbiIl = typeBuilder.DefineMethod(
                "GetAbiVersion",
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(int),
                Type.EmptyTypes)
            .GetILGenerator();
        getAbiIl.Emit(OpCodes.Ldc_I4, PreCombatSts2SimAbi.Version);
        getAbiIl.Emit(OpCodes.Ret);

        ILGenerator getIdIl = typeBuilder.DefineMethod(
                "GetBundleIdentityJson",
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(string),
                Type.EmptyTypes)
            .GetILGenerator();
        getIdIl.Emit(OpCodes.Ldstr, identityJson);
        getIdIl.Emit(OpCodes.Ret);

        ILGenerator prewarmIl = typeBuilder.DefineMethod(
                "Prewarm",
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(void),
                Type.EmptyTypes)
            .GetILGenerator();
        prewarmIl.Emit(OpCodes.Ret);

        MethodInfo utf8Getter = typeof(Encoding).GetProperty(nameof(Encoding.UTF8))!.GetMethod!;
        MethodInfo getBytesMethod = typeof(Encoding).GetMethod(nameof(Encoding.GetBytes), [typeof(string)])!;
        MethodInfo taskFromResultMethod = typeof(Task)
            .GetMethod(nameof(Task.FromResult), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(typeof(byte[]));
        ILGenerator runIl = typeBuilder.DefineMethod(
                "RunJsonAsync",
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(Task<byte[]>),
                [typeof(byte[]), typeof(CancellationToken)])
            .GetILGenerator();
        runIl.Emit(OpCodes.Call, utf8Getter);
        runIl.Emit(OpCodes.Ldstr, preCombatResponseJson);
        runIl.Emit(OpCodes.Callvirt, getBytesMethod);
        runIl.Emit(OpCodes.Call, taskFromResultMethod);
        runIl.Emit(OpCodes.Ret);

        typeBuilder.CreateType();
        asmBuilder.Save(bridgeDllPath);

        File.WriteAllText(Path.Combine(sts2simDir, "Sts2Sim.Solver.dll"), "placeholder-solver");
        File.WriteAllText(Path.Combine(sts2simDir, "Sts2Sim.Core.dll"), "placeholder-core");
        File.WriteAllText(
            Path.Combine(sts2simDir, "Sts2Sim.PreCombatBridge.deps.json"),
            "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v9.0\"},\"targets\":{\".NETCoreApp,Version=v9.0\":{}}}");

        var manifestEntries = new List<Sts2SimBundleFileManifestEntry>
        {
            new(
                "Sts2Sim.PreCombatBridge.dll",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(bridgeDllPath))),
                bridgeMvid),
            new(
                "Sts2Sim.Solver.dll",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(sts2simDir, "Sts2Sim.Solver.dll")))),
                solverMvid),
            new(
                "Sts2Sim.Core.dll",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(sts2simDir, "Sts2Sim.Core.dll")))),
                coreMvid),
            new(
                "Sts2Sim.PreCombatBridge.deps.json",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(sts2simDir, "Sts2Sim.PreCombatBridge.deps.json")))),
                null),
        };
        File.WriteAllText(
            Path.Combine(sts2simDir, "sts2sim-bundle.manifest.json"),
            JsonSerializer.Serialize(
                new Sts2SimBundleManifest(
                    PreCombatSts2SimAbi.Version,
                    PreCombatSts2SimAbi.SupportedGameVersion,
                    PreCombatSts2SimAbi.SupportedGameCommit,
                    manifestEntries),
                PreCombatSts2SimAbi.JsonOptions));

        PreCombatSts2SimBridge.ResetForTesting();
        PreCombatSts2SimBridge.ModDirectoryOverrideForTesting = scratchRoot;
        CombatSolver.Entry.Logger.ThrowOnError = false;
        CombatSolver.Entry.Logger.Errors.Clear();

        Equal(PreCombatBridgeAvailability.Ready, PreCombatSts2SimBridge.EnsureActivated(out string? err));
        Equal<string?>(null, err);
        Equal(false, PreCombatSts2SimBridge.IsCorrupt);
        Equal(true, PreCombatSts2SimBridge.IsInCombatUnavailable);
        Equal(InCombatBridgeAvailability.InCombatUnsupported, PreCombatSts2SimBridge.EnsureInCombatActivated(out string? inCombatErr));
        Equal(true, !string.IsNullOrWhiteSpace(inCombatErr));
        Equal(false, PreCombatSts2SimBridge.IsCorrupt);

        PreCombatSts2SimBridge.Prewarm();
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "abi-v1-fixtures.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        Sts2SimBridgeRequestDto sampleRequest = JsonSerializer.Deserialize<Sts2SimBridgeRequestDto>(
            doc.RootElement.GetProperty("sampleRequest").GetRawText(),
            PreCombatSts2SimAbi.JsonOptions)!;
        Sts2SimBridgeResponseDto response = await PreCombatSts2SimBridge.RunAsync(sampleRequest, CancellationToken.None);
        Equal("Succeeded", response.Outcome);
        Equal(3, response.ProjectedHpLoss);
        Equal(false, PreCombatSts2SimBridge.IsCorrupt);

        var inCombatPolicy = new InCombatSts2SimPolicyDto(
            "Auto", [], false, false, [],
            new Sts2SimGrowthBudgetsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null),
            null, false, "KeepHigh", "KeepHigh", 0, false,
            false, false, false, 8, null, null, 100, 16, 8, 8, 1000, null);
        var playerCounters = new InCombatPlayerCountersDto(
            0m, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var playerDto = new InCombatPlayerDto(
            80, 80, 0, 0, 99, 3, 0, true, true, 3, 0, "Play", 1, playerCounters,
            [], [], [], [], [], [], [], 0, [], []);
        var emptySemanticHistory = new InCombatSemanticHistoryDto([], [], [], [], [], []);
        var emptyVerification = new InCombatVerificationDto(
            80, 80, 0, 3, 0, 99, 0, [], [], [], [], [], [], [], [], []);
        var inCombatReq = new InCombatSts2SimRequestDto(
            InCombatSts2SimAbi.Version,
            "c2-only-req",
            "{}",
            new InCombatLiveStateDto(
                "TEST",
                "Monster",
                [],
                "Player",
                1,
                false,
                false,
                1u,
                "seed",
                new Dictionary<string, InCombatRngStreamDto>(),
                0UL,
                new Dictionary<string, InCombatRngStreamDto>(),
                playerDto,
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                emptySemanticHistory,
                emptyVerification),
            0, 1000, 15000, 1, inCombatPolicy);
        await ExpectThrowsAsync<NotSupportedException>(() => PreCombatSts2SimBridge.RunInCombatAsync(inCombatReq, null, CancellationToken.None));
    }
    finally
    {
        PreCombatSts2SimBridge.ResetForTesting();
        PreCombatSts2SimBridge.ModDirectoryOverrideForTesting = null;
        CombatSolver.Entry.Logger.ThrowOnError = true;
        if (Directory.Exists(scratchRoot))
            Directory.Delete(scratchRoot, recursive: true);
    }
});
await Check("isolated bridge loader rejects missing, tampered, and unmanifested bundles", () =>
{
    string repoRoot = FindRepoRoot();
    string scratchRoot = Path.Combine(repoRoot, ".local", "tool-tasks", "sts2sim-bridge", "loader-check-" + Guid.NewGuid().ToString("N"));
    try
    {
        PreCombatSts2SimBridge.ResetForTesting();
        PreCombatSts2SimBridge.ModDirectoryOverrideForTesting = scratchRoot;
        CombatSolver.Entry.Logger.ThrowOnError = false;
        CombatSolver.Entry.Logger.Errors.Clear();

        Equal(false, PreCombatSts2SimBridge.HasInstalledBackendArtifact());
        Equal(PreCombatBridgeAvailability.Absent, PreCombatSts2SimBridge.EnsureActivated(out _));

        string sts2simDir = Path.Combine(scratchRoot, "sts2sim");
        Directory.CreateDirectory(sts2simDir);
        Equal(true, PreCombatSts2SimBridge.HasInstalledBackendArtifact());
        Equal(PreCombatBridgeAvailability.CorruptOrIncompatible, PreCombatSts2SimBridge.EnsureActivated(out string? missingManifestErr));
        Equal(true, missingManifestErr?.Contains("could not be activated", StringComparison.Ordinal) == true);

        PreCombatSts2SimBridge.ResetForTesting();
        string[] coreFiles =
        [
            "Sts2Sim.PreCombatBridge.dll",
            "Sts2Sim.Solver.dll",
            "Sts2Sim.Core.dll",
            "Sts2Sim.PreCombatBridge.deps.json",
        ];
        var entries = new List<Sts2SimBundleFileManifestEntry>();
        foreach (string file in coreFiles)
        {
            string p = Path.Combine(sts2simDir, file);
            File.WriteAllText(p, $"placeholder-{file}");
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
            entries.Add(new Sts2SimBundleFileManifestEntry(file, sha, file.EndsWith(".dll", StringComparison.Ordinal) ? Guid.NewGuid().ToString("D") : null));
        }
        File.WriteAllText(
            Path.Combine(sts2simDir, "sts2sim-bundle.manifest.json"),
            JsonSerializer.Serialize(
                new Sts2SimBundleManifest(PreCombatSts2SimAbi.Version, PreCombatSts2SimAbi.SupportedGameVersion, PreCombatSts2SimAbi.SupportedGameCommit, entries),
                PreCombatSts2SimAbi.JsonOptions));
        File.WriteAllText(Path.Combine(sts2simDir, "System.IO.Hashing.dll"), "unmanifested");
        Equal(PreCombatBridgeAvailability.CorruptOrIncompatible, PreCombatSts2SimBridge.EnsureActivated(out _));
        Equal(true, CombatSolver.Entry.Logger.Errors.Last().Contains("Unmanifested System.IO.Hashing.dll", StringComparison.Ordinal));

        PreCombatSts2SimBridge.ResetForTesting();
        File.Delete(Path.Combine(sts2simDir, "System.IO.Hashing.dll"));
        File.WriteAllText(Path.Combine(sts2simDir, "Sts2Sim.Core.dll"), "tampered-bytes");
        Equal(PreCombatBridgeAvailability.CorruptOrIncompatible, PreCombatSts2SimBridge.EnsureActivated(out _));
        Equal(true, CombatSolver.Entry.Logger.Errors.Last().Contains("SHA-256 mismatch", StringComparison.Ordinal));
        Equal(PreCombatBridgeAvailability.CorruptOrIncompatible, PreCombatSts2SimBridge.EnsureActivated(out _));
    }
    finally
    {
        PreCombatSts2SimBridge.ResetForTesting();
        PreCombatSts2SimBridge.ModDirectoryOverrideForTesting = null;
        CombatSolver.Entry.Logger.ThrowOnError = true;
        if (Directory.Exists(scratchRoot))
            Directory.Delete(scratchRoot, recursive: true);
    }
    return Task.CompletedTask;
});
await Check("isolated bridge loader activates staged bundle and runs ABI v1 wire", async () =>
{
    string repoRoot = FindRepoRoot();
    string modDir = Path.Combine(repoRoot, ".godot", "mono", "temp", "bin", "Release");
    string manifestPath = Path.Combine(modDir, "sts2sim", "sts2sim-bundle.manifest.json");
    if (!File.Exists(manifestPath))
    {
        Console.WriteLine("  (skipped: staged sts2sim bundle not present in this checkout)");
        return;
    }

    try
    {
        PreCombatSts2SimBridge.ResetForTesting();
        PreCombatSts2SimBridge.ModDirectoryOverrideForTesting = modDir;
        Equal(true, PreCombatSts2SimBridge.HasInstalledBackendArtifact());
        Equal(PreCombatBridgeAvailability.Ready, PreCombatSts2SimBridge.EnsureActivated(out string? err));
        Equal<string?>(null, err);
        PreCombatSts2SimBridge.Prewarm();

        string fixturePath = Path.Combine(AppContext.BaseDirectory, "abi-v1-fixtures.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        Sts2SimBridgeRequestDto sampleRequest = JsonSerializer.Deserialize<Sts2SimBridgeRequestDto>(
            doc.RootElement.GetProperty("sampleRequest").GetRawText(),
            PreCombatSts2SimAbi.JsonOptions)!;
        Sts2SimBridgeResponseDto response = await PreCombatSts2SimBridge.RunAsync(sampleRequest, CancellationToken.None);
        Equal(PreCombatSts2SimAbi.Version, response.AbiVersion);
        Equal("Failed", response.Outcome);
        Equal(1, response.EffectiveDegreeOfParallelism);

        foreach (JsonProperty badSave in doc.RootElement.GetProperty("invalidSavePayloads").EnumerateObject())
        {
            string badJson = badSave.Value.GetString()!;
            Sts2SimBridgeResponseDto badResp = await PreCombatSts2SimBridge.RunAsync(
                sampleRequest with { RequestId = $"invalid-{badSave.Name}", NormalizedSaveJson = badJson },
                CancellationToken.None);
            Equal("Failed", badResp.Outcome);
        }
    }
    finally
    {
        PreCombatSts2SimBridge.ResetForTesting();
        PreCombatSts2SimBridge.ModDirectoryOverrideForTesting = null;
    }
});
await Check("idle expiry stops only its session", PreCombatForecastWorker.CheckIdleExpiryAsync);
await Check("in-process idle expiry clears readiness", PreCombatForecastWorker.CheckInProcessIdleExpiryAsync);
await Check("idle cancellation keeps the session reusable", PreCombatForecastWorker.CheckIdleCancellationAsync);
await Check("idle cancellation releases a queued gate waiter", PreCombatForecastWorker.CheckIdleGateCancellationAsync);
await Check("idle stale cleanup preserves replacement ownership", PreCombatForecastWorker.CheckStaleIdleCompletionAsync);
await Check("idle cleanup preserves an in-flight cancellation source", PreCombatForecastWorker.CheckIdleCancelDisposeOverlapAsync);
await Check("idle cancellation before timer startup uses the captured token", PreCombatForecastWorker.CheckIdleCancellationBeforeStartAsync);
await Check("idle indefinite lifetime cancels the old timer", PreCombatForecastWorker.CheckIndefiniteIdleLifetimeAsync);
await Check("extra fields eligibility allows cosmetic TestSubjectKills and rejects unsupported fields", () =>
{
    var baseline = new PreCombatExtraFieldsState(
        StartedWithNeow: true,
        FreedRepy: false,
        TestSubjectKills: 0,
        UsedDebugCommand: false,
        WongoPoints: 0,
        IsPlayingRotatingCharacter: false);
    Equal(false, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline));
    Equal(false, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { TestSubjectKills = 1 }));
    Equal(false, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { TestSubjectKills = 3 }));
    Equal(false, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { TestSubjectKills = 12 }));

    Equal(true, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { StartedWithNeow = false }));
    Equal(true, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { FreedRepy = true }));
    Equal(true, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { UsedDebugCommand = true }));
    Equal(true, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { WongoPoints = 1 }));
    Equal(true, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { WongoPoints = -1 }));
    Equal(true, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { IsPlayingRotatingCharacter = true }));

    Equal(true, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { TestSubjectKills = 2, FreedRepy = true }));
    Equal(true, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { TestSubjectKills = 2, WongoPoints = 1 }));
    Equal(true, PreCombatSts2SimEligibility.HasUnsupportedExtraFields(baseline with { TestSubjectKills = 2, StartedWithNeow = false }));
    return Task.CompletedTask;
});
await Check("card OnPlay audit rejects foreign patches for Sts2Sim even when BuiltIn adapted", () =>
{
    Equal(false, PreCombatSts2SimEligibility.ShouldRejectCardOnPlayForSts2Sim(
        new PreCombatCardOnPlayAuditOutcome(HasForeignPatch: false, HasBuiltInAdaptation: false, ForeignDescription: null),
        out string cleanReason));
    Equal(string.Empty, cleanReason);

    Equal(true, PreCombatSts2SimEligibility.ShouldRejectCardOnPlayForSts2Sim(
        new PreCombatCardOnPlayAuditOutcome(
            HasForeignPatch: true,
            HasBuiltInAdaptation: false,
            ForeignDescription: "Unadapted foreign OnPlay patch"),
        out string unadaptedReason));
    Equal("Unadapted foreign OnPlay patch", unadaptedReason);

    Equal(true, PreCombatSts2SimEligibility.ShouldRejectCardOnPlayForSts2Sim(
        new PreCombatCardOnPlayAuditOutcome(
            HasForeignPatch: true,
            HasBuiltInAdaptation: true,
            ForeignDescription: "AdaptedMod (adapted_mod): Harmony patch on DefendIronclad.OnPlay"),
        out string adaptedReason));
    Equal("AdaptedMod (adapted_mod): Harmony patch on DefendIronclad.OnPlay", adaptedReason);
    return Task.CompletedTask;
});
await Check("critical Harmony patch audit catches combat command and hook patches while allowing neutral or room utility patches", () =>
{
    static PreCombatHarmonyPatchDescriptor Patch(
        string ns,
        string rootType,
        string method,
        bool? affectsGameplay = true,
        bool isMonsterModel = false,
        bool isTrustedOwner = false,
        bool hasKnownModId = true) => new(
            DeclaringNamespace: ns,
            RootDeclaringTypeName: rootType,
            MethodName: method,
            IsMonsterModel: isMonsterModel,
            IsBaseGamePatch: false,
            IsTrustedOwner: isTrustedOwner,
            AffectsGameplay: affectsGameplay,
            HasKnownModId: hasKnownModId,
            PatchDisplayName: "ProbeMod.Patch.Prefix",
            TargetDisplayName: $"{ns}.{rootType}.{method}",
            Owner: "probe_mod");

    PreCombatHarmonyPatchDescriptor[] rejectedCombatPatches =
    [
        Patch("MegaCrit.Sts2.Core.Commands", "CombatCmd", "EndTurn"),
        Patch("MegaCrit.Sts2.Core.Commands", "DamageCmd", "MoveNext"),
        Patch("MegaCrit.Sts2.Core.Commands.Builders", "AttackCommand", "Execute"),
        Patch("MegaCrit.Sts2.Core.Commands", "CardCmd", "Discard"),
        Patch("MegaCrit.Sts2.Core.Commands", "PowerCmd", "Apply"),
        Patch("MegaCrit.Sts2.Core.Commands", "RelicCmd", "Obtain"),
        Patch("MegaCrit.Sts2.Core.Commands", "CreatureCmd", "Heal"),
        Patch("MegaCrit.Sts2.Core.Hooks", "Hook", "ModifyDamage"),
        Patch("MegaCrit.Sts2.Core.Combat", "CombatManager", "SetUpCombat"),
        Patch("MegaCrit.Sts2.Core.GameActions", "PlayCardAction", "ExecuteAction"),
        Patch("MegaCrit.Sts2.Core.MonsterMoves.Intents", "AttackIntent", "GetSingleDamage"),
        Patch("MegaCrit.Sts2.Core.Models", "CardModel", "SpendResources"),
        Patch("MegaCrit.Sts2.Core.Models.Powers", "StrengthPower", "ModifyDamageAdditive"),
        Patch("MegaCrit.Sts2.Core.Models.Monsters", "Mawler", "GenerateMoveStateMachine", isMonsterModel: true),
        Patch("MegaCrit.Sts2.Core.Commands", "DamageCmd", "Attack", affectsGameplay: null, hasKnownModId: false),
        Patch("MegaCrit.Sts2.Core.Random", "Rng", "NextInt", affectsGameplay: false),
    ];

    foreach (PreCombatHarmonyPatchDescriptor descriptor in rejectedCombatPatches)
    {
        Equal(true, PreCombatSts2SimEligibility.TryFindCriticalHarmonyPatchViolation([descriptor], out string reason));
        Equal(true, reason.Contains(descriptor.MethodName, StringComparison.Ordinal));
    }

    PreCombatHarmonyPatchDescriptor[] allowedPatches =
    [
        Patch("MegaCrit.Sts2.Core.Commands", "CreatureCmd", "Heal", affectsGameplay: false),
        Patch("MegaCrit.Sts2.Core.Commands", "DamageCmd", "Attack", affectsGameplay: false),
        Patch("MegaCrit.Sts2.Core.Commands", "CreatureCmd", "Heal", affectsGameplay: true, isTrustedOwner: true),
        Patch("MegaCrit.Sts2.Core.Combat", "CombatManager", "SetUpCombat", affectsGameplay: true, isTrustedOwner: true),
        Patch("MegaCrit.Sts2.Core.Runs", "RunManager", "EnterRoomInternal", affectsGameplay: true),
        Patch("MegaCrit.Sts2.Core.Commands", "VfxCmd", "Play", affectsGameplay: true),
        Patch("MegaCrit.Sts2.Core.Commands", "SfxCmd", "Play", affectsGameplay: true),
        Patch("MegaCrit.Sts2.Core.Nodes.Combat", "NCombatUi", "Update", affectsGameplay: true),
    ];

    Equal(false, PreCombatSts2SimEligibility.TryFindCriticalHarmonyPatchViolation(allowedPatches, out string allowedReason));
    Equal(string.Empty, allowedReason);
    return Task.CompletedTask;
});
await Check("concrete card gameplay hook overrides classify as CombatGameplay while metadata getters stay None", () =>
{
    static PreCombatHarmonyPatchDescriptor CardPatch(
        string cardName,
        string methodName,
        bool? affectsGameplay = true,
        bool hasKnownModId = true,
        bool isConcreteCardGameplayOverride = false,
        string? targetDisplayName = null) => new(
            DeclaringNamespace: "MegaCrit.Sts2.Core.Models.Cards",
            RootDeclaringTypeName: cardName,
            MethodName: methodName,
            IsMonsterModel: false,
            IsBaseGamePatch: false,
            IsTrustedOwner: false,
            AffectsGameplay: affectsGameplay,
            HasKnownModId: hasKnownModId,
            PatchDisplayName: "ProbeMod.CardPatch.Prefix",
            TargetDisplayName: targetDisplayName ?? $"MegaCrit.Sts2.Core.Models.Cards.{cardName}.{methodName}",
            Owner: "probe_mod",
            IsConcreteCardGameplayOverride: isConcreteCardGameplayOverride);

    PreCombatHarmonyPatchDescriptor[] rejectedCardOverrides =
    [
        CardPatch("Normality", "ShouldPlay"),
        CardPatch("Normality", "ShouldPlay", affectsGameplay: null, hasKnownModId: false),
        CardPatch("Normality", "MoveNext", isConcreteCardGameplayOverride: true, targetDisplayName: "MegaCrit.Sts2.Core.Models.Cards.Normality+<ShouldPlay>d__3.MoveNext"),
        CardPatch("Normality", "<ShouldPlay>d__3.MoveNext"),
        CardPatch("Normality", "<OnPlay>d__12.MoveNext"),
        CardPatch("Normality", "<ShouldPlay>g__Check|0_0"),
        CardPatch("Normality", "MoveNext", targetDisplayName: "MegaCrit.Sts2.Core.Models.Cards.Normality+<ShouldPlay>d__3.MoveNext"),
        CardPatch("StrikeIronclad", "OnPlay"),
        CardPatch("StrikeIronclad", "CanPlay"),
        CardPatch("Regret", "OnTurnEndInHand"),
        CardPatch("StrikeIronclad", "BeforeCardPlayed"),
        CardPatch("StrikeIronclad", "AfterCardDrawn"),
        CardPatch("StrikeIronclad", "ModifyDamageAdditive"),
        CardPatch("StrikeIronclad", "TryModifyEnergyCostInCombat"),
        CardPatch("StrikeIronclad", "OnUpgrade"),
        CardPatch("StrikeIronclad", "OnExhaust"),
        CardPatch("StrikeIronclad", "OnDiscard"),
        CardPatch("StrikeIronclad", "OnDrawn"),
    ];

    foreach (PreCombatHarmonyPatchDescriptor descriptor in rejectedCardOverrides)
        Equal(true, PreCombatSts2SimEligibility.TryFindCriticalHarmonyPatchViolation([descriptor], out _));

    PreCombatHarmonyPatchDescriptor[] allowedCardPatches =
    [
        CardPatch("Normality", "ShouldPlay", affectsGameplay: false),
        CardPatch("Normality", "MoveNext", affectsGameplay: false, isConcreteCardGameplayOverride: true, targetDisplayName: "MegaCrit.Sts2.Core.Models.Cards.Normality+<ShouldPlay>d__3.MoveNext"),
        CardPatch("StrikeIronclad", "get_PortraitPath", affectsGameplay: true),
        CardPatch("StrikeIronclad", "get_BetaPortraitPath", affectsGameplay: true),
        CardPatch("StrikeIronclad", "get_OverlayPath", affectsGameplay: true),
        CardPatch("StrikeIronclad", "get_AllPortraitPaths", affectsGameplay: true),
        CardPatch("StrikeIronclad", "get_Title", affectsGameplay: true),
        CardPatch("StrikeIronclad", "get_Description", affectsGameplay: true),
        CardPatch("StrikeIronclad", "GetDescription", affectsGameplay: true),
        CardPatch("StrikeIronclad", "ToString", affectsGameplay: true),
        CardPatch("StrikeIronclad", "Equals", affectsGameplay: true),
        CardPatch("StrikeIronclad", "GetHashCode", affectsGameplay: true),
        CardPatch("StrikeIronclad", "get_ExtraHoverTips", affectsGameplay: true),
        CardPatch("StrikeIronclad", "get_ShouldGlowGoldInternal", affectsGameplay: true),
        CardPatch("StrikeIronclad", "<LoadPortrait>d__1.MoveNext", affectsGameplay: true),
    ];

    Equal(false, PreCombatSts2SimEligibility.TryFindCriticalHarmonyPatchViolation(allowedCardPatches, out string allowedCardReason));
    Equal(string.Empty, allowedCardReason);
    return Task.CompletedTask;
});
await Check("bridge IsCorrupt reads immutable snapshot without blocking during activation lock", async () =>
{
    string repoRoot = FindRepoRoot();
    string scratchRoot = Path.Combine(repoRoot, ".local", "tool-tasks", "sts2sim-bridge", "lockfree-check-" + Guid.NewGuid().ToString("N"));
    using var enteredLock = new ManualResetEventSlim(false);
    using var releaseLock = new ManualResetEventSlim(false);
    try
    {
        Directory.CreateDirectory(Path.Combine(scratchRoot, "sts2sim"));
        PreCombatSts2SimBridge.ResetForTesting();
        PreCombatSts2SimBridge.ModDirectoryOverrideForTesting = scratchRoot;
        CombatSolver.Entry.Logger.ThrowOnError = false;
        CombatSolver.Entry.Logger.Errors.Clear();
        PreCombatSts2SimBridge.ActivationHookForTesting = () =>
        {
            enteredLock.Set();
            Equal(true, releaseLock.Wait(TimeSpan.FromSeconds(5)));
        };

        Task<PreCombatBridgeAvailability> activationTask = Task.Run(() => PreCombatSts2SimBridge.EnsureActivated(out _));
        Equal(true, enteredLock.Wait(TimeSpan.FromSeconds(2)));

        Equal(false, PreCombatSts2SimBridge.IsCorrupt);
        Equal(false, PreCombatSts2SimBridge.IsInCombatUnavailable);
        PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.CorruptOrIncompatible;
        Equal(true, PreCombatSts2SimBridge.IsCorrupt);
        Equal(InCombatBridgeAvailability.CorruptOrIncompatible, PreCombatSts2SimBridge.EnsureInCombatActivated(out _));
        PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.Absent;
        Equal(InCombatBridgeAvailability.Absent, PreCombatSts2SimBridge.EnsureInCombatActivated(out _));
        PreCombatSts2SimBridge.AvailabilityOverrideForTesting = PreCombatBridgeAvailability.Ready;
        Equal(false, PreCombatSts2SimBridge.IsCorrupt);
        Equal(InCombatBridgeAvailability.Ready, PreCombatSts2SimBridge.EnsureInCombatActivated(out _));
        PreCombatSts2SimBridge.RunInCombatHookForTesting = (_, _, _) =>
            throw new InvalidOperationException("Should not invoke hook during activation check.");
        Equal(false, PreCombatSts2SimBridge.IsInCombatUnavailable);
        Equal(InCombatBridgeAvailability.Ready, PreCombatSts2SimBridge.EnsureInCombatActivated(out _));
        PreCombatSts2SimBridge.RunInCombatHookForTesting = null;
        PreCombatSts2SimBridge.AvailabilityOverrideForTesting = null;
        Equal(false, PreCombatSts2SimBridge.IsCorrupt);
        Equal(false, PreCombatSts2SimBridge.IsInCombatUnavailable);

        releaseLock.Set();
        Equal(PreCombatBridgeAvailability.CorruptOrIncompatible, await activationTask);
        Equal(true, PreCombatSts2SimBridge.IsCorrupt);
        int attemptsAfterFailure = PreCombatSts2SimBridge.ActivationAttemptCountForTesting;
        Equal(PreCombatBridgeAvailability.CorruptOrIncompatible, PreCombatSts2SimBridge.EnsureActivated(out _));
        Equal(attemptsAfterFailure, PreCombatSts2SimBridge.ActivationAttemptCountForTesting);

        PreCombatSts2SimBridge.ResetForTesting();
        Equal(false, PreCombatSts2SimBridge.IsCorrupt);
    }
    finally
    {
        releaseLock.Set();
        PreCombatSts2SimBridge.ResetForTesting();
        PreCombatSts2SimBridge.ModDirectoryOverrideForTesting = null;
        CombatSolver.Entry.Logger.ThrowOnError = true;
        if (Directory.Exists(scratchRoot))
            Directory.Delete(scratchRoot, recursive: true);
    }
});
Console.WriteLine($"Passed {passed} pre-combat request, ABI, and idle checks.");

async Task Check(string name, Func<Task> body)
{
    if (args.Length != 0 && !name.Contains(args[0], StringComparison.Ordinal))
        return;
    await body().WaitAsync(TimeSpan.FromSeconds(5));
    Equal(false, PreCombatForecastWorker.Started.Reader.TryRead(out _));
    passed++;
    Console.WriteLine($"PASS {name}");
}
static RunState NewRun() => RunManager.Instance.Run = new();
static Task<PreCombatForecastResult> Forecast(RunState run, PreCombatForecastOptions? options = null,
    CancellationToken cancellation = default) => PreCombatForecastApi.ForecastAsync(
        run, new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
        options: options, cancellationToken: cancellation);
static Task<PreCombatForecastResult> Simulate(RunState run) => PreCombatForecastApi.SimulateAsync(
    run, new EncounterModel(), PreCombatRoomKind.Normal);
static Task<PreCombatForecastResult> SimulatePlanning(RunState run) => PreCombatForecastApi.SimulatePlanningAsync(
    run, new SerializableRun(), new EncounterModel(), 2, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal);
static async Task<PreCombatForecastWorker.Work> NextWork() =>
    await PreCombatForecastWorker.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}.");
}
static TException ExpectThrows<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException ex) { return ex; }
    throw new Exception($"Expected {typeof(TException).Name} to be thrown.");
}
static async Task ExpectThrowsAsync<TException>(Func<Task> action, string? requiredMessageFragment = null)
    where TException : Exception
{
    try { await action(); }
    catch (TException ex)
    {
        if (requiredMessageFragment is not null
            && !ex.Message.Contains(requiredMessageFragment, StringComparison.Ordinal))
        {
            throw new Exception($"Expected message fragment '{requiredMessageFragment}', got '{ex.Message}'.");
        }
        return;
    }
    throw new Exception($"Expected {typeof(TException).Name} to be thrown.");
}
static string FindRepoRoot()
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "CombatSolver.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate CombatSolver.csproj ancestor directory.");
}
