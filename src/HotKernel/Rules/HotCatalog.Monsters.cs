using System.Collections.Frozen;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace CombatSolver.HotKernel;

internal readonly record struct HotMonsterCatalogEntry(
    HotMonsterId Id,
    string Entry,
    string TypeName,
    HotMonsterStateKind StateKind);

/// <summary>
/// Static STS2 0.114.0 catalog covering all 121 vanilla monsters, spawn-closure graph,
/// move-effect precompilation, and Orb / Enchantment / Affliction / Dominion ID resolvers.
/// </summary>
internal static class HotMonsterCatalog
{
    public static readonly HotMonsterCatalogEntry[] ById = BuildById();
    public static readonly FrozenDictionary<string, HotMonsterId> IdByEntry = BuildIdByEntry();
    public static readonly FrozenDictionary<string, HotMonsterId> IdByTypeName = BuildIdByTypeName();
    public static readonly FrozenDictionary<string, HotOrbId> OrbIdByEntry = BuildOrbIds();
    public static readonly FrozenDictionary<string, HotEnchantmentId> EnchIdByEntry = BuildEnchIds();
    public static readonly FrozenDictionary<string, HotAfflictionId> AfflIdByEntry = BuildAfflIds();
    public static readonly FrozenDictionary<string, HotDominionId> DominionIdByEntry = BuildDominionIds();

    public static HotMonsterId FindByEntry(string entry)
        => IdByEntry.TryGetValue(entry, out HotMonsterId id) ? id : HotMonsterId.None;

    public static HotMonsterId FindByTypeName(string typeName)
        => IdByTypeName.TryGetValue(typeName, out HotMonsterId id) ? id : HotMonsterId.None;

    public static HotOrbId FindOrbByEntry(string entry)
        => OrbIdByEntry.TryGetValue(entry, out HotOrbId id) ? id : HotOrbId.None;

    public static HotEnchantmentId FindEnchantmentByEntry(string entry)
        => EnchIdByEntry.TryGetValue(entry, out HotEnchantmentId id) ? id : HotEnchantmentId.None;

    public static HotAfflictionId FindAfflictionByEntry(string entry)
        => AfflIdByEntry.TryGetValue(entry, out HotAfflictionId id) ? id : HotAfflictionId.None;

    public static HotDominionId FindDominionByEntry(string? entry)
        => entry != null && DominionIdByEntry.TryGetValue(entry, out HotDominionId id) ? id : HotDominionId.None;

    public static bool RemovesOwnerOnMove(HotMonsterId monsterId, string moveId)
        => (monsterId, moveId) is
            (HotMonsterId.GasBomb, "EXPLODE_MOVE") or
            (HotMonsterId.WaterfallGiant, "EXPLODE_MOVE") or
            (HotMonsterId.FatGremlin, "FLEE_MOVE") or
            (HotMonsterId.ThievingHopper, "ESCAPE_MOVE");

    public static ReadOnlySpan<HotMonsterId> GetSpawnClosure(HotMonsterId monsterId)
        => monsterId switch
        {
            HotMonsterId.LivingFog => [HotMonsterId.GasBomb],
            HotMonsterId.Fogmog => [HotMonsterId.EyeWithTeeth],
            HotMonsterId.Ovicopter => [HotMonsterId.ToughEgg],
            HotMonsterId.TheObscura => [HotMonsterId.Parafright],
            HotMonsterId.TwoTailedRat => [HotMonsterId.TwoTailedRat],
            HotMonsterId.Fabricator => [HotMonsterId.Guardbot, HotMonsterId.Noisebot, HotMonsterId.Zapbot, HotMonsterId.Stabbot],
            HotMonsterId.PhrogParasite => [HotMonsterId.Wriggler],
            HotMonsterId.InfestedPrism => [HotMonsterId.Wriggler],
            HotMonsterId.GremlinMerc => [HotMonsterId.FatGremlin, HotMonsterId.SneakyGremlin],
            HotMonsterId.Axebot => [HotMonsterId.Axebot],
            _ => ReadOnlySpan<HotMonsterId>.Empty,
        };

