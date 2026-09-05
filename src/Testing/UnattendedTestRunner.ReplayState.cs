using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task ApplyReplayStateAsync(
        CombatState combatState,
        Player player,
        string replayStatePath,
        string? runSnapshotPath)
    {
        if (!File.Exists(replayStatePath))
            throw new FileNotFoundException("找不到无人测试中途战斗状态。", replayStatePath);
        if (string.IsNullOrWhiteSpace(runSnapshotPath) || !File.Exists(runSnapshotPath))
        {
            throw new InvalidOperationException(
                "中途战斗状态导入需要同一检查点的 run-state 快照，以恢复完整跑局和 RNG。");
        }

        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(replayStatePath));
        JsonElement root = document.RootElement;
        int schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        if (schemaVersion != 1)
            throw new InvalidOperationException($"不支持 replay-state schemaVersion={schemaVersion}。");
        string expectedEncounterId = RequiredString(root, "encounterId");
        if (combatState.Encounter == null
            || !ModelMatches(combatState.Encounter, expectedEncounterId))
        {
            throw new InvalidOperationException(
                $"replay-state 遭遇为 {expectedEncounterId}，当前夹具为 " +
                $"{combatState.Encounter?.Id.Entry ?? "-"}。");
        }
        string currentSide = RequiredString(root, "currentSide");
        if (!string.Equals(currentSide, combatState.CurrentSide.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"replay-state 阵营为 {currentSide}，当前夹具为 {combatState.CurrentSide}。");
        }

        JsonElement[] savedPlayers = root.GetProperty("players").EnumerateArray().ToArray();
        if (savedPlayers.Length != 1)
            throw new InvalidOperationException("replay-state 导入器只支持单人战斗。");
        JsonElement savedPlayer = savedPlayers[0];
        string expectedCharacterId = RequiredString(savedPlayer, "characterId");
        if (!ModelMatches(player.Character, expectedCharacterId))
        {
            throw new InvalidOperationException(
                $"replay-state 角色为 {expectedCharacterId}，当前夹具为 {player.Character.Id.Entry}。");
        }

        await RestoreReplayCreaturesAsync(combatState, root.GetProperty("creatures"));
        CombatState replayCombat = combatState;
        replayCombat.RoundNumber = root.GetProperty("roundNumber").GetInt32();
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("replay-state 导入时玩家没有战斗状态。");
        int turnNumber = savedPlayer.GetProperty("turnNumber").GetInt32();
        if (turnNumber < playerState.TurnNumber)
        {
            throw new InvalidOperationException(
                $"replay-state 回合 {turnNumber} 早于当前夹具回合 {playerState.TurnNumber}。");
        }
        while (playerState.TurnNumber < turnNumber)
            playerState.IncrementTurnNumber();
        string expectedPhase = RequiredString(savedPlayer, "phase");
        if (!string.Equals(expectedPhase, playerState.Phase.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"replay-state 玩家阶段为 {expectedPhase}，当前夹具为 {playerState.Phase}。");
        }
        SetEnergy(player, savedPlayer.GetProperty("energy").GetInt32());
        SetStars(player, savedPlayer.GetProperty("stars").GetInt32());
        player.Gold = savedPlayer.GetProperty("gold").GetInt32();

        RestoreReplayInventory(player, savedPlayer);
        await RestoreReplayOrbsAsync(player, savedPlayer.GetProperty("orbs"));
        await ClearPlayerPilesAsync(player);
        await RestoreReplayPilesAsync(combatState, player, savedPlayer.GetProperty("piles"));
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        RebuildReplayDampenState(player);
        RestoreReplayTurnCardHistory(
            combatState,
            player,
            RequiredString(root, "exactContinuationState"));
        ReloadRunSnapshotRng((RunState)combatState.RunState, player, runSnapshotPath);

        string expectedState = RequiredString(root, "exactContinuationState");
        string actualState = ContinuationStamp.CaptureLive(combatState).StateText;
        if (!expectedState.Contains("/baselib=", StringComparison.Ordinal))
            actualState = actualState.Replace("/baselib=-", "", StringComparison.Ordinal);
        ContinuationStamp expected = new(expectedState);
        ContinuationStamp actual = new(actualState);
        if (!ReplayContinuationMatches(expected.StateText, actual.StateText))
        {
            throw new InvalidOperationException(
                "replay-state 严格导入不一致：" + expected.DescribeFirstDifference(actual));
        }
    }

    private static void RebuildReplayDampenState(Player player)
    {
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("replay-state 压制恢复时玩家没有战斗状态。");
        foreach (DampenPower power in player.Creature.Powers.OfType<DampenPower>())
        {
            object data = typeof(PowerModel)
                .GetField("_internalData", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(power)
                ?? throw new InvalidOperationException("replay-state 压制缺少内部状态。");
            Type dataType = data.GetType();
            var casters = (HashSet<Creature>)(dataType.GetField("casters")?.GetValue(data)
                ?? throw new MissingFieldException(dataType.FullName, "casters"));
            var originalUpgrades = (Dictionary<CardModel, int>)(dataType
                .GetField("downgradedCardsToOldUpgradeLevels")?.GetValue(data)
                ?? throw new MissingFieldException(dataType.FullName, "downgradedCardsToOldUpgradeLevels"));
            Creature caster = power.Applier is { CurrentHp: > 0 } applier
                ? applier
                : throw new InvalidOperationException("replay-state 压制没有存活的施法者。");
            casters.Clear();
            casters.Add(caster);
            originalUpgrades.Clear();
            foreach (CardModel card in playerState.AllCards)
            {
                int originalLevel = card.DeckVersion?.CurrentUpgradeLevel ?? card.CurrentUpgradeLevel;
                if (originalLevel > card.CurrentUpgradeLevel)
                    originalUpgrades.Add(card, originalLevel);
            }
        }
    }

    private static async Task RestoreReplayCreaturesAsync(
        CombatState combatState,
        JsonElement savedCreaturesElement)
    {
        JsonElement[] savedCreatures = savedCreaturesElement.EnumerateArray().ToArray();
        if (savedCreatures.Length != combatState.Creatures.Count)
        {
            throw new InvalidOperationException(
                $"replay-state 生物数 {savedCreatures.Length} 与当前夹具 " +
                $"{combatState.Creatures.Count} 不同。");
        }

        foreach (JsonElement saved in savedCreatures)
        {
            uint combatId = saved.GetProperty("combatId").GetUInt32();
            Creature creature = combatState.Creatures.SingleOrDefault(candidate =>
                    candidate.CombatId == combatId)
                ?? throw new InvalidOperationException($"当前夹具缺少 CombatId={combatId} 的生物。");
            string? monsterId = OptionalString(saved, "monsterId");
            if (monsterId == null)
            {
                ulong expectedPlayerNetId = saved.GetProperty("playerNetId").GetUInt64();
                if (creature.Player == null || creature.Player.NetId != expectedPlayerNetId)
                {
                    throw new InvalidOperationException($"CombatId={combatId} 不是 replay-state 中的玩家。");
                }
            }
            else if (creature.Monster == null || !ModelMatches(creature.Monster, monsterId))
            {
                throw new InvalidOperationException(
                    $"CombatId={combatId} 怪物为 {creature.Monster?.Id.Entry ?? "-"}，" +
                    $"replay-state 为 {monsterId}。");
            }

            await CreatureCmd.SetMaxHp(creature, saved.GetProperty("maxHp").GetInt32());
            await CreatureCmd.SetCurrentHp(creature, saved.GetProperty("currentHp").GetInt32());
            await SetBlockAsync(creature, saved.GetProperty("block").GetInt32());
            await RestoreReplayPowersAsync(combatState, creature, saved.GetProperty("powers"));
            if (creature.Monster != null)
                RestoreReplayMonsterMove(creature.Monster, saved);
        }
    }

    private static async Task RestoreReplayPowersAsync(
        CombatState combatState,
        Creature creature,
        JsonElement savedPowersElement)
    {
        JsonElement[] savedPowers = savedPowersElement.EnumerateArray().ToArray();
        List<PowerModel> unmatched = creature.Powers.ToList();
        HashSet<string> savedPowerIds = savedPowers
            .Select(saved => RequiredString(saved, "id"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (PowerModel extra in unmatched
                     .Where(power => !savedPowerIds.Contains(power.Id.Entry))
                     .ToArray())
        {
            await PowerCmd.Remove(extra);
            unmatched.Remove(extra);
        }

        foreach (JsonElement saved in savedPowers)
        {
            string id = RequiredString(saved, "id");
            PowerModel? power = unmatched.FirstOrDefault(candidate => ModelMatches(candidate, id));
            if (power != null)
            {
                unmatched.Remove(power);
            }
            else
            {
                PowerModel canonical = ResolveUnique(ModelDb.AllPowers, id, "Power");
                PowerModel injected = canonical.ToMutable();
                Creature? target = ReplayCreatureReference(combatState, saved, "targetCombatId");
                Creature applier = ReplayCreatureReference(combatState, saved, "applierCombatId")
                    ?? creature;
                injected.Target = target ?? creature;
                int applyAmount = Math.Max(1, saved.GetProperty("amount").GetInt32());
                await PowerCmd.Apply(
                    new BlockingPlayerChoiceContext(),
                    injected,
                    creature,
                    applyAmount,
                    applier,
                    null);
                power = creature.Powers.LastOrDefault(candidate => ModelMatches(candidate, id))
                    ?? throw new InvalidOperationException(
                        $"CombatId={creature.CombatId} 无法恢复 Power {id}，当前 Powers=" +
                        string.Join(',', creature.Powers.Select(candidate => candidate.Id.Entry)));
            }

            power.Amount = saved.GetProperty("amount").GetInt32();
            power.AmountOnTurnStart = saved.GetProperty("amountOnTurnStart").GetInt32();
            RestoreReplayPrimitiveState(power, typeof(PowerModel), saved.GetProperty("fields"));
            RestoreReplayDynamicVars(power, saved.GetProperty("dynamicVars"));
        }
        foreach (PowerModel extra in unmatched)
            await PowerCmd.Remove(extra);
    }

    private static Creature? ReplayCreatureReference(
        CombatState combatState,
        JsonElement saved,
        string propertyName)
    {
        JsonElement reference = saved.GetProperty(propertyName);
        if (reference.ValueKind == JsonValueKind.Null)
            return null;
        uint combatId = reference.GetUInt32();
        return combatState.Creatures.SingleOrDefault(candidate => candidate.CombatId == combatId)
            ?? throw new InvalidOperationException(
                $"replay-state {propertyName} 引用缺失的 CombatId={combatId}。");
    }

    private static void RestoreReplayMonsterMove(MonsterModel monster, JsonElement saved)
    {
        MonsterMoveStateMachine machine = monster.MoveStateMachine
            ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
        machine.StateLog.Clear();
        foreach (JsonElement stateElement in saved.GetProperty("moveStateLog").EnumerateArray())
        {
            string stateId = stateElement.GetString()
                ?? throw new InvalidOperationException("replay-state 怪物行动历史包含空 ID。");
            if (!machine.States.TryGetValue(stateId, out MonsterState? state))
                throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态 {stateId}。");
            machine.StateLog.Add(state);
        }
        string? nextMoveId = OptionalString(saved, "nextMoveId");
        if (nextMoveId == null)
            return;
        if (string.Equals(monster.NextMove?.Id, nextMoveId, StringComparison.Ordinal))
            return;
        if (!machine.States.TryGetValue(nextMoveId, out MonsterState? nextState)
            || nextState is not MoveState move)
        {
            throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动 {nextMoveId}。");
        }
        monster.SetMoveImmediate(move, true);
    }

    private static void RestoreReplayInventory(Player player, JsonElement savedPlayer)
    {
        JsonElement[] savedPotions = savedPlayer.GetProperty("potions").EnumerateArray().ToArray();
        if (savedPotions.Length != player.PotionSlots.Count)
            throw new InvalidOperationException("replay-state 药水槽数量与跑局快照不同。");
        for (int slot = 0; slot < savedPotions.Length; slot++)
        {
            string? expected = OptionalString(savedPotions[slot], "id");
            string? actual = player.GetPotionAtSlotIndex(slot)?.Id.Entry;
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"药水槽 {slot} 为 {actual ?? "-"}，replay-state 为 {expected ?? "-"}。");
            }
        }

        JsonElement[] savedRelics = savedPlayer.GetProperty("relics").EnumerateArray().ToArray();
        if (savedRelics.Length != player.Relics.Count)
            throw new InvalidOperationException("replay-state 遗物数量与跑局快照不同。");
        for (int index = 0; index < savedRelics.Length; index++)
        {
            JsonElement savedRelic = savedRelics[index];
            RelicModel relic = player.Relics[index];
            string expected = RequiredString(savedRelic, "id");
            if (!ModelMatches(relic, expected))
            {
                throw new InvalidOperationException(
                    $"遗物[{index}] 为 {relic.Id.Entry}，replay-state 为 {expected}。");
            }
            RestoreReplayPrimitiveState(relic, typeof(RelicModel), savedRelic.GetProperty("fields"));
        }
    }

    private static async Task RestoreReplayOrbsAsync(Player player, JsonElement savedOrbs)
    {
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("replay-state 导入充能球时玩家没有战斗状态。");
        int capacity = savedOrbs.GetProperty("capacity").GetInt32();
        if (capacity is < 0 or > 10)
            throw new InvalidOperationException($"replay-state 充能球槽位超出范围：{capacity}。");

        playerState.OrbQueue.Clear();
        playerState.OrbQueue.AddCapacity(capacity);
        JsonElement[] savedItems = savedOrbs.GetProperty("items").EnumerateArray().ToArray();
        if (savedItems.Length > capacity)
        {
            throw new InvalidOperationException(
                $"replay-state 充能球数量 {savedItems.Length} 超过槽位 {capacity}。");
        }

        for (int index = 0; index < savedItems.Length; index++)
        {
            JsonElement saved = savedItems[index];
            int savedIndex = saved.GetProperty("index").GetInt32();
            if (savedIndex != index)
            {
                throw new InvalidOperationException(
                    $"replay-state 充能球顺序不连续：位置 {index} 记录为 {savedIndex}。");
            }

            OrbModel orb = ResolveOrbForTest(RequiredString(saved, "id")).ToMutable();
            orb.Owner = player;
            RestoreReplayPrimitiveState(orb, typeof(OrbModel), saved.GetProperty("fields"));
            int expectedPassive = saved.GetProperty("passive").GetInt32();
            int expectedEvoke = saved.GetProperty("evoke").GetInt32();
            if (orb.PassiveVal != expectedPassive || orb.EvokeVal != expectedEvoke)
            {
                throw new InvalidOperationException(
                    $"replay-state 充能球 {orb.Id.Entry} 数值为 " +
                    $"{orb.PassiveVal}/{orb.EvokeVal}，记录为 {expectedPassive}/{expectedEvoke}。");
            }
            if (!await playerState.OrbQueue.TryEnqueue(orb))
                throw new InvalidOperationException($"replay-state 无法恢复充能球 {orb.Id.Entry}。");
        }
    }

    private static void RestoreReplayPrimitiveState(
        object target,
        Type baseType,
        JsonElement savedFields)
    {
        for (Type? type = target.GetType();
             type != null && type != baseType;
             type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Instance
                         | BindingFlags.Public
                         | BindingFlags.NonPublic
                         | BindingFlags.DeclaredOnly))
            {
                if (field.IsInitOnly
                    || !savedFields.TryGetProperty($"{type.Name}.{field.Name}", out JsonElement saved))
                {
                    continue;
                }

                object? value = DeserializeReplayPrimitive(field.FieldType, saved);
                if (value != null || saved.ValueKind == JsonValueKind.Null)
                    field.SetValue(target, value);
            }
        }
    }

    private static void RestoreReplayDynamicVars(
        PowerModel power,
        JsonElement savedDynamicVars)
    {
        foreach (JsonProperty property in savedDynamicVars.EnumerateObject())
        {
            if (!power.DynamicVars.TryGetValue(property.Name, out var dynamicVar))
            {
                throw new InvalidOperationException(
                    $"Power {power.Id.Entry} 不存在动态变量 {property.Name}。");
            }

            decimal baseValue = property.Value.GetProperty("baseValue").GetDecimal();
            dynamicVar.BaseValue = baseValue;
        }
    }

    private static object? DeserializeReplayPrimitive(Type fieldType, JsonElement saved)
    {
        Type valueType = Nullable.GetUnderlyingType(fieldType) ?? fieldType;
        if (saved.ValueKind == JsonValueKind.Null)
            return null;
        if (valueType.IsEnum)
        {
            string member = saved.GetString()
                ?? throw new InvalidOperationException($"replay-state 枚举 {valueType.Name} 为空。");
            return Enum.Parse(valueType, member, false);
        }
        if (valueType == typeof(bool))
            return saved.GetBoolean();
        if (valueType == typeof(byte))
            return saved.GetByte();
        if (valueType == typeof(short))
            return saved.GetInt16();
        if (valueType == typeof(int))
            return saved.GetInt32();
        if (valueType == typeof(long))
            return saved.GetInt64();
        if (valueType == typeof(float))
            return saved.GetSingle();
        if (valueType == typeof(double))
            return saved.GetDouble();
        if (valueType == typeof(decimal))
            return saved.GetDecimal();
        if (valueType == typeof(string))
            return saved.GetString();
        return null;
    }

    private static bool ReplayContinuationMatches(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
            return true;

        string[] expectedFields = expected.Split(';');
        string[] actualFields = actual.Split(';');
        if (expectedFields.Length != actualFields.Length)
            return false;
        for (int index = 0; index < expectedFields.Length; index++)
        {
            string expectedField = expectedFields[index];
            string actualField = actualFields[index];
            if (string.Equals(expectedField, actualField, StringComparison.Ordinal))
                continue;
            if (!expectedField.StartsWith("R=", StringComparison.Ordinal)
                || !actualField.StartsWith("R=", StringComparison.Ordinal)
                || !LegacyRngContinuationMatches(expectedField[2..], actualField[2..]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool LegacyRngContinuationMatches(string expected, string actual)
    {
        string[] expectedRngs = expected.Split('/');
        string[] actualRngs = actual.Split('/');
        if (expectedRngs.Length != actualRngs.Length)
            return false;
        for (int index = 0; index < expectedRngs.Length; index++)
        {
            string expectedRng = expectedRngs[index];
            string actualRng = actualRngs[index];
            if (!string.Equals(expectedRng, actualRng, StringComparison.Ordinal)
                && (expectedRng.Contains(':', StringComparison.Ordinal)
                    || !actualRng.StartsWith(expectedRng + ":", StringComparison.Ordinal)))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task RestoreReplayPilesAsync(
        CombatState combatState,
        Player player,
        JsonElement savedPilesElement)
    {
        foreach (JsonElement savedPile in savedPilesElement.EnumerateArray())
        {
            string pile = RequiredString(savedPile, "pile");
            JsonElement[] savedCards = savedPile.GetProperty("cards").EnumerateArray().ToArray();
            if (string.Equals(pile, "Play", StringComparison.OrdinalIgnoreCase))
            {
                if (savedCards.Length != 0)
                    throw new InvalidOperationException("replay-state 导入暂不支持非空 Play 牌堆。");
                continue;
            }
            foreach (JsonElement savedCard in savedCards)
            {
                UnattendedCardInjection injection = BuildReplayCardInjection(savedCard, pile);
                CardModel restored = (await InjectCardAsync(combatState, player, injection)).Single();
                AttachReplayDeckVersion(restored, savedCard, player);
                RestoreReplayPrimitiveState(
                    restored,
                    typeof(AbstractModel),
                    savedCard.GetProperty("fields"));
                JsonElement savedEnchantment = savedCard.GetProperty("enchantment");
                if (savedEnchantment.ValueKind != JsonValueKind.Null)
                {
                    if (restored.Enchantment == null)
                    {
                        throw new InvalidOperationException(
                            $"卡牌 {restored.Id.Entry} 缺少 replay-state 中的附魔 " +
                            $"{RequiredString(savedEnchantment, "id")}。");
                    }

                    RestoreReplayPrimitiveState(
                        restored.Enchantment,
                        typeof(AbstractModel),
                        savedEnchantment.GetProperty("fields"));
                }
                RestoreReplayCardKeywords(restored, savedCard.GetProperty("keywords"));
            }
        }
    }

    private static void RestoreReplayCardKeywords(CardModel card, JsonElement savedKeywordsElement)
    {
        HashSet<CardKeyword> savedKeywords = savedKeywordsElement
            .EnumerateArray()
            .Select(element => Enum.Parse<CardKeyword>(
                element.GetString()
                    ?? throw new InvalidOperationException("replay-state 卡牌关键词为空。"),
                false))
            .ToHashSet();
        foreach (CardKeyword keyword in card.Keywords.Where(keyword => !savedKeywords.Contains(keyword)).ToArray())
            card.RemoveKeyword(keyword);
        foreach (CardKeyword keyword in savedKeywords.Where(keyword => !card.Keywords.Contains(keyword)))
            card.AddKeyword(keyword);
    }

    private static void RestoreReplayTurnCardHistory(
        CombatState combatState,
        Player player,
        string continuationState)
    {
        const string marker = ";Y=";
        int start = continuationState.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException("replay-state 状态戳缺少本回合卡牌历史。");
        start += marker.Length;
        int end = continuationState.IndexOf(';', start);
        string[] values = continuationState[start..(end < 0 ? continuationState.Length : end)].Split('/');
        if (values.Length != 2
            || !int.TryParse(values[0], out int expectedStatusDraws)
            || !int.TryParse(values[1], out int expectedZeroCostAttackStarts))
        {
            throw new InvalidOperationException("replay-state 本回合卡牌历史格式无效。");
        }

        int actualStatusDraws = CombatManager.Instance.History.Entries
            .OfType<CardDrawnEntry>()
            .Count(entry => entry.HappenedThisTurn(combatState)
                && entry.Actor.Player == player
                && entry.Card.Type == CardType.Status);
        int actualZeroCostAttackStarts = CombatManager.Instance.History.CardPlaysStarted.Count(entry =>
            entry.HappenedThisTurn(combatState)
            && entry.CardPlay.Player == player
            && entry.CardPlay.Card.Type == CardType.Attack
            && entry.CardPlay.Resources.EnergyValue == 0);
        if (actualStatusDraws > expectedStatusDraws
            || actualZeroCostAttackStarts != expectedZeroCostAttackStarts)
        {
            throw new InvalidOperationException(
                $"无法精确恢复本回合卡牌历史：状态牌={actualStatusDraws}/{expectedStatusDraws}，" +
                $"零费攻击={actualZeroCostAttackStarts}/{expectedZeroCostAttackStarts}。");
        }

        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("replay-state 历史恢复时玩家没有战斗状态。");
        CardModel statusCard = playerState.Hand.Cards
            .Concat(playerState.DrawPile.Cards)
            .Concat(playerState.DiscardPile.Cards)
            .Concat(playerState.ExhaustPile.Cards)
            .FirstOrDefault(card => card.Type == CardType.Status)
            ?? combatState.CreateCard(
                ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Wound>(),
                player);
        for (int index = actualStatusDraws; index < expectedStatusDraws; index++)
        {
            CombatManager.Instance.History.Add(
                combatState,
                new CardDrawnEntry(
                    statusCard,
                    combatState.RoundNumber,
                    combatState.CurrentSide,
                    true,
                    CombatManager.Instance.History,
                    combatState.Players));
        }
    }

    private static UnattendedCardInjection BuildReplayCardInjection(
        JsonElement savedCard,
        string pile)
    {
        Dictionary<string, int> dynamicVars = new(StringComparer.Ordinal);
        foreach (JsonProperty property in savedCard.GetProperty("dynamicVars").EnumerateObject())
        {
            decimal baseValue = property.Value.GetProperty("baseValue").GetDecimal();
            if (baseValue != decimal.Truncate(baseValue)
                || baseValue < int.MinValue
                || baseValue > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"卡牌 {RequiredString(savedCard, "id")} 动态变量 {property.Name} " +
                    $"无法以整数恢复：{baseValue}。");
            }
            dynamicVars[property.Name] = decimal.ToInt32(baseValue);
        }
        Dictionary<string, string> enumMembers = new(StringComparer.Ordinal);
        foreach (JsonProperty field in savedCard.GetProperty("fields").EnumerateObject())
        {
            if (field.Name.EndsWith("._tinkerTimeType", StringComparison.Ordinal)
                || field.Name.EndsWith("._tinkerTimeRider", StringComparison.Ordinal))
            {
                string member = field.Name[(field.Name.LastIndexOf('.') + 1)..];
                enumMembers[member] = field.Value.GetString()
                    ?? throw new InvalidOperationException($"卡牌枚举字段 {field.Name} 为空。");
            }
        }
        JsonElement enchantment = savedCard.GetProperty("enchantment");
        JsonElement affliction = savedCard.GetProperty("affliction");
        return new UnattendedCardInjection
        {
            CardId = RequiredString(savedCard, "id"),
            Pile = pile,
            Count = 1,
            UpgradeLevels = savedCard.GetProperty("currentUpgradeLevel").GetInt32(),
            EnchantmentId = enchantment.ValueKind == JsonValueKind.Null
                ? null
                : RequiredString(enchantment, "id"),
            EnchantmentAmount = enchantment.ValueKind == JsonValueKind.Null
                ? 1
                : enchantment.GetProperty("amount").GetInt32(),
            AfflictionId = affliction.ValueKind == JsonValueKind.Null
                ? null
                : RequiredString(affliction, "id"),
            AfflictionAmount = affliction.ValueKind == JsonValueKind.Null
                ? 1
                : affliction.GetProperty("amount").GetInt32(),
            DynamicVars = dynamicVars,
            EnumMembers = enumMembers,
        };
    }

    private static void AttachReplayDeckVersion(
        CardModel restored,
        JsonElement savedCard,
        Player player)
    {
        JsonElement serialized = savedCard.GetProperty("serialized");
        if (!serialized.TryGetProperty("floor_added_to_deck", out JsonElement floor)
            || floor.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        int floorAdded = floor.GetInt32();
        CardModel[] matches = player.Deck.Cards.Where(candidate =>
                candidate.Id.Entry.Equals(restored.Id.Entry, StringComparison.Ordinal)
                && candidate.ToSerializable().FloorAddedToDeck == floorAdded
                && MatchesReplayDeckEnchantment(candidate, serialized))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"卡牌 {restored.Id.Entry}@{floorAdded} 找不到语义一致的跑局版本，" +
                "无法严格恢复 DeckVersion。");
        }
        restored.DeckVersion = matches[0];
    }

    private static bool MatchesReplayDeckEnchantment(CardModel candidate, JsonElement serialized)
    {
        if (!serialized.TryGetProperty("enchantment", out JsonElement savedEnchantment)
            || savedEnchantment.ValueKind == JsonValueKind.Null)
        {
            return candidate.Enchantment == null;
        }

        return candidate.Enchantment != null
               && ModelMatches(candidate.Enchantment, RequiredString(savedEnchantment, "id"))
               && candidate.Enchantment.Amount == savedEnchantment.GetProperty("amount").GetInt32();
    }

    private static string RequiredString(JsonElement element, string propertyName)
        => element.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"replay-state 字段 {propertyName} 为空。");

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        JsonElement value = element.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }
}
