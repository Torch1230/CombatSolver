using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Helpers;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Encounters;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Runs.Transplant;
using Sts2Sim.Core.Saves;

namespace Sts2Sim.PreCombatBridge.Tests;

internal static class TestSaveBuilder
{
    public const string DefaultSeed = "PRECOMBAT-BRIDGE-TEST-001";

    public static void EnsurePrewarmed() => BridgeEntry.Prewarm();

    public static RunState CreateRun(
        string seed = DefaultSeed,
        int ascension = 10,
        int actIndex = 0,
        string characterId = "CHARACTER.SILENT")
    {
        EnsurePrewarmed();
        ActDefinition[] acts = [new Overgrowth(), new Hive(), new Glory()];
        var run = new RunState(seed, acts, ascension);
        CharacterModel character = ModelDb.GetById<CharacterModel>(ModelId.Deserialize(characterId));
        var player = Sts2Sim.Core.Entities.Players.Player.CreateForNewRun(character, run);
        run.AddPlayer(player);
        _ = run.CurrentAncientEventType;
        while (run.CurrentActIndex < actIndex)
            run.AdvanceToNextAct();
        return run;
    }

    public static string SerializeNormalizedSave(
        RunState run,
        IReadOnlyList<JsonObject>? priorActHistories = null,
        IReadOnlyList<JsonObject>? currentActHistory = null,
        Action<JsonObject>? mutate = null)
    {
        EnsurePrewarmed();
        Schema21SaveData snapshot = run.CaptureSchema21SaveData();
        var root = new JsonObject
        {
            ["schema_version"] = 21,
            ["game_mode"] = "standard",
            ["is_custom_game"] = false,
            ["dailyTime"] = null,
            ["modifiers"] = new JsonArray(),
            ["pre_finished_room"] = null,
            ["ascension"] = snapshot.Ascension,
            ["current_act_index"] = run.CurrentActIndex,
            ["acts"] = BuildActsArray(run, snapshot),
            ["players"] = new JsonArray { BuildPlayerObject(run, snapshot.Player) },
            ["rng"] = BuildRunRngObject(run.Rng.ToSerializable()),
            ["odds"] = BuildRunOddsObject(run.Odds.ToSerializable()),
            ["shared_relic_grab_bag"] = BuildRelicBagObject(snapshot.SharedRelicBag),
            ["visited_map_coords"] = BuildVisitedCoordsArray(run.VisitedMapCoords),
            ["map_point_history"] = BuildMapPointHistory(run, priorActHistories, currentActHistory),
            ["events_seen"] = ToJsonStringArray(snapshot.VisitedEventIds),
            ["extra_fields"] = new JsonObject
            {
                ["started_with_neow"] = true,
                ["test_subject_kills"] = 0,
                ["freed_repy"] = false,
                ["used_debug_command"] = false,
            },
        };

        mutate?.Invoke(root);
        return root.ToJsonString();
    }

    public static JsonObject MakeHistoryEntry(string mapPointType, params string[] roomTypes)
    {
        var rooms = new JsonArray();
        foreach (string roomType in roomTypes)
        {
            rooms.Add(new JsonObject
            {
                ["room_type"] = roomType,
            });
        }
        return new JsonObject
        {
            ["map_point_type"] = mapPointType,
            ["rooms"] = rooms,
        };
    }

