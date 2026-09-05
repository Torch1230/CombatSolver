using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private const int MaxBulkCardInjectionCount = 4096;

    private static void ClearRunDeck(RunState runState, Player player)
    {
        CardModel[] cards = player.Deck.Cards.ToArray();
        player.Deck.Clear(silent: true);
        foreach (CardModel card in cards)
            runState.RemoveCard(card);
        if (player.Deck.Cards.Count != 0)
            throw new InvalidOperationException("无人测试未能完整清空跑局牌组。");
    }

    private static async Task ApplyRunSnapshotAsync(
        RunState runState,
        Player player,
        string snapshotPath)
    {
        if (!File.Exists(snapshotPath))
            throw new FileNotFoundException("找不到无人测试跑局快照。", snapshotPath);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath));
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("rng", out _))
            LoadRunRng(runState, root);
        JsonElement savedPlayer = root.GetProperty("players")[0];
        if (savedPlayer.TryGetProperty("rng", out JsonElement savedPlayerRng))
            player.PlayerRng.LoadFromSerializable(ParsePlayerRng(savedPlayerRng));
        if (savedPlayer.TryGetProperty("odds", out JsonElement savedPlayerOdds))
        {
            player.PlayerOdds.LoadFromSerializable(new SerializablePlayerOddsSet
            {
                CardRarityOddsValue = savedPlayerOdds
                    .GetProperty("card_rarity_odds_value")
                    .GetSingle(),
                PotionRewardOddsValue = savedPlayerOdds
                    .GetProperty("potion_reward_odds_value")
                    .GetSingle(),
            });
        }
        if (savedPlayer.TryGetProperty("gold", out JsonElement savedGold))
            player.Gold = savedGold.GetInt32();

        ClearRunDeck(runState, player);
        foreach (JsonElement savedCard in savedPlayer.GetProperty("deck").EnumerateArray())
        {
            CardModel card = runState.LoadCard(ParseSerializableCard(savedCard), player);
            player.Deck.AddInternal(card, -1);
        }

        foreach (RelicModel relic in player.Relics.ToArray())
            player.RemoveRelicInternal(relic, silent: true);
        foreach (JsonElement savedRelic in savedPlayer.GetProperty("relics").EnumerateArray())
            player.AddRelicInternal(RelicModel.FromSerializable(ParseSerializableRelic(savedRelic)), silent: true);

        if (savedPlayer.TryGetProperty("max_potion_slot_count", out JsonElement savedMaxPotionSlots))
        {
            int targetCount = savedMaxPotionSlots.GetInt32();
            int difference = targetCount - player.MaxPotionCount;
            if (difference > 0)
                player.AddToMaxPotionCount(difference);
            else if (difference < 0)
                player.SubtractFromMaxPotionCount(-difference);
        }

        if (savedPlayer.TryGetProperty("potions", out JsonElement savedPotions))
        {
            if (player.PotionSlots.Any(potion => potion != null))
                throw new InvalidOperationException("应用跑局快照药水前测试玩家的药水槽并非全空。");
            foreach (JsonElement savedPotion in savedPotions.EnumerateArray())
            {
                string potionId = savedPotion.GetProperty("id").GetString()
                    ?? throw new InvalidOperationException("跑局快照药水没有 ID。");
                int slot = savedPotion.GetProperty("slot_index").GetInt32();
                PotionModel potion = ResolveUnique(ModelDb.AllPotions, potionId, "药水").ToMutable();
                if (!player.AddPotionInternal(potion, slot, silent: false).success)
                    throw new InvalidOperationException($"无法把跑局快照药水 {potionId} 恢复到槽位 {slot}。");
            }
        }

        bool hasMaxHp = savedPlayer.TryGetProperty("max_hp", out JsonElement savedMaxHp);
        bool hasCurrentHp = savedPlayer.TryGetProperty("current_hp", out JsonElement savedCurrentHp);
        if (hasMaxHp != hasCurrentHp)
            throw new InvalidOperationException("跑局快照必须同时声明 max_hp 和 current_hp。");
        if (hasMaxHp)
        {
            await CreatureCmd.SetMaxHp(player.Creature, savedMaxHp.GetInt32());
            await CreatureCmd.SetCurrentHp(player.Creature, savedCurrentHp.GetInt32());
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
    }

    private static void ReloadRunSnapshotRng(
        RunState runState,
        Player player,
        string snapshotPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(snapshotPath));
        JsonElement root = document.RootElement;
        LoadRunRng(runState, root);
        JsonElement savedPlayer = root.GetProperty("players")[0];
        if (savedPlayer.TryGetProperty("rng", out JsonElement savedPlayerRng))
            player.PlayerRng.LoadFromSerializable(ParsePlayerRng(savedPlayerRng));
        if (savedPlayer.TryGetProperty("odds", out JsonElement savedPlayerOdds))
        {
            player.PlayerOdds.LoadFromSerializable(new SerializablePlayerOddsSet
            {
                CardRarityOddsValue = savedPlayerOdds
                    .GetProperty("card_rarity_odds_value")
                    .GetSingle(),
                PotionRewardOddsValue = savedPlayerOdds
                    .GetProperty("potion_reward_odds_value")
                    .GetSingle(),
            });
        }
    }

    private static void LoadRunRng(RunState runState, JsonElement root)
    {
        SerializableRunRngSet rng = ParseRunRng(root.GetProperty("rng"));
        runState.Rng.LoadFromSerializable(rng);
    }

    private static SerializableRunRngSet ParseRunRng(JsonElement element)
    {
        SerializableRunRngSet result = new()
        {
            Seed = element.GetProperty("seed").GetString()
                ?? throw new InvalidOperationException("跑局快照 RNG 没有种子。"),
        };
        foreach (JsonProperty property in element.GetProperty("rngs").EnumerateObject())
        {
            RunRngType type = property.Name switch
            {
                "up_front" => RunRngType.UpFront,
                "shuffle" => RunRngType.Shuffle,
                "unknown_map_point" => RunRngType.UnknownMapPoint,
                "combat_card_generation" => RunRngType.CombatCardGeneration,
                "combat_potion_generation" => RunRngType.CombatPotionGeneration,
                "combat_card_selection" => RunRngType.CombatCardSelection,
                "combat_energy_costs" => RunRngType.CombatEnergyCosts,
                "combat_targets" => RunRngType.CombatTargets,
                "monster_ai" => RunRngType.MonsterAi,
                "niche" => RunRngType.Niche,
                "combat_orbs" => RunRngType.CombatOrbs,
                "treasure_room_relics" => RunRngType.TreasureRoomRelics,
                _ => throw new InvalidOperationException($"跑局快照包含未知 RNG {property.Name}。"),
            };
            JsonElement value = property.Value;
            result.Rngs[type] = new SerializableRng
            {
                counter = value.GetProperty("counter").GetInt32(),
                state0 = value.GetProperty("s0").GetUInt64(),
                state1 = value.GetProperty("s1").GetUInt64(),
                state2 = value.GetProperty("s2").GetUInt64(),
                state3 = value.GetProperty("s3").GetUInt64(),
            };
        }
        return result;
    }

    private static SerializablePlayerRngSet ParsePlayerRng(JsonElement element)
    {
        SerializablePlayerRngSet result = new()
        {
            Seed = element.GetProperty("seed").GetUInt64(),
        };
        foreach (JsonProperty property in element.GetProperty("rngs").EnumerateObject())
        {
            PlayerRngType type = property.Name switch
            {
                "rewards" => PlayerRngType.Rewards,
                "shops" => PlayerRngType.Shops,
                "transformations" => PlayerRngType.Transformations,
                _ => throw new InvalidOperationException($"跑局快照包含未知玩家 RNG {property.Name}。"),
            };
            JsonElement value = property.Value;
            result.Rngs[type] = new SerializableRng
            {
                counter = value.GetProperty("counter").GetInt32(),
                state0 = value.GetProperty("s0").GetUInt64(),
                state1 = value.GetProperty("s1").GetUInt64(),
                state2 = value.GetProperty("s2").GetUInt64(),
                state3 = value.GetProperty("s3").GetUInt64(),
            };
        }
        return result;
    }

    private static SerializableCard ParseSerializableCard(JsonElement element)
    {
        SerializableCard result = new()
        {
            Id = ModelId.Deserialize(element.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("跑局快照卡牌没有 ID。")),
            CurrentUpgradeLevel = element.TryGetProperty("current_upgrade_level", out JsonElement upgrade)
                ? upgrade.GetInt32()
                : 0,
            FloorAddedToDeck = element.TryGetProperty("floor_added_to_deck", out JsonElement floor)
                ? floor.GetInt32()
                : null,
        };
        if (element.TryGetProperty("enchantment", out JsonElement enchantment))
        {
            result.Enchantment = new SerializableEnchantment
            {
                Id = ModelId.Deserialize(enchantment.GetProperty("id").GetString()
                    ?? throw new InvalidOperationException("跑局快照附魔没有 ID。")),
                Amount = enchantment.GetProperty("amount").GetInt32(),
            };
        }
        if (element.TryGetProperty("props", out JsonElement props))
            result.Props = ParseSavedProperties(props);
        return result;
    }

    private static SerializableRelic ParseSerializableRelic(JsonElement element)
    {
        SerializableRelic result = new()
        {
            Id = ModelId.Deserialize(element.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("跑局快照遗物没有 ID。")),
            FloorAddedToDeck = element.TryGetProperty("floor_added_to_deck", out JsonElement floor)
                ? floor.GetInt32()
                : null,
        };
        if (element.TryGetProperty("props", out JsonElement props))
            result.Props = ParseSavedProperties(props);
        return result;
    }

    private static SavedProperties ParseSavedProperties(JsonElement element)
    {
        SavedProperties result = new();
        if (element.TryGetProperty("ints", out JsonElement ints))
        {
            result.ints = ints.EnumerateArray()
                .Select(item => new SavedProperties.SavedProperty<int>(
                    item.GetProperty("name").GetString()
                        ?? throw new InvalidOperationException("整数保存属性没有名称。"),
                    item.GetProperty("value").GetInt32()))
                .ToList();
        }
        if (element.TryGetProperty("bools", out JsonElement bools))
        {
            result.bools = bools.EnumerateArray()
                .Select(item => new SavedProperties.SavedProperty<bool>(
                    item.GetProperty("name").GetString()
                        ?? throw new InvalidOperationException("布尔保存属性没有名称。"),
                    item.GetProperty("value").GetBoolean()))
                .ToList();
        }
        if (element.TryGetProperty("strings", out JsonElement strings))
        {
            result.strings = strings.EnumerateArray()
                .Select(item => new SavedProperties.SavedProperty<string>(
                    item.GetProperty("name").GetString()
                        ?? throw new InvalidOperationException("字符串保存属性没有名称。"),
                    item.GetProperty("value").GetString() ?? string.Empty))
                .ToList();
        }
        return result;
    }

    private static async Task PrepareSearchBoundaryStateAsync(
        CombatState combatState,
        Player player,
        Creature enemy,
        UnattendedMonsterMoveCheck check)
    {
        if (check.PlayerHpBefore is { } playerHp)
            await CreatureCmd.SetCurrentHp(player.Creature, Math.Clamp(playerHp, 1, player.Creature.MaxHp));
        if (check.PlayerBlockBefore is { } playerBlock)
            await SetBlockAsync(player.Creature, playerBlock);
        if (check.PlayerEnergyBefore is { } playerEnergy)
            SetEnergy(player, playerEnergy);
        if (check.PlayerStarsBefore is { } playerStars)
            SetStars(player, playerStars);
        if (check.EnemyHpBefore is { } enemyHp)
            await CreatureCmd.SetCurrentHp(enemy, Math.Clamp(enemyHp, 1, enemy.MaxHp));
        if (check.EnemyBlockBefore is { } enemyBlock)
            await SetBlockAsync(enemy, enemyBlock);
        if (check.ClearPlayerHandBeforeMove)
        {
            await CardCmd.Discard(
                new BlockingPlayerChoiceContext(),
                player.PlayerCombatState!.Hand.Cards.ToArray());
        }
        if (check.PowerBeforeMove is { } power)
            await InjectPowerAsync(combatState, player, power, enemy);
        foreach (UnattendedPowerInjection injectedPower in check.PowersBeforeMove)
            await InjectPowerAsync(combatState, player, injectedPower, enemy);
        if (check.CardBeforeMove is { } card)
            await InjectCardAsync(combatState, player, card);
        foreach (UnattendedCardInjection injectedCard in check.CardsBeforeMove)
            await InjectCardAsync(combatState, player, injectedCard);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static async Task<IReadOnlyList<CardModel>> InjectCardAsync(
        CombatState combatState,
        Player player,
        UnattendedCardInjection injection,
        bool restoreSnapshot = false)
    {
        if (!Enum.TryParse(injection.Pile, true, out PileType pileType)
            || pileType is not (PileType.Hand or PileType.Draw or PileType.Discard or PileType.Exhaust))
        {
            throw new InvalidOperationException($"无人测试不支持注入牌堆 {injection.Pile}。");
        }
        if (injection.Count is < 1 or > MaxBulkCardInjectionCount)
        {
            throw new InvalidOperationException(
                $"注入卡牌数量 {injection.Count} 超出 1-{MaxBulkCardInjectionCount}。");
        }

        CardModel canonical = ResolveUnique(ModelDb.AllCards, injection.CardId, "卡牌");
        List<CardModel> cards = [];
        for (int index = 0; index < injection.Count; index++)
        {
            CardModel card = combatState.CreateCard(canonical, player);
            if (injection.TreatAsDeckCard)
            {
                CardModel deckVersion = combatState.RunState.CreateCard(canonical, player);
                CardPileAddResult deckResult = await CardPileCmd.Add(deckVersion, PileType.Deck);
                if (!deckResult.success)
                    throw new InvalidOperationException($"游戏拒绝为测试卡牌 {canonical.Id} 建立跑局版本。");
                card.DeckVersion = deckVersion;
            }
            for (int level = 0; level < injection.UpgradeLevels && card.IsUpgradable; level++)
                CardCmd.Upgrade(card, CardPreviewStyle.None);
            foreach ((string key, int value) in injection.DynamicVars)
            {
                if (!card.DynamicVars.TryGetValue(key, out var dynamicVar))
                    throw new InvalidOperationException($"卡牌 {card.Id.Entry} 不存在动态变量 {key}。");
                dynamicVar.BaseValue = value;
            }
            ApplyCardEnumMembers(card, injection.EnumMembers);
            if (!string.IsNullOrWhiteSpace(injection.EnchantmentId))
            {
                EnchantmentModel enchantment = ResolveUnique(
                    ModelDb.DebugEnchantments,
                    injection.EnchantmentId,
                    "附魔").ToMutable();
                if (!enchantment.CanEnchant(card))
                {
                    throw new InvalidOperationException(
                        $"附魔 {enchantment.Id} 不能用于测试卡牌 {card.Id}。");
                }
                CardCmd.Enchant(enchantment, card, injection.EnchantmentAmount);
            }
            if (!string.IsNullOrWhiteSpace(injection.AfflictionId))
            {
                AfflictionModel affliction = ResolveUnique(
                    ModelDb.DebugAfflictions,
                    injection.AfflictionId,
                    "苦难").ToMutable();
                await CardCmd.Afflict(affliction, card, injection.AfflictionAmount);
            }
            if (restoreSnapshot)
            {
                // Snapshot cards already include their generation effects.
                CardPile pile = CardPile.Get(pileType, player)
                    ?? throw new InvalidOperationException($"Snapshot pile {pileType} is unavailable.");
                pile.AddInternal(card, -1);
            }
            else
            {
                CardPileAddResult result = await CardPileCmd.AddGeneratedCardToCombat(card, pileType, player);
                if (!result.success)
                    throw new InvalidOperationException($"游戏拒绝把 {canonical.Id} 注入 {pileType}。");
            }
            cards.Add(card);
        }
        return cards;
    }

    private static async Task InjectRunCardAsync(
        RunState runState,
        Player player,
        UnattendedCardInjection injection)
    {
        if (injection.Count is < 1 or > MaxBulkCardInjectionCount)
        {
            throw new InvalidOperationException(
                $"注入跑局卡牌数量 {injection.Count} 超出 1-{MaxBulkCardInjectionCount}。");
        }
        CardModel canonical = ResolveUnique(ModelDb.AllCards, injection.CardId, "卡牌");
        for (int index = 0; index < injection.Count; index++)
        {
            CardModel card = runState.CreateCard(canonical, player);
            for (int level = 0; level < injection.UpgradeLevels && card.IsUpgradable; level++)
                CardCmd.Upgrade(card, CardPreviewStyle.None);
            foreach ((string key, int value) in injection.DynamicVars)
            {
                if (!card.DynamicVars.TryGetValue(key, out var dynamicVar))
                    throw new InvalidOperationException($"卡牌 {card.Id.Entry} 不存在动态变量 {key}。");
                dynamicVar.BaseValue = value;
            }
            ApplyCardEnumMembers(card, injection.EnumMembers);
            if (!string.IsNullOrWhiteSpace(injection.EnchantmentId))
            {
                EnchantmentModel enchantment = ResolveUnique(
                    ModelDb.DebugEnchantments,
                    injection.EnchantmentId,
                    "附魔").ToMutable();
                if (!enchantment.CanEnchant(card))
                    throw new InvalidOperationException($"附魔 {enchantment.Id} 不能用于测试卡牌 {card.Id}。");
                CardCmd.Enchant(enchantment, card, injection.EnchantmentAmount);
            }
            CardPileAddResult result = await CardPileCmd.Add(card, PileType.Deck);
            if (!result.success)
                throw new InvalidOperationException($"游戏拒绝把 {canonical.Id} 注入跑局牌组。");
        }
    }

    private static void ApplyCardEnumMembers(
        CardModel card,
        IReadOnlyDictionary<string, string> members)
    {
        foreach ((string memberName, string serializedValue) in members)
        {
            bool assigned = false;
            for (Type? type = card.GetType(); type != null && !assigned; type = type.BaseType)
            {
                const BindingFlags flags = BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly;
                if (type.GetProperty(memberName, flags) is { } property)
                {
                    if (!property.PropertyType.IsEnum || property.SetMethod == null)
                        throw new InvalidOperationException($"卡牌 {card.Id.Entry} 成员 {memberName} 不是可写枚举属性。");
                    property.SetValue(card, Enum.Parse(property.PropertyType, serializedValue, ignoreCase: true));
                    assigned = true;
                }
                else if (type.GetField(memberName, flags) is { } field)
                {
                    if (!field.FieldType.IsEnum || field.IsInitOnly)
                        throw new InvalidOperationException($"卡牌 {card.Id.Entry} 成员 {memberName} 不是可写枚举字段。");
                    field.SetValue(card, Enum.Parse(field.FieldType, serializedValue, ignoreCase: true));
                    assigned = true;
                }
            }
            if (!assigned)
                throw new InvalidOperationException($"卡牌 {card.Id.Entry} 不存在枚举成员 {memberName}。");
        }
    }

    private static async Task InjectOrbAsync(Player player, UnattendedOrbInjection injection)
    {
        if (injection.Count is < 1 or > 20)
            throw new InvalidOperationException($"注入充能球数量 {injection.Count} 超出 1-20。");
        OrbModel canonical = ResolveUnique(ModelDb.Orbs, injection.OrbId, "充能球");
        var choiceContext = new BlockingPlayerChoiceContext();
        for (int index = 0; index < injection.Count; index++)
        {
            OrbModel orb = canonical.ToMutable();
            foreach ((string memberName, decimal value) in injection.DecimalMembers)
                SetObjectStateMember(orb, orb.Id.Entry, memberName, value);
            await OrbCmd.Channel(choiceContext, orb, player);
        }
    }

    private static async Task InjectRelicAsync(
        Player player,
        UnattendedRelicInjection injection)
    {
        RelicModel canonical = ResolveUnique(ModelDb.AllRelics, injection.RelicId, "遗物");
        RelicModel relic = canonical.ToMutable();
        if (injection.AddWithoutObtainedEffects)
            player.AddRelicInternal(relic);
        else
            relic = await RelicCmd.Obtain(relic, player);
        foreach ((string memberName, int value) in injection.IntegerMembers)
            SetRelicStateMember(relic, memberName, value);
        foreach ((string memberName, bool value) in injection.BooleanMembers)
            SetRelicStateMember(relic, memberName, value);
    }

    private static void SetRelicStateMember<T>(RelicModel relic, string memberName, T value)
    {
        for (Type? type = relic.GetType(); type != null; type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly;
            if (type.GetProperty(memberName, flags) is { } property)
            {
                if (property.PropertyType != typeof(T) || property.SetMethod == null)
                {
                    throw new InvalidOperationException(
                        $"遗物 {relic.Id.Entry} 状态属性 {memberName} 不是可写的 {typeof(T).Name}。");
                }
                property.SetValue(relic, value);
                return;
            }
            if (type.GetField(memberName, flags) is { } field)
            {
                if (field.FieldType != typeof(T) || field.IsInitOnly)
                {
                    throw new InvalidOperationException(
                        $"遗物 {relic.Id.Entry} 状态字段 {memberName} 不是可写的 {typeof(T).Name}。");
                }
                field.SetValue(relic, value);
                return;
            }
        }
        throw new InvalidOperationException($"遗物 {relic.Id.Entry} 不存在状态成员 {memberName}。");
    }

    private static async Task InjectPowerAsync(
        CombatState combatState,
        Player player,
        UnattendedPowerInjection injection,
        Creature? checkedEnemy = null)
    {
        if (injection.Amount is < 1 or > 999_999_999)
            throw new InvalidOperationException($"注入 Power 数值 {injection.Amount} 超出 1-999999999。");

        Creature owner = ResolvePowerInjectionCreature(
            combatState,
            player,
            checkedEnemy,
            injection.Target,
            injection.TargetIndex,
            "所有者");
        Creature powerTarget = string.IsNullOrWhiteSpace(injection.PowerTarget)
            ? owner
            : ResolvePowerInjectionCreature(
                combatState,
                player,
                checkedEnemy,
                injection.PowerTarget,
                injection.PowerTargetIndex,
                "效果目标");
        Creature applier = string.IsNullOrWhiteSpace(injection.Applier)
            ? player.Creature
            : ResolvePowerInjectionCreature(
                combatState,
                player,
                checkedEnemy,
                injection.Applier,
                injection.ApplierIndex,
                "施加者");
        PowerModel canonical = ResolveUnique(ModelDb.AllPowers, injection.PowerId, "Power");
        PowerModel power = canonical.ToMutable();
        foreach ((string key, int value) in injection.DynamicVars)
        {
            if (!power.DynamicVars.TryGetValue(key, out var dynamicVar))
                throw new InvalidOperationException($"Power {power.Id.Entry} 不存在动态变量 {key}。");
            dynamicVar.BaseValue = value;
        }
        power.Target = powerTarget;
        await PowerCmd.Apply(
            new BlockingPlayerChoiceContext(),
            power,
            owner,
            injection.Amount,
            applier,
            null);
        if (injection.InternalIntegerMembers.Count > 0)
        {
            PowerModel appliedPower = owner.Powers.Last(candidate => candidate.Id == canonical.Id);
            object internalData = appliedPower switch
            {
                AutomationPower automation => automation.GetInternalData<AutomationPower.Data>(),
                HellraiserPower hellraiser => hellraiser.GetInternalData<HellraiserPower.Data>(),
                VoidFormPower voidForm => voidForm.GetInternalData<VoidFormPower.Data>(),
                _ => throw new InvalidOperationException(
                    $"测试尚不支持设置 Power {appliedPower.Id.Entry} 的内部整数状态。"),
            };
            foreach ((string memberName, int value) in injection.InternalIntegerMembers)
                SetObjectStateMember(internalData, appliedPower.Id.Entry, memberName, value);
        }
    }

    private static void SetObjectStateMember<T>(object target, string ownerId, string memberName, T value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();
        if (type.GetField(memberName, flags) is { } field)
        {
            if (field.FieldType != typeof(T) || field.IsInitOnly)
                throw new InvalidOperationException($"{ownerId} 内部字段 {memberName} 不是可写的 {typeof(T).Name}。");
            field.SetValue(target, value);
            return;
        }
        if (type.GetProperty(memberName, flags) is { } property)
        {
            if (property.PropertyType != typeof(T) || property.SetMethod == null)
                throw new InvalidOperationException($"{ownerId} 内部属性 {memberName} 不是可写的 {typeof(T).Name}。");
            property.SetValue(target, value);
            return;
        }
        throw new InvalidOperationException($"{ownerId} 不存在内部状态成员 {memberName}。");
    }

    private static Creature ResolvePowerInjectionCreature(
        CombatState combatState,
        Player player,
        Creature? checkedEnemy,
        string selector,
        int index,
        string role)
        => selector switch
        {
            "Player" => player.Creature,
            "Osty" when player.Osty != null => player.Osty,
            "CheckedEnemy" when checkedEnemy != null => checkedEnemy,
            "Enemy" when index >= 0 && index < combatState.Enemies.Count => combatState.Enemies[index],
            _ => throw new InvalidOperationException($"无效的 Power 注入{role} {selector}[{index}]。"),
        };

    private static async Task EnsureMonsterExistsAsync(
        CombatState combatState,
        string monsterId,
        string? spawnInitialMoveId)
    {
        if (combatState.Enemies.Any(candidate =>
                candidate.IsAlive
                && candidate.Monster != null
                && ModelMatches(candidate.Monster, monsterId)))
        {
            return;
        }
        await AddMonsterForTestAsync(combatState, monsterId, spawnInitialMoveId);
    }

    private static async Task AddMonsterForTestAsync(
        CombatState combatState,
        string monsterId,
        string? spawnInitialMoveId)
    {
        MonsterModel monster = ResolveMonsterForTest(monsterId);
        EncounterModel encounter = combatState.Encounter
            ?? throw new InvalidOperationException("当前战斗没有 Encounter，无法为无人测试选择怪物槽位。");
        string? slot = encounter.GetNextSlot(combatState);
        if (string.IsNullOrWhiteSpace(slot))
            slot = null;
        if (string.IsNullOrWhiteSpace(spawnInitialMoveId))
        {
            await CreatureCmd.Add(monster, combatState, CombatSide.Enemy, slot);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            return;
        }

        Creature creature = combatState.CreateCreature(monster, CombatSide.Enemy, slot);
        combatState.AddCreature(creature);
        CombatManager.Instance.AddCreature(creature);
        MonsterMoveStateMachine machine = monster.MoveStateMachine
            ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
        if (!machine.States.TryGetValue(spawnInitialMoveId, out MonsterState? state)
            || state is not MoveState)
        {
            throw new InvalidOperationException(
                $"怪物 {monster.Id.Entry} 没有出生初始行动 {spawnInitialMoveId}。");
        }
        machine.ForceCurrentState(state);
        NCombatRoom.Instance?.AddCreature(creature);
        await CombatManager.Instance.AfterCreatureAdded(creature);
        if (combatState.CurrentSide != CombatSide.Enemy)
        {
            creature.PrepareForNextTurn(
                combatState.Players.Select(static current => current.Creature),
                rollNewMove: false);
        }
        await Hook.AfterCreatureAddedToCombat(combatState, creature);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static Creature ResolveEnemyByIndex(CombatState combatState, int enemyIndex)
    {
        if (enemyIndex < 0 || enemyIndex >= combatState.Enemies.Count)
            throw new InvalidOperationException($"怪物行动测试目标索引 {enemyIndex} 越界。");
        return combatState.Enemies[enemyIndex];
    }

    private static bool ModelMatches(AbstractModel model, string input)
        => model.Id.ToString().Equals(input, StringComparison.OrdinalIgnoreCase)
            || model.Id.Entry.Equals(input, StringComparison.OrdinalIgnoreCase)
            || model.GetType().Name.Equals(input, StringComparison.OrdinalIgnoreCase);

    private static MonsterModel ResolveMonsterForTest(string input)
    {
        MonsterModel[] registered = ModelDb.Monsters
            .Where(candidate => ModelMatches(candidate, input))
            .DistinctBy(static candidate => candidate.Id)
            .Take(2)
            .ToArray();
        if (registered.Length == 1)
            return registered[0].ToMutable();
        if (registered.Length > 1)
            throw new InvalidOperationException($"怪物 {input} 不唯一，请使用完整模型 ID。");

        Type[] exactTypes = typeof(MonsterModel).Assembly.GetTypes()
            .Where(type => !type.IsAbstract
                && typeof(MonsterModel).IsAssignableFrom(type)
                && (type.Name.Equals(input, StringComparison.OrdinalIgnoreCase)
                    || type.FullName?.Equals(input, StringComparison.OrdinalIgnoreCase) == true))
            .Take(2)
            .ToArray();
        if (exactTypes.Length == 0)
            throw new InvalidOperationException($"找不到怪物 {input}。");
        if (exactTypes.Length > 1)
            throw new InvalidOperationException($"怪物类型 {input} 不唯一，请使用完整类型名。");

        ModelId id = ModelDb.GetId(exactTypes[0]);
        MonsterModel canonical = ModelDb.GetByIdOrNull<MonsterModel>(id)
            ?? throw new InvalidOperationException($"游戏模型库未返回测试怪物 {id} 的规范实例。");
        return canonical.ToMutable();
    }

    private static TModel ResolveUnique<TModel>(
        IEnumerable<TModel> candidates,
        string input,
        string kind)
        where TModel : AbstractModel
    {
        TModel[] matches = candidates.Where(candidate => ModelMatches(candidate, input))
            .DistinctBy(static candidate => candidate.Id)
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"找不到{kind} {input}。"),
            _ => throw new InvalidOperationException($"{kind} {input} 不唯一，请使用完整模型 ID。"),
        };
    }
}
