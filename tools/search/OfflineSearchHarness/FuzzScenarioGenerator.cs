using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Dread;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

internal sealed record FuzzScenarioSpec
{
    public int ScenarioIndex { get; init; }
    public string Phase { get; init; } = "sweep";
    public string Tag { get; init; } = string.Empty;
    public int ActionRngSeed { get; init; }
    public int MaxTurns { get; init; } = 8;
    public required CombatSetupSpec Setup { get; init; }
}

internal sealed record RecordedFuzzChoice(
    HotChoiceKind Kind,
    string CardEntry,
    int UpgradeLevel,
    string StateKey,
    int GeneratedOptionIndex,
    int OptionOccurrence = 0);

internal sealed record RecordedFuzzAction(
    int StepIndex,
    int Turn,
    HotActionKind Kind,
    int SlotOrHandIdx,
    int TargetEnemySlot,
    string EntityId,
    RecordedFuzzChoice[] Choices);

internal sealed record FuzzFailureArtifact
{
    public string BucketKey { get; init; } = string.Empty;
    public string ActionEntityId { get; init; } = string.Empty;
    public string FirstDiffSection { get; init; } = string.Empty;
    public string ActionKind { get; init; } = string.Empty;
    public string ExceptionType { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string FirstDiffLine { get; init; } = string.Empty;
    public string ExpectedOutcome { get; init; } = string.Empty;
    public string ActualOutcome { get; init; } = string.Empty;
    public string? ExceptionMessage { get; init; }
    public string? ExceptionStack { get; init; }
    public required FuzzScenarioSpec Scenario { get; init; }
    public RecordedFuzzAction[] Steps { get; init; } = [];
}

/// <summary>
/// 覆盖引导的单人随机战斗生成器（Phase A 100% 实体清扫 + Phase B 高协同/容量边界压测）。
/// 严格排除 MultiplayerOnly / Mock / Deprecated 实体。
/// </summary>
internal static class FuzzScenarioGenerator
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] AllCharacters =
        ["IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER", "FOOL"];

    private sealed class SingleplayerPools
    {
        public required Dictionary<string, CardModel[]> CharacterCards { get; init; }
        public required Dictionary<string, CardModel[]> HighRiskCharacterCards { get; init; }
        public required Dictionary<string, CardModel[]> UltraHighRiskCharacterCards { get; init; }
        public required Dictionary<string, CardModel[]> ModerateCharacterCards { get; init; }
        public required CardModel[] SharedCards { get; init; }
        public required CardModel[] AllCards { get; init; }
        public required CardModel[] HighRiskCards { get; init; }
        public required CardModel[] UltraHighRiskCards { get; init; }
        public required CardModel[] ModerateCards { get; init; }
        public required RelicModel[] CombatRelics { get; init; }
        public required RelicModel[] HighRiskRelics { get; init; }
        public required RelicModel[] AllRelics { get; init; }
        public required PotionModel[] AllPotions { get; init; }
        public required EncounterModel[] AllEncounters { get; init; }
        public required EncounterModel[] HighRiskEncounters { get; init; }
        public required EnchantmentModel[] AllEnchantments { get; init; }
        public required AfflictionModel[] AllAfflictions { get; init; }
        public required OrbModel[] AllOrbs { get; init; }
        public required DominionModel[] AllDominions { get; init; }
        public required HashSet<string> ValidCardEntries { get; init; }
        public required HashSet<string> HighRiskCardEntries { get; init; }
        public required HashSet<string> ValidRelicEntries { get; init; }
        public required HashSet<string> HighRiskRelicEntries { get; init; }
        public required HashSet<string> ValidPotionEntries { get; init; }
        public required HashSet<string> ValidPowerEntries { get; init; }
        public required Dictionary<string, string> EncounterEntryByKey { get; init; }
        public required Dictionary<string, CharacterModel> CharacterByEntry { get; init; }
    }

    public static List<FuzzScenarioSpec> Generate(int fights, int seed, string phase, int maxTurns)
    {
        SingleplayerPools pools = CapturePools();
        List<FuzzScenarioSpec> result = new(fights);

        string normPhase = phase.ToLowerInvariant();
        if (normPhase == "sweep")
        {
            result.AddRange(BuildPhaseASweep(pools, fights, seed, maxTurns, startIndex: 0));
        }
        else if (normPhase == "synergy")
        {
            result.AddRange(BuildPhaseBSynergy(pools, fights, seed, maxTurns, startIndex: 0));
        }
        else if (normPhase == "high-risk")
        {
            // high-risk: 一半高风险实体清扫（严格高风险卡 + 挂钩战斗遗物 + 高风险遭遇 + 附魔/痛苦/Dominion），
            // 一半定向高风险多机制协同组合压测
            int sweepCount = fights <= 1 ? fights : fights / 2;
            int synergyCount = Math.Max(0, fights - sweepCount);
            result.AddRange(BuildPhaseHighRiskSweep(pools, sweepCount, seed, maxTurns, startIndex: 0));
            result.AddRange(BuildPhaseHighRiskSynergy(pools, synergyCount, seed ^ 0x5A5A_1337, maxTurns, startIndex: sweepCount));
        }
        else
        {
            // all: 优先保证 Phase A 扫清全部单人实体（~70-80 场），其余分配给 Phase B 协同与容量压测（23 个专项场景）
            int sweepCount = fights <= 30
                ? Math.Max(1, (int)Math.Round(fights * 0.6))
                : Math.Min(Math.Max(72, (int)Math.Round(fights * 0.67)), Math.Max(1, fights - 23));
            int synergyCount = Math.Max(0, fights - sweepCount);
            result.AddRange(BuildPhaseASweep(pools, sweepCount, seed, maxTurns, startIndex: 0));
            result.AddRange(BuildPhaseBSynergy(pools, synergyCount, seed ^ 0x5A5A_1337, maxTurns, startIndex: sweepCount));
        }

        return result;
    }

    private static List<FuzzScenarioSpec> BuildPhaseASweep(
        SingleplayerPools pools,
        int count,
        int seed,
        int maxTurns,
        int startIndex)
    {
        Random rng = new(seed);

        List<CardModel> cardQueue = ShuffleCopy(pools.AllCards, rng);
        List<RelicModel> relicQueue = ShuffleCopy(pools.CombatRelics, rng);
        List<PotionModel> potionQueue = ShuffleCopy(pools.AllPotions, rng);
        List<EncounterModel> encounterQueue = ShuffleCopy(pools.AllEncounters, rng);
        List<EnchantmentModel> enchQueue = ShuffleCopy(pools.AllEnchantments, rng);
        List<AfflictionModel> afflQueue = ShuffleCopy(pools.AllAfflictions, rng);
        List<DominionModel> dominionQueue = ShuffleCopy(pools.AllDominions, rng);
        List<OrbModel> orbQueue = ShuffleCopy(pools.AllOrbs, rng);

        // 先构建各角色专属卡队列，再把共享卡混入，确保每场都能刷出未覆盖卡
        int cardCursor = 0;
        int relicCursor = 0;
        int potionCursor = 0;
        int encounterCursor = 0;
        int enchCursor = 0;
        int afflCursor = 0;
        int dominionCursor = 0;
        int orbCursor = 0;

        int cardsPerFight = Math.Clamp((int)Math.Ceiling((double)cardQueue.Count / Math.Max(1, count)) + 1, 8, 16);
        int relicsPerFight = Math.Clamp((int)Math.Ceiling((double)relicQueue.Count / Math.Max(1, count)) + 1, 3, 7);
        int potionsPerFight = 3;

        List<FuzzScenarioSpec> scenarios = new(count);
        for (int i = 0; i < count; i++)
        {
            string charId = AllCharacters[i % AllCharacters.Length];
            // 保证 3 种模式循环覆盖，同时 Dread 至少覆盖全部 12 个 Dominion
            GameMode mode = (i % 5) switch
            {
                0 or 1 => GameMode.Standard,
                2 or 3 => GameMode.Dread,
                _ => GameMode.Blitz,
            };
            if (i < dominionQueue.Count * 2 && (i % 2 == 1))
                mode = GameMode.Dread;

            string? dominionId = null;
            if (mode == GameMode.Dread && dominionQueue.Count > 0)
            {
                dominionId = dominionQueue[dominionCursor % dominionQueue.Count].Id.Entry;
                dominionCursor++;
            }

            EncounterModel enc = encounterQueue[encounterCursor % encounterQueue.Count];
            encounterCursor++;

            // 挑选本场卡牌（优先消费未扫描卡牌，Blitz 模式下仅塞允许进 Blitz 的卡，避免模式冲突）
            List<CardModel> pickedCards = new(cardsPerFight);
            int scanGuard = 0;
            while (pickedCards.Count < cardsPerFight && scanGuard < cardQueue.Count * 2)
            {
                CardModel candidate = cardQueue[cardCursor % cardQueue.Count];
                cardCursor++;
                scanGuard++;
                if (mode == GameMode.Blitz && !candidate.IsAllowedInBlitz)
                    continue;
                pickedCards.Add(candidate);
            }

            // 挑 1~2 个附魔与 1 个痛苦贴到可兼容卡牌上
            List<EnchantmentModel> fightEnchs = [];
            for (int e = 0; e < 2 && enchQueue.Count > 0; e++)
            {
                fightEnchs.Add(enchQueue[enchCursor % enchQueue.Count]);
                enchCursor++;
            }
            AfflictionModel? fightAffl = afflQueue.Count > 0
                ? afflQueue[afflCursor++ % afflQueue.Count]
                : null;

            List<CardInjectionSpec> cardSpecs = new(pickedCards.Count);
            HashSet<int> enchantedIndices = [];
            int afflictedIndex = -1;
            for (int cIdx = 0; cIdx < pickedCards.Count; cIdx++)
            {
                CardModel c = pickedCards[cIdx];
                string? enchId = null;
                int enchAmt = 1;
                if (fightEnchs.Count > 0)
                {
                    for (int eIdx = 0; eIdx < fightEnchs.Count; eIdx++)
                    {
                        if (!enchantedIndices.Contains(eIdx) && RealCombatDriver.CanSafelyEnchant(fightEnchs[eIdx], c))
                        {
                            enchId = fightEnchs[eIdx].Id.Entry;
                            enchAmt = 1 + (rng.Next(2));
                            enchantedIndices.Add(eIdx);
                            break;
                        }
                    }
                }

                string? afflId = null;
                if (afflictedIndex < 0 && fightAffl != null && fightAffl.CanAfflict(c))
                {
                    afflId = fightAffl.Id.Entry;
                    afflictedIndex = cIdx;
                }

                int upgrade = (rng.Next(4) == 0 && c.MaxUpgradeLevel > 0) ? 1 : 0;
                cardSpecs.Add(new CardInjectionSpec(c.Id.Entry, upgrade, enchId, enchAmt, afflId, 1));
            }

            // 挑选本场战斗遗物
            List<string> pickedRelics = new(relicsPerFight);
            HashSet<string> usedRelicIds = new(StringComparer.Ordinal);
            int rGuard = 0;
            while (pickedRelics.Count < relicsPerFight && rGuard < relicQueue.Count * 2)
            {
                RelicModel r = relicQueue[relicCursor % relicQueue.Count];
                relicCursor++;
                rGuard++;
                if (mode == GameMode.Blitz && RelicLists.notAllowedInBlitz.Contains(r.Id))
                    continue;
                if (usedRelicIds.Add(r.Id.Entry))
                    pickedRelics.Add(r.Id.Entry);
            }

            // 挑选本场药水（全 67 种药水轮询）
            List<string> pickedPotions = new(potionsPerFight);
            for (int p = 0; p < potionsPerFight && potionQueue.Count > 0; p++)
            {
                pickedPotions.Add(potionQueue[potionCursor % potionQueue.Count].Id.Entry);
                potionCursor++;
            }

            // 充能球（Defect 或每 6 场轮换覆盖全部 5 种球）
            string[] orbs = [];
            if (charId == "DEFECT" || (i % 6 == 2))
            {
                orbs =
                [
                    orbQueue[orbCursor++ % orbQueue.Count].Id.Entry,
                    orbQueue[orbCursor++ % orbQueue.Count].Id.Entry,
                ];
            }

            int ascension = mode == GameMode.Blitz
                ? (i % 6)
                : ((i % 4 == 0) ? 10 : (i % 3));
            int actIndex = i % 3;
            string runSeed = $"SWEEP{seed:X4}_{i:D3}";

            scenarios.Add(new FuzzScenarioSpec
            {
                ScenarioIndex = startIndex + i,
                Phase = "sweep",
                Tag = $"sweep_{i:D3}_{charId.ToLowerInvariant()}_{mode.ToString().ToLowerInvariant()}_{enc.Id.Entry.ToLowerInvariant()}",
                ActionRngSeed = seed * 31 + i * 997 + 17,
                MaxTurns = maxTurns,
                Setup = new CombatSetupSpec
                {
                    CharacterId = charId,
                    EncounterId = enc.Id.Entry,
                    Seed = runSeed,
                    GameMode = mode,
                    DominionId = dominionId,
                    Ascension = ascension,
                    ActIndex = actIndex,
                    PlayerMaxHp = 110,
                    PlayerHp = 90,
                    ClearStarterDeck = false,
                    ClearStarterRelics = false,
                    Cards = cardSpecs.ToArray(),
                    Relics = pickedRelics.ToArray(),
                    PotionSlotCount = 4,
                    Potions = pickedPotions.ToArray(),
                    InitialOrbs = orbs,
                    ExtraEnergy = 2,
                    ExtraStars = charId == "REGENT" ? 3 : 0,
                },
            });
        }

        return scenarios;
    }

    private static List<FuzzScenarioSpec> BuildPhaseBSynergy(
        SingleplayerPools pools,
        int count,
        int seed,
        int maxTurns,
        int startIndex)
    {
        if (count <= 0)
            return [];

        Random rng = new(seed);
        List<Func<int, FuzzScenarioSpec>> factories =
        [
            // 1. Silent Discard / Sly
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "silent_discard_sly",
                "SILENT", PickEncounter(pools, "ExoskeletonsNormal", i), GameMode.Standard, null,
                cards: Cards(pools, ("ACROBATICS", 1), ("PREPARED", 1), ("CALCULATED_GAMBLE", 1),
                    ("TACTICIAN", 1), ("REFLEX", 1), ("TOOLS_OF_THE_TRADE", 1),
                    ("HAND_TRICK", 0), ("DAGGER_THROW", 0), ("BLADE_DANCE", 1), ("AFTERIMAGE", 0)),
                relics: Relics(pools, "GAMBLING_CHIP", "TOUGH_BANDAGES", "TINGSHA", "KUNAI"),
                potions: Potions(pools, "GAMBLERS_BREW", "SWIFT_POTION", "CUNNING_POTION")),

            // 2. Ironclad Exhaust / Anger-spam
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "ironclad_exhaust_anger",
                "IRONCLAD", PickEncounter(pools, "LivingFogNormal", i), GameMode.Standard, null,
                cards: Cards(pools, ("ANGER", 1), ("ANGER", 0), ("CORRUPTION", 1), ("FEEL_NO_PAIN", 1),
                    ("DARK_EMBRACE", 1), ("FIEND_FIRE", 0), ("BURNING_PACT", 1), ("TRUE_GRIT", 1),
                    ("SECOND_WIND", 0), ("OFFERING", 0)),
                relics: Relics(pools, "CHARONS_ASHES", "BURNING_BLOOD", "SHURIKEN", "PEN_NIB"),
                potions: Potions(pools, "BLOCK_POTION", "STRENGTH_POTION", "BLOOD_POTION")),

            // 3. Defect Orbs
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "defect_orbs",
                "DEFECT", PickEncounter(pools, "PhantasmalGardenersElite", i), GameMode.Standard, null,
                cards: Cards(pools, ("ZAP", 1), ("BALL_LIGHTNING", 0), ("COOLHEADED", 1),
                    ("DARKNESS", 1), ("FUSION", 1), ("DUALCAST", 1), ("MULTI_CAST", 0),
                    ("DEFRAGMENT", 1), ("BIASED_COGNITION", 0), ("LOOP", 1), ("TURBO", 0)),
                relics: Relics(pools, "EMOTION_CHIP", "GOLD_PLATED_CABLES"),
                potions: Potions(pools, "FOCUS_POTION", "ESSENCE_OF_DARKNESS", "POTION_OF_CAPACITY"),
                orbs: ["LIGHTNING_ORB", "FROST_ORB", "DARK_ORB", "PLASMA_ORB", "GLASS_ORB"]),

            // 4. Regent Stars / Forge
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "regent_stars_forge",
                "REGENT", PickEncounter(pools, "TheObscuraNormal", i), GameMode.Standard, null,
                cards: Cards(pools, ("VENERATE", 1), ("SOVEREIGN_BLADE", 1), ("THE_SMITH", 1),
                    ("WROUGHT_IN_WAR", 1), ("BLACK_HOLE", 0), ("CHILD_OF_THE_STARS", 1),
                    ("ARSENAL", 0), ("ALIGNMENT", 1), ("BIG_BANG", 0), ("ASTRAL_PULSE", 1), ("BEAT_INTO_SHAPE", 0)),
                relics: Relics(pools, "GALACTIC_DUST", "MINI_REGENT"),
                potions: Potions(pools, "STAR_POTION", "KINGS_COURAGE", "RADIANT_TINCTURE"),
                extraStars: 5),

            // 5. Necrobinder Osty / Doom
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "necrobinder_osty_doom",
                "NECROBINDER", PickEncounter(pools, "OvicopterNormal", i), GameMode.Standard, null,
                cards: Cards(pools, ("BODYGUARD", 1), ("AFTERLIFE", 1), ("BONE_SHARDS", 0),
                    ("BLIGHT_STRIKE", 1), ("COUNTDOWN", 1), ("CALCIFY", 1), ("BORROWED_TIME", 0),
                    ("BANSHEES_CRY", 0), ("SCULPTING_STRIKE", 0), ("SNAP", 0), ("UNDEATH", 0)),
                relics: Relics(pools, "BOUND_PHYLACTERY", "BONE_FLUTE"),
                potions: Potions(pools, "BONE_BREW", "POTION_OF_DOOM", "POWDERED_DEMISE")),

            // 6. Fool Arrows / Melodies / Tarot
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "fool_arrows_melodies_tarot",
                "FOOL", PickEncounter(pools, "TerrorEelElite", i), GameMode.Standard, null,
                cards: Cards(pools, ("ARCHERY", 1), ("BLOSSOM_SHOT", 1), ("WHISTLING_ARROWS", 0),
                    ("WAH_CHO", 0), ("WAH_KI", 0), ("WAH_CHO_KI", 0), ("CODA", 1), ("YAH", 1),
                    ("THE_CHARIOT", 0), ("THE_EMPEROR", 0), ("THE_HIGH_PRIESTESS", 0),
                    ("WHEEL_OF_FORTUNE", 0), ("SHOW_OFF", 1), ("LOCKED_IN", 0), ("ARROW_STAB", 0), ("KI_CHANT", 1)),
                relics: Relics(pools, "CONDUCTORS_BATON", "METRONOME", "MUSIC_BOX"),
                potions: Potions(pools, "PRECISION_POTION", "BOTTLED_SONG", "ENERGY_POTION")),

            // 7. Multi-Choice Chains (filtered & sorted draw/hand/discard/generated choices)
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "multi_choice_chains",
                "IRONCLAD", PickEncounter(pools, "InfestedPrismsElite", i), GameMode.Standard, null,
                cards: Cards(pools, ("ARMAMENTS", 0), ("SECRET_WEAPON", 1), ("SECRET_TECHNIQUE", 1),
                    ("DUAL_WIELD", 1), ("HOLOGRAM", 1), ("HEADBUTT", 0), ("WELL_LAID_PLANS", 1),
                    ("DISCOVERY", 1), ("DECISIONS_DECISIONS", 0), ("HEIRLOOM_HAMMER", 0), ("BURNING_PACT", 0)),
                relics: Relics(pools, "GAMBLING_CHIP", "THROWING_AXE"),
                potions: Potions(pools, "LIQUID_MEMORIES", "DROPLET_OF_PRECOGNITION", "ATTACK_POTION", "COLORLESS_POTION")),

            // 8. Revive encounter (TestSubject)
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "revive_test_subject",
                "IRONCLAD", PickEncounter(pools, "TestSubjectBoss", i), GameMode.Standard, null,
                cards: Cards(pools, ("BLUDGEON", 1), ("BASH", 1), ("IMPERVIOUS", 1), ("OFFERING", 0),
                    ("ANGER", 1), ("INFLAME", 1), ("TWIN_STRIKE", 1)),
                relics: Relics(pools, "PAPER_PHROG", "PEN_NIB", "BURNING_BLOOD"),
                potions: Potions(pools, "FIRE_POTION", "STRENGTH_POTION", "DUPLICATOR")),

            // 9. Summon + FurCoat (Fabricator)
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "summon_fabricator_furcoat",
                "DEFECT", PickEncounter(pools, "FabricatorNormal", i), GameMode.Standard, null,
                cards: Cards(pools, ("ZAP", 1), ("BALL_LIGHTNING", 1), ("COOLHEADED", 1), ("BARRAGE", 1), ("CLAW", 1)),
                relics: Relics(pools, "FUR_COAT", "EMOTION_CHIP"),
                potions: Potions(pools, "EXPLOSIVE_AMPOULE", "BLOCK_POTION")),

            // 10. Escape / Theft (GremlinMerc + ThievingHopper)
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "escape_theft_gremlin_hopper",
                "SILENT", PickEncounter(pools, i % 2 == 0 ? "GremlinMercNormal" : "ThievingHopperWeak", i), GameMode.Standard, null,
                cards: Cards(pools, ("BACKSTAB", 1), ("BLADE_DANCE", 1), ("ACCURACY", 1), ("DAGGER_SPRAY", 1), ("BACKFLIP", 1)),
                relics: Relics(pools, "FUR_COAT", "SHURIKEN", "ORNAMENTAL_FAN"),
                potions: Potions(pools, "FIRE_POTION", "POISON_POTION")),

            // 11. SoulNexus Death Bypass (verifies SoulNexus.AfterDeath Harmony patch!)
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "soul_nexus_death_bypass",
                "IRONCLAD", PickEncounter(pools, "SoulNexusElite", i), GameMode.Standard, null,
                cards: Cards(pools, ("BLUDGEON", 1), ("BLUDGEON", 1), ("BLUDGEON", 1), ("BASH", 1),
                    ("OFFERING", 1), ("IMPERVIOUS", 1), ("FIEND_FIRE", 1)),
                relics: Relics(pools, "PAPER_PHROG", "PEN_NIB", "AKABEKO"),
                potions: Potions(pools, "STRENGTH_POTION", "VULNERABLE_POTION", "DUPLICATOR"),
                powers: [new PowerInjectionSpec("STRENGTH_POWER", 40, OnPlayer: true)],
                extraEnergy: 6),

            // 12. 0.113.1 Semantic Change: Adroit + StretchPower
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "v01131_adroit_stretch",
                "FOOL", PickEncounter(pools, "FuzzyWurmCrawlerWeak", i), GameMode.Standard, null,
                cards:
                [
                    new CardInjectionSpec("DEEP_STRETCH", 1),
                    new CardInjectionSpec("BOUNCE", 1),
                    new CardInjectionSpec("STRIKE_FOOL", 0, EnchantmentId: "ADROIT", EnchantmentAmount: 4),
                    new CardInjectionSpec("STRIKE_FOOL", 1, EnchantmentId: "ADROIT", EnchantmentAmount: 3),
                    new CardInjectionSpec("ARCHERY", 0),
                    new CardInjectionSpec("WAH_CHO", 0, EnchantmentId: "ADROIT", EnchantmentAmount: 2),
                ],
                relics: Relics(pools, "THUMBSCREW", "WUPPY"),
                potions: Potions(pools, "BLOCK_POTION", "DEXTERITY_POTION"),
                powers: [new PowerInjectionSpec("STRETCH_POWER", 5, OnPlayer: true)]),

            // 13. Alchemize + Full Belt + WhiteBeastStatue + EntropicBrew + FruitJuice
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "alchemize_entropic_fruitjuice",
                "SILENT", PickEncounter(pools, "FuzzyWurmCrawlerWeak", i), GameMode.Standard, null,
                cards: Cards(pools, ("ALCHEMIZE", 1), ("ALCHEMIZE", 0), ("ACROBATICS", 1), ("BACKFLIP", 1), ("BLADE_DANCE", 1)),
                relics: Relics(pools, "WHITE_BEAST_STATUE", "POTION_BELT", "BELT_BUCKLE", "REPTILE_TRINKET", "PETRIFIED_TOAD"),
                potions: Potions(pools, "ENTROPIC_BREW", "FRUIT_JUICE", "BLOCK_POTION", "BLOOD_POTION", "AMBERGRIS"),
                potionSlotCount: 5,
                playerHp: 55,
                playerMaxHp: 90),

            // 14. HistoryCourse + Thumbscrew (multi-turn auto-preplay + multi-hit attacks)
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "history_course_thumbscrew",
                "IRONCLAD", PickEncounter(pools, "ExoskeletonsNormal", i), GameMode.Standard, null,
                cards: Cards(pools, ("TWIN_STRIKE", 1), ("TWIN_STRIKE", 0), ("WHIRLWIND", 1),
                    ("BASH", 1), ("SHRUG_IT_OFF", 1), ("POMMEL_STRIKE", 1), ("ANGER", 0)),
                relics: Relics(pools, "HISTORY_COURSE", "THUMBSCREW", "PAPER_PHROG", "NUNCHAKU"),
                potions: Potions(pools, "STRENGTH_POTION", "ENERGY_POTION", "FIRE_POTION")),

            // 15. HardToKillPower (Exoskeleton) + RampartPower + ShriekPower + SkittishPower
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "hard_to_kill_and_monster_powers",
                "IRONCLAD", PickEncounter(pools, (i % 3) switch
                {
                    0 => "ExoskeletonsNormal",
                    1 => "TurretOperatorWeak",
                    _ => "PhantasmalGardenersElite",
                }, i), GameMode.Standard, null,
                cards: Cards(pools, ("BLUDGEON", 1), ("TWIN_STRIKE", 1), ("WHIRLWIND", 1), ("THUNDERCLAP", 1), ("IMPERVIOUS", 1)),
                relics: Relics(pools, "THUMBSCREW", "PAPER_PHROG"),
                potions: Potions(pools, "FIRE_POTION", "VULNERABLE_POTION"),
                powers:
                [
                    new PowerInjectionSpec("HARD_TO_KILL_POWER", 12, OnPlayer: false, EnemyIndex: 0),
                    new PowerInjectionSpec("STRENGTH_POWER", 8, OnPlayer: true),
                ]),

            // 16. Dread QueenDominion (YouAreMinePower) + Blitz mode stress
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "queen_dominion_you_are_mine",
                "NECROBINDER", PickEncounter(pools, "LivingFogNormal", i), GameMode.Dread, "QUEEN_DOMINION",
                cards: Cards(pools, ("BODYGUARD", 1), ("AFTERLIFE", 1), ("BONE_SHARDS", 1), ("BLIGHT_STRIKE", 1), ("COUNTDOWN", 0)),
                relics: Relics(pools, "BOUND_PHYLACTERY", "LIZARD_TAIL"),
                potions: Potions(pools, "AMBERGRIS", "BLOCK_POTION", "REGEN_POTION")),

            // 17. Phase 6 New SP Cards + Powers (Anointed, BeatDown, Catastrophe, Distraction, HiddenGem, MadScience, Metamorphosis, MyShinies, Entropy, Stampede)
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "phase6_cards_and_powers",
                "SILENT", PickEncounter(pools, "FuzzyWurmCrawlerWeak", i), GameMode.Standard, null,
                cards: Cards(pools, ("ALCHEMIZE", 0), ("ANOINTED", 0), ("BEAT_DOWN", 0), ("CATASTROPHE", 0),
                    ("DISTRACTION", 1), ("HIDDEN_GEM", 0), ("MAD_SCIENCE", 0), ("METAMORPHOSIS", 1),
                    ("MY_SHINIES", 0), ("ENTROPY", 0), ("STAMPEDE", 0)),
                relics: Relics(pools, "HISTORY_COURSE", "THUMBSCREW", "FUR_COAT"),
                potions: Potions(pools, "DISTILLED_CHAOS", "SNECKO_OIL", "TOUCH_OF_INSANITY"),
                extraEnergy: 5),

            // 18. Phase 6 Enchantments (Instinct, TezcatarasEmber, PerfectFit, SlumberingEssence, Spiral)
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "phase6_enchantments",
                "IRONCLAD", PickEncounter(pools, "ExoskeletonsWeak", i), GameMode.Standard, null,
                cards:
                [
                    new CardInjectionSpec("STRIKE_IRONCLAD", 0, EnchantmentId: "INSTINCT", EnchantmentAmount: 1),
                    new CardInjectionSpec("STRIKE_IRONCLAD", 1, EnchantmentId: "TEZCATARAS_EMBER", EnchantmentAmount: 5),
                    new CardInjectionSpec("DEFEND_IRONCLAD", 0, EnchantmentId: "SPIRAL", EnchantmentAmount: 1),
                    new CardInjectionSpec("POMMEL_STRIKE", 1, EnchantmentId: "PERFECT_FIT", EnchantmentAmount: 1),
                    new CardInjectionSpec("BLUDGEON", 0, EnchantmentId: "SLUMBERING_ESSENCE", EnchantmentAmount: 1),
                    new CardInjectionSpec("SHRUG_IT_OFF", 0, EnchantmentId: "ADROIT", EnchantmentAmount: 3),
                    new CardInjectionSpec("DEEP_STRETCH", 0),
                ],
                relics: Relics(pools, "THUMBSCREW", "POCKETWATCH"),
                potions: Potions(pools, "SWIFT_POTION", "FORTIFIER")),

            // 19. BattlewornDummy TimeLimitPower
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "battleworn_dummy_timelimit",
                "REGENT", PickEncounter(pools, "BattlewornDummyEventV1Encounter", i), GameMode.Standard, null,
                cards: Cards(pools, ("VENERATE", 1), ("SOVEREIGN_BLADE", 1), ("THE_SMITH", 1), ("ASTRAL_PULSE", 1)),
                relics: Relics(pools, "ART_OF_WAR", "PEN_NIB"),
                potions: Potions(pools, "STRENGTH_POTION", "STAR_POTION"),
                extraStars: 4),

            // 20. Blitz Mode Multi-Character Stress
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "blitz_mode_combat",
                AllCharacters[i % AllCharacters.Length], PickEncounter(pools, "ExoskeletonsNormal", i),
                GameMode.Blitz, null,
                cards: Cards(pools, ("ANGER", 1), ("SHRUG_IT_OFF", 1), ("POMMEL_STRIKE", 1), ("TWIN_STRIKE", 1)),
                relics: Relics(pools, "AKABEKO", "ANCHOR", "BAG_OF_PREPARATION"),
                potions: Potions(pools, "BLOCK_POTION", "FIRE_POTION", "ENERGY_POTION")),

            // 21. Capacity Stress A: >32 relics (42 relics) & >40 powers (45+ powers across player & enemies)
            i => BuildCapacityRelicsPowersStress(pools, startIndex + i, seed, maxTurns, rng),

            // 22. Capacity Stress B: >100 cards (110 cards in deck + Anger/BladeDance generation)
            i => BuildCapacityCardsStress(pools, startIndex + i, seed, maxTurns, rng),

            // 23. PhrogParasiteElite kill -> InfestedPower spawns 4 Wrigglers (live regression from #137!)
            i => MakeSynergy(pools, startIndex + i, seed, maxTurns, "phrog_parasite_infested_wrigglers",
                "IRONCLAD", PickEncounter(pools, "PhrogParasiteElite", i), GameMode.Standard, null,
                cards: Cards(pools, ("BLUDGEON", 1), ("BLUDGEON", 1), ("BASH", 1), ("OFFERING", 1),
                    ("WHIRLWIND", 1), ("TWIN_STRIKE", 1), ("THUNDERCLAP", 1), ("IMPERVIOUS", 1)),
                relics: Relics(pools, "PAPER_PHROG", "AKABEKO", "PEN_NIB", "BURNING_BLOOD"),
                potions: Potions(pools, "FIRE_POTION", "VULNERABLE_POTION", "STRENGTH_POTION"),
                powers: [new PowerInjectionSpec("STRENGTH_POWER", 25, OnPlayer: true)],
                extraEnergy: 6),
        ];

        List<FuzzScenarioSpec> list = new(count);
        for (int i = 0; i < count; i++)
            list.Add(factories[i % factories.Count](i));
        return list;
    }

    private static List<FuzzScenarioSpec> BuildPhaseHighRiskSweep(
        SingleplayerPools pools,
        int count,
        int seed,
        int maxTurns,
        int startIndex)
    {
        if (count <= 0)
            return [];

        Random rng = new(seed);

        List<CardModel> tier2Queue = ShuffleCopy(pools.UltraHighRiskCards, rng);
        List<CardModel> tier1Queue = ShuffleCopy(pools.ModerateCards, rng);
        List<RelicModel> relicQueue = ShuffleCopy(pools.HighRiskRelics, rng);
        List<RelicModel> statefulRelicQueue = ShuffleCopy(
            pools.HighRiskRelics.Where(HighRiskPoolFilter.IsStatefulOrTrackedRelic), rng);
        List<PotionModel> potionQueue = ShuffleCopy(pools.AllPotions, rng);
        List<EncounterModel> bossQueue = ShuffleCopy(
            pools.HighRiskEncounters.Where(static e => HighRiskPoolFilter.ClassifyEncounter(e, includeEventEncounters: true).PriorityTier == 3), rng);
        List<EncounterModel> eliteQueue = ShuffleCopy(
            pools.HighRiskEncounters.Where(static e => HighRiskPoolFilter.ClassifyEncounter(e, includeEventEncounters: true).PriorityTier == 2), rng);
        List<EncounterModel> complexNormalQueue = ShuffleCopy(
            pools.HighRiskEncounters.Where(static e => HighRiskPoolFilter.ClassifyEncounter(e, includeEventEncounters: true).PriorityTier == 1), rng);
        List<EnchantmentModel> enchQueue = ShuffleCopy(pools.AllEnchantments, rng);
        List<AfflictionModel> afflQueue = ShuffleCopy(pools.AllAfflictions, rng);
        List<DominionModel> dominionQueue = ShuffleCopy(pools.AllDominions, rng);
        List<OrbModel> orbQueue = ShuffleCopy(pools.AllOrbs, rng);

        int tier2Cursor = 0;
        int tier1Cursor = 0;
        int relicCursor = 0;
        int statefulCursor = 0;
        int potionCursor = 0;
        int bossCursor = 0, eliteCursor = 0, normalCursor = 0;
        int enchCursor = 0;
        int afflCursor = 0;
        int dominionCursor = 0;
        int orbCursor = 0;

        int tier2NeededPerFight = (int)Math.Ceiling((double)tier2Queue.Count / Math.Max(1, count));
        int tier1NeededPerFight = (int)Math.Ceiling((double)tier1Queue.Count / Math.Max(1, count));
        int sweptCardsPerFight = Math.Clamp(tier2NeededPerFight + tier1NeededPerFight + 1, 10, 15);
        // 严格 0% Tier 0，每场按 ~80% Tier 2 (UltraHighRisk) + ~20% Tier 1 (Moderate) 分层清扫，同时保证双队列 100% 覆盖
        int tier1PerFight = tier1Queue.Count == 0
            ? 0
            : Math.Clamp((int)Math.Round(sweptCardsPerFight * 0.20), 1, Math.Max(1, sweptCardsPerFight - 1));
        int tier2PerFight = Math.Max(0, sweptCardsPerFight - tier1PerFight);
        int relicsPerFight = Math.Clamp((int)Math.Ceiling((double)relicQueue.Count / Math.Max(1, count)) + 1, 4, 7);
        int potionsPerFight = 3;

        List<FuzzScenarioSpec> scenarios = new(count);
        for (int i = 0; i < count; i++)
        {
            string charId = AllCharacters[i % AllCharacters.Length];
            GameMode mode = (i % 5) switch
            {
                0 or 1 => GameMode.Standard,
                2 or 3 => GameMode.Dread,
                _ => GameMode.Blitz,
            };
            if (i < dominionQueue.Count * 2 && (i % 2 == 1))
                mode = GameMode.Dread;

            string? dominionId = null;
            if (mode == GameMode.Dread && dominionQueue.Count > 0)
            {
                dominionId = dominionQueue[dominionCursor % dominionQueue.Count].Id.Entry;
                dominionCursor++;
            }

            // 优先分层覆盖 Boss (35%) + Elite (35%) + 复杂机制 Normal (30%)
            EncounterModel enc = (i % 10) switch
            {
                0 or 3 or 6 when bossQueue.Count > 0 => bossQueue[bossCursor++ % bossQueue.Count],
                1 or 4 or 7 or 9 when eliteQueue.Count > 0 => eliteQueue[eliteCursor++ % eliteQueue.Count],
                _ when complexNormalQueue.Count > 0 => complexNormalQueue[normalCursor++ % complexNormalQueue.Count],
                _ => pools.HighRiskEncounters[i % pools.HighRiskEncounters.Length],
            };

            // 1. 清空初始打击/防御，保留 2~3 张低费可打出的高风险骨架牌（按 80% Tier2 / 20% Tier1 补足），避免全是无法打出的 Power/Status
            List<CardModel> pickedCards = new(sweptCardsPerFight + 3);
            if (pools.CharacterByEntry.TryGetValue(charId, out CharacterModel? ch))
            {
                foreach (CardModel sig in HighRiskPoolFilter.GetHighRiskStarterBackbone(ch, mode))
                    pickedCards.Add(sig);
            }
            CardModel[] cheapBackboneT2 = pools.UltraHighRiskCharacterCards.TryGetValue(charId, out CardModel[]? charT2)
                ? charT2.Where(c => HighRiskPoolFilter.IsCheapPlayableBackboneCard(c) && (mode != GameMode.Blitz || c.IsAllowedInBlitz)).ToArray()
                : [];
            CardModel[] cheapBackboneT1 = pools.ModerateCharacterCards.TryGetValue(charId, out CardModel[]? charT1)
                ? charT1.Where(c => HighRiskPoolFilter.IsCheapPlayableBackboneCard(c) && (mode != GameMode.Blitz || c.IsAllowedInBlitz)).ToArray()
                : [];
            while (pickedCards.Count < 3 && (cheapBackboneT2.Length > 0 || cheapBackboneT1.Length > 0))
                pickedCards.Add(HighRiskPoolFilter.PickTierWeighted(cheapBackboneT2, cheapBackboneT1, rng));

            // 2. 分层扫入高风险实体卡：严格 0% Tier0，~80% 从 Tier2 队列取出 + ~20% 从 Tier1 队列取出
            int addedTier2 = 0;
            int scanGuard2 = 0;
            while (addedTier2 < tier2PerFight && tier2Queue.Count > 0 && scanGuard2 < tier2Queue.Count * 2)
            {
                CardModel candidate = tier2Queue[tier2Cursor % tier2Queue.Count];
                tier2Cursor++;
                scanGuard2++;
                if (mode == GameMode.Blitz && !candidate.IsAllowedInBlitz)
                    continue;
                pickedCards.Add(candidate);
                addedTier2++;
            }

            int addedTier1 = 0;
            int scanGuard1 = 0;
            while (addedTier1 < tier1PerFight && tier1Queue.Count > 0 && scanGuard1 < tier1Queue.Count * 2)
            {
                CardModel candidate = tier1Queue[tier1Cursor % tier1Queue.Count];
                tier1Cursor++;
                scanGuard1++;
                if (mode == GameMode.Blitz && !candidate.IsAllowedInBlitz)
                    continue;
                pickedCards.Add(candidate);
                addedTier1++;
            }

            // 若某档在 Blitz 下未能补满目标槽位，用另一档高风险队列兜底补齐
            int remainingSlots = sweptCardsPerFight - (addedTier2 + addedTier1);
            int fallbackGuard = 0;
            while (remainingSlots > 0 && tier2Queue.Count > 0 && fallbackGuard < tier2Queue.Count * 2)
            {
                CardModel candidate = tier2Queue[tier2Cursor % tier2Queue.Count];
                tier2Cursor++;
                fallbackGuard++;
                if (mode == GameMode.Blitz && !candidate.IsAllowedInBlitz)
                    continue;
                pickedCards.Add(candidate);
                remainingSlots--;
            }

            // 3. 挑 2 个附魔与 1~2 个痛苦贴到可兼容卡牌上
            List<EnchantmentModel> fightEnchs = [];
            for (int e = 0; e < 2 && enchQueue.Count > 0; e++)
            {
                fightEnchs.Add(enchQueue[enchCursor % enchQueue.Count]);
                enchCursor++;
            }
            List<AfflictionModel> fightAffls = [];
            for (int a = 0; a < 2 && afflQueue.Count > 0; a++)
            {
                fightAffls.Add(afflQueue[afflCursor % afflQueue.Count]);
                afflCursor++;
            }

            List<CardInjectionSpec> cardSpecs = new(pickedCards.Count);
            HashSet<int> enchantedIndices = [];
            HashSet<int> afflictedIndices = [];
            for (int cIdx = 0; cIdx < pickedCards.Count; cIdx++)
            {
                CardModel c = pickedCards[cIdx];
                string? enchId = null;
                int enchAmt = 1;
                for (int eIdx = 0; eIdx < fightEnchs.Count; eIdx++)
                {
                    if (!enchantedIndices.Contains(eIdx) && RealCombatDriver.CanSafelyEnchant(fightEnchs[eIdx], c))
                    {
                        enchId = fightEnchs[eIdx].Id.Entry;
                        enchAmt = 1 + rng.Next(2);
                        enchantedIndices.Add(eIdx);
                        break;
                    }
                }

                string? afflId = null;
                for (int aIdx = 0; aIdx < fightAffls.Count; aIdx++)
                {
                    if (!afflictedIndices.Contains(aIdx) && fightAffls[aIdx].CanAfflict(c))
                    {
                        afflId = fightAffls[aIdx].Id.Entry;
                        afflictedIndices.Add(aIdx);
                        break;
                    }
                }

                int upgrade = (rng.Next(3) == 0 && c.MaxUpgradeLevel > 0) ? 1 : 0;
                cardSpecs.Add(new CardInjectionSpec(c.Id.Entry, upgrade, enchId, enchAmt, afflId, 1));
            }

            // 4. 严格仅从高风险挂钩/有状态战斗遗物选取（每场优先带 2 个有状态/追踪遗物）
            List<string> pickedRelics = new(relicsPerFight);
            HashSet<string> usedRelicIds = new(StringComparer.Ordinal);
            int sGuard = 0;
            while (pickedRelics.Count < Math.Min(2, relicsPerFight)
                && statefulRelicQueue.Count > 0
                && sGuard < statefulRelicQueue.Count * 2)
            {
                RelicModel r = statefulRelicQueue[statefulCursor % statefulRelicQueue.Count];
                statefulCursor++;
                sGuard++;
                if (mode == GameMode.Blitz && RelicLists.notAllowedInBlitz.Contains(r.Id))
                    continue;
                if (usedRelicIds.Add(r.Id.Entry))
                    pickedRelics.Add(r.Id.Entry);
            }
            int rGuard = 0;
            while (pickedRelics.Count < relicsPerFight && relicQueue.Count > 0 && rGuard < relicQueue.Count * 2)
            {
                RelicModel r = relicQueue[relicCursor % relicQueue.Count];
                relicCursor++;
                rGuard++;
                if (mode == GameMode.Blitz && RelicLists.notAllowedInBlitz.Contains(r.Id))
                    continue;
                if (usedRelicIds.Add(r.Id.Entry))
                    pickedRelics.Add(r.Id.Entry);
            }

            List<string> pickedPotions = new(potionsPerFight);
            for (int p = 0; p < potionsPerFight && potionQueue.Count > 0; p++)
            {
                pickedPotions.Add(potionQueue[potionCursor % potionQueue.Count].Id.Entry);
                potionCursor++;
            }

            string[] orbs = [];
            if (charId == "DEFECT" || (i % 4 == 2))
            {
                orbs =
                [
                    orbQueue[orbCursor++ % orbQueue.Count].Id.Entry,
                    orbQueue[orbCursor++ % orbQueue.Count].Id.Entry,
                ];
            }

            int ascension = mode == GameMode.Blitz ? (i % 6) : ((i % 4 == 0) ? 10 : (i % 3));
            int actIndex = i % 3;
            string runSeed = $"HRSWP{seed:X4}_{i:D3}";

            scenarios.Add(new FuzzScenarioSpec
            {
                ScenarioIndex = startIndex + i,
                Phase = "high-risk-sweep",
                Tag = $"hr_sweep_{i:D3}_{charId.ToLowerInvariant()}_{mode.ToString().ToLowerInvariant()}_{enc.Id.Entry.ToLowerInvariant()}",
                ActionRngSeed = seed * 37 + i * 997 + 23,
                MaxTurns = maxTurns,
                Setup = new CombatSetupSpec
                {
                    CharacterId = charId,
                    EncounterId = enc.Id.Entry,
                    Seed = runSeed,
                    GameMode = mode,
                    DominionId = dominionId,
                    Ascension = ascension,
                    ActIndex = actIndex,
                    PlayerMaxHp = 115,
                    PlayerHp = 95,
                    ClearStarterDeck = true,
                    ClearStarterRelics = false,
                    Cards = cardSpecs.ToArray(),
                    Relics = pickedRelics.ToArray(),
                    PotionSlotCount = 4,
                    Potions = pickedPotions.ToArray(),
                    InitialOrbs = orbs,
                    ExtraEnergy = 2,
                    ExtraStars = charId == "REGENT" ? 3 : 0,
                },
            });
        }

        return scenarios;
    }

    private static List<FuzzScenarioSpec> BuildPhaseHighRiskSynergy(
        SingleplayerPools pools,
        int count,
        int seed,
        int maxTurns,
        int startIndex)
    {
        if (count <= 0)
            return [];

        Random rng = new(seed);
        List<Func<int, FuzzScenarioSpec>> factories =
        [
            // 1. Silent Discard / Sly + ToughBandages + Tingsha + GamblingChip
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "silent_discard_sly",
                "SILENT", PickHighRiskEncounter(pools, "ExoskeletonsNormal", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("ACROBATICS", 1), ("PREPARED", 1), ("CALCULATED_GAMBLE", 1),
                    ("TACTICIAN", 1), ("REFLEX", 1), ("TOOLS_OF_THE_TRADE", 1), ("FLICK_FLACK", 1),
                    ("UNTOUCHABLE", 1), ("HAND_TRICK", 0), ("DAGGER_THROW", 0), ("BLADE_DANCE", 1), ("AFTERIMAGE", 0)),
                relics: HighRiskRelics(pools, "GAMBLING_CHIP", "TOUGH_BANDAGES", "TINGSHA", "KUNAI"),
                potions: Potions(pools, "GAMBLERS_BREW", "SWIFT_POTION", "CUNNING_POTION")),

            // 2. Ironclad Exhaust / Anger / Corruption / DarkEmbrace / FeelNoPain
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "ironclad_exhaust_anger",
                "IRONCLAD", PickHighRiskEncounter(pools, "LivingFogNormal", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("ANGER", 1), ("ANGER", 0), ("CORRUPTION", 1), ("FEEL_NO_PAIN", 1),
                    ("DARK_EMBRACE", 1), ("FIEND_FIRE", 0), ("BURNING_PACT", 1), ("TRUE_GRIT", 1),
                    ("SECOND_WIND", 0), ("OFFERING", 0), ("HAVOC", 1), ("CINDER", 0)),
                relics: HighRiskRelics(pools, "CHARONS_ASHES", "JOSS_PAPER", "SHURIKEN", "PEN_NIB"),
                potions: Potions(pools, "BLOCK_POTION", "STRENGTH_POTION", "BLOOD_POTION")),

            // 3. Defect Orbs / Evoke / MultiCast / Loop / BiasedCognition
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "defect_orbs_evoke",
                "DEFECT", PickHighRiskEncounter(pools, "PhantasmalGardenersElite", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("ZAP", 1), ("BALL_LIGHTNING", 0), ("COOLHEADED", 1),
                    ("DARKNESS", 1), ("FUSION", 1), ("DUALCAST", 1), ("MULTI_CAST", 0),
                    ("DEFRAGMENT", 1), ("BIASED_COGNITION", 0), ("LOOP", 1), ("TURBO", 0), ("TEMPEST", 0)),
                relics: HighRiskRelics(pools, "EMOTION_CHIP", "GOLD_PLATED_CABLES", "METRONOME"),
                potions: Potions(pools, "FOCUS_POTION", "ESSENCE_OF_DARKNESS", "POTION_OF_CAPACITY"),
                orbs: ["LIGHTNING_ORB", "FROST_ORB", "DARK_ORB", "PLASMA_ORB", "GLASS_ORB"]),

            // 4. Regent Stars / Forge / SovereignBlade + Dread Dominion + Replay
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "regent_stars_forge_dread",
                "REGENT", PickHighRiskEncounter(pools, i % 2 == 0 ? "BygoneEffigyElite" : "SkulkingColonyElite", i),
                GameMode.Dread, "TEST_SUBJECT_DOMINION",
                cards:
                [
                    new CardInjectionSpec("SOVEREIGN_BLADE", 1, EnchantmentId: "SPIRAL", EnchantmentAmount: 1),
                    .. HighRiskCards(pools, ("VENERATE", 1), ("THE_SMITH", 1), ("WROUGHT_IN_WAR", 1),
                        ("BULWARK", 1), ("BLACK_HOLE", 0), ("CHILD_OF_THE_STARS", 1),
                        ("ARSENAL", 0), ("ALIGNMENT", 1), ("BIG_BANG", 0), ("ASTRAL_PULSE", 1), ("SEEKING_EDGE", 0)),
                ],
                relics: HighRiskRelics(pools, "GALACTIC_DUST", "MINI_REGENT", "THROWING_AXE", "REGALITE"),
                potions: Potions(pools, "STAR_POTION", "KINGS_COURAGE", "RADIANT_TINCTURE"),
                extraStars: 5),

            // 5. Necrobinder Osty / Doom / Ethereal / Retain
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "necrobinder_osty_doom",
                "NECROBINDER", PickHighRiskEncounter(pools, "OvicopterNormal", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("BODYGUARD", 1), ("AFTERLIFE", 1), ("BONE_SHARDS", 0),
                    ("BLIGHT_STRIKE", 1), ("COUNTDOWN", 1), ("CALCIFY", 1), ("BORROWED_TIME", 0),
                    ("BANSHEES_CRY", 0), ("SCULPTING_STRIKE", 0), ("SNAP", 0), ("UNDEATH", 0), ("UNLEASH", 1)),
                relics: HighRiskRelics(pools, "BOUND_PHYLACTERY", "BONE_FLUTE", "PHYLACTERY_UNBOUND"),
                potions: Potions(pools, "BONE_BREW", "POTION_OF_DOOM", "POWDERED_DEMISE")),

            // 6. Fool Arrows / Melodies / Tarot / Encore / Lullaby
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "fool_arrows_melodies_tarot",
                "FOOL", PickHighRiskEncounter(pools, "TerrorEelElite", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("ARCHERY", 1), ("BLOSSOM_SHOT", 1), ("WHISTLING_ARROWS", 0),
                    ("WAH_CHO", 0), ("WAH_KI", 0), ("WAH_CHO_KI", 0), ("CODA", 1), ("YAH", 1),
                    ("ENCORE", 0), ("LULLABY", 0), ("THE_CHARIOT", 0), ("THE_EMPEROR", 0),
                    ("THE_HIGH_PRIESTESS", 0), ("WHEEL_OF_FORTUNE", 0), ("SHOW_OFF", 1), ("ARROW_STAB", 0)),
                relics: HighRiskRelics(pools, "CONDUCTORS_BATON", "METRONOME", "MUSIC_BOX"),
                potions: Potions(pools, "PRECISION_POTION", "BOTTLED_SONG", "ENERGY_POTION")),

            // 7. Multi-Choice Chains (Draw/Hand/Discard/Generated Choices)
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "multi_choice_chains",
                "IRONCLAD", PickHighRiskEncounter(pools, "InfestedPrismsElite", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("ARMAMENTS", 0), ("SECRET_WEAPON", 1), ("SECRET_TECHNIQUE", 1),
                    ("DUAL_WIELD", 1), ("HOLOGRAM", 1), ("HEADBUTT", 0), ("WELL_LAID_PLANS", 1),
                    ("DISCOVERY", 1), ("DECISIONS_DECISIONS", 0), ("HEIRLOOM_HAMMER", 0), ("BURNING_PACT", 0), ("THINKING_AHEAD", 1)),
                relics: HighRiskRelics(pools, "GAMBLING_CHIP", "THROWING_AXE", "TOOLBOX"),
                potions: Potions(pools, "LIQUID_MEMORIES", "DROPLET_OF_PRECOGNITION", "ATTACK_POTION", "COLORLESS_POTION")),

            // 8. Cost-Modifier & EndTurn Cleanup Torture (BulletTime / Enlightenment / Eidolon / SerpentForm / Skewer / Snecko / SlumberingEssence)
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "cost_modifiers_endturn_cleanup",
                "SILENT", PickHighRiskEncounter(pools, "SlimedBerserkerNormal", i), GameMode.Standard, null,
                cards:
                [
                    new CardInjectionSpec("SERPENT_FORM", 0, EnchantmentId: "SLUMBERING_ESSENCE", EnchantmentAmount: 1),
                    new CardInjectionSpec("SKEWER", 0, EnchantmentId: "SLITHER", EnchantmentAmount: 1),
                    new CardInjectionSpec("BULLET_TIME", 1),
                    new CardInjectionSpec("ENLIGHTENMENT", 0),
                    new CardInjectionSpec("EIDOLON", 0),
                    new CardInjectionSpec("PINPOINT", 1),
                    new CardInjectionSpec("KINGLY_KICK", 0),
                    new CardInjectionSpec("BANSHEES_CRY", 0),
                    new CardInjectionSpec("CODA", 0),
                    new CardInjectionSpec("ACROBATICS", 1),
                    new CardInjectionSpec("PREPARED", 1),
                    new CardInjectionSpec("BACKSTAB", 0),
                ],
                relics: HighRiskRelics(pools, "BRILLIANT_SCARF", "MUMMIFIED_HAND", "POCKETWATCH", "SNECKO_EYE"),
                potions: Potions(pools, "SNECKO_OIL", "TOUCH_OF_INSANITY", "SWIFT_POTION"),
                extraEnergy: 4),

            // 9. X-Cost + ChemicalX + Replay + Duplication
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "xcost_replay_duplication",
                "IRONCLAD", PickHighRiskEncounter(pools, "KaiserCrabBoss", i), GameMode.Standard, null,
                cards:
                [
                    new CardInjectionSpec("WHIRLWIND", 1, EnchantmentId: "SPIRAL", EnchantmentAmount: 1),
                    new CardInjectionSpec("SKEWER", 1, EnchantmentId: "MOMENTUM", EnchantmentAmount: 3),
                    .. HighRiskCards(pools, ("CASCADE", 1), ("VOLLEY", 1), ("MALAISE", 1), ("TEMPEST", 1),
                        ("BURST", 1), ("ONE_TWO_PUNCH", 1), ("ECHO_FORM", 0), ("OFFERING", 1), ("BLOODLETTING", 1), ("IMPERVIOUS", 1)),
                ],
                relics: HighRiskRelics(pools, "CHEMICAL_X", "THUMBSCREW", "THROWING_AXE", "ICE_CREAM"),
                potions: Potions(pools, "DUPLICATOR", "ENERGY_POTION", "LIQUID_MEMORIES"),
                extraEnergy: 4),

            // 10. Revive + Multi-Phase Boss (TestSubjectBoss) + High-Risk Cards
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "revive_test_subject_boss",
                "IRONCLAD", PickHighRiskEncounter(pools, "TestSubjectBoss", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("FIEND_FIRE", 1), ("OFFERING", 1), ("IMPERVIOUS", 1),
                    ("WHIRLWIND", 1), ("ANGER", 1), ("RAMPAGE", 1), ("SHOCKWAVE", 1),
                    ("BLOODLETTING", 1), ("HEMOKINESIS", 1), ("BURNING_PACT", 1), ("DISMANTLE", 1)),
                relics: HighRiskRelics(pools, "PEN_NIB", "AKABEKO", "RED_SKULL", "THUMBSCREW"),
                potions: Potions(pools, "FIRE_POTION", "STRENGTH_POTION", "DUPLICATOR")),

            // 11. Summon + FurCoat (FabricatorNormal)
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "summon_fabricator_furcoat",
                "DEFECT", PickHighRiskEncounter(pools, "FabricatorNormal", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("ZAP", 1), ("BALL_LIGHTNING", 1), ("COOLHEADED", 1),
                    ("BARRAGE", 1), ("CLAW", 1), ("CLAW", 0), ("SCRAPE", 1), ("HOLOGRAM", 1),
                    ("ALL_FOR_ONE", 0), ("COMPILE_DRIVER", 1)),
                relics: HighRiskRelics(pools, "FUR_COAT", "EMOTION_CHIP", "GOLD_PLATED_CABLES"),
                potions: Potions(pools, "EXPLOSIVE_AMPOULE", "BLOCK_POTION", "FOCUS_POTION")),

            // 12. Escape / Theft (GremlinMercNormal / ThievingHopperWeak)
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "escape_theft_gremlin_hopper",
                "SILENT", PickHighRiskEncounter(pools, i % 2 == 0 ? "GremlinMercNormal" : "ThievingHopperWeak", i),
                GameMode.Standard, null,
                cards: HighRiskCards(pools, ("BACKSTAB", 1), ("BLADE_DANCE", 1), ("ACCURACY", 1),
                    ("DAGGER_THROW", 1), ("ACROBATICS", 1), ("CALCULATED_GAMBLE", 1),
                    ("CLOAK_AND_DAGGER", 1), ("FINISHER", 1), ("ADRENALINE", 1), ("FLICK_FLACK", 1)),
                relics: HighRiskRelics(pools, "FUR_COAT", "SHURIKEN", "ORNAMENTAL_FAN", "KUNAI"),
                potions: Potions(pools, "FIRE_POTION", "POISON_POTION", "CUNNING_POTION")),

            // 13. SoulNexus Death Bypass + High-Risk Burst
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "soul_nexus_death_bypass",
                "IRONCLAD", PickHighRiskEncounter(pools, "SoulNexusElite", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("FIEND_FIRE", 1), ("WHIRLWIND", 1), ("OFFERING", 1),
                    ("IMPERVIOUS", 1), ("HEMOKINESIS", 1), ("BREAKTHROUGH", 1), ("BODY_SLAM", 1),
                    ("FEED", 1), ("SHOCKWAVE", 1), ("ANGER", 1)),
                relics: HighRiskRelics(pools, "PEN_NIB", "AKABEKO", "CHEMICAL_X", "RED_SKULL"),
                potions: Potions(pools, "STRENGTH_POTION", "VULNERABLE_POTION", "DUPLICATOR"),
                powers: [new PowerInjectionSpec("STRENGTH_POWER", 35, OnPlayer: true)],
                extraEnergy: 5),

            // 14. v0.113.1 Adroit + StretchPower + Fool Blitz Stress
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "v01131_adroit_stretch_fool",
                "FOOL", PickHighRiskEncounter(pools, "MytesNormal", i), GameMode.Blitz, null,
                cards:
                [
                    new CardInjectionSpec("DEEP_STRETCH", 1),
                    new CardInjectionSpec("BOUNCE", 1),
                    new CardInjectionSpec("FOOLISH_SHOT", 0, EnchantmentId: "ADROIT", EnchantmentAmount: 4),
                    new CardInjectionSpec("WAH_CHO", 0, EnchantmentId: "ADROIT", EnchantmentAmount: 3),
                    new CardInjectionSpec("ARCHERY", 0),
                    new CardInjectionSpec("RAPID_SHOT", 1, EnchantmentId: "ADROIT", EnchantmentAmount: 2),
                    new CardInjectionSpec("LULLABY", 0),
                    new CardInjectionSpec("KI_CHANT", 1),
                    new CardInjectionSpec("YAH", 1),
                    new CardInjectionSpec("EPIC_RETRIEVAL", 0),
                ],
                relics: HighRiskRelics(pools, "THUMBSCREW", "WUPPY", "CONDUCTORS_BATON"),
                potions: Potions(pools, "BLOCK_POTION", "DEXTERITY_POTION", "PRECISION_POTION"),
                powers: [new PowerInjectionSpec("STRETCH_POWER", 5, OnPlayer: true)]),

            // 15. Alchemize + EntropicBrew + FruitJuice + Boss Fight
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "alchemize_entropic_boss",
                "SILENT", PickHighRiskEncounter(pools, "TheInsatiableBoss", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("ALCHEMIZE", 1), ("ALCHEMIZE", 0), ("ACROBATICS", 1),
                    ("BLADE_DANCE", 1), ("ADRENALINE", 1), ("BURST", 1), ("PREPARED", 1),
                    ("CALCULATED_GAMBLE", 1), ("BLUR", 1), ("ESCAPE_PLAN", 1)),
                relics: HighRiskRelics(pools, "BELT_BUCKLE", "REPTILE_TRINKET", "PETRIFIED_TOAD", "GAMBLING_CHIP"),
                potions: Potions(pools, "ENTROPIC_BREW", "FRUIT_JUICE", "BLOCK_POTION", "BLOOD_POTION", "AMBERGRIS"),
                potionSlotCount: 5,
                playerHp: 60,
                playerMaxHp: 100),

            // 16. HistoryCourse + AutoPlay + Stampede + Mayhem + AeonglassBoss
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "history_course_autoplay_boss",
                "IRONCLAD", PickHighRiskEncounter(pools, "AeonglassBoss", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("HOWL_FROM_BEYOND", 1), ("STAMPEDE", 1), ("MAYHEM", 1),
                    ("HAVOC", 1), ("CATASTROPHE", 0), ("BEAT_DOWN", 0), ("WHIRLWIND", 1),
                    ("ANGER", 1), ("IMPERVIOUS", 1), ("OFFERING", 0), ("BATTLE_TRANCE", 1)),
                relics: HighRiskRelics(pools, "HISTORY_COURSE", "THUMBSCREW", "NUNCHAKU", "POCKETWATCH"),
                potions: Potions(pools, "DISTILLED_CHAOS", "ENERGY_POTION", "FIRE_POTION")),

            // 17. Status / Affliction / Hand-Full / Discard Storm vs QueenBoss / SoulFyshBoss
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "status_affliction_storm",
                "NECROBINDER", PickHighRiskEncounter(pools, i % 2 == 0 ? "QueenBoss" : "SoulFyshBoss", i),
                GameMode.Dread, "QUEEN_DOMINION",
                cards:
                [
                    new CardInjectionSpec("BODYGUARD", 1, AfflictionId: "HEXED"),
                    new CardInjectionSpec("BANSHEES_CRY", 0, AfflictionId: "ENTANGLED"),
                    new CardInjectionSpec("PAGESTORM", 1, AfflictionId: "GALVANIZED"),
                    new CardInjectionSpec("DARK_EMBRACE", 1),
                    new CardInjectionSpec("FEEL_NO_PAIN", 1),
                    new CardInjectionSpec("SECOND_WIND", 1),
                    new CardInjectionSpec("AFTERLIFE", 1, AfflictionId: "BOUND"),
                    new CardInjectionSpec("BLIGHT_STRIKE", 1, AfflictionId: "SMOG"),
                    new CardInjectionSpec("SCRAWL", 1),
                    new CardInjectionSpec("UNLEASH", 1, AfflictionId: "TAINTED"),
                ],
                relics: HighRiskRelics(pools, "BOUND_PHYLACTERY", "LIZARD_TAIL", "RUNIC_PYRAMID", "BURNING_STICKS"),
                potions: Potions(pools, "AMBERGRIS", "BLOCK_POTION", "REGEN_POTION")),

            // 18. Phase 6 Cards & Powers vs GlobeHeadNormal (GalvanicPower)
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "phase6_cards_and_powers",
                "SILENT", PickHighRiskEncounter(pools, "GlobeHeadNormal", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("ALCHEMIZE", 0), ("ANOINTED", 0), ("BEAT_DOWN", 0), ("CATASTROPHE", 0),
                    ("DISTRACTION", 1), ("HIDDEN_GEM", 0), ("MAD_SCIENCE", 0), ("METAMORPHOSIS", 1),
                    ("MY_SHINIES", 0), ("ENTROPY", 0), ("STAMPEDE", 0), ("BACKSTAB", 1)),
                relics: HighRiskRelics(pools, "HISTORY_COURSE", "THUMBSCREW", "FUR_COAT", "TOOLBOX"),
                potions: Potions(pools, "DISTILLED_CHAOS", "SNECKO_OIL", "TOUCH_OF_INSANITY"),
                extraEnergy: 5),

            // 19. Phase 6 Enchantments on High-Risk Cards vs MechaKnightElite
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "phase6_enchantments_highrisk",
                "IRONCLAD", PickHighRiskEncounter(pools, "MechaKnightElite", i), GameMode.Standard, null,
                cards:
                [
                    new CardInjectionSpec("ANGER", 0, EnchantmentId: "INSTINCT", EnchantmentAmount: 1),
                    new CardInjectionSpec("WHIRLWIND", 1, EnchantmentId: "TEZCATARAS_EMBER", EnchantmentAmount: 5),
                    new CardInjectionSpec("IMPERVIOUS", 0, EnchantmentId: "SPIRAL", EnchantmentAmount: 1),
                    new CardInjectionSpec("ARMAMENTS", 1, EnchantmentId: "PERFECT_FIT", EnchantmentAmount: 1),
                    new CardInjectionSpec("FIEND_FIRE", 0, EnchantmentId: "SLUMBERING_ESSENCE", EnchantmentAmount: 1),
                    new CardInjectionSpec("SECOND_WIND", 0, EnchantmentId: "ADROIT", EnchantmentAmount: 3),
                    new CardInjectionSpec("RAMPAGE", 1, EnchantmentId: "MOMENTUM", EnchantmentAmount: 4),
                    new CardInjectionSpec("HEADBUTT", 1, EnchantmentId: "GOOPY", EnchantmentAmount: 2),
                    new CardInjectionSpec("DEEP_STRETCH", 0),
                    new CardInjectionSpec("OFFERING", 0),
                ],
                relics: HighRiskRelics(pools, "THUMBSCREW", "POCKETWATCH", "ART_OF_WAR"),
                potions: Potions(pools, "SWIFT_POTION", "FORTIFIER", "ENERGY_POTION")),

            // 20. PhrogParasiteElite Infested Wrigglers + HardToKill / Rampart
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "phrog_parasite_infested_wrigglers",
                "IRONCLAD", PickHighRiskEncounter(pools, (i % 3) switch
                {
                    0 => "PhrogParasiteElite",
                    1 => "TurretOperatorWeak",
                    _ => "ExoskeletonsNormal",
                }, i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("FIEND_FIRE", 1), ("WHIRLWIND", 1), ("OFFERING", 1),
                    ("IMPERVIOUS", 1), ("SHOCKWAVE", 1), ("HEMOKINESIS", 1), ("BREAKTHROUGH", 1),
                    ("ANGER", 1), ("BATTLE_TRANCE", 1), ("TRUE_GRIT", 1)),
                relics: HighRiskRelics(pools, "AKABEKO", "PEN_NIB", "THUMBSCREW", "CHARONS_ASHES"),
                potions: Potions(pools, "FIRE_POTION", "VULNERABLE_POTION", "STRENGTH_POTION"),
                powers:
                [
                    new PowerInjectionSpec("HARD_TO_KILL_POWER", 12, OnPlayer: false, EnemyIndex: 0),
                    new PowerInjectionSpec("STRENGTH_POWER", 20, OnPlayer: true),
                ],
                extraEnergy: 5),

            // 21. Boss Gauntlet (KnowledgeDemon / LagavulinMatriarch / Vantom / WaterfallGiant / CeremonialBeast / TheKin)
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "boss_gauntlet_dread",
                AllCharacters[i % AllCharacters.Length],
                PickHighRiskEncounter(pools, (i % 6) switch
                {
                    0 => "KnowledgeDemonBoss",
                    1 => "LagavulinMatriarchBoss",
                    2 => "VantomBoss",
                    3 => "WaterfallGiantBoss",
                    4 => "CeremonialBeastBoss",
                    _ => "TheKinBoss",
                }, i),
                GameMode.Dread,
                pools.AllDominions.Length > 0 ? pools.AllDominions[i % pools.AllDominions.Length].Id.Entry : null,
                cards: BuildRandomHighRiskArchetypeDeck(pools, AllCharacters[i % AllCharacters.Length], GameMode.Dread, rng, 12),
                relics: PickRandomHighRiskRelics(pools, GameMode.Dread, rng, 5),
                potions: Potions(pools, "DISTILLED_CHAOS", "DUPLICATOR", "ENERGY_POTION"),
                extraEnergy: 3,
                extraStars: AllCharacters[i % AllCharacters.Length] == "REGENT" ? 4 : 0),

            // 22. BattlewornDummy TimeLimitPower (High-Risk Regent Deck)
            i => MakeHighRiskSynergy(pools, startIndex + i, seed, maxTurns, "battleworn_dummy_timelimit",
                "REGENT", PickHighRiskEncounter(pools, "BattlewornDummyEventV1Encounter", i), GameMode.Standard, null,
                cards: HighRiskCards(pools, ("VENERATE", 1), ("SOVEREIGN_BLADE", 1), ("THE_SMITH", 1),
                    ("ASTRAL_PULSE", 1), ("WROUGHT_IN_WAR", 1), ("ALIGNMENT", 1), ("CONQUEROR", 1),
                    ("ORBIT", 0), ("FALLING_STAR", 1), ("VOID_FORM", 0)),
                relics: HighRiskRelics(pools, "ART_OF_WAR", "PEN_NIB", "GALACTIC_DUST", "MINI_REGENT"),
                potions: Potions(pools, "STRENGTH_POTION", "STAR_POTION", "KINGS_COURAGE"),
                extraStars: 4),

            // 23. High-Risk Capacity Stress A: 42 hooked/stateful relics & 50+ powers
            i => BuildHighRiskCapacityRelicsPowersStress(pools, startIndex + i, seed, maxTurns),

            // 24. High-Risk Capacity Stress B: 110 strictly high-risk cards
            i => BuildHighRiskCapacityCardsStress(pools, startIndex + i, seed, maxTurns),
        ];

        List<FuzzScenarioSpec> list = new(count);
        for (int i = 0; i < count; i++)
        {
            if (i < factories.Count || (i % 2 == 0))
            {
                list.Add(factories[i % factories.Count](i));
            }
            else
            {
                list.Add(BuildRandomHighRiskCombo(pools, startIndex + i, seed, maxTurns, rng));
            }
        }
        return list;
    }

    private static FuzzScenarioSpec BuildRandomHighRiskCombo(
        SingleplayerPools pools,
        int index,
        int seed,
        int maxTurns,
        Random rng)
    {
        string charId = AllCharacters[index % AllCharacters.Length];
        GameMode mode = (index % 3) switch
        {
            0 => GameMode.Standard,
            1 => GameMode.Dread,
            _ => GameMode.Blitz,
        };
        string? dominionId = mode == GameMode.Dread && pools.AllDominions.Length > 0
            ? pools.AllDominions[rng.Next(pools.AllDominions.Length)].Id.Entry
            : null;
        EncounterModel enc = pools.HighRiskEncounters[rng.Next(pools.HighRiskEncounters.Length)];

        CardInjectionSpec[] deck = BuildRandomHighRiskArchetypeDeck(pools, charId, mode, rng, 12);
        string[] relics = PickRandomHighRiskRelics(pools, mode, rng, 5);
        string[] potions = pools.AllPotions.Length > 0
            ? Enumerable.Range(0, 3)
                .Select(_ => pools.AllPotions[rng.Next(pools.AllPotions.Length)].Id.Entry)
                .ToArray()
            : [];
        string[] orbs = charId == "DEFECT" && pools.AllOrbs.Length > 0
            ? [pools.AllOrbs[rng.Next(pools.AllOrbs.Length)].Id.Entry, pools.AllOrbs[rng.Next(pools.AllOrbs.Length)].Id.Entry]
            : [];

        return MakeHighRiskSynergy(
            pools, index, seed, maxTurns, $"combo_{charId.ToLowerInvariant()}_{enc.Id.Entry.ToLowerInvariant()}",
            charId, enc.Id.Entry, mode, dominionId,
            cards: deck,
            relics: relics,
            potions: potions,
            orbs: orbs,
            extraEnergy: 3,
            extraStars: charId == "REGENT" ? 4 : 0);
    }

    private static CardInjectionSpec[] BuildRandomHighRiskArchetypeDeck(
        SingleplayerPools pools,
        string charId,
        GameMode mode,
        Random rng,
        int count)
    {
        List<CardModel> picked = new(count + 2);
        if (pools.CharacterByEntry.TryGetValue(charId, out CharacterModel? ch))
        {
            foreach (CardModel sig in HighRiskPoolFilter.GetHighRiskStarterBackbone(ch, mode))
                picked.Add(sig);
        }

        CardModel[] charTier2 = pools.UltraHighRiskCharacterCards.TryGetValue(charId, out CardModel[]? arrT2)
            ? arrT2.Where(c => mode != GameMode.Blitz || c.IsAllowedInBlitz).ToArray()
            : pools.UltraHighRiskCards.Where(c => mode != GameMode.Blitz || c.IsAllowedInBlitz).ToArray();
        CardModel[] charTier1 = pools.ModerateCharacterCards.TryGetValue(charId, out CardModel[]? arrT1)
            ? arrT1.Where(c => mode != GameMode.Blitz || c.IsAllowedInBlitz).ToArray()
            : pools.ModerateCards.Where(c => mode != GameMode.Blitz || c.IsAllowedInBlitz).ToArray();
        CardModel[] crossTier2 = pools.UltraHighRiskCards.Where(c => mode != GameMode.Blitz || c.IsAllowedInBlitz).ToArray();
        CardModel[] crossTier1 = pools.ModerateCards.Where(c => mode != GameMode.Blitz || c.IsAllowedInBlitz).ToArray();
        CardModel[] cheapCharT2 = charTier2.Where(HighRiskPoolFilter.IsCheapPlayableBackboneCard).ToArray();
        CardModel[] cheapCharT1 = charTier1.Where(HighRiskPoolFilter.IsCheapPlayableBackboneCard).ToArray();

        while (picked.Count < 3 && (cheapCharT2.Length > 0 || cheapCharT1.Length > 0))
            picked.Add(HighRiskPoolFilter.PickTierWeighted(cheapCharT2, cheapCharT1, rng));

        while (picked.Count < count && (charTier2.Length > 0 || charTier1.Length > 0))
        {
            CardModel next = (rng.Next(4) == 0 && (crossTier2.Length > 0 || crossTier1.Length > 0))
                ? HighRiskPoolFilter.PickTierWeighted(crossTier2, crossTier1, rng)
                : HighRiskPoolFilter.PickTierWeighted(charTier2, charTier1, rng);
            picked.Add(next);
        }

        List<CardInjectionSpec> specs = new(picked.Count);
        int enchLeft = 2;
        int afflLeft = 1;
        foreach (CardModel c in picked)
        {
            string? enchId = null;
            if (enchLeft > 0 && pools.AllEnchantments.Length > 0 && rng.Next(4) == 0)
            {
                EnchantmentModel cand = pools.AllEnchantments[rng.Next(pools.AllEnchantments.Length)];
                if (RealCombatDriver.CanSafelyEnchant(cand, c))
                {
                    enchId = cand.Id.Entry;
                    enchLeft--;
                }
            }
            string? afflId = null;
            if (afflLeft > 0 && pools.AllAfflictions.Length > 0 && rng.Next(5) == 0)
            {
                AfflictionModel cand = pools.AllAfflictions[rng.Next(pools.AllAfflictions.Length)];
                if (cand.CanAfflict(c))
                {
                    afflId = cand.Id.Entry;
                    afflLeft--;
                }
            }
            int upg = rng.Next(3) == 0 && c.MaxUpgradeLevel > 0 ? 1 : 0;
            specs.Add(new CardInjectionSpec(c.Id.Entry, upg, enchId, 1 + rng.Next(2), afflId, 1));
        }
        return specs.ToArray();
    }

    private static string[] PickRandomHighRiskRelics(SingleplayerPools pools, GameMode mode, Random rng, int count)
    {
        RelicModel[] allowed = pools.HighRiskRelics
            .Where(r => mode != GameMode.Blitz || !RelicLists.notAllowedInBlitz.Contains(r.Id))
            .ToArray();
        RelicModel[] stateful = allowed.Where(HighRiskPoolFilter.IsStatefulOrTrackedRelic).ToArray();
        HashSet<string> chosen = new(StringComparer.Ordinal);
        for (int s = 0; s < 6 && chosen.Count < Math.Min(2, count) && stateful.Length > 0; s++)
            chosen.Add(stateful[rng.Next(stateful.Length)].Id.Entry);
        for (int r = 0; r < count * 3 && chosen.Count < count && allowed.Length > 0; r++)
            chosen.Add(allowed[rng.Next(allowed.Length)].Id.Entry);
        return chosen.ToArray();
    }

    private static FuzzScenarioSpec BuildHighRiskCapacityRelicsPowersStress(
        SingleplayerPools pools,
        int index,
        int seed,
        int maxTurns)
    {
        string[] relics = pools.HighRiskRelics
            .Take(42)
            .Select(static r => r.Id.Entry)
            .ToArray();

        string[] playerPowerPool =
        [
            "STRENGTH_POWER", "DEXTERITY_POWER", "THORNS_POWER", "ARTIFACT_POWER",
            "PLATING_POWER", "BLUR_POWER", "BARRICADE_POWER", "AFTERIMAGE_POWER",
            "DARK_EMBRACE_POWER", "FEEL_NO_PAIN_POWER", "BUFFER_POWER", "REGEN_POWER",
            "VIGOR_POWER", "RITUAL_POWER", "FOCUS_POWER", "AIM_POWER",
            "STRETCH_POWER", "ENTROPY_POWER", "STAMPEDE_POWER", "ACCURACY_POWER",
        ];
        string[] enemyPowerPool =
        [
            "VULNERABLE_POWER", "WEAK_POWER", "POISON_POWER", "DOOM_POWER",
            "STRENGTH_POWER", "THORNS_POWER", "ARTIFACT_POWER", "PLATING_POWER",
            "HARD_TO_KILL_POWER", "RITUAL_POWER",
        ];

        List<PowerInjectionSpec> powers = [];
        foreach (string p in playerPowerPool)
        {
            if (pools.ValidPowerEntries.Contains(p))
                powers.Add(new PowerInjectionSpec(p, 2, OnPlayer: true));
        }
        foreach (string p in enemyPowerPool)
        {
            if (pools.ValidPowerEntries.Contains(p))
                powers.Add(new PowerInjectionSpec(p, 2, OnPlayer: false, EnemyIndex: -1));
        }

        string encounter = PickHighRiskEncounter(pools, "ExoskeletonsNormal", index);
        return new FuzzScenarioSpec
        {
            ScenarioIndex = index,
            Phase = "high-risk-synergy",
            Tag = $"hr_stress_capacity_42relics_50powers_{index:D3}",
            ActionRngSeed = seed * 17 + index * 313 + 7,
            MaxTurns = Math.Min(maxTurns, 5),
            Setup = new CombatSetupSpec
            {
                CharacterId = "IRONCLAD",
                EncounterId = encounter,
                Seed = $"HRCAPRP{seed:X4}_{index:D3}",
                GameMode = GameMode.Standard,
                Ascension = 0,
                ActIndex = 2,
                PlayerMaxHp = 120,
                PlayerHp = 100,
                ClearStarterDeck = true,
                Cards = HighRiskCards(pools, ("WHIRLWIND", 1), ("SHOCKWAVE", 1), ("FIEND_FIRE", 1),
                    ("IMPERVIOUS", 1), ("ANGER", 1), ("OFFERING", 1), ("BATTLE_TRANCE", 1), ("DARK_EMBRACE", 1)),
                Relics = relics,
                PotionSlotCount = 4,
                Potions = Potions(pools, "BLOCK_POTION", "FIRE_POTION", "STRENGTH_POTION"),
                InitialPowers = powers.ToArray(),
                ExtraEnergy = 4,
            },
        };
    }

    private static FuzzScenarioSpec BuildHighRiskCapacityCardsStress(
        SingleplayerPools pools,
        int index,
        int seed,
        int maxTurns)
    {
        List<CardInjectionSpec> bigDeck = new(115);
        string[] core = ["ANGER", "BLADE_DANCE", "ACROBATICS", "WHIRLWIND", "CALCULATED_GAMBLE", "HOLOGRAM", "SECOND_WIND"];
        string fallback = pools.UltraHighRiskCards.Length > 0
            ? pools.UltraHighRiskCards[0].Id.Entry
            : pools.HighRiskCards.Length > 0 ? pools.HighRiskCards[0].Id.Entry : "ANGER";
        for (int c = 0; c < 110; c++)
        {
            string entry = core[c % core.Length];
            if (!pools.HighRiskCardEntries.Contains(entry))
                entry = fallback;
            bigDeck.Add(new CardInjectionSpec(entry, c % 3 == 0 ? 1 : 0));
        }

        return new FuzzScenarioSpec
        {
            ScenarioIndex = index,
            Phase = "high-risk-synergy",
            Tag = $"hr_stress_capacity_110cards_{index:D3}",
            ActionRngSeed = seed * 19 + index * 521 + 11,
            MaxTurns = Math.Min(maxTurns, 5),
            Setup = new CombatSetupSpec
            {
                CharacterId = "IRONCLAD",
                EncounterId = PickHighRiskEncounter(pools, "ExoskeletonsNormal", index),
                Seed = $"HRCAPCD{seed:X4}_{index:D3}",
                GameMode = GameMode.Standard,
                Ascension = 0,
                ActIndex = 1,
                PlayerMaxHp = 120,
                PlayerHp = 110,
                ClearStarterDeck = true,
                Cards = bigDeck.ToArray(),
                Relics = HighRiskRelics(pools, "BAG_OF_PREPARATION", "CHARONS_ASHES", "SHURIKEN"),
                PotionSlotCount = 3,
                Potions = Potions(pools, "SWIFT_POTION", "ENERGY_POTION"),
                ExtraEnergy = 5,
            },
        };
    }

    private static FuzzScenarioSpec MakeHighRiskSynergy(
        SingleplayerPools pools,
        int index,
        int seed,
        int maxTurns,
        string name,
        string characterId,
        string encounterId,
        GameMode mode,
        string? dominionId,
        CardInjectionSpec[] cards,
        string[] relics,
        string[] potions,
        PowerInjectionSpec[]? powers = null,
        string[]? orbs = null,
        int? potionSlotCount = null,
        int playerHp = 95,
        int playerMaxHp = 115,
        int extraEnergy = 3,
        int extraStars = 0)
    {
        return new FuzzScenarioSpec
        {
            ScenarioIndex = index,
            Phase = "high-risk-synergy",
            Tag = $"hr_synergy_{name}_{index:D3}",
            ActionRngSeed = seed * 29 + index * 733 + name.Length * 13,
            MaxTurns = maxTurns,
            Setup = new CombatSetupSpec
            {
                CharacterId = characterId,
                EncounterId = encounterId,
                Seed = $"HRSYN{seed:X4}_{index:D3}",
                GameMode = mode,
                DominionId = dominionId,
                Ascension = mode == GameMode.Blitz ? (index % 6) : (index % 3 == 0 ? 10 : 0),
                ActIndex = index % 3,
                PlayerMaxHp = playerMaxHp,
                PlayerHp = playerHp,
                ClearStarterDeck = true,
                ClearStarterRelics = false,
                Cards = cards,
                Relics = relics,
                PotionSlotCount = potionSlotCount ?? Math.Max(3, potions.Length),
                Potions = potions,
                InitialPowers = powers ?? [],
                InitialOrbs = orbs ?? [],
                ExtraEnergy = extraEnergy,
                ExtraStars = extraStars,
            },
        };
    }

    private static CardInjectionSpec[] HighRiskCards(SingleplayerPools pools, params (string Id, int Upgrade)[] items)
        => items
            .Where(x => pools.HighRiskCardEntries.Contains(x.Id))
            .Select(x => new CardInjectionSpec(x.Id, x.Upgrade))
            .ToArray();

    private static string[] HighRiskRelics(SingleplayerPools pools, params string[] ids)
        => ids.Where(pools.HighRiskRelicEntries.Contains).Distinct(StringComparer.Ordinal).ToArray();

    private static string PickHighRiskEncounter(SingleplayerPools pools, string preferred, int fallbackIndex)
    {
        if (pools.EncounterEntryByKey.TryGetValue(preferred, out string? entry))
            return entry;
        return pools.HighRiskEncounters[fallbackIndex % pools.HighRiskEncounters.Length].Id.Entry;
    }

    private static FuzzScenarioSpec BuildCapacityRelicsPowersStress(
        SingleplayerPools pools,
        int index,
        int seed,
        int maxTurns,
        Random rng)
    {
        // 42 个不同战斗遗物（严格超过旧上限 32，验证 MaxRelicStates=64）
        string[] relics = pools.CombatRelics
            .Take(42)
            .Select(static r => r.Id.Entry)
            .ToArray();

        // 玩家 + 敌方总计 >45 个 Power 实例（验证 MaxPowers=96）
        string[] playerPowerPool =
        [
            "STRENGTH_POWER", "DEXTERITY_POWER", "THORNS_POWER", "ARTIFACT_POWER",
            "PLATING_POWER", "BLUR_POWER", "BARRICADE_POWER", "AFTERIMAGE_POWER",
            "DARK_EMBRACE_POWER", "FEEL_NO_PAIN_POWER", "BUFFER_POWER", "REGEN_POWER",
            "VIGOR_POWER", "RITUAL_POWER", "FOCUS_POWER", "AIM_POWER",
            "STRETCH_POWER", "ENTROPY_POWER", "STAMPEDE_POWER", "ACCURACY_POWER",
        ];
        string[] enemyPowerPool =
        [
            "VULNERABLE_POWER", "WEAK_POWER", "POISON_POWER", "DOOM_POWER",
            "STRENGTH_POWER", "THORNS_POWER", "ARTIFACT_POWER", "PLATING_POWER",
            "HARD_TO_KILL_POWER", "RITUAL_POWER",
        ];

        List<PowerInjectionSpec> powers = [];
        foreach (string p in playerPowerPool)
        {
            if (pools.ValidPowerEntries.Contains(p))
                powers.Add(new PowerInjectionSpec(p, 2, OnPlayer: true));
        }
        foreach (string p in enemyPowerPool)
        {
            if (pools.ValidPowerEntries.Contains(p))
                powers.Add(new PowerInjectionSpec(p, 2, OnPlayer: false, EnemyIndex: -1));
        }

        // 选择多敌人遭遇，让敌方 Power 实例数翻倍（3 敌人 × 10 Power + 20 玩家 Power = 50+ Powers!）
        string encounter = PickEncounter(pools, "ExoskeletonsNormal", index);
        return new FuzzScenarioSpec
        {
            ScenarioIndex = index,
            Phase = "synergy",
            Tag = $"stress_capacity_42relics_50powers_{index:D3}",
            ActionRngSeed = seed * 17 + index * 313 + 7,
            MaxTurns = Math.Min(maxTurns, 5),
            Setup = new CombatSetupSpec
            {
                CharacterId = "IRONCLAD",
                EncounterId = encounter,
                Seed = $"CAPRP{seed:X4}_{index:D3}",
                GameMode = GameMode.Standard,
                Ascension = 0,
                ActIndex = 2,
                PlayerMaxHp = 120,
                PlayerHp = 100,
                Cards = Cards(pools, ("WHIRLWIND", 1), ("THUNDERCLAP", 1), ("TWIN_STRIKE", 1),
                    ("SHRUG_IT_OFF", 1), ("ANGER", 1), ("POMMEL_STRIKE", 1)),
                Relics = relics,
                PotionSlotCount = 4,
                Potions = Potions(pools, "BLOCK_POTION", "FIRE_POTION", "STRENGTH_POTION"),
                InitialPowers = powers.ToArray(),
                ExtraEnergy = 4,
            },
        };
    }

    private static FuzzScenarioSpec BuildCapacityCardsStress(
        SingleplayerPools pools,
        int index,
        int seed,
        int maxTurns,
        Random rng)
    {
        List<CardInjectionSpec> bigDeck = new(115);
        string[] core = ["ANGER", "BLADE_DANCE", "SHRUG_IT_OFF", "POMMEL_STRIKE", "ACROBATICS", "WHIRLWIND", "BACKFLIP"];
        for (int c = 0; c < 110; c++)
        {
            string entry = core[c % core.Length];
            if (!pools.ValidCardEntries.Contains(entry))
                entry = "STRIKE_IRONCLAD";
            bigDeck.Add(new CardInjectionSpec(entry, c % 3 == 0 ? 1 : 0));
        }

        return new FuzzScenarioSpec
        {
            ScenarioIndex = index,
            Phase = "synergy",
            Tag = $"stress_capacity_110cards_{index:D3}",
            ActionRngSeed = seed * 19 + index * 521 + 11,
            MaxTurns = Math.Min(maxTurns, 5),
            Setup = new CombatSetupSpec
            {
                CharacterId = "IRONCLAD",
                EncounterId = PickEncounter(pools, "ExoskeletonsNormal", index),
                Seed = $"CAPCD{seed:X4}_{index:D3}",
                GameMode = GameMode.Standard,
                Ascension = 0,
                ActIndex = 1,
                PlayerMaxHp = 120,
                PlayerHp = 110,
                Cards = bigDeck.ToArray(),
                Relics = Relics(pools, "BAG_OF_PREPARATION", "CHARONS_ASHES", "SHURIKEN"),
                PotionSlotCount = 3,
                Potions = Potions(pools, "SWIFT_POTION", "ENERGY_POTION"),
                ExtraEnergy = 5,
            },
        };
    }

    private static FuzzScenarioSpec MakeSynergy(
        SingleplayerPools pools,
        int index,
        int seed,
        int maxTurns,
        string name,
        string characterId,
        string encounterId,
        GameMode mode,
        string? dominionId,
        CardInjectionSpec[] cards,
        string[] relics,
        string[] potions,
        PowerInjectionSpec[]? powers = null,
        string[]? orbs = null,
        int? potionSlotCount = null,
        int playerHp = 95,
        int playerMaxHp = 110,
        int extraEnergy = 3,
        int extraStars = 0)
    {
        return new FuzzScenarioSpec
        {
            ScenarioIndex = index,
            Phase = "synergy",
            Tag = $"synergy_{name}_{index:D3}",
            ActionRngSeed = seed * 29 + index * 733 + name.Length * 13,
            MaxTurns = maxTurns,
            Setup = new CombatSetupSpec
            {
                CharacterId = characterId,
                EncounterId = encounterId,
                Seed = $"SYN{seed:X4}_{index:D3}",
                GameMode = mode,
                DominionId = dominionId,
                Ascension = mode == GameMode.Blitz ? (index % 6) : (index % 3 == 0 ? 10 : 0),
                ActIndex = index % 3,
                PlayerMaxHp = playerMaxHp,
                PlayerHp = playerHp,
                ClearStarterDeck = false,
                ClearStarterRelics = false,
                Cards = cards,
                Relics = relics,
                PotionSlotCount = potionSlotCount ?? Math.Max(3, potions.Length),
                Potions = potions,
                InitialPowers = powers ?? [],
                InitialOrbs = orbs ?? [],
                ExtraEnergy = extraEnergy,
                ExtraStars = extraStars,
            },
        };
    }

    private static CardInjectionSpec[] Cards(SingleplayerPools pools, params (string Id, int Upgrade)[] items)
        => items
            .Where(x => pools.ValidCardEntries.Contains(x.Id))
            .Select(x => new CardInjectionSpec(x.Id, x.Upgrade))
            .ToArray();

    private static string[] Relics(SingleplayerPools pools, params string[] ids)
        => ids.Where(pools.ValidRelicEntries.Contains).Distinct(StringComparer.Ordinal).ToArray();

    private static string[] Potions(SingleplayerPools pools, params string[] ids)
        => ids.Where(pools.ValidPotionEntries.Contains).ToArray();

    private static string PickEncounter(SingleplayerPools pools, string preferred, int fallbackIndex)
    {
        if (pools.EncounterEntryByKey.TryGetValue(preferred, out string? entry))
            return entry;
        return pools.AllEncounters[fallbackIndex % pools.AllEncounters.Length].Id.Entry;
    }

    private static SingleplayerPools CapturePools()
    {
        HashSet<ModelId> excludedCards = [];
        try
        {
            foreach (ModelId id in ModelDb.CardPool<MockCardPool>().AllCardIds) excludedCards.Add(id);
            foreach (ModelId id in ModelDb.CardPool<DeprecatedCardPool>().AllCardIds) excludedCards.Add(id);
        }
        catch { }

        CardModel[] allCards = ModelDb.AllCards
            .Where(c => !HotCoverageAuditor.IsMockOrDeprecated(c)
                && !excludedCards.Contains(c.Id)
                && c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
            .DistinctBy(static c => c.Id)
            .OrderBy(static c => c.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, CardModel[]> byChar = new(StringComparer.Ordinal);
        Dictionary<string, CardModel[]> highRiskByChar = new(StringComparer.Ordinal);
        Dictionary<string, CardModel[]> ultraByChar = new(StringComparer.Ordinal);
        Dictionary<string, CardModel[]> moderateByChar = new(StringComparer.Ordinal);
        Dictionary<string, CharacterModel> charByEntry = new(StringComparer.Ordinal);
        HashSet<ModelId> charCardIds = [];
        foreach (CharacterModel ch in ModelDb.AllCharacters)
        {
            charByEntry[ch.Id.Entry] = ch;
            CardModel[] list = ch.CardPool.AllCards
                .Where(c => !HotCoverageAuditor.IsMockOrDeprecated(c)
                    && c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
                .DistinctBy(static c => c.Id)
                .OrderBy(static c => c.Id.Entry, StringComparer.Ordinal)
                .ToArray();
            byChar[ch.Id.Entry] = list;
            highRiskByChar[ch.Id.Entry] = list.Where(HighRiskPoolFilter.IsHighRiskCard).ToArray();
            ultraByChar[ch.Id.Entry] = list.Where(HighRiskPoolFilter.IsUltraHighRiskCard).ToArray();
            moderateByChar[ch.Id.Entry] = list.Where(HighRiskPoolFilter.IsModerateCard).ToArray();
            foreach (CardModel c in list)
                charCardIds.Add(c.Id);
        }
        CardModel[] sharedCards = allCards.Where(c => !charCardIds.Contains(c.Id)).ToArray();
        CardModel[] highRiskCards = allCards.Where(HighRiskPoolFilter.IsHighRiskCard).ToArray();
        CardModel[] ultraHighRiskCards = highRiskCards.Where(HighRiskPoolFilter.IsUltraHighRiskCard).ToArray();
        CardModel[] moderateCards = highRiskCards.Where(HighRiskPoolFilter.IsModerateCard).ToArray();

        RelicModel[] allRelics = ModelDb.AllRelics
            .Where(r => !HotCoverageAuditor.IsMockOrDeprecated(r) && GeneratedCombatScenario.IsSingleplayerRelic(r))
            .DistinctBy(static r => r.Id)
            .OrderBy(static r => r.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        RelicModel[] combatRelics = allRelics
            .Where(r =>
            {
                HotRelicId rid = HotRelicCatalog.FindByEntry(r.Id.Entry);
                return rid != HotRelicId.None && HotRelicCatalog.Get(rid).HookMask != HotHookMask.None;
            })
            .ToArray();

        RelicModel[] highRiskRelics = allRelics
            .Where(HighRiskPoolFilter.IsHighRiskRelic)
            .ToArray();

        PotionModel[] allPotions = ModelDb.AllPotions
            .Where(p => !HotCoverageAuditor.IsMockOrDeprecated(p))
            .DistinctBy(static p => p.Id)
            .OrderBy(static p => p.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        EncounterModel[] allEncounters = ModelDb.AllEncounters
            .Where(e => !HotCoverageAuditor.IsMockOrDeprecated(e))
            .DistinctBy(static e => e.Id)
            .OrderBy(static e => e.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        EncounterModel[] highRiskEncounters = HighRiskPoolFilter.SelectHighRiskEncounters(
            allEncounters, includeEventEncounters: true);
        if (highRiskEncounters.Length == 0)
            highRiskEncounters = allEncounters;

        EnchantmentModel[] allEnchantments = ModelDb.DebugEnchantments
            .Where(e => !HotCoverageAuditor.IsMockOrDeprecated(e))
            .DistinctBy(static e => e.Id)
            .OrderBy(static e => e.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        AfflictionModel[] allAfflictions = ModelDb.DebugAfflictions
            .Where(a => !HotCoverageAuditor.IsMockOrDeprecated(a))
            .DistinctBy(static a => a.Id)
            .OrderBy(static a => a.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        OrbModel[] allOrbs = ModelDb.Orbs
            .Where(o => !HotCoverageAuditor.IsMockOrDeprecated(o))
            .DistinctBy(static o => o.Id)
            .OrderBy(static o => o.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        DominionModel[] allDominions = ModelDb.All.OfType<DominionModel>()
            .Where(d => !HotCoverageAuditor.IsMockOrDeprecated(d))
            .DistinctBy(static d => d.Id)
            .OrderBy(static d => d.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, string> encMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (EncounterModel e in allEncounters)
        {
            encMap[e.Id.Entry] = e.Id.Entry;
            encMap[e.GetType().Name] = e.Id.Entry;
        }

        return new SingleplayerPools
        {
            CharacterCards = byChar,
            HighRiskCharacterCards = highRiskByChar,
            UltraHighRiskCharacterCards = ultraByChar,
            ModerateCharacterCards = moderateByChar,
            SharedCards = sharedCards,
            AllCards = allCards,
            HighRiskCards = highRiskCards,
            UltraHighRiskCards = ultraHighRiskCards,
            ModerateCards = moderateCards,
            CombatRelics = combatRelics,
            HighRiskRelics = highRiskRelics,
            AllRelics = allRelics,
            AllPotions = allPotions,
            AllEncounters = allEncounters,
            HighRiskEncounters = highRiskEncounters,
            AllEnchantments = allEnchantments,
            AllAfflictions = allAfflictions,
            AllOrbs = allOrbs,
            AllDominions = allDominions,
            ValidCardEntries = allCards.Select(static c => c.Id.Entry).ToHashSet(StringComparer.Ordinal),
            HighRiskCardEntries = highRiskCards.Select(static c => c.Id.Entry).ToHashSet(StringComparer.Ordinal),
            ValidRelicEntries = allRelics.Select(static r => r.Id.Entry).ToHashSet(StringComparer.Ordinal),
            HighRiskRelicEntries = highRiskRelics.Select(static r => r.Id.Entry).ToHashSet(StringComparer.Ordinal),
            ValidPotionEntries = allPotions.Select(static p => p.Id.Entry).ToHashSet(StringComparer.Ordinal),
            ValidPowerEntries = ModelDb.AllPowers
                .Where(p => !HotCoverageAuditor.IsMockOrDeprecated(p))
                .Select(static p => p.Id.Entry)
                .ToHashSet(StringComparer.Ordinal),
            EncounterEntryByKey = encMap,
            CharacterByEntry = charByEntry,
        };
    }

    private static List<T> ShuffleCopy<T>(IEnumerable<T> source, Random rng)
    {
        List<T> list = source.ToList();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