    public static BridgePolicyDto DefaultPolicy(
        int beamWidth = 12,
        int maxExpandedNodes = 1200,
        bool useBeamWidthPortfolio = false,
        int softTimeBudgetMs = 4000) =>
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
            MaxExpandedNodes: maxExpandedNodes,
            MaxCardBranchesPerNode: 8,
            MaxPileChoiceBranchesPerAction: 6,
            MaxHandChoiceBranchesPerAction: 6,
            SoftTimeBudgetMilliseconds: softTimeBudgetMs);

    public static BridgeRequestDto CreateRequest(
        RunState run,
        string encounterId,
        MapCoord targetCoord,
        string roomKind = "Normal",
        string mapPointKind = "Normal",
        bool isSecondBoss = false,
        IReadOnlyList<BridgeMapStepDto>? intervening = null,
        int? playerCurrentHpOverride = null,
        ulong? simulationSeed = null,
        bool hasLiveSidecar = true,
        BridgeSpoilsMapVerificationDto? spoilsVerification = null,
        int? nextRoomId = null,
        int searchBudgetMs = 4000,
        int overallTimeoutMs = 20000,
        BridgePolicyDto? policy = null,
        Action<JsonObject>? mutateSave = null)
    {
        string saveJson = SerializeNormalizedSave(run, mutate: mutateSave);
        return new BridgeRequestDto(
            AbiVersion: BridgeAbi.Version,
            RequestId: Guid.NewGuid().ToString("N"),
            NormalizedSaveJson: saveJson,
            HasLiveSidecar: hasLiveSidecar,
            LiveActFloor: hasLiveSidecar ? run.VisitedMapCoords.Count : null,
            NextRoomId: hasLiveSidecar ? (nextRoomId ?? 0) : null,
            SpoilsMapVerification: spoilsVerification,
            EncounterId: encounterId,
            TargetActFloor: targetCoord.row + 1,
            TargetMapColumn: targetCoord.col,
            RoomKind: roomKind,
            MapPointKind: mapPointKind,
            IsSecondBoss: isSecondBoss,
            InterveningMapPoints: intervening ?? [],
            PlayerCurrentHpOverride: playerCurrentHpOverride,
            SimulationSeed: simulationSeed,
            SearchBudgetMilliseconds: searchBudgetMs,
            OverallTimeoutMilliseconds: overallTimeoutMs,
            MaxDegreeOfParallelism: 1,
            Policy: policy ?? DefaultPolicy());
    }

    public static async Task<(BridgeResponseDto Dto, JsonElement Json)> RunAsync(
        BridgeRequestDto request,
        CancellationToken ct = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, BridgeAbi.JsonOptions);
        byte[] responseBytes = await BridgeEntry.RunJsonAsync(payload, ct);
        BridgeResponseDto dto = JsonSerializer.Deserialize<BridgeResponseDto>(responseBytes, BridgeAbi.JsonOptions)
            ?? throw new InvalidOperationException("Null response DTO.");
        using JsonDocument doc = JsonDocument.Parse(responseBytes);
        return (dto, doc.RootElement.Clone());
    }

    public static MapPoint FindFirstUnvisitedPoint(RunState run, MapPointType pointType) =>
        run.Map.GetAllMapPoints()
            .First(p => p.PointType == pointType && !run.VisitedMapCoords.Contains(p.coord));

    public static (MapPoint Step, MapPoint Target) FindAdjacentStepAndTarget(
        RunState run,
        MapPointType stepType,
        MapPointType targetType)
    {
        if (run.VisitedMapCoords.Count == 0)
        {
            foreach (MapPoint candidate in run.Map.GetPointsInRow(1))
            {
                if (candidate.PointType != stepType) continue;
                MapPoint? child = candidate.Children.FirstOrDefault(c => c.PointType == targetType);
                if (child is not null) return (candidate, child);
            }
        }
        else
        {
            MapCoord last = run.VisitedMapCoords[^1];
            MapPoint? lastPoint = run.Map.GetPoint(last);
            if (lastPoint is not null)
            {
                foreach (MapPoint step in lastPoint.Children)
                {
                    if (step.PointType != stepType) continue;
                    MapPoint? target = step.Children.FirstOrDefault(c => c.PointType == targetType);
                    if (target is not null) return (step, target);
                }
            }
        }
        throw new InvalidOperationException($"No {stepType} -> {targetType} pair found on act map.");
    }

    private static JsonArray BuildActsArray(RunState run, Schema21SaveData snapshot)
    {
        var actsArray = new JsonArray();
        for (int i = 0; i < run.Acts.Count; i++)
        {
            Schema21ActRooms actRooms = snapshot.Acts[i];
            int normalVisited = i == run.CurrentActIndex ? snapshot.NormalEncountersVisited : 0;
            int eliteVisited = i == run.CurrentActIndex ? snapshot.EliteEncountersVisited : 0;
            int bossVisited = i == run.CurrentActIndex ? snapshot.BossEncountersVisited : 0;
            int eventsVisited = i == run.CurrentActIndex ? snapshot.EventsVisited : 0;

            var roomsObj = new JsonObject
            {
                ["event_ids"] = ToJsonStringArray(actRooms.EventIds),
                ["events_visited"] = eventsVisited,
                ["normal_encounter_ids"] = ToJsonStringArray(actRooms.NormalEncounterIds),
                ["normal_encounters_visited"] = normalVisited,
                ["elite_encounter_ids"] = ToJsonStringArray(actRooms.EliteEncounterIds),
                ["elite_encounters_visited"] = eliteVisited,
                ["boss_encounters_visited"] = bossVisited,
                ["boss_id"] = actRooms.BossId,
                ["second_boss_id"] = actRooms.SecondBossId,
                ["ancient_id"] = actRooms.AncientId,
            };

            var actObj = new JsonObject
            {
                ["id"] = actRooms.ActId,
                ["rooms"] = roomsObj,
            };

            if (i == run.CurrentActIndex)
            {
                actObj["saved_map"] = BuildSavedMapObject(run.Map);
            }

            actsArray.Add(actObj);
        }
        return actsArray;
    }

    private static JsonObject BuildSavedMapObject(ActMap map)
    {
        var points = new JsonArray();
        foreach (MapPoint point in map.GetAllMapPoints())
            points.Add(SerializeMapPoint(point));

        return new JsonObject
        {
            ["width"] = map.GetColumnCount(),
            ["height"] = map.GetRowCount(),
            ["start"] = SerializeMapPoint(map.StartingMapPoint),
            ["boss"] = SerializeMapPoint(map.BossMapPoint),
            ["second_boss"] = map.SecondBossMapPoint is null ? null : SerializeMapPoint(map.SecondBossMapPoint),
            ["points"] = points,
        };
    }

    private static JsonObject SerializeMapPoint(MapPoint point)
    {
        var children = new JsonArray();
        foreach (MapPoint child in point.Children)
            children.Add(new JsonObject { ["col"] = child.coord.col, ["row"] = child.coord.row });

        return new JsonObject
        {
            ["coord"] = new JsonObject { ["col"] = point.coord.col, ["row"] = point.coord.row },
            ["type"] = StringHelper.SnakeCase(point.PointType.ToString()),
            ["can_modify"] = point.CanBeModified,
            ["children"] = children,
        };
    }

    private static JsonObject BuildPlayerObject(RunState run, PlayerSnapshot inventory)
    {
        var player = run.Players[0];
        var deck = new JsonArray();
        for (int i = 0; i < player.Deck.Cards.Count; i++)
        {
            CardModel card = player.Deck.Cards[i];
            var cardObj = new JsonObject
            {
                ["id"] = card.Id.ToString(),
                ["current_upgrade_level"] = card.CurrentUpgradeLevel,
            };
            if (card.FloorAddedToDeck is int floor)
                cardObj["floor_added_to_deck"] = floor;

            IReadOnlyDictionary<string, JsonElement> exportedProps =
                inventory.Deck[i].Props;
            if (exportedProps.TryGetValue("props", out JsonElement innerProps))
                cardObj["props"] = JsonNode.Parse(innerProps.GetRawText());

            EnchantmentModel? enchantment = card.Enchantments.SingleOrDefault();
            if (enchantment is not null)
            {
                cardObj["enchantment"] = new JsonObject
                {
                    ["id"] = enchantment.Id.ToString(),
                    ["amount"] = (int)enchantment.Magnitude,
                };
            }
            deck.Add(cardObj);
        }

        var relics = new JsonArray();
        for (int i = 0; i < player.Relics.Count; i++)
        {
            RelicModel relic = player.Relics[i];
            var relicObj = new JsonObject { ["id"] = relic.Id.ToString() };
            if (relic.FloorAddedToDeck is int relicFloor)
                relicObj["floor_added_to_deck"] = relicFloor;
            IReadOnlyDictionary<string, JsonElement> exportedProps =
                inventory.Relics[i].Props;
            if (exportedProps.TryGetValue("props", out JsonElement innerProps))
                relicObj["props"] = JsonNode.Parse(innerProps.GetRawText());
            relics.Add(relicObj);
        }

        var potions = new JsonArray();
        for (int i = 0; i < player.PotionSlots.Count; i++)
        {
            if (player.PotionSlots[i] is { } potion)
            {
                potions.Add(new JsonObject
                {
                    ["id"] = potion.Id.ToString(),
                    ["slot_index"] = i,
                });
            }
        }

        var unlockedEpochs = new JsonArray();
        foreach (string epoch in Schema21SaveData.VanillaEpochs)
            unlockedEpochs.Add(epoch);

        SerializablePlayerRngSet playerRng = player.PlayerRng.ToSerializable();
        var playerRngMap = new JsonObject();
        foreach ((PlayerRngType key, SerializableRng val) in playerRng.Rngs)
            playerRngMap[StringHelper.SnakeCase(key.ToString())] = SerializeRng(val);

        SerializablePlayerOddsSet playerOdds = player.Odds.ToSerializable();

        return new JsonObject
        {
            ["character_id"] = player.Character.Id.ToString(),
            ["current_hp"] = player.Creature.CurrentHp,
            ["max_hp"] = player.Creature.MaxHp,
            ["max_energy"] = player.MaxEnergy,
            ["max_potion_slot_count"] = player.MaxPotionCount,
            ["gold"] = player.Gold,
            ["base_orb_slot_count"] = player.BaseOrbSlotCount,
            ["deck"] = deck,
            ["relics"] = relics,
            ["potions"] = potions,
            ["rng"] = new JsonObject
            {
                ["seed"] = playerRng.Seed,
                ["rngs"] = playerRngMap,
            },
            ["odds"] = new JsonObject
            {
                ["card_rarity_odds_value"] = playerOdds.CardRarityOddsValue,
                ["potion_reward_odds_value"] = playerOdds.PotionRewardOddsValue,
            },
            ["relic_grab_bag"] = BuildRelicBagObject(inventory.RelicBag),
            ["unlock_state"] = new JsonObject
            {
                ["unlocked_epochs"] = unlockedEpochs,
            },
            ["extra_fields"] = new JsonObject
            {
                ["card_shop_removals_used"] = player.CardRemovalsUsed,
                ["damage_dealt"] = 0,
                ["debuffs_applied"] = 0,
                ["wongo_points"] = 0,
                ["ccccombo_badge_unlocked"] = false,
                ["is_playing_rotating_character"] = false,
            },
        };
    }

    private static JsonObject BuildRunRngObject(SerializableRunRngSet runRng)
    {
        var rngs = new JsonObject();
        foreach ((RunRngType key, SerializableRng val) in runRng.Rngs)
            rngs[StringHelper.SnakeCase(key.ToString())] = SerializeRng(val);
        return new JsonObject
        {
            ["seed"] = runRng.Seed,
            ["rngs"] = rngs,
        };
    }

    public static JsonObject SerializeRng(SerializableRng rng) => new()
    {
        ["counter"] = rng.counter,
        ["s0"] = rng.state0,
        ["s1"] = rng.state1,
        ["s2"] = rng.state2,
        ["s3"] = rng.state3,
    };

    private static JsonObject BuildRunOddsObject(SerializableRunOddsSet odds) => new()
    {
        ["unknown_map_point_monster_odds_value"] = odds.UnknownMapPointMonsterOddsValue,
        ["unknown_map_point_elite_odds_value"] = odds.UnknownMapPointEliteOddsValue,
        ["unknown_map_point_treasure_odds_value"] = odds.UnknownMapPointTreasureOddsValue,
        ["unknown_map_point_shop_odds_value"] = odds.UnknownMapPointShopOddsValue,
    };

    private static JsonObject BuildRelicBagObject(RelicBagSnapshot bag)
    {
        var lists = new JsonObject();
        foreach ((string rarity, IReadOnlyList<string> ids) in bag.Buckets)
            lists[StringHelper.SnakeCase(rarity)] = ToJsonStringArray(ids);
        return new JsonObject { ["relic_id_lists"] = lists };
    }

    private static JsonArray BuildVisitedCoordsArray(IReadOnlyList<MapCoord> coords)
    {
        var arr = new JsonArray();
        foreach (MapCoord c in coords)
            arr.Add(new JsonObject { ["col"] = c.col, ["row"] = c.row });
        return arr;
    }

    private static JsonArray BuildMapPointHistory(
        RunState run,
        IReadOnlyList<JsonObject>? priorActHistories,
        IReadOnlyList<JsonObject>? currentActHistory)
    {
        var all = new JsonArray();
        int priorCount = run.TotalFloor - run.VisitedMapCoords.Count;
        for (int actIdx = 0; actIdx < run.CurrentActIndex; actIdx++)
        {
            var actHist = new JsonArray();
            if (actIdx == 0 && priorActHistories is null)
            {
                for (int f = 0; f < priorCount; f++)
                    actHist.Add(MakeHistoryEntry("monster", "monster"));
            }
            else if (actIdx == 0 && priorActHistories is not null)
            {
                foreach (JsonObject entry in priorActHistories)
                    actHist.Add(entry.DeepClone());
            }
            all.Add(actHist);
        }

        var currentArr = new JsonArray();
        if (currentActHistory is not null)
        {
            foreach (JsonObject entry in currentActHistory)
                currentArr.Add(entry.DeepClone());
        }
        else
        {
            foreach (MapCoord coord in run.VisitedMapCoords)
            {
                MapPointType pt = run.Map.GetPoint(coord)?.PointType ?? MapPointType.Monster;
                string roomType = pt == MapPointType.Shop ? "shop" : pt == MapPointType.RestSite ? "rest_site" : "monster";
                currentArr.Add(MakeHistoryEntry(StringHelper.SnakeCase(pt.ToString()), roomType));
            }
        }
        all.Add(currentArr);
        return all;
    }

    private static JsonArray ToJsonStringArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (string v in values)
            arr.Add(v);
        return arr;
    }

}
