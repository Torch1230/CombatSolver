using System.Text.Json.Nodes;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed class BlitzModePreCombatBridgeTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task BlitzSaveMapping_AcceptsAscensionZeroToFive_RejectsSix_AndValidatesBlitzMapTopology(
        int ascension,
        bool expectSuccess)
    {
        TestSaveBuilder.EnsurePrewarmed();
        int validCreationAscension = Math.Clamp(ascension, 0, 5);
        RunState run = TestSaveBuilder.CreateRun(
            seed: $"BLITZ-SAVE-A{ascension}",
            ascension: validCreationAscension,
            actIndex: 0,
            gameMode: GameMode.Blitz);

        MapPoint firstMonster = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgeRequestDto req = TestSaveBuilder.CreateRequest(
            run,
            "NIBBITS_WEAK",
            firstMonster.coord,
            mutateSave: root =>
            {
                if (!expectSuccess)
                {
                    root["ascension"] = ascension;
                }
            });

        (BridgeResponseDto dto, _) = await TestSaveBuilder.RunAsync(req);
        if (expectSuccess)
        {
            Assert.Equal("Succeeded", dto.Outcome);
            RunState restored = BridgeSaveRestore.Restore(req);
            Assert.Equal(GameMode.Blitz, restored.GameMode);
            Assert.IsType<BlitzActMap>(restored.Map);
        }
        else
        {
            Assert.Equal("Failed", dto.Outcome);
        }
    }

    [Fact]
    public async Task BlitzSavedMapMismatch_AndUnknownModeRemainFailClosed()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(seed: "BLITZ-MAP-MISMATCH", ascension: 0, gameMode: GameMode.Blitz);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);

        BridgeRequestDto badShape = TestSaveBuilder.CreateRequest(
            run,
            "NIBBITS_WEAK",
            target.coord,
            mutateSave: root => root["acts"]![0]!["saved_map"]!["height"] = 10);
        (BridgeResponseDto shapeMiss, _) = await TestSaveBuilder.RunAsync(badShape);
        Assert.Equal("CapabilityMiss", shapeMiss.Outcome);
        Assert.Equal("map_topology_unsupported", shapeMiss.FailureCode);

        BridgeRequestDto unsupportedReq = TestSaveBuilder.CreateRequest(
            run,
            "NIBBITS_WEAK",
            target.coord,
            mutateSave: root => root["game_mode"] = "future_mode");
        (BridgeResponseDto unsupportedMiss, _) = await TestSaveBuilder.RunAsync(unsupportedReq);
        Assert.Equal("CapabilityMiss", unsupportedMiss.Outcome);
        Assert.Equal("non_standard_mode", unsupportedMiss.FailureCode);
    }

    [Theory]
    [InlineData(0, "NIBBITS_WEAK", RoomType.Monster, MapPointType.Monster, false, false, false, false, false)]
    [InlineData(0, "MAWLER_NORMAL", RoomType.Monster, MapPointType.Monster, false, false, false, false, false)]
    [InlineData(1, "MAWLER_NORMAL", RoomType.Monster, MapPointType.Monster, true, false, true, false, false)]
    [InlineData(2, "MAWLER_NORMAL", RoomType.Monster, MapPointType.Monster, false, true, true, true, true)]
    [InlineData(0, "BYRDONIS_ELITE", RoomType.Elite, MapPointType.Elite, false, false, false, false, false)]
    [InlineData(0, "CEREMONIAL_BEAST_BOSS", RoomType.Boss, MapPointType.Boss, false, false, true, false, false)]
    [InlineData(1, "VANTOM_BOSS", RoomType.Boss, MapPointType.Boss, true, true, true, false, true)]
    [InlineData(2, "VANTOM_BOSS", RoomType.Boss, MapPointType.Boss, false, false, false, false, false)]
    public async Task PotionRewardOutlook_MatchesBlitzRewardsSetParityAcrossActsAndRelics(
        int actIndex,
        string encounterId,
        RoomType roomType,
        MapPointType mapPointType,
        bool withFresnel,
        bool withGlitter,
        bool withWingCharm,
        bool withPrismaticGem,
        bool withDingyRug)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(
            seed: $"BLITZ-OUTLOOK-{actIndex}-{encounterId}-{withWingCharm}-{withPrismaticGem}",
            ascension: 5,
            actIndex: actIndex,
            gameMode: GameMode.Blitz);
        Player player = run.Players[0];

        if (withFresnel) await ObtainRelicAsync<FresnelLens>(player);
        if (withGlitter) await ObtainRelicAsync<Glitter>(player);
        if (withWingCharm) await ObtainRelicAsync<WingCharm>(player);
        if (withPrismaticGem) await ObtainRelicAsync<PrismaticGem>(player);
        if (withDingyRug) await ObtainRelicAsync<DingyRug>(player);

        EncounterCatalog.TryGet(encounterId, out EncounterCatalogEntry entry);
        MapPoint target = run.Map.GetAllMapPoints().Last(p => p.PointType == mapPointType || (mapPointType == MapPointType.Boss));
        MapCoord coord = mapPointType == MapPointType.Boss ? run.Map.BossMapPoint.coord : target.coord;

        var openingRunner = new ForecastOpeningRunner(CancellationToken.None);
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, roomType, coord.row + 1, coord.col, mapPointType, false),
            openingRunner,
            encounterMonsterSeedOverride: null);

        CombatState combatState = entered.Room.Engine.State;
        PotionRewardOutlook outlook = PotionRewardOutlook.Capture(combatState);
        Assert.True(outlook.Enabled);

        if (roomType == RoomType.Boss && actIndex >= 2)
        {
            Assert.Equal(PotionRewardForecast.NoRewards, outlook.Forecast);
            Assert.Equal(0f, outlook.DropChance);
            return;
        }

        if (roomType == RoomType.Elite || entry.Definition.IsWeak)
        {
            Assert.Equal(PotionRewardForecast.NoDrop, outlook.Forecast);
            Assert.Equal(0f, outlook.DropChance);
            RewardsSet weakOrEliteRewards = RewardsSet.GenerateFor(player, roomType, run, encounter: entry.Definition);
            Assert.Null(weakOrEliteRewards.Potion);
            return;
        }

        Assert.Equal(1f, outlook.DropChance);
        Assert.Equal(PotionRewardForecast.Drop, outlook.Forecast);
        RewardsSet actualRewards = RewardsSet.GenerateFor(player, roomType, run, encounter: entry.Definition);
        Assert.NotNull(actualRewards.Potion);
        Assert.Equal(actualRewards.Potion!.Potion!.Id.Entry, outlook.ForecastPotionId);
    }

    [Fact]
    public async Task PotionRewardOutlook_FullBeltCreditAndSozuBlock_WorkInBlitz()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(seed: "BLITZ-FULL-BELT", ascension: 2, actIndex: 0, gameMode: GameMode.Blitz);
        Player player = run.Players[0];
        for (int slot = 0; slot < player.MaxPotionCount; slot++)
        {
            Assert.True(await PotionCmd.TryToProcure((PotionModel)ModelDb.Potion<BlockPotion>().MutableClone(), player));
        }

        MapPoint target = run.Map.GetAllMapPoints().Last(p => p.PointType == MapPointType.Monster);
        EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry);
        await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState state = ((CombatRoom)run.CurrentRoom!).Engine.State;
        PotionRewardOutlook outlook = PotionRewardOutlook.Capture(state);
        Assert.True(outlook.BeltFull);
        Assert.False(outlook.ProcureBlocked);
        Assert.Equal(PotionRewardForecast.Drop, outlook.Forecast);
        Assert.True(outlook.ReplacementHpCredit > 0);

        await RelicCmd.Obtain(ModelDb.Relic<Sozu>(), player);
        PotionRewardOutlook blocked = PotionRewardOutlook.Capture(state);
        Assert.True(blocked.ProcureBlocked);
        Assert.Equal(0, blocked.ReplacementHpCredit);
    }

    [Fact]
    public async Task EndToEndBridge_SupportsBlitzMapInterveningSteps_GremlinMercNoTheft_AndPotionPrediction()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(seed: "BLITZ-E2E-001", ascension: 5, actIndex: 0, gameMode: GameMode.Blitz);
        Player player = run.Players[0];
        Assert.True(await PotionCmd.TryToProcure((PotionModel)ModelDb.Potion<BlockPotion>().MutableClone(), player));
        Assert.True(await PotionCmd.TryToProcure((PotionModel)ModelDb.Potion<FirePotion>().MutableClone(), player));

        // 1) GREMLIN_MERC_NORMAL 在 Blitz 下正常完成战斗且不偷窃金币
        List<MapPoint> path = ((BlitzActMap)run.Map).GetMapPoints();
        run.AddVisitedMapCoord(path[0].coord);
        run.AddVisitedMapCoord(path[1].coord);

        BridgePolicyDto policy = TestSaveBuilder.DefaultPolicy(beamWidth: 16, maxExpandedNodes: 2000) with
        {
            PredictPotionReward = true,
        };

        BridgeRequestDto mercRequest = TestSaveBuilder.CreateRequest(
            run,
            "GREMLIN_MERC_NORMAL",
            path[2].coord,
            roomKind: "Normal",
            mapPointKind: "Normal",
            playerCurrentHpOverride: 60,
            simulationSeed: 7UL,
            policy: policy);

        (BridgeResponseDto mercDto, _) = await TestSaveBuilder.RunAsync(mercRequest);
        Assert.Equal("Succeeded", mercDto.Outcome);
        Assert.NotNull(mercDto.ProjectedHpLoss);
        Assert.Equal(20, await VerifyBlitzGremlinMercLeavesGoldUnchangedAsync());

        // 2) 经过 Shop 中间节点的 Blitz Elite 战前推演（含 PredictPotionReward = true）
        run.AddVisitedMapCoord(path[2].coord);
        var interveningShop = new[]
        {
            new BridgeMapStepDto(new BridgeMapCoordDto(path[3].coord.col, path[3].coord.row), "Shop", "Shop"),
        };

        BridgeRequestDto eliteRequest = TestSaveBuilder.CreateRequest(
            run,
            "BYRDONIS_ELITE",
            path[4].coord,
            roomKind: "Elite",
            mapPointKind: "Elite",
            intervening: interveningShop,
            playerCurrentHpOverride: 60,
            simulationSeed: 11UL,
            policy: policy);

        (BridgeResponseDto eliteDto, _) = await TestSaveBuilder.RunAsync(eliteRequest);
        Assert.Equal("Succeeded", eliteDto.Outcome);
        Assert.NotNull(eliteDto.ProjectedHpLoss);

        // 3) 经过 Treasure + RestSite 中间节点的 Blitz Boss 战前推演
        run.AddVisitedMapCoord(path[3].coord);
        run.AddVisitedMapCoord(path[4].coord);
        var interveningTreasureRest = new[]
        {
            new BridgeMapStepDto(new BridgeMapCoordDto(path[5].coord.col, path[5].coord.row), "Treasure", "Treasure"),
            new BridgeMapStepDto(new BridgeMapCoordDto(path[6].coord.col, path[6].coord.row), "RestSite", "RestSite"),
        };

        BridgeRequestDto bossRequest = TestSaveBuilder.CreateRequest(
            run,
            "CEREMONIAL_BEAST_BOSS",
            path[7].coord,
            roomKind: "Boss",
            mapPointKind: "Boss",
            intervening: interveningTreasureRest,
            playerCurrentHpOverride: 60,
            simulationSeed: 13UL,
            policy: policy);

        (BridgeResponseDto bossDto, _) = await TestSaveBuilder.RunAsync(bossRequest);
        Assert.Equal("Succeeded", bossDto.Outcome);
        Assert.NotNull(bossDto.ProjectedHpLoss);
    }

    private static async Task<int> VerifyBlitzGremlinMercLeavesGoldUnchangedAsync()
    {
        RunState run = TestSaveBuilder.CreateRun(seed: "BLITZ-MERC-CHECK", ascension: 3, gameMode: GameMode.Blitz);
        int goldBefore = run.Players[0].Gold;
        EncounterCatalog.TryGet("GREMLIN_MERC_NORMAL", out EncounterCatalogEntry entry);
        MapPoint monsterPoint = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, monsterPoint.coord.row + 1, monsterPoint.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);
        var state = entered.Room.Engine.State;
        var merc = Assert.IsType<GremlinMerc>(Assert.Single(state.Enemies).Monster);
        var gimme = typeof(GremlinMerc).GetMethod("Gimme", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)gimme.Invoke(merc, [state.Allies])!;
        Assert.Equal(goldBefore, state.Players[0].Gold);
        return (int)state.Enemies[0].Powers.OfType<ThieveryPower>().Single().Amount;
    }

    private static async Task ObtainRelicAsync<TRelic>(Player player) where TRelic : RelicModel
    {
        await RelicCmd.Obtain(ModelDb.Relic<TRelic>(), player);
    }
}
