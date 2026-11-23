using System.Text.Json.Nodes;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed class DreadModeTests
{
    [Theory]
    [InlineData(GameMode.Dread, 0, true, "Succeeded", null, null)]
    [InlineData(GameMode.Dread, 10, true, "Succeeded", null, null)]
    [InlineData(GameMode.Standard, 10, true, "Succeeded", null, null)]
    [InlineData(GameMode.Dread, 10, false, "Failed", null, "ascension")]
    public async Task DreadSaveValidation_HandlesModeAscensionAndRotatingCharacter(
        GameMode gameMode,
        int ascension,
        bool setRotatingFlag,
        string expectedOutcome,
        string? expectedFailureCode,
        string? expectedFieldPath)
    {
        int runAscension = expectedFieldPath == "ascension" ? 10 : ascension;
        RunState run = TestSaveBuilder.CreateRun(
            seed: $"DREAD-VAL-{gameMode}-{ascension}-{setRotatingFlag}",
            ascension: runAscension,
            gameMode: gameMode);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);

        BridgeRequestDto request = TestSaveBuilder.CreateRequest(
            run,
            "NIBBITS_WEAK",
            target.coord,
            mutateSave: root =>
            {
                if (setRotatingFlag)
                {
                    root["players"]![0]!["extra_fields"]!["is_playing_rotating_character"] = true;
                }

                if (expectedFieldPath == "ascension")
                {
                    root["ascension"] = 11;
                }
            });

        (BridgeResponseDto dto, _) = await TestSaveBuilder.RunAsync(request);

        Assert.Equal(expectedOutcome, dto.Outcome);
        Assert.Equal(expectedFailureCode, dto.FailureCode);
        Assert.Equal(expectedFieldPath, dto.FieldPath);
    }

    [Fact]
    public async Task DreadForecastWithDominions_SupportsKnowledgeDemonChoiceInsatiableThreatAndWaterfallExplosive()
    {
        // KnowledgeDemonDominion：开局选择（Sloth / Amnesia）由 ForecastOpeningRunner 分支评估并返回 Succeeded。
        RunState kdRun = TestSaveBuilder.CreateRun(
            seed: "DREAD-KD-CHOICE",
            ascension: 10,
            actIndex: 1,
            gameMode: GameMode.Dread);
        MapPoint kdTarget = TestSaveBuilder.FindFirstUnvisitedPoint(kdRun, MapPointType.Monster);
        BridgeRequestDto kdRequest = TestSaveBuilder.CreateRequest(
            kdRun,
            "SHRINKER_BEETLE_WEAK",
            kdTarget.coord,
            policy: TestSaveBuilder.DefaultPolicy(beamWidth: 8, maxExpandedNodes: 600),
            mutateSave: root =>
            {
                root["acts"]![1]!["rooms"]!["boss_id"] = "ENCOUNTER.KNOWLEDGE_DEMON_BOSS";
                root["acts"]![1]!["rooms"]!["second_boss_id"] = null;
            });

        (BridgeResponseDto kdDto, _) = await TestSaveBuilder.RunAsync(kdRequest);
        Assert.Equal("Succeeded", kdDto.Outcome);

        // InsatiableDominion：StateEvaluator 投影每回合末 2 + TurnNumber 的无加成效伤害。
        RunState insRun = TestSaveBuilder.CreateRun(
            seed: "DREAD-INSATIABLE-THREAT",
            ascension: 10,
            actIndex: 1,
            gameMode: GameMode.Dread);
        MapPoint insTarget = TestSaveBuilder.FindFirstUnvisitedPoint(insRun, MapPointType.Monster);
        BridgeRequestDto insRequest = TestSaveBuilder.CreateRequest(
            insRun,
            "SHRINKER_BEETLE_WEAK",
            insTarget.coord,
            playerCurrentHpOverride: 10,
            mutateSave: root =>
            {
                root["acts"]![1]!["rooms"]!["boss_id"] = "ENCOUNTER.THE_INSATIABLE_BOSS";
                root["acts"]![1]!["rooms"]!["second_boss_id"] = null;
            });

        RunState restoredInsRun = BridgeSaveRestore.Restore(insRequest);
        restoredInsRun.ApplyPlayerCurrentHpOverride(10);
        Assert.True(EncounterCatalog.TryGet("SHRINKER_BEETLE_WEAK", out EncounterCatalogEntry shrinkerEntry));
        ForecastRoomEntryResult insEntered = await restoredInsRun.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                shrinkerEntry.Definition,
                RoomType.Monster,
                insTarget.coord.row + 1,
                insTarget.coord.col,
                MapPointType.Monster,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);
        CombatState insCombat = insEntered.Room.Engine.State;
        Assert.True(insCombat.Players[0].Creature.HasPower<InsatiableDominionPower>());
        var evaluator = new StateEvaluator(insCombat, CombatPolicy.Default);
        StateEvaluator.ThreatProjection projection = evaluator.ProjectHpAfterThreat(insCombat);
        Assert.Equal(7, projection.Hp);

        BridgeRequestDto lethalInsReq = TestSaveBuilder.CreateRequest(
            insRun,
            "SHRINKER_BEETLE_WEAK",
            insTarget.coord,
            playerCurrentHpOverride: 2,
            mutateSave: root =>
            {
                root["acts"]![1]!["rooms"]!["boss_id"] = "ENCOUNTER.THE_INSATIABLE_BOSS";
                root["acts"]![1]!["rooms"]!["second_boss_id"] = null;
                root["players"]![0]!["deck"] = new JsonArray
                {
                    new JsonObject { ["id"] = "CARD.ASCENDERS_BANE", ["floor_added_to_deck"] = 1 },
                };
            });
        (BridgeResponseDto lethalInsDto, _) = await TestSaveBuilder.RunAsync(lethalInsReq);
        Assert.Equal("Succeeded", lethalInsDto.Outcome);
        Assert.True(lethalInsDto.OnlyDeathRoutes);
        Assert.Equal(0, lethalInsDto.FinalHp);

        // WaterfallGiantDominion：桥接重建为敌人赋予 ExplosivePower。
        TestSaveBuilder.EnsurePrewarmed();
        var wfRun = new RunState(
            "DREAD-WATERFALL-BRIDGE",
            [new Underdocks(), new Hive(), new Glory()],
            10,
            GameMode.Dread);
        var wfPlayer = Sts2Sim.Core.Entities.Players.Player.CreateForNewRun(
            Sts2Sim.Core.Models.ModelDb.GetById<Sts2Sim.Core.Models.CharacterModel>(
                Sts2Sim.Core.Models.ModelId.Deserialize("CHARACTER.SILENT")),
            wfRun);
        wfRun.AddPlayer(wfPlayer);
        _ = wfRun.CurrentAncientEventType;
        MapPoint wfTarget = TestSaveBuilder.FindFirstUnvisitedPoint(wfRun, MapPointType.Monster);
        BridgeRequestDto wfReq = TestSaveBuilder.CreateRequest(
            wfRun,
            "SHRINKER_BEETLE_WEAK",
            wfTarget.coord,
            mutateSave: root =>
            {
                root["acts"]![0]!["rooms"]!["boss_id"] = "ENCOUNTER.WATERFALL_GIANT_BOSS";
            });
        RunState restoredWfRun = BridgeSaveRestore.Restore(wfReq);
        ForecastRoomEntryResult wfEntered = await restoredWfRun.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                shrinkerEntry.Definition,
                RoomType.Monster,
                wfTarget.coord.row + 1,
                wfTarget.coord.col,
                MapPointType.Monster,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);
        Creature wfEnemy = Assert.Single(wfEntered.Room.Engine.State.Enemies);
        Assert.Equal(4, Assert.IsType<ExplosivePower>(wfEnemy.GetPower<ExplosivePower>()).Amount);
        (BridgeResponseDto wfDto, _) = await TestSaveBuilder.RunAsync(wfReq);
        Assert.Equal("Succeeded", wfDto.Outcome);
    }

    [Fact]
    public async Task DreadBossEntry_ClearsDominionAndActivatesFourBodyTheKinVariant()
    {
        RunState run = TestSaveBuilder.CreateRun(
            seed: "DREAD-BOSS-KIN",
            ascension: 10,
            gameMode: GameMode.Dread);
        MapCoord bossCoord = run.Map.BossMapPoint.coord;
        BridgeRequestDto req = TestSaveBuilder.CreateRequest(
            run,
            "THE_KIN_BOSS",
            bossCoord,
            roomKind: "Boss",
            mapPointKind: "Boss",
            mutateSave: root =>
            {
                root["acts"]![0]!["rooms"]!["boss_id"] = "ENCOUNTER.THE_KIN_BOSS";
            });

        RunState restored = BridgeSaveRestore.Restore(req);
        Assert.NotNull(restored.Dominion);

        Assert.True(EncounterCatalog.TryGet("THE_KIN_BOSS", out EncounterCatalogEntry kinEntry));
        ForecastRoomEntryResult entered = await restored.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                kinEntry.Definition,
                RoomType.Boss,
                bossCoord.row + 1,
                bossCoord.col,
                MapPointType.Boss,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);
        CombatState combat = entered.Room.Engine.State;
        Assert.Null(restored.Dominion);
        Assert.Null(combat.Dominion);
        Assert.False(combat.Players[0].Creature.HasPower<WeakPower>());
        Assert.False(combat.Players[0].Creature.HasPower<FrailPower>());
        Assert.Equal(3, combat.Enemies.Count(e => e.Monster is KinFollower));
        Assert.Single(combat.Enemies, e => e.Monster is KinPriest);

        BridgeRequestDto quickSearchReq = req with
        {
            Policy = TestSaveBuilder.DefaultPolicy(beamWidth: 2, maxExpandedNodes: 40),
        };
        (BridgeResponseDto dto, _) = await TestSaveBuilder.RunAsync(quickSearchReq);
        Assert.Equal("Succeeded", dto.Outcome);
    }

    [Fact]
    public async Task ForecastRetentionKey_DistinguishesGameModeAndDominion()
    {
        RunState stdRun = TestSaveBuilder.CreateRun(seed: "RETENTION-MODE-KEY", ascension: 10, gameMode: GameMode.Standard);
        RunState dreadRun = TestSaveBuilder.CreateRun(seed: "RETENTION-MODE-KEY", ascension: 10, gameMode: GameMode.Dread);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(stdRun, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("SHRINKER_BEETLE_WEAK", out EncounterCatalogEntry entry));

        ForecastRoomEntryResult stdEntered = await stdRun.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);
        ForecastRoomEntryResult dreadEntered = await dreadRun.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState stdCombat = stdEntered.Room.Engine.State;
        CombatState dreadCombatA = dreadEntered.Room.Engine.State.Clone();
        dreadCombatA.SetDominion((Sts2Sim.Core.Models.Dread.DominionModel)
            Sts2Sim.Core.Models.ModelDb.Dominion<Sts2Sim.Core.Models.Dread.Dominions.KaiserCrabDominion>().MutableClone());
        CombatState dreadCombatB = dreadCombatA.Clone();
        dreadCombatB.SetDominion((Sts2Sim.Core.Models.Dread.DominionModel)
            Sts2Sim.Core.Models.ModelDb.Dominion<Sts2Sim.Core.Models.Dread.Dominions.CeremonialBeastDominion>().MutableClone());

        SearchNode stdNode = SearchNode.Create(stdCombat, null, null, new StateEvaluator(stdCombat, CombatPolicy.Default));
        SearchNode dreadNodeA = SearchNode.Create(dreadCombatA, null, null, new StateEvaluator(dreadCombatA, CombatPolicy.Default));
        SearchNode dreadNodeB = SearchNode.Create(dreadCombatB, null, null, new StateEvaluator(dreadCombatB, CombatPolicy.Default));

        Assert.NotEqual(ForecastRetentionKey.Compute(stdNode), ForecastRetentionKey.Compute(dreadNodeA));
        Assert.NotEqual(ForecastRetentionKey.Compute(dreadNodeA), ForecastRetentionKey.Compute(dreadNodeB));
    }
}