    public static MonsterModel? TryGetCanonicalMonsterModel(HotMonsterId monsterId)
        => monsterId switch
        {
            HotMonsterId.GasBomb => ModelDb.Monster<GasBomb>(),
            HotMonsterId.EyeWithTeeth => ModelDb.Monster<EyeWithTeeth>(),
            HotMonsterId.ToughEgg => ModelDb.Monster<ToughEgg>(),
            HotMonsterId.Parafright => ModelDb.Monster<Parafright>(),
            HotMonsterId.TwoTailedRat => ModelDb.Monster<TwoTailedRat>(),
            HotMonsterId.Guardbot => ModelDb.Monster<Guardbot>(),
            HotMonsterId.Noisebot => ModelDb.Monster<Noisebot>(),
            HotMonsterId.Zapbot => ModelDb.Monster<Zapbot>(),
            HotMonsterId.Stabbot => ModelDb.Monster<Stabbot>(),
            HotMonsterId.Wriggler => ModelDb.Monster<Wriggler>(),
            HotMonsterId.FatGremlin => ModelDb.Monster<FatGremlin>(),
            HotMonsterId.SneakyGremlin => ModelDb.Monster<SneakyGremlin>(),
            HotMonsterId.Axebot => ModelDb.Monster<Axebot>(),
            _ => null,
        };

