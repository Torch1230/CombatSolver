using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.ValueProps;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed class ParallelForecastTests
{
    [Theory]
    [InlineData(55, false)]
    [InlineData(55, true)]
    [InlineData(80, true)]
    public async Task MawlerNormal_AndInflame_ParallelDop4_MatchBeamReplayAndValidOutcome(
        int playerHp,
        bool addUpgradedInflame)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgePolicyDto dtoPolicy = TestSaveBuilder.DefaultPolicy(
            beamWidth: 16,
            maxExpandedNodes: 5000,
            useBeamWidthPortfolio: true);

        BridgeRequestDto request = TestSaveBuilder.CreateRequest(
            run,
            "MAWLER_NORMAL",
            target.coord,
            playerCurrentHpOverride: playerHp,
            simulationSeed: 42UL,
            policy: dtoPolicy,
            mutateSave: addUpgradedInflame
                ? root =>
                {
                    var deck = (JsonArray)root["players"]![0]!["deck"]!;
                    deck.Add(new JsonObject
                    {
                        ["id"] = "CARD.INFLAME",
                        ["floor_added_to_deck"] = 1,
                        ["current_upgrade_level"] = 1,
                    });
                }
                : null) with
        {
            MaxDegreeOfParallelism = 4,
        };

        (BridgeResponseDto wire, _) = await TestSaveBuilder.RunAsync(request);
        int expectedEffectiveDop = Math.Clamp(4, 1, Math.Max(1, Environment.ProcessorCount));
        Assert.Equal("Succeeded", wire.Outcome);
        Assert.Equal(expectedEffectiveDop, wire.EffectiveDegreeOfParallelism);
        Assert.True(wire.ExpandedNodes > 0);
        Assert.Equal("None", wire.SearchBoundary);
        Assert.Equal("Complete", wire.Confidence);
        Assert.False(wire.OnlyDeathRoutes);
        Assert.True(wire.FinalHp > 0);

        // 同时验证直接调用 BeamSearch.SolveForForecastAsync(maxDegreeOfParallelism: 4) 的路线可通过 ReplayAsync。
        CombatState directRoot = await EnterMawlerCombatFromRequestAsync(request);
        (CombatPolicy basePolicy, BeamBudget budget, PreCombatPortfolioOptions portfolio) =
            BridgePolicyResolver.ValidateAndBuildBasePolicy(
                dtoPolicy,
                request.SearchBudgetMilliseconds,
                request.OverallTimeoutMilliseconds,
                4);
        CombatPolicy resolvedPolicy = BridgePolicyResolver.ResolveForCombatRoot(directRoot, basePolicy);

        BeamForecastResult forecast = await BeamSearch.SolveForForecastAsync(
            directRoot.Clone(),
            budget,
            resolvedPolicy,
            portfolio,
            openingPreRootBattleHpLost: 0,
            CancellationToken.None,
            maxDegreeOfParallelism: 4);

        Assert.True(forecast.Won);
        Assert.Equal(wire.ProjectedHpLoss, forecast.ProjectedHpLoss);
        Assert.Equal(wire.FinalHp, forecast.FinalHp);

        CombatState replayState = directRoot.Clone();
        BeamReplayResult replay = await BeamSearch.ReplayAsync(replayState, forecast.Actions);
        Assert.True(replay.CombatEnded);
        Assert.True(replay.Won);
        Assert.Equal(forecast.FinalHp, Math.Max(0, replayState.Players[0].Creature.CurrentHp));
    }

    [Fact]
    public async Task SmartPotionEarlyStop_AndForcedFoulPotion_RemainValidAtDop4()
    {
        // Smart 药水保守 + HP 达标早停：两张打击即可 0 伤收官，不得浪费火焰药水。
        CombatState smartCombat = await CreateMawlerTwoStrikesWithPotionAsync<FirePotion>(playerHp: 80, enemyHp: 10);
        BridgePolicyDto smartPolicyDto = TestSaveBuilder.DefaultPolicy(
            beamWidth: 16,
            maxExpandedNodes: 5000,
            useBeamWidthPortfolio: true) with
        {
            PotionPolicy = "Smart",
            PotionDirectives = [],
            AcceptableBattleHpLoss = 0,
            StopAtAcceptableBattleHpLoss = true,
        };
        (CombatPolicy smartBasePolicy, BeamBudget smartBudget, PreCombatPortfolioOptions smartPortfolio) =
            BridgePolicyResolver.ValidateAndBuildBasePolicy(smartPolicyDto, 4000, 20000, 4);
        CombatPolicy smartResolved = BridgePolicyResolver.ResolveForCombatRoot(smartCombat, smartBasePolicy);

        BeamForecastResult smartResult = await BeamSearch.SolveForForecastAsync(
            smartCombat.Clone(),
            smartBudget,
            smartResolved,
            smartPortfolio,
            0,
            CancellationToken.None,
            maxDegreeOfParallelism: 4);

        Assert.True(smartResult.Won);
        Assert.Equal(0, smartResult.ProjectedHpLoss);
        Assert.Equal(80, smartResult.FinalHp);
        Assert.Empty(smartResult.PotionUses);
        BeamReplayResult smartReplay = await BeamSearch.ReplayAsync(smartCombat.Clone(), smartResult.Actions);
        Assert.True(smartReplay.Won);

        // 强制使用污浊药水导致的混合池战败结果在 DOP=4 下依然保持 SearchBoundary == None / Confidence == Bounded。
        CombatState foulCombat = await CreateMawlerTwoStrikesWithPotionAsync<FoulPotion>(playerHp: 1, enemyHp: 6);
        BridgePolicyDto foulPolicyDto = TestSaveBuilder.DefaultPolicy(
            beamWidth: 16,
            maxExpandedNodes: 5000,
            useBeamWidthPortfolio: true) with
        {
            PotionPolicy = "Smart",
            PotionDirectives = [new BridgePotionDirectiveDto(0, "FOUL_POTION", "Force")],
            StopAtAcceptableBattleHpLoss = false,
        };
        (CombatPolicy foulBasePolicy, BeamBudget foulBudget, PreCombatPortfolioOptions foulPortfolio) =
            BridgePolicyResolver.ValidateAndBuildBasePolicy(foulPolicyDto, 4000, 20000, 4);
        CombatPolicy foulResolved = BridgePolicyResolver.ResolveForCombatRoot(foulCombat, foulBasePolicy);

        BeamForecastResult foulResult = await BeamSearch.SolveForForecastAsync(
            foulCombat.Clone(),
            foulBudget,
            foulResolved,
            foulPortfolio,
            0,
            CancellationToken.None,
            maxDegreeOfParallelism: 4);

        Assert.False(foulResult.Won);
        Assert.Equal(1, foulResult.ProjectedHpLoss);
        Assert.Equal(0, foulResult.FinalHp);
        Assert.Null(foulResult.CombatEndedTurn);
        Assert.False(foulResult.OnlyDeathRoutes);
        Assert.Equal(ForecastConfidence.Bounded, foulResult.Confidence);
        Assert.Equal(ForecastSearchBoundary.None, foulResult.SearchBoundary);
        ForecastPotionUse foulUse = Assert.Single(foulResult.PotionUses);
        Assert.Equal(("FOUL_POTION", 1, 0), (foulUse.Id, foulUse.Turn, foulUse.Slot));
    }

    [Fact]
    public async Task ConcurrentBridgeEntryCalls_AtDop2Each_AllSucceedWithoutSharedStateCorruption()
    {
        TestSaveBuilder.EnsurePrewarmed();
        BridgePolicyDto policy = TestSaveBuilder.DefaultPolicy(
            beamWidth: 12,
            maxExpandedNodes: 1500,
            useBeamWidthPortfolio: true);

        RunState silentRun = TestSaveBuilder.CreateRun(seed: "PARALLEL-CONCURRENT-1", characterId: "CHARACTER.SILENT");
        RunState ironcladRun = TestSaveBuilder.CreateRun(seed: "PARALLEL-CONCURRENT-2", characterId: "CHARACTER.IRONCLAD", ascension: 0);

        MapPoint silentTarget = TestSaveBuilder.FindFirstUnvisitedPoint(silentRun, MapPointType.Monster);
        MapPoint ironcladTarget = TestSaveBuilder.FindFirstUnvisitedPoint(ironcladRun, MapPointType.Monster);

        BridgeRequestDto[] requests =
        [
            TestSaveBuilder.CreateRequest(silentRun, "NIBBITS_WEAK", silentTarget.coord, simulationSeed: 11UL, policy: policy) with { MaxDegreeOfParallelism = 2 },
            TestSaveBuilder.CreateRequest(silentRun, "SHRINKER_BEETLE_WEAK", silentTarget.coord, simulationSeed: 12UL, policy: policy) with { MaxDegreeOfParallelism = 2 },
            TestSaveBuilder.CreateRequest(ironcladRun, "MAWLER_NORMAL", ironcladTarget.coord, playerCurrentHpOverride: 55, simulationSeed: 42UL, policy: policy) with { MaxDegreeOfParallelism = 2 },
            TestSaveBuilder.CreateRequest(ironcladRun, "MAWLER_NORMAL", ironcladTarget.coord, playerCurrentHpOverride: 55, simulationSeed: 43UL, policy: policy, mutateSave: root =>
            {
                ((JsonArray)root["players"]![0]!["deck"]!).Add(new JsonObject
                {
                    ["id"] = "CARD.INFLAME",
                    ["floor_added_to_deck"] = 1,
                    ["current_upgrade_level"] = 1,
                });
            }) with { MaxDegreeOfParallelism = 2 },
        ];

        (BridgeResponseDto Dto, JsonElement Json)[] results = await Task.WhenAll(
            requests.Select(req => TestSaveBuilder.RunAsync(req)));

        int expectedDop = Math.Clamp(2, 1, Math.Max(1, Environment.ProcessorCount));
        foreach ((BridgeResponseDto dto, _) in results)
        {
            Assert.Equal("Succeeded", dto.Outcome);
            Assert.Equal(expectedDop, dto.EffectiveDegreeOfParallelism);
            Assert.NotNull(dto.ProjectedHpLoss);
            Assert.True(dto.ExpandedNodes > 0);
        }
    }

    [Fact]
    public async Task ParallelForecastSearch_RespectsExternalCancellationAndPreCancelledToken()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgePolicyDto largeBudget = TestSaveBuilder.DefaultPolicy(
            beamWidth: 32,
            maxExpandedNodes: 50000,
            useBeamWidthPortfolio: true,
            softTimeBudgetMs: 10000);

        BridgeRequestDto request = TestSaveBuilder.CreateRequest(
            run,
            "MAWLER_NORMAL",
            target.coord,
            playerCurrentHpOverride: 55,
            simulationSeed: 42UL,
            searchBudgetMs: 10000,
            overallTimeoutMs: 20000,
            policy: largeBudget) with
        {
            MaxDegreeOfParallelism = 4,
        };

        using var preCanceledCts = new CancellationTokenSource();
        preCanceledCts.Cancel();
        (BridgeResponseDto canceledDto, _) = await TestSaveBuilder.RunAsync(request, preCanceledCts.Token);
        Assert.Equal("Cancelled", canceledDto.Outcome);
        Assert.Null(canceledDto.ProjectedHpLoss);
        Assert.Null(canceledDto.ExpandedNodes);

        CombatState directCombat = await EnterMawlerCombatFromRequestAsync(request);
        (CombatPolicy basePolicy, BeamBudget budget, PreCombatPortfolioOptions portfolio) =
            BridgePolicyResolver.ValidateAndBuildBasePolicy(
                largeBudget,
                request.SearchBudgetMilliseconds,
                request.OverallTimeoutMilliseconds,
                4);
        CombatPolicy resolved = BridgePolicyResolver.ResolveForCombatRoot(directCombat, basePolicy);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BeamSearch.SolveForForecastAsync(
                directCombat.Clone(),
                budget,
                resolved,
                portfolio,
                0,
                preCanceledCts.Token,
                maxDegreeOfParallelism: 4));

        // 短时外部取消在并行搜索途中触发时，桥接必须稳定收敛为 Cancelled 而非抛未处理异常。
        using var midFlightCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        (BridgeResponseDto midFlightDto, _) = await TestSaveBuilder.RunAsync(request, midFlightCts.Token);
        Assert.Contains(midFlightDto.Outcome, new[] { "Cancelled", "Succeeded" });
        if (midFlightDto.Outcome == "Cancelled")
        {
            Assert.Null(midFlightDto.ProjectedHpLoss);
            Assert.Null(midFlightDto.ExpandedNodes);
        }
    }

    [Theory]
    [InlineData(true, 0, 0, 1)]
    [InlineData(false, 3, 10, 0)]
    public async Task ActEndingBoss_WithPredictedPotionDrop_Dop1AndDop4MatchBossHpReliefAndPotionOutlook(
        bool fillPotionBelt,
        int bossStrengthBonus,
        int expectedHpLoss,
        int expectedPotionUses)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(
            seed: "BOSS-POLICY-PARITY-01",
            ascension: 0,
            actIndex: 0,
            characterId: "CHARACTER.SILENT");
        Player runPlayer = run.Players[0];
        var whiteBeastStatue = (RelicModel)ModelDb.Relic<WhiteBeastStatue>().MutableClone();
        whiteBeastStatue.AssignOwner(runPlayer);
        runPlayer.AddRelicInternal(whiteBeastStatue);

        Assert.True(runPlayer.MaxPotionCount >= 2);
        int potionCount = fillPotionBelt ? runPlayer.MaxPotionCount : 1;
        for (int i = 0; i < potionCount; i++)
        {
            var firePotion = (PotionModel)ModelDb.Potion<FirePotion>().MutableClone();
            firePotion.AssignOwner(runPlayer);
            runPlayer.AddPotionInternal(firePotion);
        }

        Assert.True(EncounterCatalog.TryGet("VANTOM_BOSS", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                RoomType.Boss,
                run.Map.BossMapPoint.coord.row + 1,
                run.Map.BossMapPoint.coord.col,
                MapPointType.Boss,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Player player = combat.Players[0];
        player.Creature.SetMaxHpInternal(80);
        player.Creature.SetCurrentHpInternal(60);

        Creature boss = combat.Enemies.First(e => e.IsAlive);
        if (boss.Powers.OfType<SlipperyPower>().FirstOrDefault() is { } slippery)
            await PowerCmd.Remove(slippery);
        boss.SetCurrentHpInternal(18);
        if (bossStrengthBonus > 0)
            await PowerCmd.Apply<StrengthPower>(combat, boss, bossStrengthBonus, boss, null);

        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearCombatAllPiles(pcs);
        for (int i = 0; i < 2; i++)
            pcs.Hand.AddInternal(CreateCombatCard<StrikeSilent>(player));
        for (int i = 0; i < 3; i++)
            pcs.DrawPile.AddInternal(CreateCombatCard<StrikeSilent>(player));
        pcs.GainEnergy(Math.Max(0, 3 - pcs.Energy));

        BridgePolicyDto policyDto = TestSaveBuilder.DefaultPolicy(
            beamWidth: 16,
            maxExpandedNodes: 5000,
            useBeamWidthPortfolio: true) with
        {
            PotionPolicy = "Smart",
            PredictPotionReward = true,
            ActTransitionBossHpStrategy = "ProgressionFirst",
            StopAtAcceptableBattleHpLoss = false,
        };
        (CombatPolicy basePolicy, BeamBudget budget, PreCombatPortfolioOptions portfolio) =
            BridgePolicyResolver.ValidateAndBuildBasePolicy(policyDto, 4000, 20000, 4);
        CombatPolicy resolvedPolicy = BridgePolicyResolver.ResolveForCombatRoot(combat, basePolicy);

        PolicyRootOperands unclonedOperands = PolicyRootOperands.Capture(combat, resolvedPolicy);
        Assert.Equal(BossHpRelief.ActClearHeal, unclonedOperands.BossHpRelief);
        Assert.Equal(PotionRewardForecast.Drop, unclonedOperands.PotionReward.Forecast);
        if (fillPotionBelt)
            Assert.True(unclonedOperands.PotionReward.ReplacementHpCredit >= 9);
        else
            Assert.Equal(0, unclonedOperands.PotionReward.ReplacementHpCredit);

        PolicyRootOperands clonedOperands = PolicyRootOperands.Capture(combat.Clone(), resolvedPolicy);
        Assert.Equal(BossHpRelief.None, clonedOperands.BossHpRelief);
        Assert.Equal(PotionRewardForecast.Unknown, clonedOperands.PotionReward.Forecast);

        BeamForecastResult dop1Result = await BeamSearch.SolveForForecastAsync(
            combat,
            budget,
            resolvedPolicy,
            portfolio,
            openingPreRootBattleHpLost: 0,
            CancellationToken.None,
            maxDegreeOfParallelism: 1);
        BeamForecastResult dop4Result = await BeamSearch.SolveForForecastAsync(
            combat,
            budget,
            resolvedPolicy,
            portfolio,
            openingPreRootBattleHpLost: 0,
            CancellationToken.None,
            maxDegreeOfParallelism: 4);

        Assert.True(dop1Result.Won);
        Assert.True(dop4Result.Won);
        Assert.Equal(expectedHpLoss, dop1Result.ProjectedHpLoss);
        Assert.Equal(expectedHpLoss, dop4Result.ProjectedHpLoss);
        Assert.Equal(60 - expectedHpLoss, dop1Result.FinalHp);
        Assert.Equal(60 - expectedHpLoss, dop4Result.FinalHp);
        Assert.Equal(dop1Result.CombatEndedTurn, dop4Result.CombatEndedTurn);
        Assert.Equal(expectedPotionUses, dop1Result.PotionUses.Count);
        Assert.Equal(expectedPotionUses, dop4Result.PotionUses.Count);

        BeamReplayResult replay = await BeamSearch.ReplayAsync(combat.Clone(), dop4Result.Actions);
        Assert.True(replay.CombatEnded);
        Assert.True(replay.Won);
        Assert.Equal(expectedHpLoss, replay.HpLost);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(5, 8)]
    [InlineData(12, 8)]
    [InlineData(25, 8)]
    public async Task ParallelForecast_AtomicNodeReservation_NeverOverspendsBudgetAcrossChoiceReplays(
        int nodeLimit,
        int dop)
    {
        var tracker = new ForecastNodeBudgetTracker(nodeLimit);
        int[] grantedPerWorker = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            int granted = 0;
            for (int i = 0; i < 64; i++)
            {
                if (tracker.TryReserveSlot())
                    granted++;
            }
            return granted;
        })));
        Assert.Equal(nodeLimit, grantedPerWorker.Sum());
        Assert.Equal(nodeLimit, tracker.Consumed);

        CombatState combat = await CreateMawlerChoiceReplayCombatAsync(
            enemyHp: 200,
            addInflameToHand: false);
        var tightBudget = new BeamBudget(
            BeamWidth: 12,
            NodeLimit: nodeLimit,
            MaxCardBranches: 8,
            MaxPileChoiceBranches: 6,
            MaxHandChoiceBranches: 6,
            TimeLimit: TimeSpan.FromSeconds(10));

        for (int iteration = 0; iteration < 5; iteration++)
        {
            ForecastPassOutcome singlePass = await BeamSearch.RunSingleForecastPassAsync(
                combat.Clone(),
                tightBudget,
                CombatPolicy.Default,
                CancellationToken.None,
                maxDegreeOfParallelism: dop);
            Assert.InRange(singlePass.ExpandedNodes, 1, nodeLimit);

            BeamForecastResult portfolioResult = await BeamSearch.SolveForForecastAsync(
                combat.Clone(),
                tightBudget,
                CombatPolicy.Default,
                new PreCombatPortfolioOptions(UseBeamWidthPortfolio: true),
                openingPreRootBattleHpLost: 0,
                CancellationToken.None,
                maxDegreeOfParallelism: dop);
            Assert.InRange(portfolioResult.ExpandedNodes, 1, nodeLimit);
        }
    }

    [Fact]
    public async Task ParallelPortfolio_EarlyCancelledSibling_FoldsConsumedAttemptsIntoTotalExpandedNodes()
    {
        CombatState heavyCombat = await CreateMawlerChoiceReplayCombatAsync(
            enemyHp: 500,
            addInflameToHand: false);
        var largeBudget = new BeamBudget(
            BeamWidth: 32,
            NodeLimit: 50000,
            MaxCardBranches: 8,
            MaxPileChoiceBranches: 6,
            MaxHandChoiceBranches: 6,
            TimeLimit: TimeSpan.FromSeconds(10));
        CombatPolicy noEarlyStopPolicy = CombatPolicy.Default with { StopAtAcceptableBattleHpLoss = false };
        PolicyRootOperands operands = PolicyRootOperands.Capture(heavyCombat, noEarlyStopPolicy);
        var tracker = new ForecastNodeBudgetTracker(largeBudget.NodeLimit);
        using var earlyStopCts = new CancellationTokenSource();

        Task<PortfolioMemberRun> memberTask = Task.Run(() => PreCombatBeamPortfolio.RunPortfolioMemberAsync(
            heavyCombat.Clone(),
            largeBudget,
            noEarlyStopPolicy,
            operands,
            aggressivePowerCommitment: false,
            openingPreRootBattleHpLost: 0,
            memberDop: 2,
            earlyStopCts,
            CancellationToken.None,
            tracker));

        var deadline = Stopwatch.StartNew();
        while (tracker.Consumed < 10 && !memberTask.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Yield();
        earlyStopCts.Cancel();

        PortfolioMemberRun runResult = await memberTask;
        Assert.Null(runResult.Outcome);
        Assert.True(runResult.ExpandedNodes >= 10, $"Expected >= 10 consumed attempts before cancel, got {runResult.ExpandedNodes}.");
        Assert.Equal(tracker.Consumed, runResult.ExpandedNodes);

        CombatState winCombat = await CreateMawlerChoiceReplayCombatAsync(
            enemyHp: 10,
            addInflameToHand: true);
        CombatPolicy earlyStopPolicy = CombatPolicy.Default with
        {
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 0,
        };
        var portfolioBudget = new BeamBudget(
            BeamWidth: 16,
            NodeLimit: 5000,
            MaxCardBranches: 8,
            MaxPileChoiceBranches: 6,
            MaxHandChoiceBranches: 6,
            TimeLimit: TimeSpan.FromSeconds(10));

        BeamForecastResult result = await BeamSearch.SolveForForecastAsync(
            winCombat,
            portfolioBudget,
            earlyStopPolicy,
            new PreCombatPortfolioOptions(UseBeamWidthPortfolio: true),
            openingPreRootBattleHpLost: 0,
            CancellationToken.None,
            maxDegreeOfParallelism: 4);

        Assert.True(result.Won);
        Assert.Equal(0, result.ProjectedHpLoss);
        Assert.InRange(result.ExpandedNodes, 1, portfolioBudget.NodeLimit);
    }

    [Fact]
    public async Task ChooseWinningPass_TwoUnfinishedSmartPotionPasses_UsesSharedBaselineWithoutThrowing()
    {
        CombatState combat = await CreateMawlerTwoStrikesWithPotionAsync<FirePotion>(playerHp: 80, enemyHp: 100);
        CombatPolicy smartPolicy = CombatPolicy.Default with
        {
            PotionPolicy = SolverPotionPolicy.Smart,
            PredictPotionReward = false,
            IgnoreLongTermRewards = false,
        };
        var baselineEvaluator = new StateEvaluator(combat, smartPolicy, aggressivePowerCommitment: false);
        var powerEvaluator = new StateEvaluator(combat, smartPolicy, aggressivePowerCommitment: true);
        SearchNode rootNode = SearchNode.Create(combat.Clone(), null, null, baselineEvaluator);
        var firePotionAction = new PlannedAction(
            Turn: 1,
            Kind: "UsePotion",
            CardId: null,
            CardOccurrence: 0,
            TargetIndex: 0,
            PotionId: "FIRE_POTION",
            PotionSlot: 0,
            ChoiceIndices: [],
            ChoiceCardIds: []);

        async Task<SearchNode> CreateDamagedNodeAsync(
            int damage,
            bool usePotion,
            StateEvaluator evaluator)
        {
            CombatState state = combat.Clone();
            if (usePotion)
                await ActionExecutor.ExecuteAsync(state, firePotionAction, []);
            await CreatureCmd.Damage(
                state,
                [state.Players[0].Creature],
                damage,
                ValueProp.Unblockable | ValueProp.Unpowered,
                null,
                null,
                null);
            return SearchNode.Create(
                state,
                rootNode,
                usePotion ? firePotionAction : null,
                evaluator);
        }

        SearchNode pass0Baseline = await CreateDamagedNodeAsync(20, usePotion: false, baselineEvaluator);
        SearchNode pass0Selected = await CreateDamagedNodeAsync(5, usePotion: true, baselineEvaluator);
        SearchNode pass1Baseline = await CreateDamagedNodeAsync(18, usePotion: false, powerEvaluator);
        SearchNode pass1Selected = await CreateDamagedNodeAsync(2, usePotion: true, powerEvaluator);

        var pass0 = new ForecastPassOutcome(
            pass0Selected,
            [
                new EvaluatedForecastCandidate(pass0Selected, ForecastSearchBoundary.NodeLimit, 75),
                new EvaluatedForecastCandidate(pass0Baseline, ForecastSearchBoundary.NodeLimit, 60),
            ],
            ForecastSearchBoundary.NodeLimit,
            ExpandedNodes: 12,
            AcceptableVictoryFound: false,
            RootInitialCumulativeHpLost: 0,
            baselineEvaluator);
        var pass1 = new ForecastPassOutcome(
            pass1Selected,
            [
                new EvaluatedForecastCandidate(pass1Selected, ForecastSearchBoundary.NodeLimit, 78),
                new EvaluatedForecastCandidate(pass1Baseline, ForecastSearchBoundary.NodeLimit, 62),
            ],
            ForecastSearchBoundary.NodeLimit,
            ExpandedNodes: 15,
            AcceptableVictoryFound: false,
            RootInitialCumulativeHpLost: 0,
            powerEvaluator);

        Assert.Throws<InvalidOperationException>(() =>
            PlanSelector.Select([pass0Selected, pass1Selected], 80, baselineEvaluator));

        ForecastPassOutcome chosen = PreCombatBeamPortfolio.ChooseWinningPass(
            [pass0, pass1],
            initialHp: 80,
            baselineEvaluator);
        Assert.Same(pass1, chosen);
        Assert.Same(pass1Selected, chosen.Selected);

        SearchNode strongFreeBaseline = await CreateDamagedNodeAsync(10, usePotion: false, powerEvaluator);
        ForecastPassOutcome pass1WithStrongBaseline = pass1 with
        {
            PassBoundary = ForecastSearchBoundary.TimeLimit,
            EvaluatedCandidates =
            [
                .. pass1.EvaluatedCandidates,
                new EvaluatedForecastCandidate(strongFreeBaseline, ForecastSearchBoundary.TimeLimit, 70),
            ],
        };
        ForecastPassOutcome fallbackChosen = PreCombatBeamPortfolio.ChooseWinningPass(
            [pass0, pass1WithStrongBaseline],
            initialHp: 80,
            baselineEvaluator);
        Assert.Same(strongFreeBaseline, fallbackChosen.Selected);
        Assert.Same(powerEvaluator, fallbackChosen.Evaluator);
        Assert.Equal(ForecastSearchBoundary.TimeLimit, fallbackChosen.PassBoundary);

        CombatState wonState = combat.Clone();
        await CreatureCmd.Kill(wonState.Enemies.First(e => e.IsAlive));
        Assert.True(wonState.Engine!.CheckWinCondition());
        SearchNode wonNode = SearchNode.Create(wonState, rootNode, null, baselineEvaluator);
        var wonPass = new ForecastPassOutcome(
            wonNode,
            [new EvaluatedForecastCandidate(wonNode, ForecastSearchBoundary.None, 80)],
            ForecastSearchBoundary.None,
            ExpandedNodes: 5,
            AcceptableVictoryFound: true,
            RootInitialCumulativeHpLost: 0,
            baselineEvaluator);

        Assert.Same(wonPass, PreCombatBeamPortfolio.ChooseWinningPass([wonPass, pass1], 80, baselineEvaluator));
        Assert.Same(wonPass, PreCombatBeamPortfolio.ChooseWinningPass([pass1, wonPass], 80, baselineEvaluator));
    }

    private static async Task<CombatState> EnterMawlerCombatFromRequestAsync(BridgeRequestDto request)
    {
        RunState restored = BridgeSaveRestore.Restore(request);
        if (request.PlayerCurrentHpOverride is int hpOverride)
            restored.ApplyPlayerCurrentHpOverride(hpOverride);
        ulong? seedOverride = request.SimulationSeed is ulong simSeed
            ? restored.ApplySimulationSeed(simSeed)
            : null;
        Assert.True(Sts2Sim.Core.Content.EncounterCatalog.TryGet(request.EncounterId, out var encounterEntry));
        var targetSpec = new ForecastTargetSpec(
            encounterEntry.Definition,
            RoomType.Monster,
            request.TargetActFloor,
            request.TargetMapColumn,
            MapPointType.Monster,
            false);
        ForecastRoomEntryResult entered = await restored.EnterForecastCombatAsync(
            targetSpec,
            new ForecastOpeningRunner(CancellationToken.None),
            seedOverride);
        return entered.Room.Engine.State;
    }

    private static async Task<CombatState> CreateMawlerTwoStrikesWithPotionAsync<TPotion>(int playerHp, int enemyHp)
        where TPotion : PotionModel
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        var potion = (PotionModel)ModelDb.Potion<TPotion>().MutableClone();
        potion.AssignOwner(run.Players[0]);
        run.Players[0].AddPotionInternal(potion);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(Sts2Sim.Core.Content.EncounterCatalog.TryGet("MAWLER_NORMAL", out var entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Player player = combat.Players[0];
        player.Creature.SetMaxHpInternal(80);
        player.Creature.SetCurrentHpInternal(playerHp);
        combat.Enemies.First(e => e.IsAlive).SetCurrentHpInternal(enemyHp);

        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearCombatAllPiles(pcs);
        pcs.Hand.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.Hand.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.GainEnergy(Math.Max(0, 3 - pcs.Energy));
        return combat;
    }

    private static async Task<CombatState> CreateMawlerChoiceReplayCombatAsync(
        int enemyHp,
        bool addInflameToHand)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Player player = combat.Players[0];
        player.Creature.SetMaxHpInternal(80);
        player.Creature.SetCurrentHpInternal(80);
        combat.Enemies.First(e => e.IsAlive).SetCurrentHpInternal(enemyHp);

        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearCombatAllPiles(pcs);
        if (addInflameToHand)
            pcs.Hand.AddInternal(CreateCombatCard<Inflame>(player));
        else
            pcs.Hand.AddInternal(CreateCombatCard<Armaments>(player));
        pcs.Hand.AddInternal(CreateCombatCard<Armaments>(player));
        pcs.Hand.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.Hand.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.Hand.AddInternal(CreateCombatCard<DefendIronclad>(player));
        if (!addInflameToHand)
            pcs.Hand.AddInternal(CreateCombatCard<DefendIronclad>(player));
        for (int i = 0; i < 5; i++)
            pcs.DrawPile.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.GainEnergy(Math.Max(0, 3 - pcs.Energy));
        return combat;
    }

    private static TCard CreateCombatCard<TCard>(Player owner) where TCard : CardModel
    {
        var card = (TCard)ModelDb.Card<TCard>().MutableClone();
        card.AssignOwner(owner);
        return card;
    }

    private static void ClearCombatAllPiles(PlayerCombatState pcs)
    {
        foreach (CardPile pile in pcs.AllPiles)
        {
            foreach (CardModel card in pile.Cards.ToArray())
                pile.RemoveInternal(card);
        }
    }
}
