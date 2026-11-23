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
    int GeneratedOptionIndex);

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
        public required CardModel[] SharedCards { get; init; }
        public required CardModel[] AllCards { get; init; }
        public required RelicModel[] CombatRelics { get; init; }
        public required RelicModel[] AllRelics { get; init; }
        public required PotionModel[] AllPotions { get; init; }
        public required EncounterModel[] AllEncounters { get; init; }
        public required EnchantmentModel[] AllEnchantments { get; init; }
        public required AfflictionModel[] AllAfflictions { get; init; }
        public required OrbModel[] AllOrbs { get; init; }
        public required DominionModel[] AllDominions { get; init; }
        public required HashSet<string> ValidCardEntries { get; init; }
        public required HashSet<string> ValidRelicEntries { get; init; }
        public required HashSet<string> ValidPotionEntries { get; init; }
        public required HashSet<string> ValidPowerEntries { get; init; }
        public required Dictionary<string, string> EncounterEntryByKey { get; init; }
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
        HashSet<ModelId> charCardIds = [];
        foreach (CharacterModel ch in ModelDb.AllCharacters)
        {
            CardModel[] list = ch.CardPool.AllCards
                .Where(c => !HotCoverageAuditor.IsMockOrDeprecated(c)
                    && c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
                .DistinctBy(static c => c.Id)
                .OrderBy(static c => c.Id.Entry, StringComparer.Ordinal)
                .ToArray();
            byChar[ch.Id.Entry] = list;
            foreach (CardModel c in list)
                charCardIds.Add(c.Id);
        }
        CardModel[] sharedCards = allCards.Where(c => !charCardIds.Contains(c.Id)).ToArray();

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
            SharedCards = sharedCards,
            AllCards = allCards,
            CombatRelics = combatRelics,
            AllRelics = allRelics,
            AllPotions = allPotions,
            AllEncounters = allEncounters,
            AllEnchantments = allEnchantments,
            AllAfflictions = allAfflictions,
            AllOrbs = allOrbs,
            AllDominions = allDominions,
            ValidCardEntries = allCards.Select(static c => c.Id.Entry).ToHashSet(StringComparer.Ordinal),
            ValidRelicEntries = allRelics.Select(static r => r.Id.Entry).ToHashSet(StringComparer.Ordinal),
            ValidPotionEntries = allPotions.Select(static p => p.Id.Entry).ToHashSet(StringComparer.Ordinal),
            ValidPowerEntries = ModelDb.AllPowers
                .Where(p => !HotCoverageAuditor.IsMockOrDeprecated(p))
                .Select(static p => p.Id.Entry)
                .ToHashSet(StringComparer.Ordinal),
            EncounterEntryByKey = encMap,
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