    public static HotMonsterMoveEffect[] CompileStandardMoveEffects(
        HotMonsterId monsterId,
        string moveId,
        IReadOnlyDictionary<string, int> staticInts,
        Func<HotPowerId, ushort> ensurePowerDefId)
    {
        short S(string key, int fallback = 0)
            => (short)(staticInts.TryGetValue(key, out int v) ? v : fallback);
        ushort P(HotPowerId id) => ensurePowerDefId(id);

        return (monsterId, moveId) switch
        {
            (HotMonsterId.FuzzyWurmCrawler, "INHALE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), 7)],
            (HotMonsterId.Nibbit, "SLICE_MOVE") => [new(HotCardOpKind.GainBlock, 0, S("SliceBlock"))],
            (HotMonsterId.Nibbit, "HISS_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("HissStrengthGain"))],
            (HotMonsterId.BowlbugEgg, "BITE_MOVE") => [new(HotCardOpKind.GainBlock, 0, S("ProtectBlock"))],
            (HotMonsterId.BowlbugNectar, "BUFF_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("BuffStrengthGain"))],
            (HotMonsterId.CalcifiedCultist, "INCANTATION_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.RitualPower), S("IncantationAmount"))],
            (HotMonsterId.DampCultist, "INCANTATION_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.RitualPower), S("IncantationAmount"))],
            (HotMonsterId.SludgeSpinner, "OIL_SPRAY_MOVE") => [new(HotCardOpKind.ApplyPowerTarget, P(HotPowerId.WeakPower), 1)],
            (HotMonsterId.SludgeSpinner, "RAGE_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), 3)],
            (HotMonsterId.Flyconid, "VULNERABLE_SPORES_MOVE") => [new(HotCardOpKind.ApplyPowerTarget, P(HotPowerId.VulnerablePower), 2)],
            (HotMonsterId.Flyconid, "FRAIL_SPORES_MOVE") => [new(HotCardOpKind.ApplyPowerTarget, P(HotPowerId.FrailPower), 2)],
            (HotMonsterId.FrogKnight, "FOR_THE_QUEEN") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), 5)],
            (HotMonsterId.FrogKnight, "TONGUE_LASH") => [new(HotCardOpKind.ApplyPowerTarget, P(HotPowerId.FrailPower), 2)],
            (HotMonsterId.CorpseSlug, "GOOP_MOVE") => [new(HotCardOpKind.ApplyPowerTarget, P(HotPowerId.FrailPower), S("GoopFrailAmt"))],
            (HotMonsterId.Mawler, "ROAR_MOVE") => [new(HotCardOpKind.ApplyPowerTarget, P(HotPowerId.VulnerablePower), 3)],
            (HotMonsterId.Myte, "SUCK_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("SuckStrength"))],
            (HotMonsterId.MagiKnight, "POWER_SHIELD_MOVE") => [new(HotCardOpKind.GainBlock, 0, S("PowerShieldBlock"))],
            (HotMonsterId.Seapunk, "BUBBLE_BURP_MOVE") => [new(HotCardOpKind.GainBlock, 0, S("BubbleBlock")), new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("BubbleStr"))],
            (HotMonsterId.KinFollower, "POWER_DANCE_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("DanceStrength"))],
            (HotMonsterId.KinPriest, "ORB_OF_FRAILTY_MOVE") => [new(HotCardOpKind.ApplyPowerTarget, P(HotPowerId.FrailPower), 1)],
            (HotMonsterId.KinPriest, "ORB_OF_WEAKNESS_MOVE") => [new(HotCardOpKind.ApplyPowerTarget, P(HotPowerId.WeakPower), 1)],
            (HotMonsterId.KinPriest, "RITUAL_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("RitualStrength"))],
            (HotMonsterId.Vantom, "PREPARE_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("PrepareStrength"))],
            (HotMonsterId.TheObscura, "HARDENING_STRIKE_MOVE") => [new(HotCardOpKind.GainBlock, 0, S("HardeningStrikeBlock"))],
            (HotMonsterId.Ovicopter, "TENDERIZER_MOVE") => [new(HotCardOpKind.ApplyPowerTarget, P(HotPowerId.VulnerablePower), 2)],
            (HotMonsterId.Ovicopter, "NUTRITIONAL_PASTE_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("NutritionalPasteStrengthAmount"))],
            (HotMonsterId.Crusher, "ADAPT_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("AdaptStrengthGain"))],
            (HotMonsterId.Rocket, "CHARGE_UP_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("ChargeUpStrengthGain"))],
            (HotMonsterId.PhantasmalGardener, "ENLARGE_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("EnlargeStr"))],
            (HotMonsterId.Axebot, "BOOT_UP_MOVE") => [new(HotCardOpKind.GainBlock, 0, S("BootUpBlock")), new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("BootUpStrGain"))],
            (HotMonsterId.AxeRubyRaider, "SWING_1") => [new(HotCardOpKind.GainBlock, 0, S("SwingBlock"))],
            (HotMonsterId.AxeRubyRaider, "SWING_2") => [new(HotCardOpKind.GainBlock, 0, S("SwingBlock"))],
            (HotMonsterId.SkulkingColony, "INERTIA_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("InertiaStrengthGain"))],
            (HotMonsterId.Exoskeleton, "ENRAGE_MOVE") => [new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), 2)],
            (HotMonsterId.LouseProgenitor, "CURL_AND_GROW_MOVE") => [new(HotCardOpKind.GainBlock, 0, S("CurlBlock")), new(HotCardOpKind.ApplyPowerSelf, P(HotPowerId.StrengthPower), S("GrowStrength"))],
            (HotMonsterId.Tunneler, "BURROW_MOVE") => [new(HotCardOpKind.GainBlock, 0, S("BlockGain"))],
            (HotMonsterId.TestSubject, "SKULL_BASH_MOVE") => [new(HotCardOpKind.ApplyPowerTarget, P(HotPowerId.VulnerablePower), 1)],
            _ => [],
        };
    }

    private static FrozenDictionary<string, HotMonsterId> BuildIdByEntry()
    {
        Dictionary<string, HotMonsterId> map = new(ById.Length, StringComparer.Ordinal);
        for (int i = 1; i < ById.Length; i++)
            map[ById[i].Entry] = ById[i].Id;
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, HotMonsterId> BuildIdByTypeName()
    {
        Dictionary<string, HotMonsterId> map = new(ById.Length, StringComparer.Ordinal);
        for (int i = 1; i < ById.Length; i++)
            map[ById[i].TypeName] = ById[i].Id;
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, HotOrbId> BuildOrbIds()
        => new Dictionary<string, HotOrbId>(StringComparer.Ordinal)
        {
            ["LIGHTNING_ORB"] = HotOrbId.Lightning,
            ["FROST_ORB"] = HotOrbId.Frost,
            ["DARK_ORB"] = HotOrbId.Dark,
            ["PLASMA_ORB"] = HotOrbId.Plasma,
            ["GLASS_ORB"] = HotOrbId.Glass,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static FrozenDictionary<string, HotEnchantmentId> BuildEnchIds()
        => new Dictionary<string, HotEnchantmentId>(StringComparer.Ordinal)
        {
            ["ADROIT"] = HotEnchantmentId.Adroit,
            ["CLONE"] = HotEnchantmentId.Clone,
            ["CORRUPTED"] = HotEnchantmentId.Corrupted,
            ["GLAM"] = HotEnchantmentId.Glam,
            ["GOOPY"] = HotEnchantmentId.Goopy,
            ["IMBUED"] = HotEnchantmentId.Imbued,
            ["INKY"] = HotEnchantmentId.Inky,
            ["INSTINCT"] = HotEnchantmentId.Instinct,
            ["MOMENTUM"] = HotEnchantmentId.Momentum,
            ["NIMBLE"] = HotEnchantmentId.Nimble,
            ["PERFECT_FIT"] = HotEnchantmentId.PerfectFit,
            ["ROYALLY_APPROVED"] = HotEnchantmentId.RoyallyApproved,
            ["SHARP"] = HotEnchantmentId.Sharp,
            ["SLITHER"] = HotEnchantmentId.Slither,
            ["SLUMBERING_ESSENCE"] = HotEnchantmentId.SlumberingEssence,
            ["SOULS_POWER"] = HotEnchantmentId.SoulsPower,
            ["SOWN"] = HotEnchantmentId.Sown,
            ["SPIRAL"] = HotEnchantmentId.Spiral,
            ["STEADY"] = HotEnchantmentId.Steady,
            ["SWIFT"] = HotEnchantmentId.Swift,
            ["TEZCATARAS_EMBER"] = HotEnchantmentId.TezcatarasEmber,
            ["VIGOROUS"] = HotEnchantmentId.Vigorous,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static FrozenDictionary<string, HotAfflictionId> BuildAfflIds()
        => new Dictionary<string, HotAfflictionId>(StringComparer.Ordinal)
        {
            ["BOUND"] = HotAfflictionId.Bound,
            ["ENTANGLED"] = HotAfflictionId.Entangled,
            ["GALVANIZED"] = HotAfflictionId.Galvanized,
            ["HEXED"] = HotAfflictionId.Hexed,
            ["RINGING"] = HotAfflictionId.Ringing,
            ["SMOG"] = HotAfflictionId.Smog,
            ["TAINTED"] = HotAfflictionId.Tainted,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static FrozenDictionary<string, HotDominionId> BuildDominionIds()
        => new Dictionary<string, HotDominionId>(StringComparer.Ordinal)
        {
            ["AEONGLASS_DOMINION"] = HotDominionId.AeonglassDominion,
            ["CEREMONIAL_BEAST_DOMINION"] = HotDominionId.CeremonialBeastDominion,
            ["INSATIABLE_DOMINION"] = HotDominionId.InsatiableDominion,
            ["KAISER_CRAB_DOMINION"] = HotDominionId.KaiserCrabDominion,
            ["KIN_DOMINION"] = HotDominionId.KinDominion,
            ["KNOWLEDGE_DEMON_DOMINION"] = HotDominionId.KnowledgeDemonDominion,
            ["LAGAVULIN_MATRIARCH_DOMINION"] = HotDominionId.LagavulinMatriarchDominion,
            ["QUEEN_DOMINION"] = HotDominionId.QueenDominion,
            ["SOUL_FYSH_DOMINION"] = HotDominionId.SoulFyshDominion,
            ["TEST_SUBJECT_DOMINION"] = HotDominionId.TestSubjectDominion,
            ["VANTOM_DOMINION"] = HotDominionId.VantomDominion,
            ["WATERFALL_GIANT_DOMINION"] = HotDominionId.WaterfallGiantDominion,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static HotMonsterCatalogEntry[] BuildById()
    {
        HotMonsterCatalogEntry[] arr = new HotMonsterCatalogEntry[(int)HotMonsterId.Count];
        arr[(int)HotMonsterId.Aeonglass] = new(HotMonsterId.Aeonglass, "AEONGLASS", "Aeonglass", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Architect] = new(HotMonsterId.Architect, "ARCHITECT", "Architect", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.AssassinRubyRaider] = new(HotMonsterId.AssassinRubyRaider, "ASSASSIN_RUBY_RAIDER", "AssassinRubyRaider", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.AxeRubyRaider] = new(HotMonsterId.AxeRubyRaider, "AXE_RUBY_RAIDER", "AxeRubyRaider", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Axebot] = new(HotMonsterId.Axebot, "AXEBOT", "Axebot", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.BattleFriendV1] = new(HotMonsterId.BattleFriendV1, "BATTLE_FRIEND_V1", "BattleFriendV1", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.BattleFriendV2] = new(HotMonsterId.BattleFriendV2, "BATTLE_FRIEND_V2", "BattleFriendV2", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.BattleFriendV3] = new(HotMonsterId.BattleFriendV3, "BATTLE_FRIEND_V3", "BattleFriendV3", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.BigDummy] = new(HotMonsterId.BigDummy, "BIG_DUMMY", "BigDummy", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.BowlbugEgg] = new(HotMonsterId.BowlbugEgg, "BOWLBUG_EGG", "BowlbugEgg", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.BowlbugNectar] = new(HotMonsterId.BowlbugNectar, "BOWLBUG_NECTAR", "BowlbugNectar", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.BowlbugRock] = new(HotMonsterId.BowlbugRock, "BOWLBUG_ROCK", "BowlbugRock", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.BowlbugSilk] = new(HotMonsterId.BowlbugSilk, "BOWLBUG_SILK", "BowlbugSilk", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.BruteRubyRaider] = new(HotMonsterId.BruteRubyRaider, "BRUTE_RUBY_RAIDER", "BruteRubyRaider", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.BygoneEffigy] = new(HotMonsterId.BygoneEffigy, "BYGONE_EFFIGY", "BygoneEffigy", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.ByrdineMonster] = new(HotMonsterId.ByrdineMonster, "BYRDINE_MONSTER", "ByrdineMonster", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Byrdonis] = new(HotMonsterId.Byrdonis, "BYRDONIS", "Byrdonis", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Byrdpip] = new(HotMonsterId.Byrdpip, "BYRDPIP", "Byrdpip", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.CalcifiedCultist] = new(HotMonsterId.CalcifiedCultist, "CALCIFIED_CULTIST", "CalcifiedCultist", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.CeremonialBeast] = new(HotMonsterId.CeremonialBeast, "CEREMONIAL_BEAST", "CeremonialBeast", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Chomper] = new(HotMonsterId.Chomper, "CHOMPER", "Chomper", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.CorpseSlug] = new(HotMonsterId.CorpseSlug, "CORPSE_SLUG", "CorpseSlug", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.CrossbowRubyRaider] = new(HotMonsterId.CrossbowRubyRaider, "CROSSBOW_RUBY_RAIDER", "CrossbowRubyRaider", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Crusher] = new(HotMonsterId.Crusher, "CRUSHER", "Crusher", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.CubexConstruct] = new(HotMonsterId.CubexConstruct, "CUBEX_CONSTRUCT", "CubexConstruct", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.DampCultist] = new(HotMonsterId.DampCultist, "DAMP_CULTIST", "DampCultist", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.DecimillipedeSegmentBack] = new(HotMonsterId.DecimillipedeSegmentBack, "DECIMILLIPEDE_SEGMENT_BACK", "DecimillipedeSegmentBack", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.DecimillipedeSegmentFront] = new(HotMonsterId.DecimillipedeSegmentFront, "DECIMILLIPEDE_SEGMENT_FRONT", "DecimillipedeSegmentFront", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.DecimillipedeSegmentMiddle] = new(HotMonsterId.DecimillipedeSegmentMiddle, "DECIMILLIPEDE_SEGMENT_MIDDLE", "DecimillipedeSegmentMiddle", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.DevotedSculptor] = new(HotMonsterId.DevotedSculptor, "DEVOTED_SCULPTOR", "DevotedSculptor", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Entomancer] = new(HotMonsterId.Entomancer, "ENTOMANCER", "Entomancer", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Exoskeleton] = new(HotMonsterId.Exoskeleton, "EXOSKELETON", "Exoskeleton", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.EyeWithTeeth] = new(HotMonsterId.EyeWithTeeth, "EYE_WITH_TEETH", "EyeWithTeeth", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Fabricator] = new(HotMonsterId.Fabricator, "FABRICATOR", "Fabricator", HotMonsterStateKind.Fabricator);
        arr[(int)HotMonsterId.FakeMerchantMonster] = new(HotMonsterId.FakeMerchantMonster, "FAKE_MERCHANT_MONSTER", "FakeMerchantMonster", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.FatGremlin] = new(HotMonsterId.FatGremlin, "FAT_GREMLIN", "FatGremlin", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.FlailKnight] = new(HotMonsterId.FlailKnight, "FLAIL_KNIGHT", "FlailKnight", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Flyconid] = new(HotMonsterId.Flyconid, "FLYCONID", "Flyconid", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Fogmog] = new(HotMonsterId.Fogmog, "FOGMOG", "Fogmog", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.FossilStalker] = new(HotMonsterId.FossilStalker, "FOSSIL_STALKER", "FossilStalker", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.FrogKnight] = new(HotMonsterId.FrogKnight, "FROG_KNIGHT", "FrogKnight", HotMonsterStateKind.FrogKnight);
        arr[(int)HotMonsterId.FuzzyWurmCrawler] = new(HotMonsterId.FuzzyWurmCrawler, "FUZZY_WURM_CRAWLER", "FuzzyWurmCrawler", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.GasBomb] = new(HotMonsterId.GasBomb, "GAS_BOMB", "GasBomb", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.GlobeHead] = new(HotMonsterId.GlobeHead, "GLOBE_HEAD", "GlobeHead", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.GremlinMerc] = new(HotMonsterId.GremlinMerc, "GREMLIN_MERC", "GremlinMerc", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Guardbot] = new(HotMonsterId.Guardbot, "GUARDBOT", "Guardbot", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.HauntedShip] = new(HotMonsterId.HauntedShip, "HAUNTED_SHIP", "HauntedShip", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.HunterKiller] = new(HotMonsterId.HunterKiller, "HUNTER_KILLER", "HunterKiller", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.InfestedPrism] = new(HotMonsterId.InfestedPrism, "INFESTED_PRISM", "InfestedPrism", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Inklet] = new(HotMonsterId.Inklet, "INKLET", "Inklet", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.KinFollower] = new(HotMonsterId.KinFollower, "KIN_FOLLOWER", "KinFollower", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.KinPriest] = new(HotMonsterId.KinPriest, "KIN_PRIEST", "KinPriest", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.KnowledgeDemon] = new(HotMonsterId.KnowledgeDemon, "KNOWLEDGE_DEMON", "KnowledgeDemon", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.LagavulinMatriarch] = new(HotMonsterId.LagavulinMatriarch, "LAGAVULIN_MATRIARCH", "LagavulinMatriarch", HotMonsterStateKind.AwakeCheck);
        arr[(int)HotMonsterId.LeafSlimeM] = new(HotMonsterId.LeafSlimeM, "LEAF_SLIME_M", "LeafSlimeM", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.LeafSlimeS] = new(HotMonsterId.LeafSlimeS, "LEAF_SLIME_S", "LeafSlimeS", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.LivingFog] = new(HotMonsterId.LivingFog, "LIVING_FOG", "LivingFog", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.LivingShield] = new(HotMonsterId.LivingShield, "LIVING_SHIELD", "LivingShield", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.LouseProgenitor] = new(HotMonsterId.LouseProgenitor, "LOUSE_PROGENITOR", "LouseProgenitor", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.MagiKnight] = new(HotMonsterId.MagiKnight, "MAGI_KNIGHT", "MagiKnight", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Mawler] = new(HotMonsterId.Mawler, "MAWLER", "Mawler", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.MechaKnight] = new(HotMonsterId.MechaKnight, "MECHA_KNIGHT", "MechaKnight", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.MultiAttackMoveMonster] = new(HotMonsterId.MultiAttackMoveMonster, "MULTI_ATTACK_MOVE_MONSTER", "MultiAttackMoveMonster", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.MysteriousKnight] = new(HotMonsterId.MysteriousKnight, "MYSTERIOUS_KNIGHT", "MysteriousKnight", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Myte] = new(HotMonsterId.Myte, "MYTE", "Myte", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Nibbit] = new(HotMonsterId.Nibbit, "NIBBIT", "Nibbit", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Noisebot] = new(HotMonsterId.Noisebot, "NOISEBOT", "Noisebot", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.OneHpMonster] = new(HotMonsterId.OneHpMonster, "ONE_HP_MONSTER", "OneHpMonster", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Osty] = new(HotMonsterId.Osty, "OSTY", "Osty", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Ovicopter] = new(HotMonsterId.Ovicopter, "OVICOPTER", "Ovicopter", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.OwlMagistrate] = new(HotMonsterId.OwlMagistrate, "OWL_MAGISTRATE", "OwlMagistrate", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.PaelsLegion] = new(HotMonsterId.PaelsLegion, "PAELS_LEGION", "PaelsLegion", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Parafright] = new(HotMonsterId.Parafright, "PARAFRIGHT", "Parafright", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.PhantasmalGardener] = new(HotMonsterId.PhantasmalGardener, "PHANTASMAL_GARDENER", "PhantasmalGardener", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.PhrogParasite] = new(HotMonsterId.PhrogParasite, "PHROG_PARASITE", "PhrogParasite", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.PunchConstruct] = new(HotMonsterId.PunchConstruct, "PUNCH_CONSTRUCT", "PunchConstruct", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Queen] = new(HotMonsterId.Queen, "QUEEN", "Queen", HotMonsterStateKind.Queen);
        arr[(int)HotMonsterId.Rocket] = new(HotMonsterId.Rocket, "ROCKET", "Rocket", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.ScrollOfBiting] = new(HotMonsterId.ScrollOfBiting, "SCROLL_OF_BITING", "ScrollOfBiting", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Seapunk] = new(HotMonsterId.Seapunk, "SEAPUNK", "Seapunk", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SewerClam] = new(HotMonsterId.SewerClam, "SEWER_CLAM", "SewerClam", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.ShrinkerBeetle] = new(HotMonsterId.ShrinkerBeetle, "SHRINKER_BEETLE", "ShrinkerBeetle", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SingleAttackMoveMonster] = new(HotMonsterId.SingleAttackMoveMonster, "SINGLE_ATTACK_MOVE_MONSTER", "SingleAttackMoveMonster", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SkulkingColony] = new(HotMonsterId.SkulkingColony, "SKULKING_COLONY", "SkulkingColony", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SlimedBerserker] = new(HotMonsterId.SlimedBerserker, "SLIMED_BERSERKER", "SlimedBerserker", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SlitheringStrangler] = new(HotMonsterId.SlitheringStrangler, "SLITHERING_STRANGLER", "SlitheringStrangler", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SludgeSpinner] = new(HotMonsterId.SludgeSpinner, "SLUDGE_SPINNER", "SludgeSpinner", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SlumberingBeetle] = new(HotMonsterId.SlumberingBeetle, "SLUMBERING_BEETLE", "SlumberingBeetle", HotMonsterStateKind.AwakeCheck);
        arr[(int)HotMonsterId.SnappingJaxfruit] = new(HotMonsterId.SnappingJaxfruit, "SNAPPING_JAXFRUIT", "SnappingJaxfruit", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SneakyGremlin] = new(HotMonsterId.SneakyGremlin, "SNEAKY_GREMLIN", "SneakyGremlin", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SoulFysh] = new(HotMonsterId.SoulFysh, "SOUL_FYSH", "SoulFysh", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SoulNexus] = new(HotMonsterId.SoulNexus, "SOUL_NEXUS", "SoulNexus", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SpectralKnight] = new(HotMonsterId.SpectralKnight, "SPECTRAL_KNIGHT", "SpectralKnight", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.SpinyToad] = new(HotMonsterId.SpinyToad, "SPINY_TOAD", "SpinyToad", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Stabbot] = new(HotMonsterId.Stabbot, "STABBOT", "Stabbot", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TenHpMonster] = new(HotMonsterId.TenHpMonster, "TEN_HP_MONSTER", "TenHpMonster", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TerrorEel] = new(HotMonsterId.TerrorEel, "TERROR_EEL", "TerrorEel", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TestSubject] = new(HotMonsterId.TestSubject, "TEST_SUBJECT", "TestSubject", HotMonsterStateKind.TestSubject);
        arr[(int)HotMonsterId.TheAdversaryMkOne] = new(HotMonsterId.TheAdversaryMkOne, "THE_ADVERSARY_MK_ONE", "TheAdversaryMkOne", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TheAdversaryMkThree] = new(HotMonsterId.TheAdversaryMkThree, "THE_ADVERSARY_MK_THREE", "TheAdversaryMkThree", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TheAdversaryMkTwo] = new(HotMonsterId.TheAdversaryMkTwo, "THE_ADVERSARY_MK_TWO", "TheAdversaryMkTwo", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TheForgotten] = new(HotMonsterId.TheForgotten, "THE_FORGOTTEN", "TheForgotten", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TheInsatiable] = new(HotMonsterId.TheInsatiable, "THE_INSATIABLE", "TheInsatiable", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TheLost] = new(HotMonsterId.TheLost, "THE_LOST", "TheLost", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TheObscura] = new(HotMonsterId.TheObscura, "THE_OBSCURA", "TheObscura", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.ThievingHopper] = new(HotMonsterId.ThievingHopper, "THIEVING_HOPPER", "ThievingHopper", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Toadpole] = new(HotMonsterId.Toadpole, "TOADPOLE", "Toadpole", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TorchHeadAmalgam] = new(HotMonsterId.TorchHeadAmalgam, "TORCH_HEAD_AMALGAM", "TorchHeadAmalgam", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.ToughEgg] = new(HotMonsterId.ToughEgg, "TOUGH_EGG", "ToughEgg", HotMonsterStateKind.ToughEgg);
        arr[(int)HotMonsterId.TrackerRubyRaider] = new(HotMonsterId.TrackerRubyRaider, "TRACKER_RUBY_RAIDER", "TrackerRubyRaider", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Tunneler] = new(HotMonsterId.Tunneler, "TUNNELER", "Tunneler", HotMonsterStateKind.Tunneler);
        arr[(int)HotMonsterId.TurretOperator] = new(HotMonsterId.TurretOperator, "TURRET_OPERATOR", "TurretOperator", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TwigSlimeM] = new(HotMonsterId.TwigSlimeM, "TWIG_SLIME_M", "TwigSlimeM", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TwigSlimeS] = new(HotMonsterId.TwigSlimeS, "TWIG_SLIME_S", "TwigSlimeS", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.TwoTailedRat] = new(HotMonsterId.TwoTailedRat, "TWO_TAILED_RAT", "TwoTailedRat", HotMonsterStateKind.TwoTailedRat);
        arr[(int)HotMonsterId.Vantom] = new(HotMonsterId.Vantom, "VANTOM", "Vantom", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.VineShambler] = new(HotMonsterId.VineShambler, "VINE_SHAMBLER", "VineShambler", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.WaterfallGiant] = new(HotMonsterId.WaterfallGiant, "WATERFALL_GIANT", "WaterfallGiant", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Wriggler] = new(HotMonsterId.Wriggler, "WRIGGLER", "Wriggler", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Wuppy] = new(HotMonsterId.Wuppy, "WUPPY", "Wuppy", HotMonsterStateKind.None);
        arr[(int)HotMonsterId.Zapbot] = new(HotMonsterId.Zapbot, "ZAPBOT", "Zapbot", HotMonsterStateKind.None);
        return arr;
    }
}
