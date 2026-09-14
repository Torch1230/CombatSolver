using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

// Scenario construction only. This RNG never touches the game's run/combat streams.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GeneratedModelSelection
{
    public int? Count { get; init; }
    public string?[] Ids { get; init; } = [];
    public int UpgradeLevels { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GeneratedCombatScenarioOptions
{
    public int SchemaVersion { get; init; } = 1;
    public string Seed { get; init; } = "GENERATED-COMBAT-001";
    public string? CharacterId { get; init; }
    public int Ascension { get; init; } = 10;
    public int? ActIndex { get; init; }
    public string? EncounterId { get; init; }
    public string EncounterKind { get; init; } = "EliteOrBoss";
    public bool IncludeStartingDeck { get; init; } = true;
    public bool IncludeStartingRelics { get; init; } = true;
    public bool IncludeAscendersBane { get; init; } = true;
    public bool ApplyRelicObtainEffects { get; init; }
    public GeneratedModelSelection CharacterCards { get; init; } = new() { Count = 8 };
    public GeneratedModelSelection ColorlessCards { get; init; } = new() { Count = 2 };
    public GeneratedModelSelection Relics { get; init; } = new() { Count = 5 };
    public GeneratedModelSelection Potions { get; init; } = new() { Count = 2 };
    public string[][]? SetupChoices { get; init; }
    public int? PlayerCurrentHp { get; init; }
    public int? PotionSlotCount { get; init; }
    public string Mode { get; init; } = "Search";
}

internal sealed record ResolvedGeneratedCombatScenario(
    GeneratedCombatScenarioOptions Options,
    CharacterModel Character,
    EncounterModel Encounter,
    string CatalogFingerprint,
    object Catalog);

internal static class GeneratedCombatScenario
{
    internal static readonly JsonSerializerOptions JsonOptions = UnattendedTestFiles.JsonOptions;

    internal static ResolvedGeneratedCombatScenario Resolve(GeneratedCombatScenarioOptions options)
    {
        if (options.CharacterCards == null || options.ColorlessCards == null || options.Relics == null || options.Potions == null
            || options.SchemaVersion != 1 || string.IsNullOrWhiteSpace(options.Seed)
            || options.Ascension is < 0 or > 10
            || options.Mode is not ("Setup" or "Search" or "Deploy")
            || options.EncounterKind is not ("EliteOrBoss" or "Elite" or "Boss" or "Monster")
            || options.PotionSlotCount is < 0 or > 10 || options.PlayerCurrentHp is <= 0)
            throw new InvalidDataException("生成场景的版本、种子、A等级、模式、遭遇类型、生命或药水槽无效。");
        CharacterModel[] characters = Native(ModelDb.AllCharacters);
        CharacterModel character = options.CharacterId is { } id
            ? Find(characters, id, "角色")
            : characters[new Generator(options.Seed, "character").Next(characters.Length)];
        var acts = ActModel.GetDefaultList();
        if (options.ActIndex is { } act && (uint)act >= (uint)acts.Count)
            throw new InvalidDataException($"幕索引必须在0到{acts.Count - 1}之间。");
        var encounters = acts.SelectMany((model, index) => model.AllEncounters
                .Where(e => e.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss)
                .Select(e => (Index: index, Encounter: e)))
            .Where(x => x.Encounter.GetType().Assembly == typeof(EncounterModel).Assembly)
            .OrderBy(x => x.Index).ThenBy(x => x.Encounter.Id.Entry, StringComparer.Ordinal).ToArray();
        var candidates = encounters.Where(x => options.ActIndex == null || x.Index == options.ActIndex)
            .Where(x => options.EncounterId is { } requested
                ? Matches(x.Encounter, requested)
                : options.EncounterKind == "EliteOrBoss"
                    ? x.Encounter.RoomType is RoomType.Elite or RoomType.Boss
                    : x.Encounter.RoomType.ToString() == options.EncounterKind).ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException("指定幕/遭遇类型/遭遇ID没有可用的原版普通战斗遭遇。");
        var selected = candidates[new Generator(options.Seed, "encounter").Next(candidates.Length)];
        CardModel[] characterCards = Native(character.CardPool.AllCards.Where(IsSingleplayerCard));
        CardModel[] colorlessCards = Native(ModelDb.CardPool<ColorlessCardPool>().AllCards.Where(IsSingleplayerCard));
        RelicModel[] relics = Native(character.RelicPool.AllRelics
            .Concat(ModelDb.RelicPool<SharedRelicPool>().AllRelics).Where(IsSingleplayerRelic));
        PotionModel[] potions = Native(character.PotionPool.AllPotions
            .Concat(ModelDb.PotionPool<SharedPotionPool>().AllPotions));
        string[] reservedRelics = options.IncludeStartingRelics
            ? character.StartingRelics.Select(r => r.Id.Entry).ToArray() : [];
        var resolved = options with
        {
            CharacterId = character.Id.Entry,
            ActIndex = selected.Index,
            EncounterId = selected.Encounter.Id.Entry,
            EncounterKind = selected.Encounter.RoomType.ToString(),
            CharacterCards = Select(options.CharacterCards, characterCards,
                characterCards.Where(IsRewardCard), options.Seed, "characterCards", distinct: false),
            ColorlessCards = Select(options.ColorlessCards, colorlessCards,
                colorlessCards.Where(IsRewardCard), options.Seed, "colorlessCards", distinct: false),
            Relics = Select(options.Relics, Native(ModelDb.AllRelics.Where(IsSingleplayerRelic)),
                relics.Where(r => r.Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare or RelicRarity.Shop),
                options.Seed, "relics", distinct: true, reservedRelics),
            Potions = Select(options.Potions, potions, potions, options.Seed, "potions", distinct: false),
        };
        object catalog = new
        {
            characters = characters.Select(x => x.Id.Entry).ToArray(),
            encounters = encounters.Select(x => new { actIndex = x.Index, id = x.Encounter.Id.Entry, roomType = x.Encounter.RoomType }).ToArray(),
            characterCards = characterCards.Select(x => x.Id.Entry).ToArray(),
            colorlessCards = colorlessCards.Select(x => x.Id.Entry).ToArray(),
            randomRelics = relics.Where(r => r.Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare or RelicRarity.Shop)
                .Select(x => x.Id.Entry).ToArray(),
            potions = potions.Select(x => x.Id.Entry).ToArray(),
        };
        string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions)));
        return new(resolved, character, selected.Encounter, fingerprint, catalog);
    }

    // Native MassiveScroll.IsAllowed requires multiple players and its reward is multiplayer-only.
    // Other IsAllowed predicates govern floors/rewards, not singleplayer legality.
    internal static bool IsSingleplayerRelic(RelicModel relic) => relic is not MassiveScroll;

    internal static bool IsSingleplayerCard(CardModel card)
        => card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly;

    private static bool IsRewardCard(CardModel card)
        => card.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare;

    private static T[] Native<T>(IEnumerable<T> models) where T : AbstractModel
        => models.Where(x => x.GetType().Assembly == typeof(AbstractModel).Assembly)
            .DistinctBy(x => x.Id).OrderBy(x => x.Id.Entry, StringComparer.Ordinal).ToArray();

    private static bool Matches(AbstractModel model, string id)
        => string.Equals(model.Id.Entry, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(model.Id.ToString(), id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(model.GetType().Name, id, StringComparison.OrdinalIgnoreCase);

    private static T Find<T>(IEnumerable<T> pool, string id, string category) where T : AbstractModel
    {
        T[] matches = pool.Where(x => Matches(x, id)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"{category}的ID必须唯一且属于允许池：{id}（匹配{matches.Length}项）。");
        return matches[0];
    }

    private static GeneratedModelSelection Select<T>(GeneratedModelSelection specification,
        IEnumerable<T> explicitPool, IEnumerable<T> randomPool, string seed, string domain,
        bool distinct, string[]? reserved = null) where T : AbstractModel
    {
        if (specification.Ids == null) throw new InvalidDataException($"{domain}.ids不能为null。");
        int count = specification.Count ?? specification.Ids.Length;
        if (count < specification.Ids.Length || count is < 0 or > 100
            || specification.UpgradeLevels is < 0 or > 1
            || (typeof(T) != typeof(CardModel) && specification.UpgradeLevels != 0))
            throw new InvalidDataException($"{domain}数量必须在0到100之间且不小于ids长度；卡牌升级级别仅支持0/1。");
        string?[] result = new string?[count];
        HashSet<string> used = new(reserved ?? [], StringComparer.Ordinal);
        // Reserve explicit entries first, so an earlier random slot cannot steal a later explicit relic.
        for (int index = 0; index < specification.Ids.Length; index++)
        {
            if (specification.Ids[index] is not { } id) continue;
            string canonicalId = Find(explicitPool, id, domain).Id.Entry;
            if (distinct && !used.Add(canonicalId))
                throw new InvalidDataException($"{domain}不能重复或与初始遗物重复：{canonicalId}。");
            result[index] = canonicalId;
        }
        var generator = new Generator(seed, domain);
        List<T> available = randomPool.Where(x => !distinct || !used.Contains(x.Id.Entry))
            .DistinctBy(x => x.Id).OrderBy(x => x.Id.Entry, StringComparer.Ordinal).ToList();
        for (int index = 0; index < result.Length; index++)
        {
            if (result[index] != null) continue;
            if (available.Count == 0)
                throw new InvalidDataException($"{domain}随机池不足，不能满足请求的{count}项。");
            int choice = generator.Next(available.Count);
            result[index] = available[choice].Id.Entry;
            if (distinct) available.RemoveAt(choice);
        }
        return specification with { Count = count, Ids = result };
    }

    internal static UnattendedTestRequest Apply(UnattendedTestRequest request, ResolvedGeneratedCombatScenario resolved)
    {
        if (!string.IsNullOrWhiteSpace(request.CheckpointArchivePath) || !string.IsNullOrWhiteSpace(request.RunSnapshotPath)
            || !string.IsNullOrWhiteSpace(request.ReplayStatePath) || !string.IsNullOrWhiteSpace(request.NativeStatePath)
            || request.RunCards.Length != 0 || request.Relics.Length != 0 || request.CombatRelics.Length != 0
            || request.Potions.Length != 0 || request.Powers.Length != 0 || request.ModifierIds.Length != 0
            || request.HoldAfterInitialSearch
            || !string.IsNullOrWhiteSpace(request.ReplayPolicyOverridePath))
            throw new InvalidDataException("生成场景不能同时恢复问题包/快照、使用归档策略覆盖、保持搜索挂起或注入另一套牌组、遗物、药水、Power、自定义规则。");
        var spec = resolved.Options;
        JsonObject json = JsonSerializer.SerializeToNode(request, JsonOptions)!.AsObject();
        void Set<T>(string key, T value) => json[key] = JsonSerializer.SerializeToNode(value, JsonOptions);
        Set("characterId", spec.CharacterId);
        Set("encounterId", spec.EncounterId);
        Set("seed", spec.Seed);
        Set("ascension", spec.Ascension);
        Set("actIndexForTest", spec.ActIndex);
        Set("targetRoomType", resolved.Encounter.RoomType);
        Set("targetMapPointType", resolved.Encounter.RoomType.ToString());
        Set("preserveNativeCombatStateForTest", true);
        Set("cards", Array.Empty<UnattendedCardInjection>());
        Set("clearRunDeck", !spec.IncludeStartingDeck);
        Set<int?>("preCombatPlayerCurrentHpOverride", null);
        Set("runCards", spec.CharacterCards.Ids.Select(id => new UnattendedCardInjection
            { CardId = id!, Count = 1, UpgradeLevels = spec.CharacterCards.UpgradeLevels })
            .Concat(spec.ColorlessCards.Ids.Select(id => new UnattendedCardInjection
            { CardId = id!, Count = 1, UpgradeLevels = spec.ColorlessCards.UpgradeLevels })).ToArray());
        Set("relics", spec.Relics.Ids.Select(id => new UnattendedRelicInjection
            { RelicId = id!, AddWithoutObtainedEffects = !spec.ApplyRelicObtainEffects }).ToArray());
        Set("potions", spec.Potions.Ids.Select(id => new UnattendedPotionInjection { PotionId = id! }).ToArray());
        Set("stopAfterCombatRootSnapshotAssertion", spec.Mode == "Setup");
        Set("stopAfterInitialSolverResultAssertion", spec.Mode == "Search");
        if (spec.Mode == "Deploy")
            Set("expectedUnexpectedReplansAtMost", request.ExpectedUnexpectedReplansAtMost ?? 0);
        Set("deploymentFastModeForTest", request.DeploymentFastModeForTest ?? SolverDeploymentFastMode.Instant);
        Set("deploymentInterActionDelaySecondsForTest", request.DeploymentInterActionDelaySecondsForTest ?? 0);
        Set("fixedSearchBudget", true);
        Set("searchBudgetOverrideMilliseconds", request.SearchBudgetOverrideMilliseconds ?? 1000);
        Set("performancePresetForTest", request.PerformancePresetForTest ?? SolverPerformancePreset.Medium);
        Set("potionPolicyForTest", request.PotionPolicyForTest ?? SolverPotionPolicy.Smart);
        return json.Deserialize<UnattendedTestRequest>(JsonOptions)!;
    }

    // SplitMix64 and rejection sampling are fixed independently of System.Random/runtime versions.
    private sealed class Generator
    {
        private ulong _state;
        internal Generator(string seed, string domain)
            => _state = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(domain + "\0" + seed)));
        private ulong Next()
        {
            ulong z = (_state += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        internal int Next(int count)
        {
            if (count <= 0) throw new InvalidDataException("随机池为空。");
            ulong divisor = (ulong)count;
            ulong threshold = unchecked(0UL - divisor) % divisor;
            ulong value;
            do value = Next(); while (value < threshold);
            return (int)(value % divisor);
        }
    }
}
