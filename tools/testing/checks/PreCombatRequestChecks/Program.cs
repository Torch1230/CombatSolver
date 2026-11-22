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
    ExpectThrows<JsonException>(() => PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(
        System.Text.Encoding.UTF8.GetBytes("{\"abiVersion\":1,\"outcome\":\"Succeeded\",\"effectiveDegreeOfParallelism\":1,\"totalElapsedMilliseconds\":\"NaN\"}")));
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
await Check("ABI v2 capabilities discovery, defaults, override, and validation", () =>
{
    PreCombatSts2SimBridge.ResetForTesting();
    Equal(0, PreCombatSts2SimBridge.ActivationAttemptCountForTesting);
    Equal(false, PreCombatSts2SimBridge.TryGetCachedCapabilities(out Sts2SimBridgeCapabilitiesDto unactivatedCached));
    Equal(Sts2SimBridgeCapabilitiesDto.DefaultV1, unactivatedCached);
    Equal(0, PreCombatSts2SimBridge.ActivationAttemptCountForTesting);
    Equal(Sts2SimBridgeCapabilitiesDto.DefaultV1, PreCombatSts2SimBridge.GetActiveCapabilities());
    Equal(1, Sts2SimBridgeCapabilitiesDto.DefaultV1.PortfolioOptionsVersion);
    Equal(false, Sts2SimBridgeCapabilitiesDto.DefaultV1.SupportsNoveltyPortfolio);
    Equal(false, Sts2SimBridgeCapabilitiesDto.DefaultV1.SupportsEarlyTurnExploration);

    Sts2SimBridgeCapabilitiesDto v2 = Sts2SimBridgeCapabilitiesDto.V2Default;
    string v2Json = JsonSerializer.Serialize(v2, PreCombatSts2SimAbi.JsonOptions);
    using (JsonDocument doc = JsonDocument.Parse(v2Json))
    {
        JsonElement root = doc.RootElement;
        Equal(2, root.GetProperty("portfolioOptionsVersion").GetInt32());
        Equal(true, root.GetProperty("supportsBeamWidthPortfolioFull").GetBoolean());
        Equal(true, root.GetProperty("supportsNoveltyPortfolio").GetBoolean());
        Equal(true, root.GetProperty("supportsAdaptiveNoveltyRefinement").GetBoolean());
        Equal(true, root.GetProperty("supportsEarlyTurnExploration").GetBoolean());
        Equal(2, root.GetProperty("maxEarlyTurnExplorationDepth").GetInt32());
        Equal(2_400_000, root.GetProperty("maxEarlyTurnExplorationBudgetMilliseconds").GetInt32());
        Equal(true, root.GetProperty("supportsParallelPortfolioScheduling").GetBoolean());
        Equal(true, root.GetProperty("supportsExtendedMetrics").GetBoolean());
        Equal(true, root.GetProperty("supportsCosmeticTestSubjectKills").GetBoolean());
    }
    Equal(v2, PreCombatSts2SimBridge.DeserializeAndValidateCapabilities(v2Json));

    PreCombatSts2SimBridge.CapabilitiesOverrideForTesting = v2;
    Equal(true, PreCombatSts2SimBridge.TryGetCachedCapabilities(out Sts2SimBridgeCapabilitiesDto forcedCached));
    Equal(v2, forcedCached);
    Equal(v2, PreCombatSts2SimBridge.GetActiveCapabilities());
    Equal(0, PreCombatSts2SimBridge.ActivationAttemptCountForTesting);
    PreCombatSts2SimBridge.ResetForTesting();
    Equal(false, PreCombatSts2SimBridge.TryGetCachedCapabilities(out _));
    Equal(Sts2SimBridgeCapabilitiesDto.DefaultV1, PreCombatSts2SimBridge.GetActiveCapabilities());

    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimBridge.DeserializeAndValidateCapabilities(""));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimBridge.ValidateCapabilities(v2 with { PortfolioOptionsVersion = 0 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimBridge.ValidateCapabilities(v2 with { MaxEarlyTurnExplorationDepth = -1 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimBridge.ValidateCapabilities(v2 with { MaxEarlyTurnExplorationDepth = 9 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimBridge.ValidateCapabilities(v2 with { MaxEarlyTurnExplorationBudgetMilliseconds = -1 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimBridge.ValidateCapabilities(v2 with { MaxEarlyTurnExplorationDepth = 0 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimBridge.ValidateCapabilities(v2 with { MaxEarlyTurnExplorationBudgetMilliseconds = 0 }));
    return Task.CompletedTask;
});
await Check("ABI v1 vs v2 portfolio policy negotiation, conflicts, and BuiltIn-only gates", () =>
{
    var baselineInput = new Sts2SimPortfolioPolicyInput(
        UseBeamWidthPortfolio: true,
        UseNoveltyPortfolio: false,
        EarlyTurnExplorationDepth: 0,
        EarlyTurnExplorationBudgetMilliseconds: 0,
        BeamWidthPortfolioWidths: null,
        BeamWidthPortfolioPlainBaselineMember: true,
        ReallocatedRefinementPortfolio: true,
        OffensiveRefinementPortfolio: false,
        BoundedOffensiveRefinementPortfolio: false,
        StopPortfolioAtHpTarget: true,
        AdaptiveNoveltyRefinement: false,
        NoveltySearch: null,
        NoveltyBudget: null,
        MaxDegreeOfParallelism: 4);

    Sts2SimBridgeCapabilitiesDto v1 = Sts2SimBridgeCapabilitiesDto.DefaultV1;
    Equal(true, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in baselineInput, v1, out var v1Resolved, out string? v1Err));
    Equal<string?>(null, v1Err);
    Equal(Sts2SimPortfolioPolicyFields.V1Empty, v1Resolved);

    var noveltySearch = new Sts2SimNoveltySearchOptionsDto(2, 1, 2048, 500_000, 128, 32);
    var noveltyBudget = new Sts2SimNoveltyPortfolioBudgetDto(2, 5_000, 2_500);
    var customNoveltyBudget = new Sts2SimNoveltyPortfolioBudgetDto(3, 8_000, 3_000);

    Sts2SimPortfolioPolicyInput[] v1Rejected =
    [
        baselineInput with { UseNoveltyPortfolio = true },
        baselineInput with { EarlyTurnExplorationDepth = 1, EarlyTurnExplorationBudgetMilliseconds = 10_000 },
        baselineInput with { EarlyTurnExplorationDepth = 2, EarlyTurnExplorationBudgetMilliseconds = 60_000 },
        baselineInput with { BeamWidthPortfolioWidths = [24, 48] },
        baselineInput with { BeamWidthPortfolioWidths = [] },
        baselineInput with { BeamWidthPortfolioPlainBaselineMember = false },
        baselineInput with { ReallocatedRefinementPortfolio = false },
        baselineInput with { StopPortfolioAtHpTarget = false },
        baselineInput with { AdaptiveNoveltyRefinement = true },
        baselineInput with { OffensiveRefinementPortfolio = true },
        baselineInput with { BoundedOffensiveRefinementPortfolio = true },
        baselineInput with { NoveltySearch = noveltySearch },
        baselineInput with { NoveltyBudget = customNoveltyBudget },
    ];
    foreach (Sts2SimPortfolioPolicyInput rejected in v1Rejected)
    {
        Equal(false, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in rejected, v1, out _, out string? reason));
        Equal(true, !string.IsNullOrWhiteSpace(reason));
    }

    Sts2SimBridgeCapabilitiesDto v2NoBeamFull = Sts2SimBridgeCapabilitiesDto.V2Default with
    {
        SupportsBeamWidthPortfolioFull = false,
        SupportsAdaptiveNoveltyRefinement = false,
        SupportsEarlyTurnExploration = false,
        MaxEarlyTurnExplorationDepth = 0,
        MaxEarlyTurnExplorationBudgetMilliseconds = 0,
        SupportsParallelPortfolioScheduling = false,
    };
    Equal(true, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in baselineInput, v2NoBeamFull, out var partialResolved, out string? partialErr));
    Equal<string?>(null, partialErr);
    Equal(2, partialResolved.PortfolioOptionsVersion);
    Equal<IReadOnlyList<int>?>(null, partialResolved.BeamWidthPortfolioWidths);
    Equal<bool?>(null, partialResolved.BeamWidthPortfolioPlainBaselineMember);
    Equal<bool?>(null, partialResolved.ReallocatedRefinementPortfolio);
    Equal<bool?>(null, partialResolved.OffensiveRefinementPortfolio);
    Equal<bool?>(null, partialResolved.BoundedOffensiveRefinementPortfolio);
    Equal<bool?>(null, partialResolved.StopPortfolioAtHpTarget);
    Equal<bool?>(null, partialResolved.AdaptiveNoveltyRefinement);
    Equal<int?>(null, partialResolved.EarlyTurnExplorationDepth);
    Equal<string?>(null, partialResolved.PortfolioSchedulingMode);

    Sts2SimPortfolioPolicyInput[] partialV2Rejected =
    [
        baselineInput with { ReallocatedRefinementPortfolio = false },
        baselineInput with { StopPortfolioAtHpTarget = false },
        baselineInput with { BeamWidthPortfolioPlainBaselineMember = false },
        baselineInput with { BeamWidthPortfolioWidths = [30] },
        baselineInput with { OffensiveRefinementPortfolio = true },
        baselineInput with { BoundedOffensiveRefinementPortfolio = true },
        baselineInput with { AdaptiveNoveltyRefinement = true },
        baselineInput with { EarlyTurnExplorationDepth = 1, EarlyTurnExplorationBudgetMilliseconds = 5_000 },
    ];
    foreach (Sts2SimPortfolioPolicyInput rejected in partialV2Rejected)
    {
        Equal(false, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in rejected, v2NoBeamFull, out _, out string? partialRejectReason));
        Equal(true, !string.IsNullOrWhiteSpace(partialRejectReason));
    }

    Sts2SimBridgeCapabilitiesDto v2AdaptiveOnly = Sts2SimBridgeCapabilitiesDto.V2Default with
    {
        SupportsNoveltyPortfolio = false,
        SupportsAdaptiveNoveltyRefinement = true,
    };
    Sts2SimPortfolioPolicyInput standaloneNoveltySearch = baselineInput with { NoveltySearch = noveltySearch };
    Equal(false, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in standaloneNoveltySearch, v2AdaptiveOnly, out _, out _));

    Sts2SimBridgeCapabilitiesDto v2 = Sts2SimBridgeCapabilitiesDto.V2Default;
    Sts2SimPortfolioPolicyInput fullV2 = baselineInput with
    {
        UseNoveltyPortfolio = true,
        EarlyTurnExplorationDepth = 2,
        EarlyTurnExplorationBudgetMilliseconds = 2_400_000,
        BeamWidthPortfolioWidths = [24, 60, 90],
        BeamWidthPortfolioPlainBaselineMember = false,
        ReallocatedRefinementPortfolio = false,
        BoundedOffensiveRefinementPortfolio = true,
        StopPortfolioAtHpTarget = false,
        AdaptiveNoveltyRefinement = true,
        NoveltySearch = noveltySearch,
        NoveltyBudget = noveltyBudget,
        MaxDegreeOfParallelism = 4,
    };
    Equal(true, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in fullV2, v2, out var resolvedParallel, out string? parallelErr));
    Equal<string?>(null, parallelErr);
    Equal(2, resolvedParallel.PortfolioOptionsVersion);
    Equal("Parallel", resolvedParallel.PortfolioSchedulingMode);
    Equal(2, resolvedParallel.EarlyTurnExplorationDepth);
    Equal(2_400_000, resolvedParallel.EarlyTurnExplorationBudgetMilliseconds);
    Equal(3, resolvedParallel.BeamWidthPortfolioWidths!.Count);
    Equal(false, resolvedParallel.BeamWidthPortfolioPlainBaselineMember);
    Equal(noveltySearch, resolvedParallel.NoveltySearch);
    Equal(noveltyBudget, resolvedParallel.NoveltyBudget);

    Sts2SimPortfolioPolicyInput dop1 = fullV2 with { MaxDegreeOfParallelism = 1 };
    Equal(true, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in dop1, v2, out var resolvedSeq, out _));
    Equal("Sequential", resolvedSeq.PortfolioSchedulingMode);

    Sts2SimPortfolioPolicyInput capBounded = fullV2 with { EarlyTurnExplorationBudgetMilliseconds = 3_000_000 };
    Equal(true, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in capBounded, v2, out var resolvedCap, out _));
    Equal(2_400_000, resolvedCap.EarlyTurnExplorationBudgetMilliseconds);

    Sts2SimPortfolioPolicyInput conflict = fullV2 with
    {
        OffensiveRefinementPortfolio = true,
        BoundedOffensiveRefinementPortfolio = true,
    };
    Equal(false, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in conflict, v2, out _, out string? conflictReason));
    Equal(true, !string.IsNullOrWhiteSpace(conflictReason));

    Sts2SimPortfolioPolicyInput overDepth = fullV2 with { EarlyTurnExplorationDepth = 3 };
    Equal(false, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in overDepth, v2, out _, out _));

    Sts2SimPortfolioPolicyInput[] builtInOnly =
    [
        baselineInput with { VerifyIncrementalSearch = true },
        baselineInput with { MeasurePhasePerformance = true },
        baselineInput with { IncludeTurnSetup = true },
        baselineInput with { HasBossTempoSearch = true },
        baselineInput with { HasContextualRanking = true },
        baselineInput with { ContinuousThreatRanking = true },
        baselineInput with { BaseScoreTacticalTies = true },
        baselineInput with { HasBeamWeightPerturbation = true },
        baselineInput with { HasDevelopmentStrategy = true },
        baselineInput with { HasPortfolioExperiment = true },
        baselineInput with { PileOrderInvariantMask = 1 },
        baselineInput with { TranspositionPruningDisabledMask = 1 },
        baselineInput with { StateKeySalt = 7 },
        baselineInput with { MemoryNoProgressRecoveryLimit = 3 },
        baselineInput with { HasBuiltInOnlyProfileTuning = true },
    ];
    foreach (Sts2SimPortfolioPolicyInput unsupported in builtInOnly)
    {
        Equal(false, Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(in unsupported, v2, out _, out string? reason));
        Equal(true, !string.IsNullOrWhiteSpace(reason));
        Equal(true, Sts2SimPortfolioPolicyBridge.IsBuiltInOnlyPolicy(in unsupported, out _));
    }
    return Task.CompletedTask;
});
await Check("ABI v2 policy wire serialization, null omission, and validator bounds", () =>
{
    var v1Policy = new Sts2SimPolicyDto(
        "Smart",
        [],
        true,
        true,
        [],
        new Sts2SimGrowthBudgetsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null),
        null,
        false,
        "ProgressionFirst",
        "ProgressionFirst",
        0,
        true,
        true,
        false,
        false,
        60,
        120_000,
        32,
        18,
        24,
        10_000);
    PreCombatSts2SimWireValidator.ValidatePolicy(v1Policy, Sts2SimBridgeCapabilitiesDto.DefaultV1);
    string v1Json = JsonSerializer.Serialize(v1Policy, PreCombatSts2SimAbi.JsonOptions);
    using (JsonDocument doc = JsonDocument.Parse(v1Json))
    {
        Equal(false, doc.RootElement.TryGetProperty("portfolioOptionsVersion", out _));
        Equal(false, doc.RootElement.TryGetProperty("beamWidthPortfolioWidths", out _));
        Equal(false, doc.RootElement.TryGetProperty("noveltySearch", out _));
        Equal(false, doc.RootElement.TryGetProperty("earlyTurnExplorationDepth", out _));
        Equal(false, doc.RootElement.TryGetProperty("portfolioSchedulingMode", out _));
    }

    var noveltySearch = new Sts2SimNoveltySearchOptionsDto(2, 0, 2048, 1_000_000, 128, 32);
    var noveltyBudget = new Sts2SimNoveltyPortfolioBudgetDto(2, 5_000, 2_500);
    var v2Policy = v1Policy with
    {
        UseNoveltyPortfolio = true,
        UseEarlyTurnExploration = true,
        PortfolioOptionsVersion = 2,
        BeamWidthPortfolioWidths = [24, 60],
        BeamWidthPortfolioPlainBaselineMember = false,
        ReallocatedRefinementPortfolio = true,
        OffensiveRefinementPortfolio = false,
        BoundedOffensiveRefinementPortfolio = true,
        StopPortfolioAtHpTarget = false,
        AdaptiveNoveltyRefinement = true,
        NoveltySearch = noveltySearch,
        NoveltyBudget = noveltyBudget,
        EarlyTurnExplorationDepth = 2,
        EarlyTurnExplorationBudgetMilliseconds = 1_200_000,
        PortfolioSchedulingMode = "Parallel",
    };
    PreCombatSts2SimWireValidator.ValidatePolicy(v2Policy, Sts2SimBridgeCapabilitiesDto.V2Default);
    string v2Json = JsonSerializer.Serialize(v2Policy, PreCombatSts2SimAbi.JsonOptions);
    Sts2SimPolicyDto roundTripped = JsonSerializer.Deserialize<Sts2SimPolicyDto>(v2Json, PreCombatSts2SimAbi.JsonOptions)!;
    Equal(2, roundTripped.PortfolioOptionsVersion);
    Equal(false, roundTripped.BeamWidthPortfolioPlainBaselineMember);
    Equal(true, roundTripped.BoundedOffensiveRefinementPortfolio);
    Equal(false, roundTripped.StopPortfolioAtHpTarget);
    Equal(noveltySearch, roundTripped.NoveltySearch);
    Equal(noveltyBudget, roundTripped.NoveltyBudget);
    Equal(2, roundTripped.EarlyTurnExplorationDepth);
    Equal(1_200_000, roundTripped.EarlyTurnExplorationBudgetMilliseconds);
    Equal("Parallel", roundTripped.PortfolioSchedulingMode);

    var inCombatV2Policy = new InCombatSts2SimPolicyDto(
        "Smart",
        [],
        true,
        true,
        [],
        v1Policy.GrowthBudgets,
        null,
        false,
        "ProgressionFirst",
        "ProgressionFirst",
        0,
        true,
        true,
        true,
        true,
        60,
        22,
        7,
        120_000,
        32,
        18,
        24,
        120_000,
        null,
        2,
        [24, 60],
        false,
        true,
        false,
        true,
        true,
        true,
        noveltySearch,
        noveltyBudget,
        2,
        2_400_000,
        "Parallel");
    InCombatSts2SimWireValidator.ValidatePolicy(inCombatV2Policy, Sts2SimBridgeCapabilitiesDto.V2Default);
    string inCombatJson = JsonSerializer.Serialize(inCombatV2Policy, PreCombatSts2SimAbi.JsonOptions);
    InCombatSts2SimPolicyDto inCombatRoundTripped =
        JsonSerializer.Deserialize<InCombatSts2SimPolicyDto>(inCombatJson, PreCombatSts2SimAbi.JsonOptions)!;
    Equal(22, inCombatRoundTripped.PotionFreeBeamWidth);
    Equal(7, inCombatRoundTripped.PotionBeamWidth);
    Equal(2_400_000, inCombatRoundTripped.EarlyTurnExplorationBudgetMilliseconds);

    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v2Policy with { PortfolioOptionsVersion = 3 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v2Policy with { BeamWidthPortfolioWidths = Enumerable.Repeat(10, 17).ToArray() }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v2Policy with { BeamWidthPortfolioWidths = [0] }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v2Policy with { OffensiveRefinementPortfolio = true, BoundedOffensiveRefinementPortfolio = true }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v2Policy with { NoveltySearch = noveltySearch with { Width = 3 } }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v2Policy with { NoveltyBudget = noveltyBudget with { TimeDivisor = 1 } }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v2Policy with { EarlyTurnExplorationDepth = 9 }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v2Policy with { EarlyTurnExplorationDepth = 1, EarlyTurnExplorationBudgetMilliseconds = null }));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v2Policy with { EarlyTurnExplorationBudgetMilliseconds = 2_400_001 }, Sts2SimBridgeCapabilitiesDto.V2Default));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v2Policy with { PortfolioSchedulingMode = "Concurrent" }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidatePolicy(
        inCombatV2Policy with { OffensiveRefinementPortfolio = true, BoundedOffensiveRefinementPortfolio = true },
        Sts2SimBridgeCapabilitiesDto.V2Default));

    Sts2SimBridgeCapabilitiesDto v1Caps = Sts2SimBridgeCapabilitiesDto.DefaultV1;
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v1Policy with { ReallocatedRefinementPortfolio = false }, v1Caps));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v1Policy with { StopPortfolioAtHpTarget = false }, v1Caps));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v1Policy with { NoveltySearch = noveltySearch }, v1Caps));
    ExpectThrows<InvalidDataException>(() => PreCombatSts2SimWireValidator.ValidatePolicy(
        v1Policy with { NoveltyBudget = noveltyBudget }, v1Caps));

    InCombatSts2SimPolicyDto inCombatBaseline = inCombatV2Policy with
    {
        UseNoveltyPortfolio = false,
        UseEarlyTurnExploration = false,
        PortfolioOptionsVersion = null,
        BeamWidthPortfolioWidths = null,
        BeamWidthPortfolioPlainBaselineMember = null,
        ReallocatedRefinementPortfolio = null,
        OffensiveRefinementPortfolio = null,
        BoundedOffensiveRefinementPortfolio = null,
        StopPortfolioAtHpTarget = null,
        AdaptiveNoveltyRefinement = null,
        NoveltySearch = null,
        NoveltyBudget = null,
        EarlyTurnExplorationDepth = null,
        EarlyTurnExplorationBudgetMilliseconds = null,
        PortfolioSchedulingMode = null,
    };
    InCombatSts2SimWireValidator.ValidatePolicy(
        inCombatBaseline with { PortfolioOptionsVersion = 2 },
        Sts2SimBridgeCapabilitiesDto.V2Default with
        {
            SupportsBeamWidthPortfolioFull = false,
            SupportsNoveltyPortfolio = false,
            SupportsAdaptiveNoveltyRefinement = false,
            SupportsEarlyTurnExploration = false,
            MaxEarlyTurnExplorationDepth = 0,
            MaxEarlyTurnExplorationBudgetMilliseconds = 0,
            SupportsParallelPortfolioScheduling = false,
        });
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidatePolicy(
        inCombatBaseline with { ReallocatedRefinementPortfolio = false }, v1Caps));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidatePolicy(
        inCombatBaseline with { StopPortfolioAtHpTarget = false }, v1Caps));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidatePolicy(
        inCombatBaseline with { PortfolioOptionsVersion = 2, ReallocatedRefinementPortfolio = true },
        Sts2SimBridgeCapabilitiesDto.V2Default with { SupportsBeamWidthPortfolioFull = false }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidatePolicy(
        inCombatBaseline with { PortfolioOptionsVersion = 2, StopPortfolioAtHpTarget = true },
        Sts2SimBridgeCapabilitiesDto.V2Default with { SupportsBeamWidthPortfolioFull = false }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidatePolicy(
        inCombatBaseline with { PortfolioOptionsVersion = 2, AdaptiveNoveltyRefinement = false },
        Sts2SimBridgeCapabilitiesDto.V2Default with { SupportsAdaptiveNoveltyRefinement = false }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidatePolicy(
        inCombatBaseline with { PortfolioOptionsVersion = 2, NoveltySearch = noveltySearch },
        Sts2SimBridgeCapabilitiesDto.V2Default with { SupportsNoveltyPortfolio = false }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidatePolicy(
        inCombatBaseline with { PortfolioOptionsVersion = 2, PortfolioSchedulingMode = "Sequential" },
        Sts2SimBridgeCapabilitiesDto.V2Default with { SupportsParallelPortfolioScheduling = false }));
    ExpectThrows<InvalidDataException>(() => InCombatSts2SimWireValidator.ValidatePolicy(
        inCombatBaseline with { PortfolioOptionsVersion = 2, EarlyTurnExplorationDepth = 0 },
        Sts2SimBridgeCapabilitiesDto.V2Default with
        {
            SupportsEarlyTurnExploration = false,
            MaxEarlyTurnExplorationDepth = 0,
            MaxEarlyTurnExplorationBudgetMilliseconds = 0,
        }));
    return Task.CompletedTask;
});
await Check("in-combat v1 compatibility, extended metrics validation, and early-turn timeout wiring", () =>
{
    string v1ResultJson = """
        {
          "abiVersion": 1,
          "outcome": "Succeeded",
          "failureCode": null,
          "fieldPath": null,
          "error": null,
          "result": {
            "actions": [],
            "turnOutcomes": [
              {
                "turn": 2,
                "hpLost": 5,
                "hpRecovered": 0,
                "enemyHpLost": 20,
                "soldHp": 0,
                "maxBlock": 0,
                "actualBlock": 0,
                "energyLeft": 1,
                "potionCount": 0,
                "potionStrategicCost": 0,
                "combatEnded": true
              }
            ],
            "terminal": {
              "playerDead": false,
              "allEnemiesDead": true,
              "playerHp": 75,
              "playerMaxHp": 80,
              "cumulativePlayerHpLost": 5,
              "recoveredPlayerHp": 0,
              "projectedPlayerHp": 75,
              "playerBlock": 0,
              "enemyHp": 0,
              "aliveEnemyCount": 0,
              "energy": 1,
              "stars": 0,
              "handCount": 2,
              "outstandingStolenResource": 0,
              "turn": 2,
              "shufflesCrossed": 0,
              "postCombatRelicHeal": 0,
              "projectedBattleHpLost": 5,
              "combatEndedTurn": 2,
              "deathTurn": null,
              "onlyDeathRoutes": false,
              "confidence": "Complete",
              "searchBoundary": "None",
              "hasRisk": false
            },
            "potionUses": [],
            "expandedNodes": 345,
            "searchElapsedMilliseconds": 42.5
          },
          "bestSoFar": null,
          "effectiveDegreeOfParallelism": 4,
          "totalElapsedMilliseconds": 48.0
        }
        """;
    InCombatSts2SimResponseDto v1Resp =
        InCombatSts2SimWireValidator.DeserializeAndValidateResponse(System.Text.Encoding.UTF8.GetBytes(v1ResultJson));
    Sts2SimMaterializedResultMetrics v1Metrics =
        Sts2SimPortfolioPolicyBridge.ResolveResultMetrics(v1Resp.Result!, v1Resp.EffectiveDegreeOfParallelism);
    Equal(345, v1Metrics.ExpandedNodes);
    Equal(345L, v1Metrics.TotalExpandedNodes);
    Equal(0, v1Metrics.TransitionCount);
    Equal(0, v1Metrics.ChoiceBranchesEvaluated);
    Equal(4, v1Metrics.MaxParallelExpansionConcurrency);

    string v2ResultJson = """
        {
          "abiVersion": 1,
          "outcome": "Succeeded",
          "failureCode": null,
          "fieldPath": null,
          "error": null,
          "result": {
            "actions": [],
            "turnOutcomes": [
              {
                "turn": 2,
                "hpLost": 5,
                "hpRecovered": 0,
                "enemyHpLost": 20,
                "soldHp": 0,
                "maxBlock": 0,
                "actualBlock": 0,
                "energyLeft": 1,
                "potionCount": 0,
                "potionStrategicCost": 0,
                "combatEnded": true
              }
            ],
            "terminal": {
              "playerDead": false,
              "allEnemiesDead": true,
              "playerHp": 78,
              "playerMaxHp": 80,
              "cumulativePlayerHpLost": 2,
              "recoveredPlayerHp": 0,
              "projectedPlayerHp": 78,
              "playerBlock": 0,
              "enemyHp": 0,
              "aliveEnemyCount": 0,
              "energy": 2,
              "stars": 0,
              "handCount": 1,
              "outstandingStolenResource": 0,
              "turn": 2,
              "shufflesCrossed": 0,
              "postCombatRelicHeal": 0,
              "projectedBattleHpLost": 2,
              "combatEndedTurn": 2,
              "deathTurn": null,
              "onlyDeathRoutes": false,
              "confidence": "Complete",
              "searchBoundary": "None",
              "hasRisk": false
            },
            "potionUses": [],
            "expandedNodes": 999,
            "searchElapsedMilliseconds": 42.5,
            "expandedParents": 120,
            "transitionCount": 860,
            "choiceReplayAttempts": 44,
            "observedPeakParallelism": 6,
            "activePhaseLabel": "EarlyTurn"
          },
          "bestSoFar": null,
          "effectiveDegreeOfParallelism": 8,
          "totalElapsedMilliseconds": 48.0
        }
        """;
    InCombatSts2SimResponseDto v2Resp =
        InCombatSts2SimWireValidator.DeserializeAndValidateResponse(System.Text.Encoding.UTF8.GetBytes(v2ResultJson));
    Sts2SimMaterializedResultMetrics v2Metrics =
        Sts2SimPortfolioPolicyBridge.ResolveResultMetrics(v2Resp.Result!, v2Resp.EffectiveDegreeOfParallelism);
    Equal(120, v2Metrics.ExpandedNodes);
    Equal(120L, v2Metrics.TotalExpandedNodes);
    Equal(860, v2Metrics.TransitionCount);
    Equal(860L, v2Metrics.TotalTransitionCount);
    Equal(44, v2Metrics.ChoiceBranchesEvaluated);
    Equal(44L, v2Metrics.TotalChoiceBranchesEvaluated);
    Equal(6, v2Metrics.MaxParallelExpansionConcurrency);

    string v1ProgressJson = """
        {
          "expandedNodes": 210,
          "maxTurnReached": 2,
          "elapsedMilliseconds": 150.0,
          "currentBestProjectedHpLoss": 8,
          "currentBestWon": true,
          "currentBestCombatEndedTurn": 2,
          "currentBestPotionCount": 0,
          "currentBest": null
        }
        """;
    InCombatSts2SimProgressDto v1Prog =
        InCombatSts2SimWireValidator.DeserializeAndValidateProgress(System.Text.Encoding.UTF8.GetBytes(v1ProgressJson));
    Sts2SimMaterializedProgressMetrics v1ProgMetrics = Sts2SimPortfolioPolicyBridge.ResolveProgressMetrics(v1Prog);
    Equal(210, v1ProgMetrics.ExpandedNodes);
    Equal(210L, v1ProgMetrics.ReviewedWorldlines);
    Equal("正在搜索·Sts2Sim", v1ProgMetrics.Phase);

    var v2Prog = v1Prog with { ExpandedParents = 95L, ObservedPeakParallelism = 5, ActivePhaseLabel = "Novelty" };
    InCombatSts2SimWireValidator.ValidateProgress(v2Prog);
    Sts2SimMaterializedProgressMetrics v2ProgMetrics = Sts2SimPortfolioPolicyBridge.ResolveProgressMetrics(v2Prog);
    Equal(95, v2ProgMetrics.ExpandedNodes);
    Equal(95L, v2ProgMetrics.ReviewedWorldlines);
    Equal("正在搜索·Sts2Sim·Novelty", v2ProgMetrics.Phase);

    string highDopResultJson = v2ResultJson
        .Replace("\"observedPeakParallelism\": 6", "\"observedPeakParallelism\": 96", StringComparison.Ordinal);
    InCombatSts2SimResponseDto highDopResp =
        InCombatSts2SimWireValidator.DeserializeAndValidateResponse(System.Text.Encoding.UTF8.GetBytes(highDopResultJson));
    Sts2SimMaterializedResultMetrics highDopMetrics =
        Sts2SimPortfolioPolicyBridge.ResolveResultMetrics(highDopResp.Result!, highDopResp.EffectiveDegreeOfParallelism);
    Equal(96, highDopMetrics.MaxParallelExpansionConcurrency);

    string invalidOptionalResultJson = """
        {
          "abiVersion": 1,
          "outcome": "Succeeded",
          "failureCode": null,
          "fieldPath": null,
          "error": null,
          "result": {
            "actions": [],
            "turnOutcomes": [
              {
                "turn": 2,
                "hpLost": 5,
                "hpRecovered": 0,
                "enemyHpLost": 20,
                "soldHp": 0,
                "maxBlock": 0,
                "actualBlock": 0,
                "energyLeft": 1,
                "potionCount": 0,
                "potionStrategicCost": 0,
                "combatEnded": true
              }
            ],
            "terminal": {
              "playerDead": false,
              "allEnemiesDead": true,
              "playerHp": 75,
              "playerMaxHp": 80,
              "cumulativePlayerHpLost": 5,
              "recoveredPlayerHp": 0,
              "projectedPlayerHp": 75,
              "playerBlock": 0,
              "enemyHp": 0,
              "aliveEnemyCount": 0,
              "energy": 1,
              "stars": 0,
              "handCount": 2,
              "outstandingStolenResource": 0,
              "turn": 2,
              "shufflesCrossed": 0,
              "postCombatRelicHeal": 0,
              "projectedBattleHpLost": 5,
              "combatEndedTurn": 2,
              "deathTurn": null,
              "onlyDeathRoutes": false,
              "confidence": "Complete",
              "searchBoundary": "None",
              "hasRisk": false
            },
            "potionUses": [],
            "expandedNodes": 345,
            "searchElapsedMilliseconds": 42.5,
            "expandedParents": -1,
            "transitionCount": -5,
            "choiceReplayAttempts": -3,
            "observedPeakParallelism": 0
          },
          "bestSoFar": null,
          "effectiveDegreeOfParallelism": 6,
          "totalElapsedMilliseconds": 48.0
        }
        """;
    InCombatSts2SimResponseDto fallbackCounterResp =
        InCombatSts2SimWireValidator.DeserializeAndValidateResponse(System.Text.Encoding.UTF8.GetBytes(invalidOptionalResultJson));
    Sts2SimMaterializedResultMetrics fallbackCounterMetrics =
        Sts2SimPortfolioPolicyBridge.ResolveResultMetrics(fallbackCounterResp.Result!, fallbackCounterResp.EffectiveDegreeOfParallelism);
    Equal(345, fallbackCounterMetrics.ExpandedNodes);
    Equal(345L, fallbackCounterMetrics.TotalExpandedNodes);
    Equal(0, fallbackCounterMetrics.TransitionCount);
    Equal(0, fallbackCounterMetrics.ChoiceBranchesEvaluated);
    Equal(6, fallbackCounterMetrics.MaxParallelExpansionConcurrency);

    string highDopProgressJson = """
        {
          "expandedNodes": 210,
          "maxTurnReached": 2,
          "elapsedMilliseconds": 150.0,
          "currentBestProjectedHpLoss": 8,
          "currentBestWon": true,
          "currentBestCombatEndedTurn": 2,
          "currentBestPotionCount": 0,
          "currentBest": null,
          "expandedParents": 104,
          "transitionCount": 420,
          "choiceReplayAttempts": 19,
          "observedPeakParallelism": 96,
          "activePhaseLabel": "EarlyTurn"
        }
        """;
    InCombatSts2SimProgressDto highDopProg =
        InCombatSts2SimWireValidator.DeserializeAndValidateProgress(System.Text.Encoding.UTF8.GetBytes(highDopProgressJson));
    Sts2SimMaterializedProgressMetrics highDopProgMetrics =
        Sts2SimPortfolioPolicyBridge.ResolveProgressMetrics(highDopProg);
    Equal(96, highDopProg.ObservedPeakParallelism);
    Equal(104, highDopProgMetrics.ExpandedNodes);
    Equal("正在搜索·Sts2Sim·EarlyTurn", highDopProgMetrics.Phase);

    string invalidOptionalProgressJson = """
        {
          "expandedNodes": 210,
          "maxTurnReached": 2,
          "elapsedMilliseconds": 150.0,
          "currentBestProjectedHpLoss": 8,
          "currentBestWon": true,
          "currentBestCombatEndedTurn": 2,
          "currentBestPotionCount": 0,
          "currentBest": null,
          "expandedParents": -2,
          "transitionCount": -1,
          "choiceReplayAttempts": -1,
          "observedPeakParallelism": 0,
          "activePhaseLabel": " "
        }
        """;
    InCombatSts2SimProgressDto invalidOptionalProg =
        InCombatSts2SimWireValidator.DeserializeAndValidateProgress(System.Text.Encoding.UTF8.GetBytes(invalidOptionalProgressJson));
    Sts2SimMaterializedProgressMetrics invalidOptionalProgMetrics =
        Sts2SimPortfolioPolicyBridge.ResolveProgressMetrics(invalidOptionalProg);
    Equal(210, invalidOptionalProgMetrics.ExpandedNodes);
    Equal(210L, invalidOptionalProgMetrics.ReviewedWorldlines);
    Equal("正在搜索·Sts2Sim", invalidOptionalProgMetrics.Phase);

    Equal(150_000, Sts2SimPortfolioPolicyBridge.ComputeInCombatOverallTimeoutMilliseconds(
        120_000, 0, 0, Sts2SimBridgeCapabilitiesDto.DefaultV1));
    Equal(630_000, Sts2SimPortfolioPolicyBridge.ComputeInCombatOverallTimeoutMilliseconds(
        600_000, 2, 2_400_000, Sts2SimBridgeCapabilitiesDto.DefaultV1));
    Equal(2_430_000, Sts2SimPortfolioPolicyBridge.ComputeInCombatOverallTimeoutMilliseconds(
        120_000, 2, 2_400_000, Sts2SimBridgeCapabilitiesDto.V2Default));
    Equal(330_000, Sts2SimPortfolioPolicyBridge.ComputeInCombatOverallTimeoutMilliseconds(
        120_000, 2, 2_400_000, Sts2SimBridgeCapabilitiesDto.V2Default with { MaxEarlyTurnExplorationBudgetMilliseconds = 300_000 }));
    return Task.CompletedTask;
});
await Check("attempt work tracker accumulates adoption/fallback totals and English catalog preserves ActivePhaseLabel", () =>
{
    var tracker = new Sts2SimAttemptWorkTracker();
    Equal(false, tracker.Snapshot().HasAnyWork);

    var term = new InCombatTerminalSnapshotDto(
        false, true, 78, 80, 2, 0, 78, 0, 0, 0, 2, 0, 1, 0, 2, 0, 0, 2, 2, null, false, "Complete", "None", false);
    var previewBest = new InCombatSts2SimResultDto(
        [],
        [new InCombatTurnOutcomeDto(2, 2, 0, 20, 0, 0, 0, 2, 0, 0, true)],
        term,
        [],
        150,
        40.0,
        ExpandedParents: 40,
        TransitionCount: 220,
        ChoiceReplayAttempts: 11,
        ObservedPeakParallelism: 4);
    var prog1 = new InCombatSts2SimProgressDto(
        200, 2, 120.0, 2, true, 2, 0, previewBest,
        ExpandedParents: 80,
        TransitionCount: 500,
        ChoiceReplayAttempts: 24,
        ObservedPeakParallelism: 8,
        ActivePhaseLabel: "Novelty");
    tracker.RecordProgress(prog1, fallbackParallelism: 4);

    var prog2LegacyFallback = new InCombatSts2SimProgressDto(
        260, 2, 300.0, 2, true, 2, 0, null,
        ExpandedParents: -1,
        TransitionCount: 640,
        ChoiceReplayAttempts: null,
        ObservedPeakParallelism: 0,
        ActivePhaseLabel: "EarlyTurn");
    tracker.RecordProgress(prog2LegacyFallback, fallbackParallelism: 4);

    Sts2SimAttemptWorkSnapshot fallbackWork = tracker.Snapshot();
    Equal(true, fallbackWork.HasAnyWork);
    Equal(260L, fallbackWork.ExpandedParents);
    Equal(640L, fallbackWork.TransitionCount);
    Equal(24L, fallbackWork.ChoiceReplayAttempts);
    Equal(8, fallbackWork.ObservedPeakParallelism);
    Equal(TimeSpan.FromMilliseconds(300.0), fallbackWork.Elapsed);

    var finalDto = previewBest with
    {
        ExpandedParents = 410,
        TransitionCount = 1_250,
        ChoiceReplayAttempts = 53,
        ObservedPeakParallelism = 12,
        SearchElapsedMilliseconds = 450.0,
    };
    tracker.RecordResult(finalDto, effectiveDegreeOfParallelism: 8, elapsed: TimeSpan.FromMilliseconds(480.0));
    Sts2SimAttemptWorkSnapshot adoptedTotals = tracker.Snapshot();
    Equal(410L, adoptedTotals.ExpandedParents);
    Equal(1_250L, adoptedTotals.TransitionCount);
    Equal(53L, adoptedTotals.ChoiceReplayAttempts);
    Equal(12, adoptedTotals.ObservedPeakParallelism);
    Equal(TimeSpan.FromMilliseconds(480.0), adoptedTotals.Elapsed);

    Equal(true, Sts2SimPortfolioPolicyBridge.TryExtractSts2SimActivePhaseLabel("正在搜索·Sts2Sim", out string? baseLabel));
    Equal<string?>(null, baseLabel);
    Equal(true, Sts2SimPortfolioPolicyBridge.TryExtractSts2SimActivePhaseLabel("正在搜索·Sts2Sim·EarlyTurn", out string? earlyLabel));
    Equal<string?>("EarlyTurn", earlyLabel);
    Equal(false, Sts2SimPortfolioPolicyBridge.TryExtractSts2SimActivePhaseLabel("正在整理内存", out _));
    Equal(false, Sts2SimPortfolioPolicyBridge.TryExtractSts2SimActivePhaseLabel(string.Empty, out _));

    string englishJsonPath = Path.Combine(FindRepoRoot(), "src", "UI", "English.json");
    Dictionary<string, string> catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(englishJsonPath))!;
    Equal(true, catalog.TryGetValue("正在搜索·Sts2Sim", out string? enBase) && !string.IsNullOrWhiteSpace(enBase));
    Equal(false, enBase!.Contains("{0}", StringComparison.Ordinal));
    Equal(true, catalog.TryGetValue("正在搜索·Sts2Sim·{0}", out string? enLabel) && enLabel!.Contains("{0}", StringComparison.Ordinal));
    Equal(false, enBase.Any(static ch => ch is >= '\u4e00' and <= '\u9fff'));
    Equal(false, enLabel!.Any(static ch => ch is >= '\u4e00' and <= '\u9fff'));
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
static void ExpectThrows<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
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
