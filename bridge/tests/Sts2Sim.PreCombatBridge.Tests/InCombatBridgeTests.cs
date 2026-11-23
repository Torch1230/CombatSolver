using System.Reflection;
using System.Text.Json;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Orbs;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Runs.Transplant;
using Sts2Sim.Core.Saves;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed class InCombatBridgeTests
{
    [Fact]
    public void PrivateStateCoverageAudit_CoversEveryContributorFieldInRegistry()
    {
        FieldInfo contributorField = typeof(CombatPrivateStateRegistry).GetField(
            "ContributorFields",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing CombatPrivateStateRegistry.ContributorFields.");
        var contributorFields = (HashSet<string>)contributorField.GetValue(null)!;
        IReadOnlySet<string> handled = LiveCombatPrivateStateMapper.GetHandledAndNonCombatFieldsForAudit();

        string[] missing = contributorFields
            .Where(field => !handled.Contains(field))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"LiveCombatPrivateStateMapper is missing ContributorFields: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task MidCombatTurn2RoundTrip_RestoresExactCombatStateDigest()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        Player player = run.Players[0];
        RelicModel happyFlower = (RelicModel)ModelDb.Relic<HappyFlower>().MutableClone();
        happyFlower.AssignOwner(player);
        player.AddRelicInternal(happyFlower);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));

        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                RoomType.Monster,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatRoom room = entered.Room;
        CombatState state = room.Engine.State;
        PlayerCombatState pcs = player.PlayerCombatState!;

        CardModel? strike = pcs.Hand.Cards.FirstOrDefault(c => c.CanPlay(out _) && c.TargetType == TargetType.AnyEnemy);
        Assert.NotNull(strike);
        Creature enemyTarget = state.HittableEnemies[0];
        Assert.NotNull(await room.Engine.PlayCardWithResultAsync(player, strike!, enemyTarget));
        await room.Engine.EndPlayerTurnAsync();

        Assert.Equal(2, pcs.TurnNumber);
        Assert.Equal(CombatSide.Player, state.CurrentSide);

        CombatStateDescriptionDigest expectedDigest = ComputeExactDigest(state);
        InCombatLiveStateDto liveDto = ExportLiveState(room, "NIBBITS_WEAK");

        RunState restoredRun = BridgeSaveRestore.RestoreLive(saveJson);
        LiveCombatSnapshotSpec liveSpec = LiveCombatTransplantMapper.Map(liveDto);
        CombatRoom restoredRoom = CombatState.RestoreLiveCombat(restoredRun, liveSpec);

        CombatStateDescriptionDigest actualDigest = ComputeExactDigest(restoredRoom.Engine.State);
        Assert.Equal(expectedDigest, actualDigest);
    }

    [Theory]
    [InlineData("mismatch_hp", "live_state_mismatch")]
    [InlineData("unmapped_relic_field", "live_private_state_unmapped")]
    [InlineData("unmapped_monster_state", "live_monster_state_unmapped")]
    public async Task RunInCombatJsonAsync_FailsClosedAsCapabilityMiss(string faultKind, string expectedFailureCode)
    {
        (string saveJson, InCombatLiveStateDto baseDto, _) = await BuildTurn1CombatAsync("NIBBITS_WEAK", withHappyFlower: true);
        InCombatLiveStateDto faultedDto = faultKind switch
        {
            "mismatch_hp" => baseDto with
            {
                Verification = baseDto.Verification with
                {
                    PlayerHp = baseDto.Verification.PlayerHp - 1,
                },
            },
            "unmapped_relic_field" => baseDto with
            {
                Relics = baseDto.Relics
                    .Select(r => r.RelicId == "RELIC.HAPPY_FLOWER"
                        ? r with
                        {
                            PrivateFields = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal)
                            {
                                ["HappyFlower._turnCounter"] = new("Int", IntValue: 1),
                                ["HappyFlower._unknownField"] = new("Int", IntValue: 99),
                            },
                        }
                        : r)
                    .ToArray(),
            },
            "unmapped_monster_state" => baseDto with
            {
                Creatures = baseDto.Creatures
                    .Select(c => !c.IsPlayer && c.Monster is not null
                        ? c with
                        {
                            Monster = c.Monster with
                            {
                                CurrentStateId = "NONEXISTENT_STATE",
                            },
                        }
                        : c)
                    .ToArray(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(faultKind)),
        };

        if (faultKind == "unmapped_relic_field")
        {
            // Keep verification consistent and ensure the unknown field triggers in LiveCombatPrivateStateMapper
            // via an unmapped relic type when a custom field is present cleanly if HappyFlower reads by key:
            faultedDto = faultedDto with
            {
                Relics = faultedDto.Relics
                    .Select(r => r.RelicId == "RELIC.BURNING_BLOOD"
                        ? r with
                        {
                            PrivateFields = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal)
                            {
                                ["BurningBlood._unknownRuntime"] = new("Int", IntValue: 7),
                            },
                        }
                        : r)
                    .ToArray(),
            };
        }

        InCombatBridgeRequestDto request = CreateInCombatRequest(saveJson, faultedDto, maxDop: 1);
        InCombatBridgeResponseDto response = await RunWireAsync(request);

        Assert.Equal("CapabilityMiss", response.Outcome);
        Assert.Equal(expectedFailureCode, response.FailureCode);
        Assert.Null(response.Result);
        Assert.Null(response.BestSoFar);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RunInCombatJsonAsync_Turn1AndTurn2_SucceedAndProduceExecutableRoutes(int requestedDop)
    {
        (string saveJsonTurn1, InCombatLiveStateDto liveTurn1, _) = await BuildTurn1CombatAsync("NIBBITS_WEAK", withHappyFlower: true);
        InCombatBridgeRequestDto req1 = CreateInCombatRequest(saveJsonTurn1, liveTurn1, maxDop: requestedDop, softTimeBudgetMs: 8000);
        InCombatBridgeResponseDto resp1 = await RunWireAsync(req1);

        int expectedDop = Math.Clamp(requestedDop, 1, Math.Max(1, Environment.ProcessorCount));
        Assert.Equal("Succeeded", resp1.Outcome);
        Assert.Equal(expectedDop, resp1.EffectiveDegreeOfParallelism);
        Assert.NotNull(resp1.Result);
        Assert.NotEmpty(resp1.Result!.Actions);
        Assert.NotEmpty(resp1.Result.TurnOutcomes);
        Assert.Equal(1, resp1.Result.Actions[0].Turn);
        Assert.True(resp1.Result.ExpandedNodes > 0);
        Assert.True(resp1.Result.Terminal.ProjectedPlayerHp > 0);

        (string saveJsonTurn2, InCombatLiveStateDto liveTurn2, _) = await BuildTurn2CombatAsync("NIBBITS_WEAK");
        InCombatBridgeRequestDto req2 = CreateInCombatRequest(saveJsonTurn2, liveTurn2, maxDop: requestedDop, softTimeBudgetMs: 8000);
        InCombatBridgeResponseDto resp2 = await RunWireAsync(req2);

        Assert.Equal("Succeeded", resp2.Outcome);
        Assert.Equal(expectedDop, resp2.EffectiveDegreeOfParallelism);
        Assert.NotNull(resp2.Result);
        Assert.NotEmpty(resp2.Result!.Actions);
        Assert.All(resp2.Result.Actions, action => Assert.True(action.Turn >= 2));
        Assert.True(resp2.Result.ExpandedNodes > 0);
    }

    [Fact]
    public async Task RunInCombatJsonAsync_StreamsProgressAndReturnsBestSoFarOnCancel()
    {
        (string saveJson, InCombatLiveStateDto liveDto, _) = await BuildTurn1CombatAsync("PHROG_PARASITE_ELITE", withHappyFlower: true);
        InCombatPolicyDto policy = CreateDefaultPolicy(
            beamWidth: 48,
            maxExpandedNodes: 200_000,
            useBeamWidthPortfolio: true,
            softTimeBudgetMs: 15_000);
        InCombatBridgeRequestDto req = CreateInCombatRequest(
            saveJson,
            liveDto,
            maxDop: 2,
            searchBudgetMs: 15_000,
            overallTimeoutMs: 25_000,
            policy: policy);

        using var cts = new CancellationTokenSource();
        var progressUpdates = new List<InCombatProgressDto>();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(req, BridgeAbi.JsonOptions);

        byte[] responseBytes = await BridgeEntry.RunInCombatJsonAsync(
            payload,
            bytes =>
            {
                InCombatProgressDto? update = JsonSerializer.Deserialize<InCombatProgressDto>(bytes, BridgeAbi.JsonOptions);
                if (update is not null)
                {
                    lock (progressUpdates)
                    {
                        progressUpdates.Add(update);
                        if (update.ExpandedNodes > 0 && update.CurrentBest is not null)
                            cts.Cancel();
                    }
                }
            },
            cts.Token);

        InCombatBridgeResponseDto? response = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(responseBytes, BridgeAbi.JsonOptions);
        Assert.NotNull(response);
        Assert.NotEmpty(progressUpdates);
        Assert.Contains(progressUpdates, u => u.ExpandedNodes > 0);
        Assert.True(
            response!.Outcome is "Cancelled" or "Succeeded",
            $"Expected Cancelled or Succeeded, got {response.Outcome} ({response.Error}).");
        if (response.Outcome == "Cancelled")
        {
            Assert.NotNull(response.BestSoFar);
            Assert.NotEmpty(response.BestSoFar!.Actions);
        }
        else
        {
            Assert.NotNull(response.Result);
            Assert.NotEmpty(response.Result!.Actions);
        }
    }

    [Fact]
    public async Task RunInCombatJsonAsync_DeckWithSpoilsMapWithoutSidecar_Succeeds()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        Player player = run.Players[0];
        var spoilsMap = (SpoilsMap)ModelDb.Card<SpoilsMap>().MutableClone();
        spoilsMap.AssignOwner(player);
        player.Deck.AddInternal(spoilsMap);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        Assert.DoesNotContain("spoils_coord", saveJson, StringComparison.Ordinal);

        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
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

        InCombatLiveStateDto liveDto = ExportLiveState(entered.Room, "NIBBITS_WEAK");
        InCombatBridgeRequestDto req = CreateInCombatRequest(saveJson, liveDto, maxDop: 1);
        InCombatBridgeResponseDto resp = await RunWireAsync(req);

        Assert.Equal("Succeeded", resp.Outcome);
        Assert.NotNull(resp.Result);
        Assert.NotEmpty(resp.Result!.Actions);
    }

    [Fact]
    public async Task RunInCombatJsonAsync_CompletedSpoilsMapAct2SavedMap_Succeeds()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        Player player = run.Players[0];
        var spoilsMap = (SpoilsMap)ModelDb.Card<SpoilsMap>().MutableClone();
        spoilsMap.AssignOwner(player);
        player.Deck.AddInternal(spoilsMap);

        run.AdvanceToNextAct();
        Assert.IsType<SpoilsActMap>(run.Map);

        // 模拟进入战斗前已完成藏宝图任务并将其从牌组移除，而当前幕 saved_map 仍为 SpoilsActMap 拓扑。
        player.Deck.RemoveInternal(spoilsMap);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);

        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
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

        InCombatLiveStateDto liveDto = ExportLiveState(entered.Room, "NIBBITS_WEAK");
        InCombatBridgeRequestDto req = CreateInCombatRequest(saveJson, liveDto, maxDop: 1);
        InCombatBridgeResponseDto resp = await RunWireAsync(req);

        Assert.Equal("Succeeded", resp.Outcome);
        Assert.NotNull(resp.Result);
        Assert.NotEmpty(resp.Result!.Actions);
    }

    [Fact]
    public async Task RunInCombatJsonAsync_UnplayableAndXCostCardsInHand_Succeeds()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 10);
        Player player = run.Players[0];

        CardModel AddDeckCard<TCard>() where TCard : CardModel
        {
            var card = (CardModel)ModelDb.Card<TCard>().MutableClone();
            card.AssignOwner(player);
            player.Deck.AddInternal(card);
            return card;
        }

        _ = AddDeckCard<Sts2Sim.Core.Models.Cards.AscendersBane>();
        _ = AddDeckCard<SpoilsMap>();
        _ = AddDeckCard<Sts2Sim.Core.Models.Cards.Whirlwind>();

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);

        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
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

        PlayerCombatState pcs = player.PlayerCombatState!;
        void EnsureInHand<TCard>() where TCard : CardModel
        {
            if (pcs.Hand.Cards.Any(c => c is TCard))
                return;
            var handCard = (CardModel)ModelDb.Card<TCard>().MutableClone();
            handCard.AssignOwner(player);
            pcs.Hand.AddInternal(handCard);
        }

        EnsureInHand<Sts2Sim.Core.Models.Cards.AscendersBane>();
        EnsureInHand<SpoilsMap>();
        EnsureInHand<Sts2Sim.Core.Models.Cards.Burn>();
        EnsureInHand<Sts2Sim.Core.Models.Cards.Whirlwind>();

        InCombatLiveStateDto liveDto = ExportLiveState(entered.Room, "NIBBITS_WEAK");
        InCombatBridgeRequestDto req = CreateInCombatRequest(saveJson, liveDto, maxDop: 1);
        InCombatBridgeResponseDto resp = await RunWireAsync(req);

        Assert.Equal("Succeeded", resp.Outcome);
        Assert.NotNull(resp.Result);
        Assert.NotEmpty(resp.Result!.Actions);
    }

    [Fact]
    public async Task RunInCombatJsonAsync_CcccomboBadgeUnlockedInSave_Succeeds()
    {
        (string baseSaveJson, InCombatLiveStateDto liveDto, _) = await BuildTurn1CombatAsync("NIBBITS_WEAK");
        var rootNode = System.Text.Json.Nodes.JsonNode.Parse(baseSaveJson)!.AsObject();
        rootNode["players"]![0]!["extra_fields"]!["ccccombo_badge_unlocked"] = true;
        string mutatedSaveJson = rootNode.ToJsonString();

        InCombatBridgeRequestDto req = CreateInCombatRequest(mutatedSaveJson, liveDto, maxDop: 1);
        InCombatBridgeResponseDto resp = await RunWireAsync(req);

        Assert.Equal("Succeeded", resp.Outcome);
        Assert.NotNull(resp.Result);
        Assert.NotEmpty(resp.Result!.Actions);
    }

    [Fact]
    public async Task RunInCombatJsonAsync_TestSubjectKillsInSave_SucceedsAndRejectsNegative()
    {
        (string baseSaveJson, InCombatLiveStateDto liveDto, _) = await BuildTurn1CombatAsync("NIBBITS_WEAK");
        var rootNode = System.Text.Json.Nodes.JsonNode.Parse(baseSaveJson)!.AsObject();
        rootNode["extra_fields"]!["test_subject_kills"] = 1;
        string mutatedSaveJson = rootNode.ToJsonString();

        RunState restored = BridgeSaveRestore.RestoreLive(mutatedSaveJson);
        Assert.NotNull(restored);

        InCombatBridgeRequestDto req = CreateInCombatRequest(mutatedSaveJson, liveDto, maxDop: 1);
        InCombatBridgeResponseDto resp = await RunWireAsync(req);

        Assert.Equal("Succeeded", resp.Outcome);
        Assert.NotNull(resp.Result);
        Assert.NotEmpty(resp.Result!.Actions);

        rootNode["extra_fields"]!["test_subject_kills"] = -1;
        string negativeSaveJson = rootNode.ToJsonString();
        BridgeRequestFailedException ex = Assert.Throws<BridgeRequestFailedException>(
            () => BridgeSaveRestore.RestoreLive(negativeSaveJson));
        Assert.Equal("extra_fields.test_subject_kills", ex.FieldPath);
    }

    [Fact]
    public async Task RunJsonAsync_TestSubjectKillsInSave_SucceedsAndRejectsNegativeInPreCombat()
    {
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgeRequestDto req = TestSaveBuilder.CreateRequest(
            run,
            "NIBBITS_WEAK",
            target.coord,
            mutateSave: root => root["extra_fields"]!["test_subject_kills"] = 2);

        (BridgeResponseDto dto, _) = await TestSaveBuilder.RunAsync(req);

        Assert.Equal("Succeeded", dto.Outcome);
        Assert.NotNull(dto.ProjectedHpLoss);

        BridgeRequestDto negativeReq = TestSaveBuilder.CreateRequest(
            run,
            "NIBBITS_WEAK",
            target.coord,
            mutateSave: root => root["extra_fields"]!["test_subject_kills"] = -1);

        (BridgeResponseDto negativeDto, _) = await TestSaveBuilder.RunAsync(negativeReq);

        Assert.Equal("Failed", negativeDto.Outcome);
        Assert.Equal("extra_fields.test_subject_kills", negativeDto.FieldPath);
    }

    [Fact]
    public async Task RestoreLiveCombat_RelicStackCountMismatch_FailsClosed()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        var circlet = (RelicModel)ModelDb.Relic<Circlet>().MutableClone();
        circlet.AssignOwner(run.Players[0]);
        while (circlet.StackCount < 3)
            circlet.IncrementStackCount();
        run.Players[0].AddRelicInternal(circlet);

        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
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

        InCombatLiveStateDto liveDto = ExportLiveState(entered.Room, "NIBBITS_WEAK");
        InCombatLiveStateDto lowerLiveStack = liveDto with
        {
            Relics = liveDto.Relics
                .Select(r => r.RelicId == "RELIC.CIRCLET" ? r with { StackCount = 1 } : r)
                .ToArray(),
        };

        RunState stackedRun = BridgeSaveRestore.RestoreLive(saveJson);
        RelicModel restoredCirclet = Assert.Single(stackedRun.Players[0].Relics, r => r.Id.ToString() == "RELIC.CIRCLET");
        while (restoredCirclet.StackCount < 3)
            restoredCirclet.IncrementStackCount();

        ForecastCapabilityMissException mismatchEx = Assert.Throws<ForecastCapabilityMissException>(
            () => CombatState.RestoreLiveCombat(stackedRun, LiveCombatTransplantMapper.Map(lowerLiveStack)));
        Assert.Equal("live_state_mismatch", mismatchEx.FailureCode);
        Assert.Contains(".stackCount", mismatchEx.FieldPath, StringComparison.Ordinal);

        InCombatLiveStateDto zeroLiveStack = liveDto with
        {
            Relics = liveDto.Relics
                .Select(r => r.RelicId == "RELIC.CIRCLET" ? r with { StackCount = 0 } : r)
                .ToArray(),
        };

        InCombatBridgeResponseDto zeroResp = await RunWireAsync(
            CreateInCombatRequest(saveJson, zeroLiveStack, maxDop: 1));
        Assert.Equal("CapabilityMiss", zeroResp.Outcome);
        Assert.Equal("live_state_mismatch", zeroResp.FailureCode);
        Assert.Contains(".stackCount", zeroResp.FieldPath, StringComparison.Ordinal);

        InCombatRelicDto starterRelic = Assert.Single(liveDto.Relics, r => r.RelicId == "RELIC.BURNING_BLOOD");
        InCombatLiveStateDto nonStackableOverflow = liveDto with
        {
            Relics = liveDto.Relics
                .Select(r => r.RelicId == starterRelic.RelicId ? r with { StackCount = 2 } : r)
                .ToArray(),
        };

        InCombatBridgeResponseDto nonStackableResp = await RunWireAsync(
            CreateInCombatRequest(saveJson, nonStackableOverflow, maxDop: 1));
        Assert.Equal("CapabilityMiss", nonStackableResp.Outcome);
        Assert.Equal("live_state_mismatch", nonStackableResp.FailureCode);
        Assert.Contains(".stackCount", nonStackableResp.FieldPath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(LagavulinMatriarch), "SLASH_MOVE")]
    [InlineData(typeof(SlumberingBeetle), "ROLL_OUT_MOVE")]
    [InlineData(typeof(Tunneler), "BITE_MOVE")]
    public void RestoreFromLiveSnapshot_TransientStunnedOnWakeUpAndBurrowMonsters_Succeeds(
        Type monsterType,
        string followUpStateId)
    {
        TestSaveBuilder.EnsurePrewarmed();
        var monster = (MonsterModel)ModelDb.Get(monsterType).MutableClone();
        var creature = new Creature(monster, CombatSide.Enemy);
        monster.AssignCreature(creature);
        monster.SetUpForCombat();

        MoveState? nextMove = monster.MoveStateMachine!.RestoreFromLiveSnapshot(
            currentStateId: "STUNNED",
            nextMoveId: "STUNNED",
            performedFirstMove: true,
            stateLogIds: ["STUNNED"],
            transientFollowUpStateId: followUpStateId,
            nextMoveCanTransitionAway: false,
            owner: monster,
            monsterPath: "test");

        Assert.NotNull(nextMove);
        Assert.Equal("STUNNED", nextMove!.Id);
        Assert.NotNull(nextMove.CombatCloneFactory);
    }

    [Fact]
    public async Task RunInCombatJsonAsync_DeckAndRelicsWithNativeSavedProperties_Succeeds()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.DEFECT", ascension: 10);
        Player player = run.Players[0];

        var ga = (GeneticAlgorithm)ModelDb.Card<GeneticAlgorithm>().MutableClone();
        ga.AssignOwner(player);
        typeof(GeneticAlgorithm).GetField("_currentBlock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ga, 4);
        typeof(GeneticAlgorithm).GetField("_increasedBlock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ga, 3);
        player.Deck.AddInternal(ga);

        var scythe = (TheScythe)ModelDb.Card<TheScythe>().MutableClone();
        scythe.AssignOwner(player);
        typeof(TheScythe).GetField("_increasedDamage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scythe, 5);
        player.Deck.AddInternal(scythe);

        var tooth = (ArchaicTooth)ModelDb.Relic<ArchaicTooth>().MutableClone();
        tooth.AssignOwner(player);
        player.AddRelicInternal(tooth);

        var byrdine = (Byrdine)ModelDb.Relic<Byrdine>().MutableClone();
        byrdine.AssignOwner(player);
        player.AddRelicInternal(byrdine);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run, mutate: root =>
        {
            var relicsArr = root["players"]![0]!["relics"]!.AsArray();
            foreach (var rNode in relicsArr)
            {
                var rObj = rNode!.AsObject();
                string? id = rObj["id"]?.GetValue<string>();
                if (id == "RELIC.ARCHAIC_TOOTH")
                {
                    rObj["props"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["cards"] = new System.Text.Json.Nodes.JsonArray
                        {
                            new System.Text.Json.Nodes.JsonObject
                            {
                                ["name"] = "StarterCard",
                                ["value"] = new System.Text.Json.Nodes.JsonObject
                                {
                                    ["id"] = "CARD.DUALCAST",
                                    ["current_upgrade_level"] = 0,
                                },
                            },
                            new System.Text.Json.Nodes.JsonObject
                            {
                                ["name"] = "AncientCard",
                                ["value"] = new System.Text.Json.Nodes.JsonObject
                                {
                                    ["id"] = "CARD.QUADCAST",
                                    ["current_upgrade_level"] = 0,
                                },
                            },
                        },
                    };
                }
                else if (id == "RELIC.BYRDINE")
                {
                    rObj["props"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["strings"] = new System.Text.Json.Nodes.JsonArray
                        {
                            new System.Text.Json.Nodes.JsonObject
                            {
                                ["name"] = "Hair",
                                ["value"] = "hair/v2",
                            },
                        },
                    };
                }
            }
        });

        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
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

        InCombatLiveStateDto liveDto = ExportLiveState(entered.Room, "NIBBITS_WEAK");
        InCombatBridgeRequestDto req = CreateInCombatRequest(saveJson, liveDto, maxDop: 1);
        InCombatBridgeResponseDto resp = await RunWireAsync(req);

        Assert.Equal("Succeeded", resp.Outcome);
        Assert.NotNull(resp.Result);
        Assert.NotEmpty(resp.Result!.Actions);
    }

    [Fact]
    public void RestoreFromLiveSnapshot_UnsetMoveOnOstyPet_ReturnsNullNextMove()
    {
        TestSaveBuilder.EnsurePrewarmed();
        var osty = (MonsterModel)ModelDb.Monster<Osty>().MutableClone();
        var creature = new Creature(osty, CombatSide.Player);
        osty.AssignCreature(creature);
        osty.SetUpForCombat();

        MoveState? nextMove = osty.MoveStateMachine!.RestoreFromLiveSnapshot(
            currentStateId: "NOTHING_MOVE",
            nextMoveId: "UNSET_MOVE",
            performedFirstMove: false,
            stateLogIds: ["NOTHING_MOVE"],
            transientFollowUpStateId: null,
            nextMoveCanTransitionAway: true,
            owner: osty,
            monsterPath: "test");

        Assert.Null(nextMove);
    }

    [Theory]
    [InlineData("enemy_side", "liveState.currentSide")]
    [InlineData("non_play_phase", "liveState.player.phase")]
    [InlineData("performing_move", ".monster.isPerformingMove")]
    public async Task RunInCombatJsonAsync_RejectsNonQuiescentOrNonPlayLiveStates(
        string faultKind,
        string expectedFieldPathSuffix)
    {
        (string saveJson, InCombatLiveStateDto baseDto, _) = await BuildTurn1CombatAsync("NIBBITS_WEAK");
        InCombatLiveStateDto faultedDto = faultKind switch
        {
            "enemy_side" => baseDto with { CurrentSide = CombatSide.Enemy.ToString() },
            "non_play_phase" => baseDto with
            {
                Player = baseDto.Player with { Phase = PlayerTurnPhase.End.ToString() },
            },
            "performing_move" => baseDto with
            {
                Creatures = baseDto.Creatures
                    .Select(c => !c.IsPlayer && c.Monster is not null
                        ? c with { Monster = c.Monster with { IsPerformingMove = true } }
                        : c)
                    .ToArray(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(faultKind)),
        };

        InCombatBridgeRequestDto req = CreateInCombatRequest(saveJson, faultedDto, maxDop: 1);
        InCombatBridgeResponseDto resp = await RunWireAsync(req);

        Assert.Equal("CapabilityMiss", resp.Outcome);
        Assert.Equal("live_combat_precondition", resp.FailureCode);
        Assert.NotNull(resp.FieldPath);
        Assert.EndsWith(expectedFieldPathSuffix, resp.FieldPath, StringComparison.Ordinal);

        LiveCombatSnapshotSpec validSpec = LiveCombatTransplantMapper.Map(baseDto);
        LiveCombatSnapshotSpec directFaultSpec = faultKind switch
        {
            "enemy_side" => validSpec with { CurrentSide = CombatSide.Enemy },
            "non_play_phase" => validSpec with { Player = validSpec.Player with { Phase = PlayerTurnPhase.End } },
            "performing_move" => validSpec with
            {
                Creatures = validSpec.Creatures
                    .Select(c => !c.IsPlayer && c.Monster is not null
                        ? c with { Monster = c.Monster with { IsPerformingMove = true } }
                        : c)
                    .ToArray(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(faultKind)),
        };
        RunState restoredRun = BridgeSaveRestore.RestoreLive(saveJson);
        ForecastCapabilityMissException directEx = Assert.Throws<ForecastCapabilityMissException>(
            () => CombatState.RestoreLiveCombat(restoredRun, directFaultSpec));
        Assert.Equal("live_combat_precondition", directEx.FailureCode);
    }

    [Theory]
    [InlineData("missing_deck_handle", ".deckCardHandle")]
    [InlineData("missing_clone_handle", ".cloneOfHandle")]
    [InlineData("self_cycle", ".cloneOfHandle")]
    [InlineData("multi_node_cycle", ".cloneOfHandle")]
    public async Task RunInCombatJsonAsync_RejectsInvalidCardReferencesAndCloneOfCycles(
        string faultKind,
        string expectedFieldPathSuffix)
    {
        (string saveJson, InCombatLiveStateDto baseDto, _) = await BuildTurn1CombatAsync("NIBBITS_WEAK");
        int missingHandle = baseDto.Cards.Max(c => c.Handle) + 999;
        int firstHandle = baseDto.Player.HandHandles[0];
        int secondHandle = baseDto.Player.HandHandles[1];

        InCombatLiveStateDto faultedDto = baseDto with
        {
            Cards = baseDto.Cards.Select(c => faultKind switch
            {
                "missing_deck_handle" when c.Handle == firstHandle => c with { DeckCardHandle = missingHandle },
                "missing_clone_handle" when c.Handle == firstHandle => c with { CloneOfHandle = missingHandle },
                "self_cycle" when c.Handle == firstHandle => c with { CloneOfHandle = firstHandle },
                "multi_node_cycle" when c.Handle == firstHandle => c with { CloneOfHandle = secondHandle },
                "multi_node_cycle" when c.Handle == secondHandle => c with { CloneOfHandle = firstHandle },
                _ => c,
            }).ToArray(),
        };

        InCombatBridgeRequestDto req = CreateInCombatRequest(saveJson, faultedDto, maxDop: 1);
        InCombatBridgeResponseDto resp = await RunWireAsync(req);

        Assert.Equal("CapabilityMiss", resp.Outcome);
        Assert.Equal("live_state_mismatch", resp.FailureCode);
        Assert.NotNull(resp.FieldPath);
        Assert.EndsWith(expectedFieldPathSuffix, resp.FieldPath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("known_relic", "HappyFlower._unexpectedKey")]
    [InlineData("known_card", "GeneticAlgorithm._unexpectedKey")]
    [InlineData("known_enchantment", "Momentum._unexpectedKey")]
    [InlineData("known_monster_setup", "Nibbit._unexpectedSetup")]
    public async Task RunInCombatJsonAsync_RejectsUnconsumedPrivateFieldOnRecognizedModels(
        string faultKind,
        string expectedExtraKey)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        Player player = run.Players[0];

        if (faultKind == "known_relic")
        {
            var happyFlower = (RelicModel)ModelDb.Relic<HappyFlower>().MutableClone();
            happyFlower.AssignOwner(player);
            player.AddRelicInternal(happyFlower);
        }
        else if (faultKind == "known_card")
        {
            var ga = (GeneticAlgorithm)ModelDb.Card<GeneticAlgorithm>().MutableClone();
            ga.AssignOwner(player);
            player.Deck.AddInternal(ga);
        }
        else if (faultKind == "known_enchantment")
        {
            CardModel strike = player.Deck.Cards.First(c => c is StrikeIronclad);
            var momentum = (Sts2Sim.Core.Models.Enchantments.Momentum)ModelDb.Get(typeof(Sts2Sim.Core.Models.Enchantments.Momentum)).MutableClone();
            momentum.AssignMagnitude(1);
            strike.AttachEnchantment(momentum);
        }

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
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

        InCombatLiveStateDto baseDto = ExportLiveState(entered.Room, "NIBBITS_WEAK");
        InCombatLiveStateDto faultedDto = faultKind switch
        {
            "known_relic" => baseDto with
            {
                Relics = baseDto.Relics
                    .Select(r => r.RelicId == "RELIC.HAPPY_FLOWER"
                        ? r with
                        {
                            PrivateFields = new Dictionary<string, InCombatFieldValueDto>(r.PrivateFields, StringComparer.Ordinal)
                            {
                                [expectedExtraKey] = new("Int", IntValue: 7),
                            },
                        }
                        : r)
                    .ToArray(),
            },
            "known_card" => MutateFirstCard(baseDto, "CARD.GENETIC_ALGORITHM", expectedExtraKey),
            "known_enchantment" => MutateFirstEnchantedCard(baseDto, "ENCHANTMENT.MOMENTUM", expectedExtraKey),
            "known_monster_setup" => MutateFirstMonsterSetup(baseDto, expectedExtraKey),
            _ => throw new ArgumentOutOfRangeException(nameof(faultKind)),
        };

        InCombatBridgeRequestDto req = CreateInCombatRequest(saveJson, faultedDto, maxDop: 1);
        InCombatBridgeResponseDto resp = await RunWireAsync(req);

        Assert.Equal("CapabilityMiss", resp.Outcome);
        Assert.Equal("live_private_state_unmapped", resp.FailureCode);
        Assert.NotNull(resp.FieldPath);
        Assert.EndsWith($".{expectedExtraKey}", resp.FieldPath, StringComparison.Ordinal);

        static InCombatLiveStateDto MutateFirstCard(InCombatLiveStateDto dto, string cardId, string extraKey)
        {
            bool mutated = false;
            return dto with
            {
                Cards = dto.Cards.Select(c =>
                {
                    if (!mutated && c.CardId == cardId)
                    {
                        mutated = true;
                        var nextFields = new Dictionary<string, InCombatFieldValueDto>(c.PrivateFields, StringComparer.Ordinal)
                        {
                            [extraKey] = new("Int", IntValue: 1),
                        };
                        return c with { PrivateFields = nextFields };
                    }
                    return c;
                }).ToArray(),
            };
        }

        static InCombatLiveStateDto MutateFirstEnchantedCard(InCombatLiveStateDto dto, string enchId, string extraKey)
        {
            bool mutated = false;
            return dto with
            {
                Cards = dto.Cards.Select(c =>
                {
                    if (!mutated && c.Enchantment is { } ench && ench.EnchantmentId == enchId)
                    {
                        mutated = true;
                        var nextFields = new Dictionary<string, InCombatFieldValueDto>(ench.PrivateFields, StringComparer.Ordinal)
                        {
                            [extraKey] = new("Int", IntValue: 1),
                        };
                        return c with { Enchantment = ench with { PrivateFields = nextFields } };
                    }
                    return c;
                }).ToArray(),
            };
        }

        static InCombatLiveStateDto MutateFirstMonsterSetup(InCombatLiveStateDto dto, string extraKey)
        {
            bool mutated = false;
            return dto with
            {
                Creatures = dto.Creatures.Select(c =>
                {
                    if (!mutated && !c.IsPlayer && c.Monster is { } m)
                    {
                        mutated = true;
                        var nextSetup = new Dictionary<string, InCombatFieldValueDto>(m.SetupFields, StringComparer.Ordinal)
                        {
                            [extraKey] = new("Bool", BoolValue: true),
                        };
                        return c with { Monster = m with { SetupFields = nextSetup } };
                    }
                    return c;
                }).ToArray(),
            };
        }
    }

    [Fact]
    public async Task RestoreLiveCombat_RelicStackCountValidation_RejectsNonPositiveAndOversizedSaveStack()
    {
        (string baseSaveJson, InCombatLiveStateDto baseDto, _) = await BuildTurn1CombatAsync("NIBBITS_WEAK");

        foreach (int invalidCount in new[] { 0, -1 })
        {
            InCombatLiveStateDto invalidDto = baseDto with
            {
                Relics = baseDto.Relics
                    .Select((r, idx) => idx == 0 ? r with { StackCount = invalidCount } : r)
                    .ToArray(),
            };
            InCombatBridgeResponseDto invalidResp = await RunWireAsync(CreateInCombatRequest(baseSaveJson, invalidDto, maxDop: 1));
            Assert.Equal("CapabilityMiss", invalidResp.Outcome);
            Assert.Equal("live_state_mismatch", invalidResp.FailureCode);
            Assert.NotNull(invalidResp.FieldPath);
            Assert.EndsWith(".stackCount", invalidResp.FieldPath, StringComparison.Ordinal);
        }

        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        Player player = run.Players[0];
        var circlet = (Circlet)ModelDb.Relic<Circlet>().MutableClone();
        circlet.AssignOwner(player);
        player.AddRelicInternal(circlet);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string circletSaveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
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

        InCombatLiveStateDto liveBeforeIncrement = ExportLiveState(entered.Room, "NIBBITS_WEAK");
        RunState oversizedSaveRun = BridgeSaveRestore.RestoreLive(circletSaveJson);
        Circlet saveCirclet = oversizedSaveRun.Players[0].Relics.OfType<Circlet>().Single();
        saveCirclet.IncrementStackCount();
        saveCirclet.IncrementStackCount();
        Assert.Equal(3, saveCirclet.StackCount);

        LiveCombatSnapshotSpec oversizedSpec = LiveCombatTransplantMapper.Map(liveBeforeIncrement);
        ForecastCapabilityMissException ex = Assert.Throws<ForecastCapabilityMissException>(
            () => CombatState.RestoreLiveCombat(oversizedSaveRun, oversizedSpec));
        Assert.Equal("live_state_mismatch", ex.FailureCode);
        Assert.EndsWith(".stackCount", ex.FieldPath, StringComparison.Ordinal);

        circlet.IncrementStackCount();
        InCombatLiveStateDto liveAfterIncrement = ExportLiveState(entered.Room, "NIBBITS_WEAK");
        InCombatBridgeResponseDto validResp = await RunWireAsync(CreateInCombatRequest(circletSaveJson, liveAfterIncrement, maxDop: 1));
        Assert.Equal("Succeeded", validResp.Outcome);

        RunState restoredValidRun = BridgeSaveRestore.RestoreLive(circletSaveJson);
        CombatRoom restoredRoom = CombatState.RestoreLiveCombat(restoredValidRun, LiveCombatTransplantMapper.Map(liveAfterIncrement));
        Assert.Equal(2, restoredRoom.Engine.State.Players[0].Relics.OfType<Circlet>().Single().StackCount);
    }

    [Fact]
    public async Task RunInCombatJsonAsync_OverallDeadlineTimeout_ReturnsTimedOutWithoutUnboundedRetrace()
    {
        (string saveJson, InCombatLiveStateDto liveDto, CombatRoom room) = await BuildTurn1CombatAsync("PHROG_PARASITE_ELITE", withHappyFlower: true);

        var tracker = new InCombatSearchTracker(
            room.Engine.State,
            priorBattleHpLost: 0,
            System.Diagnostics.Stopwatch.StartNew(),
            progressCallback: null);
        InCombatPassTrack track = tracker.RegisterPass();
        var budgetTracker = new ForecastNodeBudgetTracker(100);
        Assert.True(budgetTracker.TryReserveSlot());
        Assert.True(budgetTracker.TryReserveSlot());
        track.AttachBudgetTracker(budgetTracker);
        Assert.Equal(2, tracker.GetTotalExpandedNodes());

        using var expiredDeadline = new CancellationTokenSource();
        expiredDeadline.Cancel();
        Assert.Null(tracker.TryGetBestSoFarOnCancel(expiredDeadline.Token, TimeSpan.FromMilliseconds(250)));
        tracker.StopAndJoin();

        var rootEval = new StateEvaluator(room.Engine.State, CombatPolicy.Default);
        SearchNode rootNode = SearchNode.Create(room.Engine.State.Clone(), null, null, rootEval);
        Assert.Throws<OperationCanceledException>(() => PlanSelector.TrySelect([rootNode], 80, rootEval, expiredDeadline.Token));

        InCombatPolicyDto heavyPolicy = CreateDefaultPolicy(
            beamWidth: 64,
            maxExpandedNodes: 250_000,
            useBeamWidthPortfolio: true,
            softTimeBudgetMs: 15_000);
        InCombatBridgeRequestDto req = CreateInCombatRequest(
            saveJson,
            liveDto,
            maxDop: 2,
            searchBudgetMs: 15_000,
            overallTimeoutMs: 1,
            policy: heavyPolicy);

        InCombatBridgeResponseDto resp = await RunWireAsync(req);
        Assert.True(resp.Outcome is "TimedOut" or "Succeeded", $"Expected TimedOut or Succeeded, got {resp.Outcome} ({resp.Error}).");
    }

    [Fact]
    public async Task TryGetBestSoFarOnCancel_RestampsCachedExpandedNodesWithDrainedConsumedTotal()
    {
        (_, _, CombatRoom room) = await BuildTurn1CombatAsync("NIBBITS_WEAK");
        CombatState rootState = room.Engine.State;
        CombatState childState = rootState.Clone();
        await childState.Engine!.EndPlayerTurnAsync();

        var evaluator = new StateEvaluator(rootState, CombatPolicy.Default);
        SearchNode rootNode = SearchNode.Create(rootState.Clone(), null, null, evaluator);
        var endTurnAction = new PlannedAction(
            Turn: 1,
            Kind: "EndTurn",
            CardId: null,
            CardOccurrence: 0,
            TargetIndex: -1,
            PotionId: null,
            PotionSlot: -1,
            ChoiceIndices: [],
            ChoiceCardIds: []);
        SearchNode childNode = SearchNode.Create(childState, rootNode, endTurnAction, evaluator);

        using var cachedReady = new ManualResetEventSlim(false);
        InCombatProgressUpdate? firstUpdate = null;
        var tracker = new InCombatSearchTracker(
            rootState,
            priorBattleHpLost: 0,
            System.Diagnostics.Stopwatch.StartNew(),
            update =>
            {
                if (update.CurrentBest is not null && !cachedReady.IsSet)
                {
                    firstUpdate = update;
                    cachedReady.Set();
                }
            });

        try
        {
            InCombatPassTrack track = tracker.RegisterPass();
            var budgetTracker = new ForecastNodeBudgetTracker(100);
            track.AttachBudgetTracker(budgetTracker);
            track.AttachEvaluator(evaluator, rootState.Players[0].Creature.CurrentHp);

            for (int i = 0; i < 3; i++)
                Assert.True(budgetTracker.TryReserveSlot());

            track.CommitWave([], [childNode], [], [], null, null, ForecastSearchBoundary.NodeLimit);
            Assert.True(cachedReady.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for cached progress snapshot.");
            tracker.StopAndJoin();

            Assert.NotNull(firstUpdate?.CurrentBest);
            InCombatBeamResult? cached = tracker.LastCachedBestResult;
            Assert.NotNull(cached);
            Assert.Equal(3, cached!.ExpandedNodes);
            Assert.Equal("EndTurn", Assert.Single(cached.Actions).Kind);

            for (int i = 0; i < 7; i++)
                Assert.True(budgetTracker.TryReserveSlot());

            InCombatPassTrack secondTrack = tracker.RegisterPass();
            var secondBudgetTracker = new ForecastNodeBudgetTracker(100);
            secondTrack.AttachBudgetTracker(secondBudgetTracker);
            for (int i = 0; i < 5; i++)
                Assert.True(secondBudgetTracker.TryReserveSlot());

            Assert.Equal(15, tracker.GetTotalExpandedNodes());

            using var expiredDeadline = new CancellationTokenSource();
            expiredDeadline.Cancel();
            InCombatBeamResult? expiredPartial = tracker.TryGetBestSoFarOnCancel(
                expiredDeadline.Token,
                TimeSpan.FromMilliseconds(250));
            Assert.NotNull(expiredPartial);
            Assert.Equal(15, expiredPartial!.ExpandedNodes);
            Assert.Equal("EndTurn", Assert.Single(expiredPartial.Actions).Kind);

            InCombatBeamResult? zeroBudgetPartial = tracker.TryGetBestSoFarOnCancel(
                CancellationToken.None,
                TimeSpan.Zero);
            Assert.NotNull(zeroBudgetPartial);
            Assert.Equal(15, zeroBudgetPartial!.ExpandedNodes);
            Assert.Equal("EndTurn", Assert.Single(zeroBudgetPartial.Actions).Kind);

            InCombatBeamResult? refreshedPartial = tracker.TryGetBestSoFarOnCancel(
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            Assert.NotNull(refreshedPartial);
            Assert.Equal(15, refreshedPartial!.ExpandedNodes);
            Assert.Equal("EndTurn", Assert.Single(refreshedPartial.Actions).Kind);
        }
        finally
        {
            tracker.StopAndJoin();
        }
    }

    [Fact]
    public async Task RunInCombatJsonAsync_BlockingProgressCallback_ReachesDeadlineFallbackCleanly()
    {
        (string saveJson, InCombatLiveStateDto liveDto, _) = await BuildTurn1CombatAsync("PHROG_PARASITE_ELITE", withHappyFlower: true);
        InCombatPolicyDto policy = CreateDefaultPolicy(
            beamWidth: 48,
            maxExpandedNodes: 200_000,
            useBeamWidthPortfolio: true,
            softTimeBudgetMs: 15_000);
        InCombatBridgeRequestDto req = CreateInCombatRequest(
            saveJson,
            liveDto,
            maxDop: 2,
            searchBudgetMs: 15_000,
            overallTimeoutMs: 800,
            policy: policy);

        using var callbackEntered = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(req, BridgeAbi.JsonOptions);
        var wall = System.Diagnostics.Stopwatch.StartNew();
        byte[] responseBytes;
        try
        {
            responseBytes = await BridgeEntry.RunInCombatJsonAsync(
                payload,
                bytes =>
                {
                    InCombatProgressDto? update = JsonSerializer.Deserialize<InCombatProgressDto>(bytes, BridgeAbi.JsonOptions);
                    if (update is { ExpandedNodes: > 0, CurrentBest: not null } && !callbackEntered.IsSet)
                    {
                        callbackEntered.Set();
                        releaseCallback.Wait(TimeSpan.FromSeconds(10));
                    }
                },
                CancellationToken.None);
        }
        finally
        {
            releaseCallback.Set();
        }
        wall.Stop();

        InCombatBridgeResponseDto? response = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(responseBytes, BridgeAbi.JsonOptions);
        Assert.NotNull(response);
        Assert.Equal("TimedOut", response!.Outcome);
        Assert.True(wall.Elapsed < TimeSpan.FromSeconds(4), $"Expected deadline return well under 4s, took {wall.Elapsed}.");
        if (callbackEntered.IsSet)
        {
            Assert.NotNull(response.BestSoFar);
            Assert.NotEmpty(response.BestSoFar!.Actions);
        }
    }

    [Fact]
    public async Task RunInCombatJsonAsync_DuplicateCardHandleAcrossDeckAndHand_FailsClosed()
    {
        (string saveJson, InCombatLiveStateDto baseDto, _) = await BuildTurn1CombatAsync("NIBBITS_WEAK");

        InCombatLiveStateDto deckAndHandDup = baseDto with
        {
            Player = baseDto.Player with
            {
                HandHandles = [baseDto.Player.DeckHandles[0], .. baseDto.Player.HandHandles.Skip(1)],
            },
        };
        InCombatBridgeResponseDto deckHandResp = await RunWireAsync(CreateInCombatRequest(saveJson, deckAndHandDup, maxDop: 1));
        Assert.Equal("CapabilityMiss", deckHandResp.Outcome);
        Assert.Equal("live_state_mismatch", deckHandResp.FailureCode);
        Assert.NotNull(deckHandResp.FieldPath);
        Assert.StartsWith("player.handHandles[", deckHandResp.FieldPath, StringComparison.Ordinal);

        InCombatLiveStateDto handAndDrawDup = baseDto with
        {
            Player = baseDto.Player with
            {
                DrawHandles = [baseDto.Player.HandHandles[0], .. baseDto.Player.DrawHandles.Skip(1)],
            },
        };
        InCombatBridgeResponseDto handDrawResp = await RunWireAsync(CreateInCombatRequest(saveJson, handAndDrawDup, maxDop: 1));
        Assert.Equal("CapabilityMiss", handDrawResp.Outcome);
        Assert.Equal("live_state_mismatch", handDrawResp.FailureCode);
        Assert.NotNull(handDrawResp.FieldPath);
        Assert.StartsWith("player.drawHandles[", handDrawResp.FieldPath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing_dark_orb_raw_evoke", "live_state_mismatch", ".rawEvokeValue")]
    [InlineData("missing_glass_orb_raw_passive", "live_state_mismatch", ".rawPassiveValue")]
    [InlineData("missing_momentum_private_counter", "live_private_state_unmapped", ".Momentum._extraDamage")]
    public async Task RunInCombatJsonAsync_MissingOrbRawValuesOrMomentumPrivateCounter_FailsClosed(
        string faultKind,
        string expectedFailureCode,
        string expectedPathSuffix)
    {
        TestSaveBuilder.EnsurePrewarmed();
        bool isMomentum = faultKind == "missing_momentum_private_counter";
        RunState run = TestSaveBuilder.CreateRun(
            characterId: isMomentum ? "CHARACTER.IRONCLAD" : "CHARACTER.DEFECT",
            ascension: 0);
        Player player = run.Players[0];

        if (isMomentum)
        {
            CardModel strike = player.Deck.Cards.First(c => c is StrikeIronclad);
            var momentum = (Sts2Sim.Core.Models.Enchantments.Momentum)ModelDb.Get(typeof(Sts2Sim.Core.Models.Enchantments.Momentum)).MutableClone();
            momentum.AssignMagnitude(1);
            strike.AttachEnchantment(momentum);
        }

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
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

        CombatState state = entered.Room.Engine.State;
        if (faultKind == "missing_dark_orb_raw_evoke")
            await OrbCmd.Channel<DarkOrb>(state, state.Players[0]);
        else if (faultKind == "missing_glass_orb_raw_passive")
            await OrbCmd.Channel<GlassOrb>(state, state.Players[0]);

        InCombatLiveStateDto baseDto = ExportLiveState(entered.Room, "NIBBITS_WEAK");
        InCombatLiveStateDto faultedDto = faultKind switch
        {
            "missing_dark_orb_raw_evoke" => baseDto with
            {
                Player = baseDto.Player with
                {
                    Orbs = baseDto.Player.Orbs.Select(o => o with { RawEvokeValue = null }).ToArray(),
                },
            },
            "missing_glass_orb_raw_passive" => baseDto with
            {
                Player = baseDto.Player with
                {
                    Orbs = baseDto.Player.Orbs.Select(o => o with { RawPassiveValue = null }).ToArray(),
                },
            },
            "missing_momentum_private_counter" => baseDto with
            {
                Cards = baseDto.Cards
                    .Select(c => c.Enchantment is { EnchantmentId: "ENCHANTMENT.MOMENTUM" } ench
                        ? c with
                        {
                            Enchantment = ench with
                            {
                                PrivateFields = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal),
                            },
                        }
                        : c)
                    .ToArray(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(faultKind)),
        };

        InCombatBridgeResponseDto resp = await RunWireAsync(CreateInCombatRequest(saveJson, faultedDto, maxDop: 1));
        Assert.Equal("CapabilityMiss", resp.Outcome);
        Assert.Equal(expectedFailureCode, resp.FailureCode);
        Assert.NotNull(resp.FieldPath);
        Assert.EndsWith(expectedPathSuffix, resp.FieldPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreLiveCombat_GlassOrbRawPassiveUnderFocusAndLargeRelicStackCount_Succeeds()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.DEFECT", ascension: 0);
        Player player = run.Players[0];
        var circlet = (Circlet)ModelDb.Relic<Circlet>().MutableClone();
        circlet.AssignOwner(player);
        player.AddRelicInternal(circlet);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
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

        CombatState state = entered.Room.Engine.State;
        Player combatPlayer = state.Players[0];
        await PowerCmd.Apply<FocusPower>(state, combatPlayer.Creature, 3m, combatPlayer.Creature, null);
        await OrbCmd.Channel<GlassOrb>(state, combatPlayer);
        GlassOrb liveGlass = Assert.IsType<GlassOrb>(combatPlayer.PlayerCombatState!.OrbQueue.Orbs[^1]);
        liveGlass.RestoreRawValue(2m);
        Assert.Equal(2m, liveGlass.RawPassiveValue);
        Assert.Equal(5m, liveGlass.PassiveVal);

        Circlet combatCirclet = combatPlayer.Relics.OfType<Circlet>().Single();
        combatCirclet.RestoreStackCount(1_000_000);

        CombatStateDescriptionDigest expectedDigest = ComputeExactDigest(state);
        InCombatLiveStateDto liveDto = ExportLiveState(entered.Room, "NIBBITS_WEAK");
        Assert.Equal(2m, liveDto.Player.Orbs[^1].RawPassiveValue);

        RunState restoredRun = BridgeSaveRestore.RestoreLive(saveJson);
        CombatRoom restoredRoom = CombatState.RestoreLiveCombat(restoredRun, LiveCombatTransplantMapper.Map(liveDto));
        CombatState restoredState = restoredRoom.Engine.State;

        Assert.Equal(expectedDigest, ComputeExactDigest(restoredState));
        GlassOrb restoredGlass = Assert.IsType<GlassOrb>(restoredState.Players[0].PlayerCombatState!.OrbQueue.Orbs[^1]);
        Assert.Equal(2m, restoredGlass.RawPassiveValue);
        Assert.Equal(5m, restoredGlass.PassiveVal);
        Assert.Equal(1_000_000, restoredState.Players[0].Relics.OfType<Circlet>().Single().StackCount);

        InCombatBridgeResponseDto wireResp = await RunWireAsync(CreateInCombatRequest(saveJson, liveDto, maxDop: 1));
        Assert.Equal("Succeeded", wireResp.Outcome);

        var nonStackable = (HappyFlower)ModelDb.Relic<HappyFlower>().MutableClone();
        Assert.Throws<ArgumentOutOfRangeException>(() => circlet.RestoreStackCount(0));
        Assert.Throws<InvalidOperationException>(() => nonStackable.RestoreStackCount(2));
    }

    private static async Task<(string SaveJson, InCombatLiveStateDto LiveDto, CombatRoom Room)> BuildTurn1CombatAsync(
        string encounterId,
        bool withHappyFlower = false)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        Player player = run.Players[0];
        if (withHappyFlower)
        {
            RelicModel happyFlower = (RelicModel)ModelDb.Relic<HappyFlower>().MutableClone();
            happyFlower.AssignOwner(player);
            player.AddRelicInternal(happyFlower);
        }

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

        return (saveJson, ExportLiveState(entered.Room, encounterId), entered.Room);
    }

    private static async Task<(string SaveJson, InCombatLiveStateDto LiveDto, CombatRoom Room)> BuildTurn2CombatAsync(
        string encounterId)
    {
        (string saveJson, _, CombatRoom room) = await BuildTurn1CombatAsync(encounterId, withHappyFlower: true);
        Player player = room.Engine.State.Players[0];
        CardModel? strike = player.PlayerCombatState!.Hand.Cards.FirstOrDefault(c => c.CanPlay(out _) && c.TargetType == TargetType.AnyEnemy);
        if (strike is not null)
        {
            await room.Engine.PlayCardWithResultAsync(player, strike, room.Engine.State.HittableEnemies[0]);
        }
        await room.Engine.EndPlayerTurnAsync();
        return (saveJson, ExportLiveState(room, encounterId), room);
    }

    private static InCombatPolicyDto CreateDefaultPolicy(
        int beamWidth = 16,
        int maxExpandedNodes = 2500,
        bool useBeamWidthPortfolio = false,
        int softTimeBudgetMs = 6000) =>
        new(
            PotionPolicy: "Smart",
            PotionDirectives: [],
            PredictPotionReward: false,
            RelicStrategyEnabled: true,
            RelicCounterRules: [],
            GrowthBudgets: new BridgeGrowthBudgetsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null),
            BrightestFlameMaxHpLossLimit: null,
            IgnoreLongTermRewards: false,
            ActTransitionBossHpStrategy: "ProgressionFirst",
            FinalBossHpStrategy: "ProgressionFirst",
            AcceptableBattleHpLoss: 0,
            StopAtAcceptableBattleHpLoss: false,
            UseBeamWidthPortfolio: useBeamWidthPortfolio,
            UseNoveltyPortfolio: false,
            UseEarlyTurnExploration: false,
            BeamWidth: beamWidth,
            PotionFreeBeamWidth: null,
            PotionBeamWidth: null,
            MaxExpandedNodes: maxExpandedNodes,
            MaxCardBranchesPerNode: 10,
            MaxPileChoiceBranchesPerAction: 6,
            MaxHandChoiceBranchesPerAction: 6,
            SoftTimeBudgetMilliseconds: softTimeBudgetMs,
            TheftPolicy: null);

    private static InCombatBridgeRequestDto CreateInCombatRequest(
        string saveJson,
        InCombatLiveStateDto liveState,
        int maxDop = 1,
        int searchBudgetMs = 6000,
        int overallTimeoutMs = 20000,
        int softTimeBudgetMs = 6000,
        InCombatPolicyDto? policy = null) =>
        new(
            AbiVersion: InCombatBridgeAbi.Version,
            RequestId: Guid.NewGuid().ToString("N"),
            NormalizedSaveJson: saveJson,
            LiveState: liveState,
            PriorBattleHpLost: 0,
            SearchBudgetMilliseconds: searchBudgetMs,
            OverallTimeoutMilliseconds: overallTimeoutMs,
            MaxDegreeOfParallelism: maxDop,
            Policy: policy ?? CreateDefaultPolicy(softTimeBudgetMs: softTimeBudgetMs));

    private static async Task<InCombatBridgeResponseDto> RunWireAsync(
        InCombatBridgeRequestDto request,
        Action<byte[]>? onProgress = null,
        CancellationToken ct = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, BridgeAbi.JsonOptions);
        byte[] responseBytes = await BridgeEntry.RunInCombatJsonAsync(payload, onProgress, ct);
        return JsonSerializer.Deserialize<InCombatBridgeResponseDto>(responseBytes, BridgeAbi.JsonOptions)
            ?? throw new InvalidOperationException("Null InCombatBridgeResponseDto.");
    }

    private static CombatStateDescriptionDigest ComputeExactDigest(CombatState state)
    {
        var builder = new CombatStateDescriptionBuilder();
        CombatStateDescription.AppendExactState(ref builder, state);
        return builder.Build();
    }

    private static InCombatLiveStateDto ExportLiveState(CombatRoom room, string encounterId)
    {
        CombatState state = room.Engine.State;
        Player player = state.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;

        var creatureHandles = new Dictionary<Creature, int>(ReferenceEqualityComparer.Instance);
        var cardHandles = new Dictionary<CardModel, int>(ReferenceEqualityComparer.Instance);

        int RegisterCreature(Creature c)
        {
            if (!creatureHandles.TryGetValue(c, out int handle))
            {
                handle = creatureHandles.Count + 1;
                creatureHandles.Add(c, handle);
            }
            return handle;
        }

        int RegisterCard(CardModel card)
        {
            if (!cardHandles.TryGetValue(card, out int handle))
            {
                handle = cardHandles.Count + 1;
                cardHandles.Add(card, handle);
                if (card.DeckVersion is { } dv)
                    RegisterCard(dv);
                if (card.CloneOf is { } co)
                    RegisterCard(co);
            }
            return handle;
        }

        RegisterCreature(player.Creature);
        foreach (Creature c in state.Allies) RegisterCreature(c);
        foreach (Creature c in state.Enemies) RegisterCreature(c);
        foreach (Creature c in state.EscapedCreatures) RegisterCreature(c);
        foreach (Creature c in state.RemovedCreatures) RegisterCreature(c);
        foreach (Creature c in state.SpawnedEnemies) RegisterCreature(c);
        foreach (Creature c in pcs.Pets) RegisterCreature(c);

        foreach (CardModel c in player.Deck.Cards) RegisterCard(c);
        foreach (CardPile pile in pcs.AllPiles)
        {
            foreach (CardModel c in pile.Cards) RegisterCard(c);
        }
        foreach (CombatDamageHistoryEntry dmg in state.DamageHistory.Entries)
        {
            RegisterCreature(dmg.Receiver);
            RegisterCreature(dmg.Result.OriginalTarget);
            if (dmg.Dealer is not null) RegisterCreature(dmg.Dealer);
            if (dmg.CardSource is not null) RegisterCard(dmg.CardSource);
        }

        // Also register card references held inside relics or powers.
        foreach (RelicModel r in player.Relics)
        {
            if (r is HistoryCourse hc)
            {
                if (ReadField<CardModel?>(hc, "_lastAttack") is { } la) RegisterCard(la);
                if (ReadField<CardModel?>(hc, "_previousAttack") is { } pa) RegisterCard(pa);
            }
            else if (r is MusicBox mb && ReadField<CardModel?>(mb, "_cardBeingPlayed") is { } mbCard)
            {
                RegisterCard(mbCard);
            }
            else if (r is UnsettlingLamp ul && ReadField<CardModel?>(ul, "_triggeringCard") is { } ulCard)
            {
                RegisterCard(ulCard);
            }
        }

        var cardsDto = cardHandles
            .OrderBy(kv => kv.Value)
            .Select(kv => ExportCard(kv.Key, kv.Value, cardHandles))
            .ToArray();

        var relicsDto = player.Relics
            .Select(r => ExportRelic(r, cardHandles))
            .ToArray();

        var creaturesDto = creatureHandles
            .OrderBy(kv => kv.Value)
            .Select(kv => ExportCreature(kv.Key, kv.Value, state, creatureHandles, cardHandles))
            .ToArray();

        string[] encounterSlots = ReadField<string[]>(state, "_encounterSlots");
        uint nextCreatureId = ReadField<uint>(state, "_nextCreatureId");

        SerializableRunRngSet runRng = state.RunState.Rng.ToSerializable();
        var runRngDtos = runRng.Rngs.ToDictionary(
            kv => kv.Key.ToString(),
            kv => ToRngDto(kv.Value),
            StringComparer.Ordinal);
        SerializablePlayerRngSet playerRng = player.PlayerRng.ToSerializable();
        var playerRngDtos = playerRng.Rngs.ToDictionary(
            kv => kv.Key.ToString(),
            kv => ToRngDto(kv.Value),
            StringComparer.Ordinal);

        var countersDto = new InCombatPlayerCountersDto(
            pcs.StarsGainedThisTurn,
            pcs.SkillCardsPlayedThisTurn,
            pcs.AttackCardsPlayedThisTurn,
            pcs.ShivsPlayedThisTurn,
            pcs.CardsDiscardedThisTurn,
            pcs.CardsPlayedThisCombat,
            pcs.CardsGeneratedThisCombat,
            pcs.SadnessGeneratedThisCombat,
            pcs.CardsDrawnThisCombat,
            pcs.CardsPlayedThisTurn,
            pcs.ManualCardsPlayedThisTurn,
            pcs.CardPlaysStartedThisTurn,
            pcs.AttackOrSkillCardPlaysStartedThisTurn,
            pcs.AttackCardsStartedThisTurn,
            pcs.ZeroCostAttacksStartedThisTurn,
            pcs.FirstInSeriesCardPlaysStartedThisTurn,
            pcs.WahStartedThisTurn,
            pcs.ChoStartedThisTurn,
            pcs.KiStartedThisTurn,
            pcs.FirstWahPlayOrdinalThisTurn,
            pcs.FirstChoPlayOrdinalThisTurn,
            pcs.FirstKiPlayOrdinalThisTurn,
            pcs.WahFinishedThisTurn,
            pcs.ChoFinishedThisTurn,
            pcs.KiFinishedThisTurn,
            pcs.MelodyCardsFinishedThisTurn);

        var orbsDto = pcs.OrbQueue.Orbs
            .Select(o => new InCombatOrbDto(
                o.Id.ToString(),
                o is DarkOrb dark ? dark.RawEvokeValue : null,
                o is GlassOrb glass ? glass.RawPassiveValue : null))
            .ToArray();

        var arrowsDto = pcs.Arrows
            .Select(a => new InCombatArrowSlotDto(a.Status.ToString(), a.DestroyAfterImpaled))
            .ToArray();

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
            orbsDto,
            arrowsDto);

        var damageDtos = state.DamageHistory.Entries
            .Select(e => new InCombatDamageHistoryEntryDto(
                creatureHandles[e.Receiver],
                e.Dealer is not null ? creatureHandles[e.Dealer] : null,
                creatureHandles[e.Result.OriginalTarget],
                (int)e.Result.Props,
                e.Result.IntendedHpLoss,
                e.Result.BlockedDamage,
                e.Result.UnblockedDamage,
                e.Result.OverkillDamage,
                e.Result.WasTargetKilled,
                e.Result.WasBlockBroken,
                e.Result.WasFullyBlocked,
                e.CardSource is not null ? cardHandles[e.CardSource] : null,
                e.RoundNumber,
                e.CurrentSide.ToString(),
                e.PlayerTurnNumbers.ToArray()))
            .ToArray();

        InCombatSemanticHistoryDto semanticDto = ExportSemanticHistory(state.SemanticHistory);

        var verificationDto = new InCombatVerificationDto(
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Creature.Block,
            pcs.Energy,
            pcs.Stars,
            player.Gold,
            pcs.OrbQueue.Capacity,
            pcs.OrbQueue.Orbs.Select(o => o.Id.ToString()).ToArray(),
            pcs.Arrows.Select(a => a.Status.ToString()).ToArray(),
            player.PotionSlots.Select(p => p?.Id.ToString()).ToArray(),
            pcs.Hand.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.DrawPile.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.DiscardPile.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.ExhaustPile.Cards.Select(ToVerifiedCard).ToArray(),
            state.Enemies.Select(e => new InCombatVerifiedCreatureDto(
                e.CombatId,
                e.Monster?.Id.ToString(),
                e.CurrentHp,
                e.MaxHp,
                e.Block,
                e.Monster?.NextMove?.Id,
                e.Powers.Select(pw => new InCombatVerifiedPowerDto(pw.Id.ToString(), pw.Amount)).ToArray())).ToArray(),
            player.Creature.Powers.Select(pw => new InCombatVerifiedPowerDto(pw.Id.ToString(), pw.Amount)).ToArray());

        return new InCombatLiveStateDto(
            encounterId,
            room.RoomType.ToString(),
            encounterSlots,
            state.CurrentSide.ToString(),
            state.RoundNumber,
            state.IsPlayerExtraTurn,
            state.GoldWasStolen,
            nextCreatureId,
            runRng.Seed,
            runRngDtos,
            playerRng.Seed,
            playerRngDtos,
            playerDto,
            cardsDto,
            relicsDto,
            creaturesDto,
            state.Allies.Select(c => creatureHandles[c]).ToArray(),
            state.Enemies.Select(c => creatureHandles[c]).ToArray(),
            state.EscapedCreatures.Select(c => creatureHandles[c]).ToArray(),
            state.RemovedCreatures.Select(c => creatureHandles[c]).ToArray(),
            state.SpawnedEnemies.Select(c => creatureHandles[c]).ToArray(),
            pcs.Pets.Select(c => creatureHandles[c]).ToArray(),
            damageDtos,
            semanticDto,
            verificationDto);

        static InCombatVerifiedCardDto ToVerifiedCard(CardModel c) =>
            new(
                c.Id.ToString(),
                c.CurrentUpgradeLevel,
                c.ResolvedEnergyCostForComparison,
                c.LocalKeywords.Select(k => k.ToString()).ToArray());
    }

    private static InCombatCardDto ExportCard(
        CardModel c,
        int handle,
        IReadOnlyDictionary<CardModel, int> cardHandles)
    {
        var modList = ReadField<System.Collections.IList>(c, "_localEnergyModifiers");
        var modsDto = new List<InCombatLocalEnergyModifierDto>(modList.Count);
        foreach (object item in modList)
        {
            Type t = item.GetType();
            object kindVal = t.GetProperty("Kind")!.GetValue(item)!;
            int amount = (int)t.GetProperty("Amount")!.GetValue(item)!;
            bool isReduceOnly = (bool)t.GetProperty("IsReduceOnly")!.GetValue(item)!;
            modsDto.Add(new InCombatLocalEnergyModifierDto(kindVal.ToString()!, amount, isReduceOnly));
        }

        EnchantmentModel? ench = c.Enchantments.SingleOrDefault();
        InCombatEnchantmentDto? enchDto = ench is null
            ? null
            : new InCombatEnchantmentDto(
                ench.Id.ToString(),
                (int)ench.Magnitude,
                ench.Status.ToString(),
                ench is Sts2Sim.Core.Models.Enchantments.Momentum m
                    ? new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal)
                    {
                        ["Momentum._extraDamage"] = new("Int", IntValue: ReadField<int>(m, "_extraDamage")),
                    }
                    : new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal));

        InCombatAfflictionDto? affDto = c.Affliction is { } aff
            ? new InCombatAfflictionDto(aff.Id.ToString(), aff.Amount)
            : null;

        HashSet<CardKeyword> addedKw = ReadField<HashSet<CardKeyword>>(c, "_keywordsAddedFromOutside");
        HashSet<CardKeyword> removedKw = ReadField<HashSet<CardKeyword>>(c, "_keywordsRemovedFromOutside");
        int energyUpgradeDelta = ReadField<int>(c, "_energyCostUpgradeDelta");
        int canonicalBase = c.CanonicalEnergyCostValue;
        int baseCost = c.CostsXEnergy || canonicalBase < 0 ? canonicalBase : Math.Max(0, canonicalBase - energyUpgradeDelta);

        var priv = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);
        if (c is Sts2Sim.Core.Models.Cards.Claw)
        {
            priv["Claw._damage"] = new("Decimal", DecimalValue: ReadField<decimal>(c, "_damage"));
            priv["Claw._increase"] = new("Decimal", DecimalValue: ReadField<decimal>(c, "_increase"));
        }
        else if (c is Sts2Sim.Core.Models.Cards.GeneticAlgorithm)
        {
            priv["GeneticAlgorithm._currentBlock"] = new("Int", IntValue: ReadField<int>(c, "_currentBlock"));
            priv["GeneticAlgorithm._increasedBlock"] = new("Int", IntValue: ReadField<int>(c, "_increasedBlock"));
            priv["GeneticAlgorithm._increase"] = new("Int", IntValue: ReadField<int>(c, "_increase"));
        }
        else if (c is Sts2Sim.Core.Models.Cards.Rampage)
        {
            priv["Rampage._damage"] = new("Decimal", DecimalValue: ReadField<decimal>(c, "_damage"));
        }
        else if (c is Sts2Sim.Core.Models.Cards.TheScythe)
        {
            priv["TheScythe._increasedDamage"] = new("Int", IntValue: ReadField<int>(c, "_increasedDamage"));
        }

        return new InCombatCardDto(
            handle,
            c.Id.ToString(),
            c.CurrentUpgradeLevel,
            c.DeckVersion is not null ? cardHandles[c.DeckVersion] : null,
            c.CloneOf is not null ? cardHandles[c.CloneOf] : null,
            c.IsDupe,
            c.ExhaustOnNextPlay,
            c.BaseReplayCount,
            ReadField<int>(c, "_capturedEnergyXValue"),
            c.FloorAddedToDeck,
            canonicalBase,
            baseCost,
            c.CostsXEnergy,
            modsDto,
            c.TemporaryStarCostOverrideThisTurn,
            c.TemporaryFreeThisTurn,
            c.TemporaryFreeUntilPlayed,
            c.TemporaryFreeThisCombat,
            c.TemporaryCostOverrideThisTurn,
            c.TemporaryCostOverrideThisTurnOrUntilPlayed,
            c.TemporaryCostOverrideThisCombat,
            c.TemporaryRetainThisTurn,
            c.TemporarySlyThisTurn,
            c.LocalKeywords.Select(k => k.ToString()).ToArray(),
            addedKw.Select(k => k.ToString()).ToArray(),
            removedKw.Select(k => k.ToString()).ToArray(),
            enchDto,
            affDto,
            priv);
    }

    private static InCombatRelicDto ExportRelic(RelicModel r, IReadOnlyDictionary<CardModel, int> cardHandles)
    {
        var priv = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);
        if (r is HappyFlower)
        {
            priv["HappyFlower._turnCounter"] = new("Int", IntValue: ReadField<int>(r, "_turnCounter"));
        }
        return new InCombatRelicDto(
            r.Id.ToString(),
            r.IsWax,
            r.IsMelted,
            r.StackCount,
            r.FloorAddedToDeck,
            priv);
    }

    private static InCombatCreatureDto ExportCreature(
        Creature c,
        int handle,
        CombatState state,
        IReadOnlyDictionary<Creature, int> creatureHandles,
        IReadOnlyDictionary<CardModel, int> cardHandles)
    {
        int? petOwnerIdx = null;
        if (c.PetOwner is not null)
        {
            for (int i = 0; i < state.Players.Count; i++)
            {
                if (ReferenceEquals(state.Players[i], c.PetOwner))
                {
                    petOwnerIdx = i;
                    break;
                }
            }
        }

        InCombatMonsterDto? monsterDto = null;
        if (c.Monster is { } m)
        {
            MonsterMoveStateMachine sm = m.MoveStateMachine!;
            string currentStateId = sm.CurrentState.Id;
            string? transientFollowUp = sm.States.ContainsKey(currentStateId)
                ? null
                : (sm.CurrentState as MoveState)?.FollowUpState?.Id ?? (sm.CurrentState as MoveState)?.FollowUpStateId;
            var setupFields = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);
            if (m is Nibbit nib)
            {
                setupFields["Nibbit._isFront"] = new("Bool", BoolValue: nib.IsFront);
                setupFields["Nibbit._isAlone"] = new("Bool", BoolValue: nib.IsAlone);
            }
            var privFields = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);

            SerializableRng serializedMonsterRng = m.Rng.ToSerializable();

            monsterDto = new InCombatMonsterDto(
                ToRngDto(serializedMonsterRng),
                m.IsPerformingMove,
                m.SpawnedThisTurn,
                currentStateId,
                m.NextMove?.Id,
                m.NextMove?.CanTransitionAway ?? true,
                sm.PerformedFirstMove,
                sm.StateLog.Select(s => s.Id).ToArray(),
                transientFollowUp,
                setupFields,
                privFields);
        }

        var powers = c.Powers.Select(p =>
        {
            var pPriv = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);
            return new InCombatPowerDto(
                p.Id.ToString(),
                p.Amount,
                p.AmountOnTurnStart,
                p.SkipNextDurationTick,
                p.Applier is not null ? creatureHandles[p.Applier] : null,
                pPriv);
        }).ToArray();

        return new InCombatCreatureDto(
            handle,
            c.CombatId,
            c.Side.ToString(),
            c.SlotName,
            c.IsPlayer,
            petOwnerIdx,
            c.Monster?.Id.ToString(),
            c.CurrentHp,
            c.MaxHp,
            c.Block,
            c.CumulativeHpLost,
            IsDetached: c.CombatState is null,
            monsterDto,
            powers);
    }

    private static InCombatSemanticHistoryDto ExportSemanticHistory(CombatSemanticHistory h)
    {
        return new InCombatSemanticHistoryDto(
            MapList("_exhausts", item => new InCombatExhaustHistoryDto(
                ExportTurnKey(Prop<object>(item, "Turn")),
                Prop<int>(item, "ActorIndex"))),
            MapList("_cardBlockGains", item => new InCombatBlockGainHistoryDto(
                ExportTurnKey(Prop<object>(item, "Turn")),
                Prop<int>(item, "ActorIndex"),
                Prop<int>(item, "PlayOrdinal"))),
            MapList("_orbChannels", item => new InCombatOrbChannelHistoryDto(
                ExportTurnKey(Prop<object>(item, "Turn")),
                Prop<int>(item, "ActorIndex"),
                Prop<ModelId>(item, "OrbId").ToString())),
            MapList("_energySpent", item => new InCombatEnergySpentHistoryDto(
                ExportTurnKey(Prop<object>(item, "Turn")),
                Prop<int>(item, "ActorIndex"),
                Prop<int>(item, "Amount"))),
            MapList("_cardDrawn", item => new InCombatCardDrawnHistoryDto(
                ExportTurnKey(Prop<object>(item, "Turn")),
                Prop<int>(item, "ActorIndex"),
                Prop<CardType>(item, "CardType").ToString(),
                Prop<bool>(item, "FromHandDraw"))),
            MapList("_actorEvents", item => new InCombatActorEventHistoryDto(
                Prop<CombatSemanticHistory.ActorEvent>(item, "Kind").ToString(),
                ExportTurnKey(Prop<object>(item, "Turn")),
                Prop<int>(item, "ActorIndex"))));

        IReadOnlyList<TOut> MapList<TOut>(string fieldName, Func<object, TOut> map)
        {
            var raw = ReadField<System.Collections.IEnumerable>(h, fieldName);
            var list = new List<TOut>();
            foreach (object item in raw)
                list.Add(map(item));
            return list;
        }

        static InCombatTurnKeyDto ExportTurnKey(object tk) =>
            new(
                Prop<int>(tk, "RoundNumber"),
                Prop<CombatSide>(tk, "Side").ToString(),
                Prop<int?[]>(tk, "PlayerTurnNumbers").ToArray());

        static TProp Prop<TProp>(object obj, string name) =>
            (TProp)obj.GetType().GetProperty(name)!.GetValue(obj)!;
    }

    private static InCombatRngStreamDto ToRngDto(SerializableRng r) =>
        new(r.counter, r.state0, r.state1, r.state2, r.state3);

    private static T ReadField<T>(object target, string name)
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
