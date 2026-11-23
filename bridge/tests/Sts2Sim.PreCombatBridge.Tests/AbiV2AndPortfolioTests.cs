using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed class AbiV2AndPortfolioTests
{
    [Fact]
    public void GetCapabilitiesJson_MatchesFrozenAbiV2Contract()
    {
        string json = BridgeEntry.GetCapabilitiesJson();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("portfolioOptionsVersion").GetInt32());
        Assert.True(root.GetProperty("supportsBeamWidthPortfolioFull").GetBoolean());
        Assert.True(root.GetProperty("supportsNoveltyPortfolio").GetBoolean());
        Assert.True(root.GetProperty("supportsAdaptiveNoveltyRefinement").GetBoolean());
        Assert.True(root.GetProperty("supportsEarlyTurnExploration").GetBoolean());
        Assert.Equal(2, root.GetProperty("maxEarlyTurnExplorationDepth").GetInt32());
        Assert.Equal(2_400_000, root.GetProperty("maxEarlyTurnExplorationBudgetMilliseconds").GetInt32());
        Assert.True(root.GetProperty("supportsParallelPortfolioScheduling").GetBoolean());
        Assert.True(root.GetProperty("supportsExtendedMetrics").GetBoolean());
        Assert.True(root.GetProperty("supportsCosmeticTestSubjectKills").GetBoolean());
    }

    [Fact]
    public void PolicyResolver_PreservesV1Defaults_AndRejectsV2FieldsUnderV1()
    {
        BridgePolicyDto v1Default = TestSaveBuilder.DefaultPolicy();
        (_, _, PreCombatPortfolioOptions resolved) = BridgePolicyResolver.ValidateAndBuildBasePolicy(
            v1Default, 4000, 20000, maxDegreeOfParallelism: 1);

        Assert.Equal(1, resolved.PortfolioOptionsVersion);
        Assert.False(resolved.UsesParentNodeBudget);
        Assert.Equal(PortfolioSchedulingMode.Sequential, resolved.SchedulingMode);

        BridgePolicyDto[] gatedV1Cases =
        [
            v1Default with { UseNoveltyPortfolio = true },
            v1Default with { AdaptiveNoveltyRefinement = true },
            v1Default with { UseEarlyTurnExploration = true },
            v1Default with { BeamWidthPortfolioWidths = [8, 16] },
            v1Default with { BeamWidthPortfolioPlainBaselineMember = false },
            v1Default with { OffensiveRefinementPortfolio = true },
            v1Default with { BoundedOffensiveRefinementPortfolio = true },
            v1Default with { ReallocatedRefinementPortfolio = false },
            v1Default with { StopPortfolioAtHpTarget = false },
            v1Default with { NoveltySearch = new BridgeNoveltySearchOptionsDto(2, 0, 1024, 10000, 64, 16) },
            v1Default with { NoveltyBudget = new BridgeNoveltyPortfolioBudgetDto(2, 5000, 2500) },
            v1Default with { EarlyTurnExplorationDepth = 1, EarlyTurnExplorationBudgetMilliseconds = 10000 },
            v1Default with { EarlyTurnExplorationBudgetMilliseconds = 10000 },
            v1Default with { PortfolioSchedulingMode = "Sequential" },
        ];

        foreach (BridgePolicyDto badCase in gatedV1Cases)
        {
            BridgeCapabilityMissException ex = Assert.Throws<BridgeCapabilityMissException>(
                () => BridgePolicyResolver.ValidateAndBuildBasePolicy(badCase, 4000, 20000, 1));
            Assert.Equal("policy_unsupported", ex.FailureCode);
        }

        BridgePolicyDto depthTooHigh = v1Default with
        {
            PortfolioOptionsVersion = 2,
            UseEarlyTurnExploration = true,
            EarlyTurnExplorationDepth = 3,
            EarlyTurnExplorationBudgetMilliseconds = 30_000,
        };
        BridgeCapabilityMissException depthEx = Assert.Throws<BridgeCapabilityMissException>(
            () => BridgePolicyResolver.ValidateAndBuildBasePolicy(depthTooHigh, 4000, 20000, 1));
        Assert.Equal("policy_unsupported", depthEx.FailureCode);
        Assert.Equal("policy.earlyTurnExplorationDepth", depthEx.FieldPath);
    }

    [Theory]
    [InlineData("version3", "policy.portfolioOptionsVersion")]
    [InlineData("widths_gt16", "policy.beamWidthPortfolioWidths")]
    [InlineData("width_zero", "policy.beamWidthPortfolioWidths[1]")]
    [InlineData("offensive_mutex", "policy.offensiveRefinementPortfolio")]
    [InlineData("novelty_width", "policy.noveltySearch")]
    [InlineData("novelty_budget", "policy.noveltyBudget")]
    [InlineData("early_depth_9", "policy.earlyTurnExplorationDepth")]
    [InlineData("early_budget_missing", "policy.earlyTurnExplorationBudgetMilliseconds")]
    [InlineData("scheduling_invalid", "policy.portfolioSchedulingMode")]
    public void PolicyResolver_V2Validation_FailsSymmetricallyWithExpectedFieldPath(
        string caseKind,
        string expectedFieldPath)
    {
        BridgePolicyDto dto = TestSaveBuilder.DefaultPolicy() with { PortfolioOptionsVersion = 2 };
        dto = caseKind switch
        {
            "version3" => dto with { PortfolioOptionsVersion = 3 },
            "widths_gt16" => dto with { BeamWidthPortfolioWidths = Enumerable.Range(1, 17).ToArray() },
            "width_zero" => dto with { BeamWidthPortfolioWidths = [8, 0] },
            "offensive_mutex" => dto with { OffensiveRefinementPortfolio = true, BoundedOffensiveRefinementPortfolio = true },
            "novelty_width" => dto with { NoveltySearch = new BridgeNoveltySearchOptionsDto(3, 0, 100, 1000, 64, 16) },
            "novelty_budget" => dto with { NoveltyBudget = new BridgeNoveltyPortfolioBudgetDto(1, 5000, 2500) },
            "early_depth_9" => dto with { EarlyTurnExplorationDepth = 9, EarlyTurnExplorationBudgetMilliseconds = 20000 },
            "early_budget_missing" => dto with { EarlyTurnExplorationDepth = 1, EarlyTurnExplorationBudgetMilliseconds = null },
            "scheduling_invalid" => dto with { PortfolioSchedulingMode = "Adaptive" },
            _ => throw new ArgumentOutOfRangeException(nameof(caseKind)),
        };

        BridgeRequestFailedException preEx = Assert.Throws<BridgeRequestFailedException>(
            () => BridgePolicyResolver.ValidateAndBuildBasePolicy(dto, 4000, 20000, 1));
        Assert.Equal(expectedFieldPath, preEx.FieldPath);

        InCombatPolicyDto inDto = ToInCombatPolicy(dto);
        BridgeRequestFailedException inEx = Assert.Throws<BridgeRequestFailedException>(
            () => BridgePolicyResolver.ValidateAndBuildInCombatPolicy(inDto, 4000, 20000, 1));
        Assert.Equal(expectedFieldPath, inEx.FieldPath);
    }

    [Fact]
    public async Task RunJsonAsync_V2PortfoliosAndCosmeticTestSubjectKills_Succeeds()
    {
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.SILENT", ascension: 10);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgePolicyDto policy = TestSaveBuilder.DefaultPolicy(beamWidth: 12, maxExpandedNodes: 1500, useBeamWidthPortfolio: true) with
        {
            PortfolioOptionsVersion = 2,
            UseNoveltyPortfolio = true,
            AdaptiveNoveltyRefinement = true,
            UseEarlyTurnExploration = false,
            PortfolioSchedulingMode = "Parallel",
        };

        BridgeRequestDto request = TestSaveBuilder.CreateRequest(
            run,
            "NIBBITS_WEAK",
            target.coord,
            policy: policy,
            mutateSave: root => root["extra_fields"]!["test_subject_kills"] = 3) with
        {
            MaxDegreeOfParallelism = 2,
        };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, BridgeAbi.JsonOptions);
        byte[] responseBytes = await BridgeEntry.RunJsonAsync(payload, CancellationToken.None);
        BridgeResponseDto? response = JsonSerializer.Deserialize<BridgeResponseDto>(responseBytes, BridgeAbi.JsonOptions);

        Assert.NotNull(response);
        Assert.Equal("Succeeded", response!.Outcome);
        Assert.True(response.ExpandedNodes > 0);

        BridgeRequestDto badKillsRequest = TestSaveBuilder.CreateRequest(
            run,
            "NIBBITS_WEAK",
            target.coord,
            policy: TestSaveBuilder.DefaultPolicy(),
            mutateSave: root => root["extra_fields"]!["test_subject_kills"] = -1);
        byte[] badBytes = await BridgeEntry.RunJsonAsync(
            JsonSerializer.SerializeToUtf8Bytes(badKillsRequest, BridgeAbi.JsonOptions),
            CancellationToken.None);
        BridgeResponseDto? badResp = JsonSerializer.Deserialize<BridgeResponseDto>(badBytes, BridgeAbi.JsonOptions);
        Assert.NotNull(badResp);
        Assert.Equal("Failed", badResp!.Outcome);
        Assert.Equal("extra_fields.test_subject_kills", badResp.FieldPath);
    }

    [Fact]
    public async Task RunInCombatJsonAsync_V1OmitsExtendedMetrics_V2PopulatesExtendedMetricsAndParentBudget()
    {
        (string saveJson, InCombatLiveStateDto liveDto) = await CreateMinimalTurn1CombatAsync("NIBBITS_WEAK");

        BridgePolicyDto v1Policy = TestSaveBuilder.DefaultPolicy(beamWidth: 12, maxExpandedNodes: 600);
        var v1Request = new InCombatBridgeRequestDto(
            InCombatBridgeAbi.Version,
            Guid.NewGuid().ToString("N"),
            saveJson,
            liveDto,
            PriorBattleHpLost: 0,
            SearchBudgetMilliseconds: 6000,
            OverallTimeoutMilliseconds: 20_000,
            MaxDegreeOfParallelism: 1,
            Policy: ToInCombatPolicy(v1Policy));

        byte[] v1ResponseBytes = await BridgeEntry.RunInCombatJsonAsync(
            JsonSerializer.SerializeToUtf8Bytes(v1Request, BridgeAbi.JsonOptions),
            progressCallback: null,
            CancellationToken.None);
        using (JsonDocument doc = JsonDocument.Parse(v1ResponseBytes))
        {
            JsonElement res = doc.RootElement.GetProperty("result");
            Assert.False(res.TryGetProperty("expandedParents", out _));
            Assert.False(res.TryGetProperty("transitionCount", out _));
            Assert.False(res.TryGetProperty("choiceReplayAttempts", out _));
            Assert.False(res.TryGetProperty("observedPeakParallelism", out _));
            Assert.False(res.TryGetProperty("activePhaseLabel", out _));
        }

        const int parentBudget = 300;
        BridgePolicyDto v2Policy = v1Policy with
        {
            MaxExpandedNodes = parentBudget,
            UseBeamWidthPortfolio = true,
            PortfolioOptionsVersion = 2,
            UseNoveltyPortfolio = true,
            AdaptiveNoveltyRefinement = true,
            PortfolioSchedulingMode = "Parallel",
        };
        var v2Request = v1Request with
        {
            RequestId = Guid.NewGuid().ToString("N"),
            MaxDegreeOfParallelism = 2,
            Policy = ToInCombatPolicy(v2Policy),
        };

        var progressEvents = new List<InCombatProgressDto>();
        byte[] v2ResponseBytes = await BridgeEntry.RunInCombatJsonAsync(
            JsonSerializer.SerializeToUtf8Bytes(v2Request, BridgeAbi.JsonOptions),
            bytes =>
            {
                InCombatProgressDto? progress = JsonSerializer.Deserialize<InCombatProgressDto>(bytes, BridgeAbi.JsonOptions);
                if (progress is not null)
                {
                    lock (progressEvents)
                        progressEvents.Add(progress);
                }
            },
            CancellationToken.None);

        InCombatBridgeResponseDto? v2Response = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(
            v2ResponseBytes, BridgeAbi.JsonOptions);
        Assert.NotNull(v2Response);
        Assert.Equal("Succeeded", v2Response!.Outcome);
        InCombatResultDto? result = v2Response.Result;
        Assert.NotNull(result);
        Assert.NotNull(result!.ExpandedParents);
        Assert.NotNull(result.TransitionCount);
        Assert.NotNull(result.ChoiceReplayAttempts);
        Assert.NotNull(result.ObservedPeakParallelism);
        Assert.False(string.IsNullOrWhiteSpace(result.ActivePhaseLabel));
        Assert.True(result.ExpandedParents > 0);
        Assert.True(result.ExpandedParents <= parentBudget + BeamWidthPortfolio.DedicatedPowerNodeReserve(parentBudget));
        Assert.True(result.TransitionCount >= 0);
        Assert.True(result.ChoiceReplayAttempts >= result.ExpandedParents);

        long lastParents = -1;
        foreach (InCombatProgressDto p in progressEvents)
        {
            Assert.NotNull(p.ExpandedParents);
            Assert.True(p.ExpandedParents >= lastParents);
            lastParents = p.ExpandedParents!.Value;
        }
    }

    [Fact]
    public async Task RunJsonAsync_V2ForcedFoulPotionWithBoundedOffensiveRefinement_DoesNotThrowOnSecondaryMember()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        var foulPotion = (PotionModel)ModelDb.Potion<Sts2Sim.Core.Models.Potions.FoulPotion>().MutableClone();
        foulPotion.AssignOwner(run.Players[0]);
        run.Players[0].AddPotionInternal(foulPotion);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgePolicyDto policy = TestSaveBuilder.DefaultPolicy(
            beamWidth: 16,
            maxExpandedNodes: 100,
            useBeamWidthPortfolio: true) with
        {
            PotionPolicy = "Smart",
            PotionDirectives = [new BridgePotionDirectiveDto(0, "FOUL_POTION", "Force")],
            StopAtAcceptableBattleHpLoss = false,
            PortfolioOptionsVersion = 2,
            BoundedOffensiveRefinementPortfolio = true,
            PortfolioSchedulingMode = "Sequential",
        };

        BridgeRequestDto request = TestSaveBuilder.CreateRequest(run, "NIBBITS_WEAK", target.coord, policy: policy);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, BridgeAbi.JsonOptions);
        byte[] responseBytes = await BridgeEntry.RunJsonAsync(payload, CancellationToken.None);
        BridgeResponseDto? response = JsonSerializer.Deserialize<BridgeResponseDto>(responseBytes, BridgeAbi.JsonOptions);

        Assert.NotNull(response);
        Assert.Equal("Succeeded", response!.Outcome);
        BridgePotionUseDto use = Assert.Single(response.PotionUses!);
        Assert.Equal("FOUL_POTION", use.Id);
    }

    [Fact]
    public async Task RunInCombatJsonAsync_V2Cancel_FlushesFinalMetricsOnlyProgressAndPreservesMonotonicity()
    {
        (string saveJson, InCombatLiveStateDto liveDto) = await CreateMinimalTurn1CombatAsync("NIBBITS_NORMAL");
        BridgePolicyDto v2Policy = TestSaveBuilder.DefaultPolicy(beamWidth: 64, maxExpandedNodes: 500_000, useBeamWidthPortfolio: true) with
        {
            PortfolioOptionsVersion = 2,
            UseNoveltyPortfolio = true,
            AdaptiveNoveltyRefinement = true,
            UseEarlyTurnExploration = true,
            EarlyTurnExplorationDepth = 2,
            EarlyTurnExplorationBudgetMilliseconds = 30_000,
            PortfolioSchedulingMode = "Parallel",
            StopAtAcceptableBattleHpLoss = false,
        };
        var request = new InCombatBridgeRequestDto(
            InCombatBridgeAbi.Version,
            Guid.NewGuid().ToString("N"),
            saveJson,
            liveDto,
            PriorBattleHpLost: 0,
            SearchBudgetMilliseconds: 30_000,
            OverallTimeoutMilliseconds: 30_000,
            MaxDegreeOfParallelism: 4,
            Policy: ToInCombatPolicy(v2Policy));

        var progressEvents = new List<InCombatProgressDto>();
        var firstProgressSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        Task<byte[]> runTask = BridgeEntry.RunInCombatJsonAsync(
            JsonSerializer.SerializeToUtf8Bytes(request, BridgeAbi.JsonOptions),
            bytes =>
            {
                InCombatProgressDto? progress = JsonSerializer.Deserialize<InCombatProgressDto>(bytes, BridgeAbi.JsonOptions);
                if (progress is not null)
                {
                    lock (progressEvents)
                        progressEvents.Add(progress);
                    firstProgressSeen.TrySetResult(true);
                }
            },
            cts.Token);

        await Task.WhenAny(firstProgressSeen.Task, Task.Delay(300));
        await Task.Delay(60);
        cts.Cancel();

        byte[] responseBytes = await runTask;
        InCombatBridgeResponseDto? response = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(
            responseBytes, BridgeAbi.JsonOptions);
        Assert.NotNull(response);
        Assert.Equal("Cancelled", response!.Outcome);

        lock (progressEvents)
        {
            Assert.NotEmpty(progressEvents);
            long prevParents = -1;
            long prevTransitions = -1;
            long prevAttempts = -1;
            foreach (InCombatProgressDto p in progressEvents)
            {
                Assert.NotNull(p.ExpandedParents);
                Assert.NotNull(p.TransitionCount);
                Assert.NotNull(p.ChoiceReplayAttempts);
                Assert.True(p.ExpandedParents >= prevParents);
                Assert.True(p.TransitionCount >= prevTransitions);
                Assert.True(p.ChoiceReplayAttempts >= prevAttempts);
                prevParents = p.ExpandedParents!.Value;
                prevTransitions = p.TransitionCount!.Value;
                prevAttempts = p.ChoiceReplayAttempts!.Value;
            }
            Assert.True(prevParents > 0);
        }
    }

    [Fact]
    public async Task RunInCombatJsonAsync_V2BlockingFinalMetricsFlushCallback_ReturnsCancelledWithinWaitBudget()
    {
        (string saveJson, InCombatLiveStateDto liveDto) = await CreateMinimalTurn1CombatAsync("NIBBITS_NORMAL");
        BridgePolicyDto v2Policy = TestSaveBuilder.DefaultPolicy(beamWidth: 64, maxExpandedNodes: 500_000, useBeamWidthPortfolio: true) with
        {
            PortfolioOptionsVersion = 2,
            UseNoveltyPortfolio = true,
            AdaptiveNoveltyRefinement = true,
            UseEarlyTurnExploration = true,
            EarlyTurnExplorationDepth = 2,
            EarlyTurnExplorationBudgetMilliseconds = 30_000,
            PortfolioSchedulingMode = "Parallel",
            StopAtAcceptableBattleHpLoss = false,
        };
        var request = new InCombatBridgeRequestDto(
            InCombatBridgeAbi.Version,
            Guid.NewGuid().ToString("N"),
            saveJson,
            liveDto,
            PriorBattleHpLost: 0,
            SearchBudgetMilliseconds: 30_000,
            OverallTimeoutMilliseconds: 30_000,
            MaxDegreeOfParallelism: 4,
            Policy: ToInCombatPolicy(v2Policy));

        var firstRegularSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finalFlushEntered = new ManualResetEventSlim(false);
        using var releaseFinalFlush = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        int cancelIssued = 0;
        var wall = System.Diagnostics.Stopwatch.StartNew();

        Task<byte[]> runTask = BridgeEntry.RunInCombatJsonAsync(
            JsonSerializer.SerializeToUtf8Bytes(request, BridgeAbi.JsonOptions),
            bytes =>
            {
                InCombatProgressDto? progress = JsonSerializer.Deserialize<InCombatProgressDto>(bytes, BridgeAbi.JsonOptions);
                if (progress is null)
                    return;
                if (progress.CurrentBest is not null)
                {
                    firstRegularSeen.TrySetResult(true);
                    return;
                }
                if (Volatile.Read(ref cancelIssued) != 0 && progress.ExpandedParents is not null)
                {
                    finalFlushEntered.Set();
                    releaseFinalFlush.Wait(TimeSpan.FromSeconds(10));
                }
            },
            cts.Token);

        await Task.WhenAny(firstRegularSeen.Task, Task.Delay(500));
        await Task.Delay(60);
        wall.Restart();
        Volatile.Write(ref cancelIssued, 1);
        cts.Cancel();

        byte[] responseBytes;
        try
        {
            responseBytes = await runTask;
        }
        finally
        {
            releaseFinalFlush.Set();
        }
        wall.Stop();

        InCombatBridgeResponseDto? response = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(
            responseBytes, BridgeAbi.JsonOptions);
        Assert.NotNull(response);
        Assert.Equal("Cancelled", response!.Outcome);
        Assert.True(wall.Elapsed < TimeSpan.FromSeconds(3), $"Expected non-blocking cancel within 3s, took {wall.Elapsed}.");
        Assert.True(finalFlushEntered.IsSet);
        if (response.BestSoFar is not null)
            Assert.True(response.BestSoFar.ExpandedParents > 0);
    }

    [Fact]
    public async Task InCombatV2_UserCancelDuringNormalShutdownWithBlockedRegularCallback_ReturnsPromptlyWithoutWaitingForHardDeadline()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatState root = entered.Room.Engine.State;
        var evaluator = new StateEvaluator(root, CombatPolicy.Default);
        CombatSolver.Beam.SearchNode rootNode = CombatSolver.Beam.SearchNode.Create(root.Clone(), null, null, evaluator);
        PlannedAction endTurn = ActionGenerator.GenerateForForecast(root, CombatPolicy.Default).First(a => a.Kind == "EndTurn");
        CombatState childState = root.Clone();
        await ActionExecutor.ExecuteAsync(childState, endTurn, endTurn.ChoiceIndices);
        CombatSolver.Beam.SearchNode childNode = CombatSolver.Beam.SearchNode.Create(childState, rootNode, endTurn, evaluator);

        // 1A) 常规回调始终阻塞时，正常关闭期间收到用户取消必须在短宽限内返回，绝不拖延到硬期限，且关闭后零回调。
        {
            var ledger = new SearchWorkLedger(useParentBudget: true);
            using var userCts = new CancellationTokenSource();
            using var hardCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var regularEntered = new ManualResetEventSlim(false);
            using var releaseRegular = new ManualResetEventSlim(false);
            int stopReturned = 0;
            int postReturnCallbacks = 0;

            var tracker = new InCombatSearchTracker(
                root,
                priorBattleHpLost: 0,
                System.Diagnostics.Stopwatch.StartNew(),
                update =>
                {
                    if (Volatile.Read(ref stopReturned) != 0)
                    {
                        Interlocked.Increment(ref postReturnCallbacks);
                        return;
                    }
                    if (!update.IsFinalMetricsFlush && !regularEntered.IsSet)
                    {
                        regularEntered.Set();
                        releaseRegular.Wait(TimeSpan.FromSeconds(15));
                    }
                },
                searchCancellation: userCts.Token,
                hardDeadlineToken: hardCts.Token,
                ledger: ledger,
                emitExtendedMetrics: true);

            InCombatPassTrack track = tracker.RegisterPass();
            track.AttachEvaluator(evaluator, root.Players[0].Creature.CurrentHp);
            ledger.AddExpandedParents(1);
            track.CommitWave([childNode], [], [], [], null, null, ForecastSearchBoundary.NodeLimit);

            Assert.True(regularEntered.Wait(TimeSpan.FromSeconds(5)));
            ledger.AddExpandedParents(5);
            ledger.AddTransitions(9);
            ledger.AddChoiceReplayAttempts(11);

            var stopWatch = System.Diagnostics.Stopwatch.StartNew();
            Task stopTask = Task.Run(() => tracker.StopAndJoin(hardCts.Token, emitFinalMetricsFlush: false, cancellationToken: userCts.Token));
            await Task.Delay(30);
            userCts.Cancel();

            await stopTask.WaitAsync(TimeSpan.FromMilliseconds(1500));
            stopWatch.Stop();
            Volatile.Write(ref stopReturned, 1);
            Assert.True(stopWatch.Elapsed < TimeSpan.FromMilliseconds(1500), $"Expected bounded normal shutdown on cancel, took {stopWatch.Elapsed}.");

            releaseRegular.Set();
            await Task.Delay(50);
            Assert.Equal(0, Volatile.Read(ref postReturnCallbacks));
        }

        // 1B) 常规回调在用户取消后很快解除阻塞时，正常关闭平滑切换为发送最终扩展指标刷新。
        {
            var ledger = new SearchWorkLedger(useParentBudget: true);
            using var userCts = new CancellationTokenSource();
            using var hardCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var regularEntered = new ManualResetEventSlim(false);
            using var releaseRegular = new ManualResetEventSlim(false);
            var finalFlushReceived = new TaskCompletionSource<InCombatProgressUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);

            var tracker = new InCombatSearchTracker(
                root,
                priorBattleHpLost: 0,
                System.Diagnostics.Stopwatch.StartNew(),
                update =>
                {
                    if (update.IsFinalMetricsFlush)
                    {
                        finalFlushReceived.TrySetResult(update);
                        return;
                    }
                    if (!regularEntered.IsSet)
                    {
                        regularEntered.Set();
                        releaseRegular.Wait(TimeSpan.FromSeconds(10));
                    }
                },
                searchCancellation: userCts.Token,
                hardDeadlineToken: hardCts.Token,
                ledger: ledger,
                emitExtendedMetrics: true);

            InCombatPassTrack track = tracker.RegisterPass();
            track.AttachEvaluator(evaluator, root.Players[0].Creature.CurrentHp);
            ledger.AddExpandedParents(1);
            track.CommitWave([childNode], [], [], [], null, null, ForecastSearchBoundary.NodeLimit);

            Assert.True(regularEntered.Wait(TimeSpan.FromSeconds(5)));
            ledger.AddExpandedParents(5);

            Task stopTask = Task.Run(() => tracker.StopAndJoin(hardCts.Token, emitFinalMetricsFlush: false, cancellationToken: userCts.Token));
            try
            {
                await Task.Delay(20);
                userCts.Cancel();
                await Task.Delay(20);
                releaseRegular.Set();

                await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
                InCombatProgressUpdate flush = await finalFlushReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(flush.IsFinalMetricsFlush);
                Assert.Equal(6L, flush.ExpandedParents);
            }
            finally
            {
                releaseRegular.Set();
            }
        }

        // 1C) 取消后立即解除回调阻塞（无 20ms 先发窗口）时，ReporterLoop 仍能感知取消令牌并发送最终扩展指标刷新。
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var ledger = new SearchWorkLedger(useParentBudget: true);
            using var userCts = new CancellationTokenSource();
            using var hardCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var regularEntered = new ManualResetEventSlim(false);
            using var releaseRegular = new ManualResetEventSlim(false);
            var finalFlushReceived = new TaskCompletionSource<InCombatProgressUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);

            var tracker = new InCombatSearchTracker(
                root,
                priorBattleHpLost: 0,
                System.Diagnostics.Stopwatch.StartNew(),
                update =>
                {
                    if (update.IsFinalMetricsFlush)
                    {
                        finalFlushReceived.TrySetResult(update);
                        return;
                    }
                    if (!regularEntered.IsSet)
                    {
                        regularEntered.Set();
                        releaseRegular.Wait(TimeSpan.FromSeconds(10));
                    }
                },
                searchCancellation: userCts.Token,
                hardDeadlineToken: hardCts.Token,
                ledger: ledger,
                emitExtendedMetrics: true);

            InCombatPassTrack track = tracker.RegisterPass();
            track.AttachEvaluator(evaluator, root.Players[0].Creature.CurrentHp);
            ledger.AddExpandedParents(1);
            track.CommitWave([childNode], [], [], [], null, null, ForecastSearchBoundary.NodeLimit);

            Assert.True(regularEntered.Wait(TimeSpan.FromSeconds(5)));
            ledger.AddExpandedParents(5);

            Task stopTask = Task.Run(() => tracker.StopAndJoin(hardCts.Token, emitFinalMetricsFlush: false, cancellationToken: userCts.Token));
            try
            {
                await Task.Delay(15);
                userCts.Cancel();
                releaseRegular.Set();

                await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
                InCombatProgressUpdate flush = await finalFlushReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(flush.IsFinalMetricsFlush);
                Assert.Equal(6L, flush.ExpandedParents);
            }
            finally
            {
                releaseRegular.Set();
            }
        }

        // 2) 端到端 BridgeEntry：硬期限 30s 下常规进度回调阻塞，用户取消后必须在 3s 内返回且报告 ProgressCallbacksDrained=false 并封闸零后续回调。
        (string saveJson, InCombatLiveStateDto liveDto) = await CreateMinimalTurn1CombatAsync("NIBBITS_NORMAL");
        BridgePolicyDto v2Policy = TestSaveBuilder.DefaultPolicy(beamWidth: 64, maxExpandedNodes: 250, useBeamWidthPortfolio: true) with
        {
            PortfolioOptionsVersion = 2,
            UseNoveltyPortfolio = true,
            AdaptiveNoveltyRefinement = true,
            UseEarlyTurnExploration = true,
            EarlyTurnExplorationDepth = 2,
            EarlyTurnExplorationBudgetMilliseconds = 30_000,
            PortfolioSchedulingMode = "Parallel",
            StopAtAcceptableBattleHpLoss = false,
        };
        var request = new InCombatBridgeRequestDto(
            InCombatBridgeAbi.Version,
            Guid.NewGuid().ToString("N"),
            saveJson,
            liveDto,
            PriorBattleHpLost: 0,
            SearchBudgetMilliseconds: 30_000,
            OverallTimeoutMilliseconds: 30_000,
            MaxDegreeOfParallelism: 4,
            Policy: ToInCombatPolicy(v2Policy));

        using var e2eEntered = new ManualResetEventSlim(false);
        using var e2eRelease = new ManualResetEventSlim(false);
        using var e2eCts = new CancellationTokenSource();
        int e2eReturned = 0;
        int e2ePostReturnCallbacks = 0;

        Task<byte[]> e2eTask = BridgeEntry.RunInCombatJsonAsync(
            JsonSerializer.SerializeToUtf8Bytes(request, BridgeAbi.JsonOptions),
            bytes =>
            {
                if (Volatile.Read(ref e2eReturned) != 0)
                {
                    Interlocked.Increment(ref e2ePostReturnCallbacks);
                    return;
                }
                InCombatProgressDto? progress = JsonSerializer.Deserialize<InCombatProgressDto>(bytes, BridgeAbi.JsonOptions);
                if (progress is not null && !e2eEntered.IsSet)
                {
                    e2eEntered.Set();
                    e2eRelease.Wait(TimeSpan.FromSeconds(10));
                }
            },
            e2eCts.Token);

        Assert.True(e2eEntered.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(50);
        var e2eCancelWatch = System.Diagnostics.Stopwatch.StartNew();
        e2eCts.Cancel();

        byte[] e2eBytes;
        try
        {
            e2eBytes = await e2eTask.WaitAsync(TimeSpan.FromSeconds(3));
            e2eCancelWatch.Stop();
            Volatile.Write(ref e2eReturned, 1);
        }
        finally
        {
            e2eRelease.Set();
        }

        InCombatBridgeResponseDto? e2eResp = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(
            e2eBytes, BridgeAbi.JsonOptions);
        Assert.NotNull(e2eResp);
        Assert.True(e2eResp!.Outcome is "Cancelled" or "Succeeded", $"Unexpected outcome: {e2eResp.Outcome} ({e2eResp.Error})");
        Assert.Equal(false, e2eResp.ProgressCallbacksDrained);
        Assert.True(e2eCancelWatch.Elapsed < TimeSpan.FromSeconds(3), $"Cancel stalled until hard deadline: {e2eCancelWatch.Elapsed}.");

        await Task.Delay(80);
        Assert.Equal(0, Volatile.Read(ref e2ePostReturnCallbacks));
    }

    [Fact]
    public async Task RunInCombatJsonAsync_ReportsProgressCallbacksDrainedContract()
    {
        (string saveJson, InCombatLiveStateDto liveDto) = await CreateMinimalTurn1CombatAsync("NIBBITS_NORMAL");
        BridgePolicyDto policy = TestSaveBuilder.DefaultPolicy(beamWidth: 16, maxExpandedNodes: 40, useBeamWidthPortfolio: false);

        // 1) 未传进度回调时省略 progressCallbacksDrained 字段（null）。
        {
            var nullCbReq = new InCombatBridgeRequestDto(
                InCombatBridgeAbi.Version,
                Guid.NewGuid().ToString("N"),
                saveJson,
                liveDto,
                PriorBattleHpLost: 0,
                SearchBudgetMilliseconds: 5_000,
                OverallTimeoutMilliseconds: 10_000,
                MaxDegreeOfParallelism: 1,
                Policy: ToInCombatPolicy(policy));
            byte[] raw = await BridgeEntry.RunInCombatJsonAsync(
                JsonSerializer.SerializeToUtf8Bytes(nullCbReq, BridgeAbi.JsonOptions),
                progressCallback: null,
                CancellationToken.None);
            using var doc = JsonDocument.Parse(raw);
            Assert.False(doc.RootElement.TryGetProperty("progressCallbacksDrained", out _));
            InCombatBridgeResponseDto? resp = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(raw, BridgeAbi.JsonOptions);
            Assert.NotNull(resp);
            Assert.Null(resp!.ProgressCallbacksDrained);
        }

        // 2) 传入进度回调且正常完成排空时返回 ProgressCallbacksDrained=true 且返回后零宿主回调。
        {
            int returned = 0;
            int postReturnCallbacks = 0;
            var req = new InCombatBridgeRequestDto(
                InCombatBridgeAbi.Version,
                Guid.NewGuid().ToString("N"),
                saveJson,
                liveDto,
                PriorBattleHpLost: 0,
                SearchBudgetMilliseconds: 5_000,
                OverallTimeoutMilliseconds: 10_000,
                MaxDegreeOfParallelism: 1,
                Policy: ToInCombatPolicy(policy));
            byte[] raw = await BridgeEntry.RunInCombatJsonAsync(
                JsonSerializer.SerializeToUtf8Bytes(req, BridgeAbi.JsonOptions),
                _ =>
                {
                    if (Volatile.Read(ref returned) != 0)
                        Interlocked.Increment(ref postReturnCallbacks);
                },
                CancellationToken.None);
            Volatile.Write(ref returned, 1);
            await Task.Delay(50);

            InCombatBridgeResponseDto? resp = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(raw, BridgeAbi.JsonOptions);
            Assert.NotNull(resp);
            Assert.Equal("Succeeded", resp!.Outcome);
            Assert.Equal(true, resp.ProgressCallbacksDrained);
            Assert.Equal(0, Volatile.Read(ref postReturnCallbacks));
        }

        // 3) 短回调排空且取消时返回 Cancelled 与 ProgressCallbacksDrained=true，且返回后零宿主回调。
        {
            BridgePolicyDto longPolicy = TestSaveBuilder.DefaultPolicy(beamWidth: 64, maxExpandedNodes: 250, useBeamWidthPortfolio: true) with
            {
                PortfolioOptionsVersion = 2,
                UseNoveltyPortfolio = true,
                AdaptiveNoveltyRefinement = true,
                UseEarlyTurnExploration = true,
                EarlyTurnExplorationDepth = 2,
                EarlyTurnExplorationBudgetMilliseconds = 30_000,
                PortfolioSchedulingMode = "Parallel",
                StopAtAcceptableBattleHpLoss = false,
            };
            var req = new InCombatBridgeRequestDto(
                InCombatBridgeAbi.Version,
                Guid.NewGuid().ToString("N"),
                saveJson,
                liveDto,
                PriorBattleHpLost: 0,
                SearchBudgetMilliseconds: 30_000,
                OverallTimeoutMilliseconds: 30_000,
                MaxDegreeOfParallelism: 4,
                Policy: ToInCombatPolicy(longPolicy));

            using var cbEntered = new ManualResetEventSlim(false);
            using var cts = new CancellationTokenSource();
            int returned = 0;
            int postReturnCallbacks = 0;

            Task<byte[]> runTask = BridgeEntry.RunInCombatJsonAsync(
                JsonSerializer.SerializeToUtf8Bytes(req, BridgeAbi.JsonOptions),
                _ =>
                {
                    if (Volatile.Read(ref returned) != 0)
                    {
                        Interlocked.Increment(ref postReturnCallbacks);
                        return;
                    }
                    cbEntered.Set();
                    Thread.Sleep(10);
                },
                cts.Token);

            Assert.True(cbEntered.Wait(TimeSpan.FromSeconds(5)));
            cts.Cancel();
            byte[] raw = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref returned, 1);
            await Task.Delay(50);

            InCombatBridgeResponseDto? resp = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(raw, BridgeAbi.JsonOptions);
            Assert.NotNull(resp);
            Assert.True(resp!.Outcome is "Cancelled" or "Succeeded", $"Unexpected outcome: {resp.Outcome} ({resp.Error})");
            Assert.Equal(true, resp.ProgressCallbacksDrained);
            Assert.Equal(0, Volatile.Read(ref postReturnCallbacks));
        }
    }

    [Fact]
    public async Task InCombatSearchTracker_V1CancelWithExpiredDeadline_RestampsDrainedBudgetTrackerTotalsOntoCachedPartial()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatState root = entered.Room.Engine.State;
        var evaluator = new StateEvaluator(root, CombatPolicy.Default);
        CombatSolver.Beam.SearchNode rootNode = CombatSolver.Beam.SearchNode.Create(root.Clone(), null, null, evaluator);
        PlannedAction endTurn = ActionGenerator.GenerateForForecast(root, CombatPolicy.Default).First(a => a.Kind == "EndTurn");
        CombatState childState = root.Clone();
        await ActionExecutor.ExecuteAsync(childState, endTurn, endTurn.ChoiceIndices);
        CombatSolver.Beam.SearchNode childNode = CombatSolver.Beam.SearchNode.Create(childState, rootNode, endTurn, evaluator);

        using var firstProgress = new ManualResetEventSlim(false);
        var tracker = new InCombatSearchTracker(
            root,
            priorBattleHpLost: 0,
            System.Diagnostics.Stopwatch.StartNew(),
            update =>
            {
                if (update.CurrentBest is not null)
                    firstProgress.Set();
            });
        InCombatPassTrack track = tracker.RegisterPass();
        var budgetTracker = new ForecastNodeBudgetTracker(100);
        track.AttachEvaluator(evaluator, root.Players[0].Creature.CurrentHp);
        track.AttachBudgetTracker(budgetTracker);
        Assert.True(budgetTracker.TryReserveSlot());
        track.CommitWave([childNode], [], [], [], null, null, ForecastSearchBoundary.NodeLimit);

        Assert.True(firstProgress.Wait(TimeSpan.FromSeconds(5)));
        for (int i = 0; i < 6; i++)
            Assert.True(budgetTracker.TryReserveSlot());
        tracker.StopAndJoin();

        using var expired = new CancellationTokenSource();
        expired.Cancel();
        InCombatBeamResult? partial = tracker.TryGetBestSoFarOnCancel(expired.Token, TimeSpan.FromMilliseconds(250));
        Assert.NotNull(partial);
        Assert.Equal(7L, partial!.ExpandedNodes);
    }

    [Fact]
    public async Task ProgressCallbackGate_PreventsPausedEntryDrainsFastCallbackAndBoundsBlockedCallback()
    {
        // 1) 关闸前暂停在 TryInvoke 之外的调用在 Close 返回后绝不进入回调。
        {
            var gate = new CombatSolver.Beam.ProgressCallbackGate();
            using var startPause = new ManualResetEventSlim(false);
            int entered = 0;

            Task pausedCaller = Task.Run(() =>
            {
                startPause.Wait(TimeSpan.FromSeconds(5));
                gate.TryInvoke(() => Interlocked.Increment(ref entered));
            });

            Assert.True(gate.Close(TimeSpan.FromMilliseconds(50)));
            startPause.Set();
            await pausedCaller.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, Volatile.Read(ref entered));
        }

        // 2) 已进入回调的短执行调用在排空预算内完成后 Close 才返回 true，返回后零回调。
        {
            var gate = new CombatSolver.Beam.ProgressCallbackGate();
            using var callbackEntered = new ManualResetEventSlim(false);
            int completed = 0;
            int closeReturned = 0;
            int completedAfterCloseReturn = 0;

            Task inFlight = Task.Run(() =>
            {
                gate.TryInvoke(() =>
                {
                    callbackEntered.Set();
                    Thread.Sleep(15);
                    if (Volatile.Read(ref closeReturned) != 0)
                        Interlocked.Increment(ref completedAfterCloseReturn);
                    Interlocked.Increment(ref completed);
                });
            });

            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(2)));
            bool drained = gate.Close(TimeSpan.FromMilliseconds(200));
            Volatile.Write(ref closeReturned, 1);

            Assert.True(drained);
            await inFlight.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref completed));
            Assert.Equal(0, Volatile.Read(ref completedAfterCloseReturn));
        }

        // 3) 回调持续阻塞时 Close 按有限预算返回 false，且解除阻塞后后续 TryInvoke 被拒绝。
        {
            var gate = new CombatSolver.Beam.ProgressCallbackGate();
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            int postUnblockEntered = 0;

            Task blocked = Task.Run(() =>
            {
                gate.TryInvoke(() =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                });
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool drained = gate.Close(TimeSpan.FromMilliseconds(30));
            sw.Stop();

            Assert.False(drained);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Bounded Close exceeded budget: {sw.Elapsed}");

            release.Set();
            await blocked.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(gate.TryInvoke(() => Interlocked.Increment(ref postUnblockEntered)));
            Assert.Equal(0, Volatile.Read(ref postUnblockEntered));
        }
    }

    [Fact]
    public async Task ProgressCallbackGate_PausedInsideAdmissionPredicate_ClosePreventsHostInvocation()
    {
        var gate = new CombatSolver.Beam.ProgressCallbackGate();
        using var predicateEntered = new ManualResetEventSlim(false);
        using var releasePredicate = new ManualResetEventSlim(false);
        int hostInvoked = 0;
        bool tryResult = true;

        Task caller = Task.Run(() =>
        {
            tryResult = gate.TryInvoke(
                () => Interlocked.Increment(ref hostInvoked),
                canInvoke: () =>
                {
                    predicateEntered.Set();
                    releasePredicate.Wait(TimeSpan.FromSeconds(5));
                    return true;
                });
        });

        Assert.True(predicateEntered.Wait(TimeSpan.FromSeconds(2)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool drained = gate.Close(TimeSpan.FromMilliseconds(50));
        sw.Stop();

        Assert.True(drained);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(40), $"Close blocked inside predicate: {sw.Elapsed}");

        releasePredicate.Set();
        await caller.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(tryResult);
        Assert.Equal(0, Volatile.Read(ref hostInvoked));
    }

    [Fact]
    public async Task InCombatSearchTracker_SuppressedRegularProgress_StillEmitsFinalMetricsFlushWithQuiescentWork()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatState root = entered.Room.Engine.State;
        var evaluator = new StateEvaluator(root, CombatPolicy.Default);
        CombatSolver.Beam.SearchNode rootNode = CombatSolver.Beam.SearchNode.Create(root.Clone(), null, null, evaluator);
        PlannedAction endTurn = ActionGenerator.GenerateForForecast(root, CombatPolicy.Default).First(a => a.Kind == "EndTurn");
        CombatState childState = root.Clone();
        await ActionExecutor.ExecuteAsync(childState, endTurn, endTurn.ChoiceIndices);
        CombatSolver.Beam.SearchNode childNode = CombatSolver.Beam.SearchNode.Create(childState, rootNode, endTurn, evaluator);

        var ledger = new SearchWorkLedger(useParentBudget: true);
        using var userCts = new CancellationTokenSource();
        using var hardCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var regularAttempted = new ManualResetEventSlim(false);
        var hostFlushes = new List<InCombatProgressUpdate>();

        var tracker = InCombatSearchTracker.CreateWithDeliveryTracking(
            root,
            priorBattleHpLost: 0,
            System.Diagnostics.Stopwatch.StartNew(),
            update =>
            {
                if (!update.IsFinalMetricsFlush)
                {
                    userCts.Cancel();
                    regularAttempted.Set();
                    return false;
                }

                lock (hostFlushes)
                    hostFlushes.Add(update);
                return true;
            },
            searchCancellation: userCts.Token,
            hardDeadlineToken: hardCts.Token,
            ledger: ledger,
            emitExtendedMetrics: true);

        InCombatPassTrack track = tracker.RegisterPass();
        track.AttachEvaluator(evaluator, root.Players[0].Creature.CurrentHp);
        ledger.AddExpandedParents(2500);
        track.CommitWave([childNode], [], [], [], null, null, ForecastSearchBoundary.NodeLimit);

        Assert.True(regularAttempted.Wait(TimeSpan.FromSeconds(5)));

        tracker.StopAndJoin(hardCts.Token, emitFinalMetricsFlush: true, cancellationToken: userCts.Token);

        lock (hostFlushes)
        {
            InCombatProgressUpdate flush = Assert.Single(hostFlushes);
            Assert.True(flush.IsFinalMetricsFlush);
            Assert.Equal(2500L, flush.ExpandedParents);
        }
    }

    private static InCombatPolicyDto ToInCombatPolicy(BridgePolicyDto dto) =>
        new(
            dto.PotionPolicy,
            dto.PotionDirectives,
            dto.PredictPotionReward,
            dto.RelicStrategyEnabled,
            dto.RelicCounterRules,
            dto.GrowthBudgets,
            dto.BrightestFlameMaxHpLossLimit,
            dto.IgnoreLongTermRewards,
            dto.ActTransitionBossHpStrategy,
            dto.FinalBossHpStrategy,
            dto.AcceptableBattleHpLoss,
            dto.StopAtAcceptableBattleHpLoss,
            dto.UseBeamWidthPortfolio,
            dto.UseNoveltyPortfolio,
            dto.UseEarlyTurnExploration,
            dto.BeamWidth,
            PotionFreeBeamWidth: null,
            PotionBeamWidth: null,
            dto.MaxExpandedNodes,
            dto.MaxCardBranchesPerNode,
            dto.MaxPileChoiceBranchesPerAction,
            dto.MaxHandChoiceBranchesPerAction,
            dto.SoftTimeBudgetMilliseconds,
            TheftPolicy: null,
            dto.PortfolioOptionsVersion,
            dto.BeamWidthPortfolioWidths,
            dto.BeamWidthPortfolioPlainBaselineMember,
            dto.ReallocatedRefinementPortfolio,
            dto.OffensiveRefinementPortfolio,
            dto.BoundedOffensiveRefinementPortfolio,
            dto.StopPortfolioAtHpTarget,
            dto.AdaptiveNoveltyRefinement,
            dto.NoveltySearch,
            dto.NoveltyBudget,
            dto.EarlyTurnExplorationDepth,
            dto.EarlyTurnExplorationBudgetMilliseconds,
            dto.PortfolioSchedulingMode);

    private static async Task<(string SaveJson, InCombatLiveStateDto LiveDto)> CreateMinimalTurn1CombatAsync(
        string encounterId)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        Assert.True(EncounterCatalog.TryGet(encounterId, out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatRoom room = entered.Room;
        CombatState state = room.Engine.State;
        Player player = state.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;

        var creatureHandles = new Dictionary<Creature, int>(ReferenceEqualityComparer.Instance);
        var cardHandles = new Dictionary<CardModel, int>(ReferenceEqualityComparer.Instance);
        int RegisterCreature(Creature c) =>
            creatureHandles.TryGetValue(c, out int h) ? h : creatureHandles[c] = creatureHandles.Count + 1;
        int RegisterCard(CardModel card)
        {
            if (!cardHandles.TryGetValue(card, out int h))
            {
                h = cardHandles.Count + 1;
                cardHandles[card] = h;
                if (card.DeckVersion is { } dv) RegisterCard(dv);
                if (card.CloneOf is { } co) RegisterCard(co);
            }
            return h;
        }

        RegisterCreature(player.Creature);
        foreach (Creature c in state.Allies) RegisterCreature(c);
        foreach (Creature c in state.Enemies) RegisterCreature(c);
        foreach (CardModel c in player.Deck.Cards) RegisterCard(c);
        foreach (CardPile pile in pcs.AllPiles)
        {
            foreach (CardModel c in pile.Cards) RegisterCard(c);
        }

        InCombatCardDto[] cardsDto = cardHandles
            .OrderBy(kv => kv.Value)
            .Select(kv => new InCombatCardDto(
                kv.Value,
                kv.Key.Id.ToString(),
                kv.Key.CurrentUpgradeLevel,
                kv.Key.DeckVersion is not null ? cardHandles[kv.Key.DeckVersion] : null,
                kv.Key.CloneOf is not null ? cardHandles[kv.Key.CloneOf] : null,
                kv.Key.IsDupe,
                kv.Key.ExhaustOnNextPlay,
                kv.Key.BaseReplayCount,
                0,
                kv.Key.FloorAddedToDeck,
                kv.Key.CanonicalEnergyCostValue,
                kv.Key.CanonicalEnergyCostValue,
                kv.Key.CostsXEnergy,
                [],
                kv.Key.TemporaryStarCostOverrideThisTurn,
                kv.Key.TemporaryFreeThisTurn,
                kv.Key.TemporaryFreeUntilPlayed,
                kv.Key.TemporaryFreeThisCombat,
                kv.Key.TemporaryCostOverrideThisTurn,
                kv.Key.TemporaryCostOverrideThisTurnOrUntilPlayed,
                kv.Key.TemporaryCostOverrideThisCombat,
                kv.Key.TemporaryRetainThisTurn,
                kv.Key.TemporarySlyThisTurn,
                kv.Key.LocalKeywords.Select(k => k.ToString()).ToArray(),
                [],
                [],
                null,
                null,
                new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal)))
            .ToArray();

        InCombatCreatureDto[] creaturesDto = creatureHandles
            .OrderBy(kv => kv.Value)
            .Select(kv =>
            {
                Creature c = kv.Key;
                InCombatMonsterDto? monsterDto = null;
                if (c.Monster is { } m)
                {
                    MonsterMoveStateMachine sm = m.MoveStateMachine!;
                    var setupFields = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);
                    if (m is Nibbit nib)
                    {
                        setupFields["Nibbit._isFront"] = new("Bool", BoolValue: nib.IsFront);
                        setupFields["Nibbit._isAlone"] = new("Bool", BoolValue: nib.IsAlone);
                    }
                    SerializableRng mrng = m.Rng.ToSerializable();
                    monsterDto = new InCombatMonsterDto(
                        new InCombatRngStreamDto(mrng.counter, mrng.state0, mrng.state1, mrng.state2, mrng.state3),
                        m.IsPerformingMove,
                        m.SpawnedThisTurn,
                        sm.CurrentState.Id,
                        m.NextMove?.Id,
                        m.NextMove?.CanTransitionAway ?? true,
                        sm.PerformedFirstMove,
                        sm.StateLog.Select(s => s.Id).ToArray(),
                        null,
                        setupFields,
                        new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal));
                }
                return new InCombatCreatureDto(
                    kv.Value,
                    c.CombatId,
                    c.Side.ToString(),
                    c.SlotName,
                    c.IsPlayer,
                    null,
                    c.Monster?.Id.ToString(),
                    c.CurrentHp,
                    c.MaxHp,
                    c.Block,
                    c.CumulativeHpLost,
                    c.CombatState is null,
                    monsterDto,
                    []);
            })
            .ToArray();

        string[] encounterSlots = ReadField<string[]>(state, "_encounterSlots");
        uint nextCreatureId = ReadField<uint>(state, "_nextCreatureId");
        SerializableRunRngSet runRng = state.RunState.Rng.ToSerializable();
        SerializablePlayerRngSet playerRng = player.PlayerRng.ToSerializable();

        var countersDto = new InCombatPlayerCountersDto(
            pcs.StarsGainedThisTurn, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var playerDto = new InCombatPlayerDto(
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Creature.Block,
            player.Creature.CumulativeHpLost,
            player.Gold,
            player.MaxEnergy,
            player.BaseOrbSlotCount,
            player.IsActiveForHooks,
            player.CanUseOrRemovePotions,
            pcs.Energy,
            pcs.Stars,
            pcs.Phase.ToString(),
            pcs.TurnNumber,
            countersDto,
            pcs.Hand.Cards.Select(c => cardHandles[c]).ToArray(),
            pcs.DrawPile.Cards.Select(c => cardHandles[c]).ToArray(),
            pcs.DiscardPile.Cards.Select(c => cardHandles[c]).ToArray(),
            pcs.ExhaustPile.Cards.Select(c => cardHandles[c]).ToArray(),
            pcs.PlayPile.Cards.Select(c => cardHandles[c]).ToArray(),
            player.Deck.Cards.Select(c => cardHandles[c]).ToArray(),
            player.PotionSlots.Select(p => p?.Id.ToString()).ToArray(),
            pcs.OrbQueue.Capacity,
            [],
            []);

        var verificationDto = new InCombatVerificationDto(
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Creature.Block,
            pcs.Energy,
            pcs.Stars,
            player.Gold,
            pcs.OrbQueue.Capacity,
            [],
            [],
            player.PotionSlots.Select(p => p?.Id.ToString()).ToArray(),
            pcs.Hand.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.DrawPile.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.DiscardPile.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.ExhaustPile.Cards.Select(ToVerifiedCard).ToArray(),
            state.Enemies.Select(e => new InCombatVerifiedCreatureDto(
                e.CombatId, e.Monster?.Id.ToString(), e.CurrentHp, e.MaxHp, e.Block, e.Monster?.NextMove?.Id, [])).ToArray(),
            []);

        var liveDto = new InCombatLiveStateDto(
            encounterId,
            room.RoomType.ToString(),
            encounterSlots,
            state.CurrentSide.ToString(),
            state.RoundNumber,
            state.IsPlayerExtraTurn,
            state.GoldWasStolen,
            nextCreatureId,
            runRng.Seed,
            runRng.Rngs.ToDictionary(kv => kv.Key.ToString(), kv => new InCombatRngStreamDto(kv.Value.counter, kv.Value.state0, kv.Value.state1, kv.Value.state2, kv.Value.state3), StringComparer.Ordinal),
            playerRng.Seed,
            playerRng.Rngs.ToDictionary(kv => kv.Key.ToString(), kv => new InCombatRngStreamDto(kv.Value.counter, kv.Value.state0, kv.Value.state1, kv.Value.state2, kv.Value.state3), StringComparer.Ordinal),
            playerDto,
            cardsDto,
            player.Relics.Select(r => new InCombatRelicDto(r.Id.ToString(), r.IsWax, r.IsMelted, r.StackCount, r.FloorAddedToDeck, new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal))).ToArray(),
            creaturesDto,
            state.Allies.Select(c => creatureHandles[c]).ToArray(),
            state.Enemies.Select(c => creatureHandles[c]).ToArray(),
            [],
            [],
            state.SpawnedEnemies.Select(c => creatureHandles[c]).ToArray(),
            [],
            [],
            new InCombatSemanticHistoryDto([], [], [], [], [], []),
            verificationDto);

        return (saveJson, liveDto);

        static InCombatVerifiedCardDto ToVerifiedCard(CardModel c) =>
            new(c.Id.ToString(), c.CurrentUpgradeLevel, c.ResolvedEnergyCostForComparison, c.LocalKeywords.Select(k => k.ToString()).ToArray());

        static T ReadField<T>(object target, string name)
        {
            for (Type? t = target.GetType(); t is not null; t = t.BaseType)
            {
                FieldInfo? fi = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi is not null)
                    return (T)fi.GetValue(target)!;
            }
            throw new MissingFieldException(target.GetType().FullName, name);
        }
    }
}
