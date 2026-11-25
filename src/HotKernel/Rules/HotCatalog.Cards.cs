using System.Collections.Frozen;

namespace CombatSolver.HotKernel;

internal readonly record struct HotCardCatalogEntry(
    HotCustomCardId Id,
    string Entry,
    HotCardDomain Domain,
    HotCalcVarKind CalcVar,
    HotHookMask HookMask,
    bool UsesBytecode,
    bool CanHealPlayer,
    bool CanGenerateUnknown,
    short SetupWeight);

/// <summary>
/// Static STS2 0.114.0 catalog covering all 717 vanilla cards: domains, all 50 CalcVarKind tags,
/// lossless bytecode compilation for standard cards, hook masks, and static setup weights.
/// </summary>
internal static class HotCardCatalog
{
    public static readonly HotCardCatalogEntry[] ById = BuildById();
    public static readonly FrozenDictionary<string, HotCustomCardId> IdByEntry = BuildIdByEntry();

    public static HotCustomCardId FindByEntry(string entry)
        => IdByEntry.TryGetValue(entry, out HotCustomCardId id) ? id : HotCustomCardId.None;

    public static ref readonly HotCardCatalogEntry Get(HotCustomCardId id)
    {
        int idx = (int)id;
        return ref ById[(uint)idx < (uint)ById.Length ? idx : 0];
    }

    private static FrozenDictionary<string, HotCustomCardId> BuildIdByEntry()
    {
        Dictionary<string, HotCustomCardId> map = new(ById.Length, StringComparer.Ordinal);
        for (int i = 1; i < ById.Length; i++)
        {
            if (ById[i].Id != HotCustomCardId.None)
                map[ById[i].Entry] = ById[i].Id;
        }
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static HotCardCatalogEntry[] BuildById()
    {
        HotCardCatalogEntry[] arr = new HotCardCatalogEntry[(int)HotCustomCardId.Count];
        arr[(int)HotCustomCardId.Abrasive] = new(HotCustomCardId.Abrasive, "ABRASIVE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.Abundance] = new(HotCustomCardId.Abundance, "ABUNDANCE", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, true, 14);
        arr[(int)HotCustomCardId.Accelerant] = new(HotCustomCardId.Accelerant, "ACCELERANT", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Accuracy] = new(HotCustomCardId.Accuracy, "ACCURACY", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 18);
        arr[(int)HotCustomCardId.Acrobatics] = new(HotCustomCardId.Acrobatics, "ACROBATICS", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.AdaptiveStrike] = new(HotCustomCardId.AdaptiveStrike, "ADAPTIVE_STRIKE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Adrenaline] = new(HotCustomCardId.Adrenaline, "ADRENALINE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Afterimage] = new(HotCustomCardId.Afterimage, "AFTERIMAGE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Afterlife] = new(HotCustomCardId.Afterlife, "AFTERLIFE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Aggression] = new(HotCustomCardId.Aggression, "AGGRESSION", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Alchemize] = new(HotCustomCardId.Alchemize, "ALCHEMIZE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.Alignment] = new(HotCustomCardId.Alignment, "ALIGNMENT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.AllForOne] = new(HotCustomCardId.AllForOne, "ALL_FOR_ONE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.AllIsWell] = new(HotCustomCardId.AllIsWell, "ALL_IS_WELL", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Amnesia] = new(HotCustomCardId.Amnesia, "AMNESIA", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Anger] = new(HotCustomCardId.Anger, "ANGER", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Anguish] = new(HotCustomCardId.Anguish, "ANGUISH", HotCardDomain.Fool, HotCalcVarKind.Anguish, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Anointed] = new(HotCustomCardId.Anointed, "ANOINTED", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Anticipate] = new(HotCustomCardId.Anticipate, "ANTICIPATE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Apotheosis] = new(HotCustomCardId.Apotheosis, "APOTHEOSIS", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Apparition] = new(HotCustomCardId.Apparition, "APPARITION", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Archery] = new(HotCustomCardId.Archery, "ARCHERY", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Armaments] = new(HotCustomCardId.Armaments, "ARMAMENTS", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ArrowStab] = new(HotCustomCardId.ArrowStab, "ARROW_STAB", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Arsenal] = new(HotCustomCardId.Arsenal, "ARSENAL", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.AscendersBane] = new(HotCustomCardId.AscendersBane, "ASCENDERS_BANE", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.AshenStrike] = new(HotCustomCardId.AshenStrike, "ASHEN_STRIKE", HotCardDomain.Ironclad, HotCalcVarKind.AshenStrike, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Assassinate] = new(HotCustomCardId.Assassinate, "ASSASSINATE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.AstralPulse] = new(HotCustomCardId.AstralPulse, "ASTRAL_PULSE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Automation] = new(HotCustomCardId.Automation, "AUTOMATION", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Backflip] = new(HotCustomCardId.Backflip, "BACKFLIP", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Backstab] = new(HotCustomCardId.Backstab, "BACKSTAB", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BadLuck] = new(HotCustomCardId.BadLuck, "BAD_LUCK", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BallLightning] = new(HotCustomCardId.BallLightning, "BALL_LIGHTNING", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BansheesCry] = new(HotCustomCardId.BansheesCry, "BANSHEES_CRY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.AfterCardEnteredCombat | HotHookMask.AfterCardPlayed, true, false, false, 0);
        arr[(int)HotCustomCardId.Barrage] = new(HotCustomCardId.Barrage, "BARRAGE", HotCardDomain.Defect, HotCalcVarKind.Barrage, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Barricade] = new(HotCustomCardId.Barricade, "BARRICADE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 34);
        arr[(int)HotCustomCardId.Bash] = new(HotCustomCardId.Bash, "BASH", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BattleTrance] = new(HotCustomCardId.BattleTrance, "BATTLE_TRANCE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BeaconOfHope] = new(HotCustomCardId.BeaconOfHope, "BEACON_OF_HOPE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.BeamCell] = new(HotCustomCardId.BeamCell, "BEAM_CELL", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BeatDown] = new(HotCustomCardId.BeatDown, "BEAT_DOWN", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BeatIntoShape] = new(HotCustomCardId.BeatIntoShape, "BEAT_INTO_SHAPE", HotCardDomain.Regent, HotCalcVarKind.BeatIntoShape, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Beckon] = new(HotCustomCardId.Beckon, "BECKON", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Begone] = new(HotCustomCardId.Begone, "BEGONE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BelieveInYou] = new(HotCustomCardId.BelieveInYou, "BELIEVE_IN_YOU", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BiasedCognition] = new(HotCustomCardId.BiasedCognition, "BIASED_COGNITION", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 36);
        arr[(int)HotCustomCardId.BigBang] = new(HotCustomCardId.BigBang, "BIG_BANG", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BlackHole] = new(HotCustomCardId.BlackHole, "BLACK_HOLE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.BladeDance] = new(HotCustomCardId.BladeDance, "BLADE_DANCE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BladeOfInk] = new(HotCustomCardId.BladeOfInk, "BLADE_OF_INK", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BladeSymphony] = new(HotCustomCardId.BladeSymphony, "BLADE_SYMPHONY", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Blaze] = new(HotCustomCardId.Blaze, "BLAZE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BlightStrike] = new(HotCustomCardId.BlightStrike, "BLIGHT_STRIKE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BloodWall] = new(HotCustomCardId.BloodWall, "BLOOD_WALL", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Bloodletting] = new(HotCustomCardId.Bloodletting, "BLOODLETTING", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BlossomShot] = new(HotCustomCardId.BlossomShot, "BLOSSOM_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BlotOutTheSky] = new(HotCustomCardId.BlotOutTheSky, "BLOT_OUT_THE_SKY", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.Bludgeon] = new(HotCustomCardId.Bludgeon, "BLUDGEON", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Blur] = new(HotCustomCardId.Blur, "BLUR", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BodySlam] = new(HotCustomCardId.BodySlam, "BODY_SLAM", HotCardDomain.Ironclad, HotCalcVarKind.BodySlam, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Bodyguard] = new(HotCustomCardId.Bodyguard, "BODYGUARD", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Bolas] = new(HotCustomCardId.Bolas, "BOLAS", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.BeforeHandDraw, true, false, false, 0);
        arr[(int)HotCustomCardId.Bombardment] = new(HotCustomCardId.Bombardment, "BOMBARDMENT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.AfterSideTurnStart, false, false, false, 0);
        arr[(int)HotCustomCardId.BoneShards] = new(HotCustomCardId.BoneShards, "BONE_SHARDS", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BoostAway] = new(HotCustomCardId.BoostAway, "BOOST_AWAY", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BootSequence] = new(HotCustomCardId.BootSequence, "BOOT_SEQUENCE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BorrowedTime] = new(HotCustomCardId.BorrowedTime, "BORROWED_TIME", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Bounce] = new(HotCustomCardId.Bounce, "BOUNCE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.BeforeHandDraw, true, false, false, 0);
        arr[(int)HotCustomCardId.BouncingFlask] = new(HotCustomCardId.BouncingFlask, "BOUNCING_FLASK", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BowSmack] = new(HotCustomCardId.BowSmack, "BOW_SMACK", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Brace] = new(HotCustomCardId.Brace, "BRACE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Brand] = new(HotCustomCardId.Brand, "BRAND", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Break] = new(HotCustomCardId.Break, "BREAK", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Breakthrough] = new(HotCustomCardId.Breakthrough, "BREAKTHROUGH", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BrightenUp] = new(HotCustomCardId.BrightenUp, "BRIGHTEN_UP", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BrightestFlame] = new(HotCustomCardId.BrightestFlame, "BRIGHTEST_FLAME", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.BubbleBubble] = new(HotCustomCardId.BubbleBubble, "BUBBLE_BUBBLE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Buffer] = new(HotCustomCardId.Buffer, "BUFFER", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 32);
        arr[(int)HotCustomCardId.BulkUp] = new(HotCustomCardId.BulkUp, "BULK_UP", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.BulletTime] = new(HotCustomCardId.BulletTime, "BULLET_TIME", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Bully] = new(HotCustomCardId.Bully, "BULLY", HotCardDomain.Ironclad, HotCalcVarKind.Bully, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Bulwark] = new(HotCustomCardId.Bulwark, "BULWARK", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BundleOfJoy] = new(HotCustomCardId.BundleOfJoy, "BUNDLE_OF_JOY", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.Burn] = new(HotCustomCardId.Burn, "BURN", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.BurningPact] = new(HotCustomCardId.BurningPact, "BURNING_PACT", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Burst] = new(HotCustomCardId.Burst, "BURST", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Bury] = new(HotCustomCardId.Bury, "BURY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.ByrdSwoop] = new(HotCustomCardId.ByrdSwoop, "BYRD_SWOOP", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.ByrdonisEgg] = new(HotCustomCardId.ByrdonisEgg, "BYRDONIS_EGG", HotCardDomain.Quest, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Cacophony] = new(HotCustomCardId.Cacophony, "CACOPHONY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Calamity] = new(HotCustomCardId.Calamity, "CALAMITY", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, true, 14);
        arr[(int)HotCustomCardId.Calcify] = new(HotCustomCardId.Calcify, "CALCIFY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.CalculatedGamble] = new(HotCustomCardId.CalculatedGamble, "CALCULATED_GAMBLE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.CallOfTheVoid] = new(HotCustomCardId.CallOfTheVoid, "CALL_OF_THE_VOID", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, true, 14);
        arr[(int)HotCustomCardId.Caltrops] = new(HotCustomCardId.Caltrops, "CALTROPS", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Capacitor] = new(HotCustomCardId.Capacitor, "CAPACITOR", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 18);
        arr[(int)HotCustomCardId.CaptureSpirit] = new(HotCustomCardId.CaptureSpirit, "CAPTURE_SPIRIT", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Cascade] = new(HotCustomCardId.Cascade, "CASCADE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Catastrophe] = new(HotCustomCardId.Catastrophe, "CATASTROPHE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.CelestialMight] = new(HotCustomCardId.CelestialMight, "CELESTIAL_MIGHT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Chaos] = new(HotCustomCardId.Chaos, "CHAOS", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Charge] = new(HotCustomCardId.Charge, "CHARGE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ChargeBattery] = new(HotCustomCardId.ChargeBattery, "CHARGE_BATTERY", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.ChildOfTheStars] = new(HotCustomCardId.ChildOfTheStars, "CHILD_OF_THE_STARS", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 22);
        arr[(int)HotCustomCardId.Chill] = new(HotCustomCardId.Chill, "CHILL", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ChoBarbs] = new(HotCustomCardId.ChoBarbs, "CHO_BARBS", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ChoChorus] = new(HotCustomCardId.ChoChorus, "CHO_CHORUS", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ChoFirm] = new(HotCustomCardId.ChoFirm, "CHO_FIRM", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ChoKi] = new(HotCustomCardId.ChoKi, "CHO_KI", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ChoLimbs] = new(HotCustomCardId.ChoLimbs, "CHO_LIMBS", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.ModifyEnergyCost, false, false, false, 0);
        arr[(int)HotCustomCardId.ChoReady] = new(HotCustomCardId.ChoReady, "CHO_READY", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Cinder] = new(HotCustomCardId.Cinder, "CINDER", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Clash] = new(HotCustomCardId.Clash, "CLASH", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Claw] = new(HotCustomCardId.Claw, "CLAW", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Cleanse] = new(HotCustomCardId.Cleanse, "CLEANSE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.CloakAndDagger] = new(HotCustomCardId.CloakAndDagger, "CLOAK_AND_DAGGER", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.CloakOfStars] = new(HotCustomCardId.CloakOfStars, "CLOAK_OF_STARS", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Clumsy] = new(HotCustomCardId.Clumsy, "CLUMSY", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Coda] = new(HotCustomCardId.Coda, "CODA", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.ModifyEnergyCost, true, false, false, 0);
        arr[(int)HotCustomCardId.ColdSnap] = new(HotCustomCardId.ColdSnap, "COLD_SNAP", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.CollisionCourse] = new(HotCustomCardId.CollisionCourse, "COLLISION_COURSE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Colossus] = new(HotCustomCardId.Colossus, "COLOSSUS", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Comet] = new(HotCustomCardId.Comet, "COMET", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Compact] = new(HotCustomCardId.Compact, "COMPACT", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.CompileDriver] = new(HotCustomCardId.CompileDriver, "COMPILE_DRIVER", HotCardDomain.Defect, HotCalcVarKind.CompileDriver, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Compost] = new(HotCustomCardId.Compost, "COMPOST", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Concoct] = new(HotCustomCardId.Concoct, "CONCOCT", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Conflagration] = new(HotCustomCardId.Conflagration, "CONFLAGRATION", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Conqueror] = new(HotCustomCardId.Conqueror, "CONQUEROR", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Constellation] = new(HotCustomCardId.Constellation, "CONSTELLATION", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ConsumingShadow] = new(HotCustomCardId.ConsumingShadow, "CONSUMING_SHADOW", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.Convergence] = new(HotCustomCardId.Convergence, "CONVERGENCE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Coolant] = new(HotCustomCardId.Coolant, "COOLANT", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Coolheaded] = new(HotCustomCardId.Coolheaded, "COOLHEADED", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Coordinate] = new(HotCustomCardId.Coordinate, "COORDINATE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.CorrosiveWave] = new(HotCustomCardId.CorrosiveWave, "CORROSIVE_WAVE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Corruption] = new(HotCustomCardId.Corruption, "CORRUPTION", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 40);
        arr[(int)HotCustomCardId.CosmicIndifference] = new(HotCustomCardId.CosmicIndifference, "COSMIC_INDIFFERENCE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Countdown] = new(HotCustomCardId.Countdown, "COUNTDOWN", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.CoveringFire] = new(HotCustomCardId.CoveringFire, "COVERING_FIRE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.CrashLanding] = new(HotCustomCardId.CrashLanding, "CRASH_LANDING", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.CreativeAi] = new(HotCustomCardId.CreativeAi, "CREATIVE_AI", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, true, 32);
        arr[(int)HotCustomCardId.CrescentSpear] = new(HotCustomCardId.CrescentSpear, "CRESCENT_SPEAR", HotCardDomain.Regent, HotCalcVarKind.CrescentSpear, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.CrimsonMantle] = new(HotCustomCardId.CrimsonMantle, "CRIMSON_MANTLE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Cruelty] = new(HotCustomCardId.Cruelty, "CRUELTY", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.CrushUnder] = new(HotCustomCardId.CrushUnder, "CRUSH_UNDER", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.CurseOfTheBell] = new(HotCustomCardId.CurseOfTheBell, "CURSE_OF_THE_BELL", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DaggerSpray] = new(HotCustomCardId.DaggerSpray, "DAGGER_SPRAY", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DaggerThrow] = new(HotCustomCardId.DaggerThrow, "DAGGER_THROW", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DanseMacabre] = new(HotCustomCardId.DanseMacabre, "DANSE_MACABRE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 24);
        arr[(int)HotCustomCardId.DarkEmbrace] = new(HotCustomCardId.DarkEmbrace, "DARK_EMBRACE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 28);
        arr[(int)HotCustomCardId.DarkShackles] = new(HotCustomCardId.DarkShackles, "DARK_SHACKLES", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Darkness] = new(HotCustomCardId.Darkness, "DARKNESS", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Dash] = new(HotCustomCardId.Dash, "DASH", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Dazed] = new(HotCustomCardId.Dazed, "DAZED", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DeadlyPoison] = new(HotCustomCardId.DeadlyPoison, "DEADLY_POISON", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Death] = new(HotCustomCardId.Death, "DEATH", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DeathMarch] = new(HotCustomCardId.DeathMarch, "DEATH_MARCH", HotCardDomain.Necrobinder, HotCalcVarKind.DeathMarch, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Deathbringer] = new(HotCustomCardId.Deathbringer, "DEATHBRINGER", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DeathsDoor] = new(HotCustomCardId.DeathsDoor, "DEATHS_DOOR", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Debilitate] = new(HotCustomCardId.Debilitate, "DEBILITATE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Debris] = new(HotCustomCardId.Debris, "DEBRIS", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Debt] = new(HotCustomCardId.Debt, "DEBT", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Decay] = new(HotCustomCardId.Decay, "DECAY", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DecisionsDecisions] = new(HotCustomCardId.DecisionsDecisions, "DECISIONS_DECISIONS", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DeepStretch] = new(HotCustomCardId.DeepStretch, "DEEP_STRETCH", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DefendDefect] = new(HotCustomCardId.DefendDefect, "DEFEND_DEFECT", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DefendFool] = new(HotCustomCardId.DefendFool, "DEFEND_FOOL", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DefendIronclad] = new(HotCustomCardId.DefendIronclad, "DEFEND_IRONCLAD", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DefendNecrobinder] = new(HotCustomCardId.DefendNecrobinder, "DEFEND_NECROBINDER", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DefendRegent] = new(HotCustomCardId.DefendRegent, "DEFEND_REGENT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DefendSilent] = new(HotCustomCardId.DefendSilent, "DEFEND_SILENT", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Defile] = new(HotCustomCardId.Defile, "DEFILE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Deflect] = new(HotCustomCardId.Deflect, "DEFLECT", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Defragment] = new(HotCustomCardId.Defragment, "DEFRAGMENT", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 24);
        arr[(int)HotCustomCardId.Defy] = new(HotCustomCardId.Defy, "DEFY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Delay] = new(HotCustomCardId.Delay, "DELAY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Demesne] = new(HotCustomCardId.Demesne, "DEMESNE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 28);
        arr[(int)HotCustomCardId.DemonForm] = new(HotCustomCardId.DemonForm, "DEMON_FORM", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 42);
        arr[(int)HotCustomCardId.DemonicShield] = new(HotCustomCardId.DemonicShield, "DEMONIC_SHIELD", HotCardDomain.Ironclad, HotCalcVarKind.DemonicShield, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Devastate] = new(HotCustomCardId.Devastate, "DEVASTATE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DevourLife] = new(HotCustomCardId.DevourLife, "DEVOUR_LIFE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.DingyDagger] = new(HotCustomCardId.DingyDagger, "DINGY_DAGGER", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Dirge] = new(HotCustomCardId.Dirge, "DIRGE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Discovery] = new(HotCustomCardId.Discovery, "DISCOVERY", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.Disintegration] = new(HotCustomCardId.Disintegration, "DISINTEGRATION", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DislikeYou] = new(HotCustomCardId.DislikeYou, "DISLIKE_YOU", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Dismantle] = new(HotCustomCardId.Dismantle, "DISMANTLE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Distraction] = new(HotCustomCardId.Distraction, "DISTRACTION", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.DivineShot] = new(HotCustomCardId.DivineShot, "DIVINE_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DodgeAndRoll] = new(HotCustomCardId.DodgeAndRoll, "DODGE_AND_ROLL", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Dominate] = new(HotCustomCardId.Dominate, "DOMINATE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DoubleEnergy] = new(HotCustomCardId.DoubleEnergy, "DOUBLE_ENERGY", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Doubt] = new(HotCustomCardId.Doubt, "DOUBT", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Dowsing] = new(HotCustomCardId.Dowsing, "DOWSING", HotCardDomain.Quest, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.DrainPower] = new(HotCustomCardId.DrainPower, "DRAIN_POWER", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DramaticEntrance] = new(HotCustomCardId.DramaticEntrance, "DRAMATIC_ENTRANCE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Dredge] = new(HotCustomCardId.Dredge, "DREDGE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DrumOfBattle] = new(HotCustomCardId.DrumOfBattle, "DRUM_OF_BATTLE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.AfterCardExhausted, false, false, false, 0);
        arr[(int)HotCustomCardId.DualWield] = new(HotCustomCardId.DualWield, "DUAL_WIELD", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.Dualcast] = new(HotCustomCardId.Dualcast, "DUALCAST", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.DyingStar] = new(HotCustomCardId.DyingStar, "DYING_STAR", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.EchoForm] = new(HotCustomCardId.EchoForm, "ECHO_FORM", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 50);
        arr[(int)HotCustomCardId.EchoingSlash] = new(HotCustomCardId.EchoingSlash, "ECHOING_SLASH", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Ecstasy] = new(HotCustomCardId.Ecstasy, "ECSTASY", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Eidolon] = new(HotCustomCardId.Eidolon, "EIDOLON", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Encore] = new(HotCustomCardId.Encore, "ENCORE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 26);
        arr[(int)HotCustomCardId.EndOfDays] = new(HotCustomCardId.EndOfDays, "END_OF_DAYS", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.EnergySurge] = new(HotCustomCardId.EnergySurge, "ENERGY_SURGE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.EnfeeblingTouch] = new(HotCustomCardId.EnfeeblingTouch, "ENFEEBLING_TOUCH", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Enlightenment] = new(HotCustomCardId.Enlightenment, "ENLIGHTENMENT", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Ennui] = new(HotCustomCardId.Ennui, "ENNUI", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Enrapture] = new(HotCustomCardId.Enrapture, "ENRAPTURE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Ensemble] = new(HotCustomCardId.Ensemble, "ENSEMBLE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Enthralled] = new(HotCustomCardId.Enthralled, "ENTHRALLED", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.BeforeCardPlayed, false, false, false, 0);
        arr[(int)HotCustomCardId.Entrench] = new(HotCustomCardId.Entrench, "ENTRENCH", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Entropy] = new(HotCustomCardId.Entropy, "ENTROPY", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, true, 14);
        arr[(int)HotCustomCardId.Envenom] = new(HotCustomCardId.Envenom, "ENVENOM", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 24);
        arr[(int)HotCustomCardId.EpicRetrieval] = new(HotCustomCardId.EpicRetrieval, "EPIC_RETRIEVAL", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.BeforeAttack, true, false, false, 0);
        arr[(int)HotCustomCardId.Equilibrium] = new(HotCustomCardId.Equilibrium, "EQUILIBRIUM", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Eradicate] = new(HotCustomCardId.Eradicate, "ERADICATE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.EscapePlan] = new(HotCustomCardId.EscapePlan, "ESCAPE_PLAN", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.EternalArmor] = new(HotCustomCardId.EternalArmor, "ETERNAL_ARMOR", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.EvilEye] = new(HotCustomCardId.EvilEye, "EVIL_EYE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ExpectAFight] = new(HotCustomCardId.ExpectAFight, "EXPECT_A_FIGHT", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Expertise] = new(HotCustomCardId.Expertise, "EXPERTISE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.ExplosiveArrows] = new(HotCustomCardId.ExplosiveArrows, "EXPLOSIVE_ARROWS", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Expose] = new(HotCustomCardId.Expose, "EXPOSE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Exterminate] = new(HotCustomCardId.Exterminate, "EXTERMINATE", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Fade] = new(HotCustomCardId.Fade, "FADE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.FallingStar] = new(HotCustomCardId.FallingStar, "FALLING_STAR", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FanOfKnives] = new(HotCustomCardId.FanOfKnives, "FAN_OF_KNIVES", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.Fasten] = new(HotCustomCardId.Fasten, "FASTEN", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Fear] = new(HotCustomCardId.Fear, "FEAR", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Feed] = new(HotCustomCardId.Feed, "FEED", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, true, false, 0);
        arr[(int)HotCustomCardId.FeedingFrenzy] = new(HotCustomCardId.FeedingFrenzy, "FEEDING_FRENZY", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FeelNoPain] = new(HotCustomCardId.FeelNoPain, "FEEL_NO_PAIN", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 24);
        arr[(int)HotCustomCardId.Feral] = new(HotCustomCardId.Feral, "FERAL", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Fetch] = new(HotCustomCardId.Fetch, "FETCH", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FiendFire] = new(HotCustomCardId.FiendFire, "FIEND_FIRE", HotCardDomain.Ironclad, HotCalcVarKind.FiendFire, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FightMe] = new(HotCustomCardId.FightMe, "FIGHT_ME", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FightThrough] = new(HotCustomCardId.FightThrough, "FIGHT_THROUGH", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Finesse] = new(HotCustomCardId.Finesse, "FINESSE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Finisher] = new(HotCustomCardId.Finisher, "FINISHER", HotCardDomain.Silent, HotCalcVarKind.Finisher, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Fisticuffs] = new(HotCustomCardId.Fisticuffs, "FISTICUFFS", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FlakCannon] = new(HotCustomCardId.FlakCannon, "FLAK_CANNON", HotCardDomain.Defect, HotCalcVarKind.FlakCannon, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FlameBarrier] = new(HotCustomCardId.FlameBarrier, "FLAME_BARRIER", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Flanking] = new(HotCustomCardId.Flanking, "FLANKING", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FlashOfSteel] = new(HotCustomCardId.FlashOfSteel, "FLASH_OF_STEEL", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Flatten] = new(HotCustomCardId.Flatten, "FLATTEN", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.AfterCardEnteredCombat | HotHookMask.AfterAttack, false, false, false, 0);
        arr[(int)HotCustomCardId.Flechettes] = new(HotCustomCardId.Flechettes, "FLECHETTES", HotCardDomain.Silent, HotCalcVarKind.Flechettes, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FlickFlack] = new(HotCustomCardId.FlickFlack, "FLICK_FLACK", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Florescence] = new(HotCustomCardId.Florescence, "FLORESCENCE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FocusedStrike] = new(HotCustomCardId.FocusedStrike, "FOCUSED_STRIKE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Folly] = new(HotCustomCardId.Folly, "FOLLY", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.FoolishShot] = new(HotCustomCardId.FoolishShot, "FOOLISH_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Footwork] = new(HotCustomCardId.Footwork, "FOOTWORK", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 22);
        arr[(int)HotCustomCardId.ForbiddenGrimoire] = new(HotCustomCardId.ForbiddenGrimoire, "FORBIDDEN_GRIMOIRE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 24);
        arr[(int)HotCustomCardId.ForegoneConclusion] = new(HotCustomCardId.ForegoneConclusion, "FOREGONE_CONCLUSION", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.ForgottenRitual] = new(HotCustomCardId.ForgottenRitual, "FORGOTTEN_RITUAL", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.FranticEscape] = new(HotCustomCardId.FranticEscape, "FRANTIC_ESCAPE", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Friendship] = new(HotCustomCardId.Friendship, "FRIENDSHIP", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.Ftl] = new(HotCustomCardId.Ftl, "FTL", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Fuel] = new(HotCustomCardId.Fuel, "FUEL", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Furnace] = new(HotCustomCardId.Furnace, "FURNACE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 24);
        arr[(int)HotCustomCardId.Fusion] = new(HotCustomCardId.Fusion, "FUSION", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.GammaBlast] = new(HotCustomCardId.GammaBlast, "GAMMA_BLAST", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.GangUp] = new(HotCustomCardId.GangUp, "GANG_UP", HotCardDomain.Colorless, HotCalcVarKind.GangUp, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.GatherLight] = new(HotCustomCardId.GatherLight, "GATHER_LIGHT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Genesis] = new(HotCustomCardId.Genesis, "GENESIS", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 28);
        arr[(int)HotCustomCardId.GeneticAlgorithm] = new(HotCustomCardId.GeneticAlgorithm, "GENETIC_ALGORITHM", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.GiantRock] = new(HotCustomCardId.GiantRock, "GIANT_ROCK", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Glacier] = new(HotCustomCardId.Glacier, "GLACIER", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Glasswork] = new(HotCustomCardId.Glasswork, "GLASSWORK", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Glimmer] = new(HotCustomCardId.Glimmer, "GLIMMER", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.GlimpseBeyond] = new(HotCustomCardId.GlimpseBeyond, "GLIMPSE_BEYOND", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Glitterstream] = new(HotCustomCardId.Glitterstream, "GLITTERSTREAM", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Glow] = new(HotCustomCardId.Glow, "GLOW", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.GoForTheEyes] = new(HotCustomCardId.GoForTheEyes, "GO_FOR_THE_EYES", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.GodByrd] = new(HotCustomCardId.GodByrd, "GOD_BYRD", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.GoldAxe] = new(HotCustomCardId.GoldAxe, "GOLD_AXE", HotCardDomain.Colorless, HotCalcVarKind.GoldAxe, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.GrandFinale] = new(HotCustomCardId.GrandFinale, "GRAND_FINALE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.GraveWarden] = new(HotCustomCardId.GraveWarden, "GRAVE_WARDEN", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Graveblast] = new(HotCustomCardId.Graveblast, "GRAVEBLAST", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Greed] = new(HotCustomCardId.Greed, "GREED", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Growth] = new(HotCustomCardId.Growth, "GROWTH", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Guards] = new(HotCustomCardId.Guards, "GUARDS", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.GuidingStar] = new(HotCustomCardId.GuidingStar, "GUIDING_STAR", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Guilty] = new(HotCustomCardId.Guilty, "GUILTY", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.AfterCombatEnd, true, false, false, 0);
        arr[(int)HotCustomCardId.GunkUp] = new(HotCustomCardId.GunkUp, "GUNK_UP", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Hailstorm] = new(HotCustomCardId.Hailstorm, "HAILSTORM", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.HammerTime] = new(HotCustomCardId.HammerTime, "HAMMER_TIME", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.HandOfGreed] = new(HotCustomCardId.HandOfGreed, "HAND_OF_GREED", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HandTrick] = new(HotCustomCardId.HandTrick, "HAND_TRICK", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Hang] = new(HotCustomCardId.Hang, "HANG", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HarmonicShield] = new(HotCustomCardId.HarmonicShield, "HARMONIC_SHIELD", HotCardDomain.Fool, HotCalcVarKind.HarmonicShield, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Haunt] = new(HotCustomCardId.Haunt, "HAUNT", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Havoc] = new(HotCustomCardId.Havoc, "HAVOC", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Haze] = new(HotCustomCardId.Haze, "HAZE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Headbutt] = new(HotCustomCardId.Headbutt, "HEADBUTT", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HeavenlyDrill] = new(HotCustomCardId.HeavenlyDrill, "HEAVENLY_DRILL", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HeavyShot] = new(HotCustomCardId.HeavyShot, "HEAVY_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Hegemony] = new(HotCustomCardId.Hegemony, "HEGEMONY", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.HeirloomHammer] = new(HotCustomCardId.HeirloomHammer, "HEIRLOOM_HAMMER", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HelixDrill] = new(HotCustomCardId.HelixDrill, "HELIX_DRILL", HotCardDomain.Defect, HotCalcVarKind.HelixDrill, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HelloWorld] = new(HotCustomCardId.HelloWorld, "HELLO_WORLD", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, true, 14);
        arr[(int)HotCustomCardId.Hellraiser] = new(HotCustomCardId.Hellraiser, "HELLRAISER", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Hemokinesis] = new(HotCustomCardId.Hemokinesis, "HEMOKINESIS", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Hibernate] = new(HotCustomCardId.Hibernate, "HIBERNATE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HiddenCache] = new(HotCustomCardId.HiddenCache, "HIDDEN_CACHE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HiddenDaggers] = new(HotCustomCardId.HiddenDaggers, "HIDDEN_DAGGERS", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HiddenGem] = new(HotCustomCardId.HiddenGem, "HIDDEN_GEM", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HiddenRoots] = new(HotCustomCardId.HiddenRoots, "HIDDEN_ROOTS", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.HighFive] = new(HotCustomCardId.HighFive, "HIGH_FIVE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.HobblingShot] = new(HotCustomCardId.HobblingShot, "HOBBLING_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Hologram] = new(HotCustomCardId.Hologram, "HOLOGRAM", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Honing] = new(HotCustomCardId.Honing, "HONING", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Hotfix] = new(HotCustomCardId.Hotfix, "HOTFIX", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.HowlFromBeyond] = new(HotCustomCardId.HowlFromBeyond, "HOWL_FROM_BEYOND", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.AfterAutoPostPlay, true, false, false, 0);
        arr[(int)HotCustomCardId.HuddleUp] = new(HotCustomCardId.HuddleUp, "HUDDLE_UP", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Hyperbeam] = new(HotCustomCardId.Hyperbeam, "HYPERBEAM", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.IAmInvincible] = new(HotCustomCardId.IAmInvincible, "I_AM_INVINCIBLE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.AfterAutoPostPlay, true, false, false, 0);
        arr[(int)HotCustomCardId.IceLance] = new(HotCustomCardId.IceLance, "ICE_LANCE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Ignition] = new(HotCustomCardId.Ignition, "IGNITION", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ImitationLearning] = new(HotCustomCardId.ImitationLearning, "IMITATION_LEARNING", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Impatience] = new(HotCustomCardId.Impatience, "IMPATIENCE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Impervious] = new(HotCustomCardId.Impervious, "IMPERVIOUS", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Infection] = new(HotCustomCardId.Infection, "INFECTION", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.InfernalBlade] = new(HotCustomCardId.InfernalBlade, "INFERNAL_BLADE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.Inferno] = new(HotCustomCardId.Inferno, "INFERNO", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.InfiniteBlades] = new(HotCustomCardId.InfiniteBlades, "INFINITE_BLADES", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 18);
        arr[(int)HotCustomCardId.Inflame] = new(HotCustomCardId.Inflame, "INFLAME", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 20);
        arr[(int)HotCustomCardId.Injury] = new(HotCustomCardId.Injury, "INJURY", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Intercept] = new(HotCustomCardId.Intercept, "INTERCEPT", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Invoke] = new(HotCustomCardId.Invoke, "INVOKE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.IronWave] = new(HotCustomCardId.IronWave, "IRON_WAVE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Iteration] = new(HotCustomCardId.Iteration, "ITERATION", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.JackOfAllTrades] = new(HotCustomCardId.JackOfAllTrades, "JACK_OF_ALL_TRADES", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.Jackpot] = new(HotCustomCardId.Jackpot, "JACKPOT", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.Jest] = new(HotCustomCardId.Jest, "JEST", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Joy] = new(HotCustomCardId.Joy, "JOY", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Judgement] = new(HotCustomCardId.Judgement, "JUDGEMENT", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Juggernaut] = new(HotCustomCardId.Juggernaut, "JUGGERNAUT", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 20);
        arr[(int)HotCustomCardId.Juggling] = new(HotCustomCardId.Juggling, "JUGGLING", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Justice] = new(HotCustomCardId.Justice, "JUSTICE", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.KiChant] = new(HotCustomCardId.KiChant, "KI_CHANT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.KiHop] = new(HotCustomCardId.KiHop, "KI_HOP", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.KiRest] = new(HotCustomCardId.KiRest, "KI_REST", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.KiVerse] = new(HotCustomCardId.KiVerse, "KI_VERSE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.KiWhip] = new(HotCustomCardId.KiWhip, "KI_WHIP", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.KinglyKick] = new(HotCustomCardId.KinglyKick, "KINGLY_KICK", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.AfterCardDrawn, true, false, false, 0);
        arr[(int)HotCustomCardId.KinglyPunch] = new(HotCustomCardId.KinglyPunch, "KINGLY_PUNCH", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.AfterCardDrawn, true, false, false, 0);
        arr[(int)HotCustomCardId.KnifeTrap] = new(HotCustomCardId.KnifeTrap, "KNIFE_TRAP", HotCardDomain.Silent, HotCalcVarKind.KnifeTrap, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Knockdown] = new(HotCustomCardId.Knockdown, "KNOCKDOWN", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.KnockoutBlow] = new(HotCustomCardId.KnockoutBlow, "KNOCKOUT_BLOW", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.KnowThyPlace] = new(HotCustomCardId.KnowThyPlace, "KNOW_THY_PLACE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.LanternKey] = new(HotCustomCardId.LanternKey, "LANTERN_KEY", HotCardDomain.Quest, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Largesse] = new(HotCustomCardId.Largesse, "LARGESSE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.LeadingStrike] = new(HotCustomCardId.LeadingStrike, "LEADING_STRIKE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Leap] = new(HotCustomCardId.Leap, "LEAP", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.LegSweep] = new(HotCustomCardId.LegSweep, "LEG_SWEEP", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.LegionOfBone] = new(HotCustomCardId.LegionOfBone, "LEGION_OF_BONE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Lethality] = new(HotCustomCardId.Lethality, "LETHALITY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 20);
        arr[(int)HotCustomCardId.Lift] = new(HotCustomCardId.Lift, "LIFT", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.LightningRod] = new(HotCustomCardId.LightningRod, "LIGHTNING_ROD", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.LockedIn] = new(HotCustomCardId.LockedIn, "LOCKED_IN", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Loop] = new(HotCustomCardId.Loop, "LOOP", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 24);
        arr[(int)HotCustomCardId.LostLute] = new(HotCustomCardId.LostLute, "LOST_LUTE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Lullaby] = new(HotCustomCardId.Lullaby, "LULLABY", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 20);
        arr[(int)HotCustomCardId.Luminesce] = new(HotCustomCardId.Luminesce, "LUMINESCE", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.LunarBlast] = new(HotCustomCardId.LunarBlast, "LUNAR_BLAST", HotCardDomain.Regent, HotCalcVarKind.LunarBlast, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MachineLearning] = new(HotCustomCardId.MachineLearning, "MACHINE_LEARNING", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 20);
        arr[(int)HotCustomCardId.MadScience] = new(HotCustomCardId.MadScience, "MAD_SCIENCE", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, true, 14);
        arr[(int)HotCustomCardId.MakeItSo] = new(HotCustomCardId.MakeItSo, "MAKE_IT_SO", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.AfterCardPlayed, true, false, false, 0);
        arr[(int)HotCustomCardId.Malaise] = new(HotCustomCardId.Malaise, "MALAISE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Mangle] = new(HotCustomCardId.Mangle, "MANGLE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.ManifestAuthority] = new(HotCustomCardId.ManifestAuthority, "MANIFEST_AUTHORITY", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.MasterOfStrategy] = new(HotCustomCardId.MasterOfStrategy, "MASTER_OF_STRATEGY", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.MasterPlanner] = new(HotCustomCardId.MasterPlanner, "MASTER_PLANNER", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Maul] = new(HotCustomCardId.Maul, "MAUL", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Mayhem] = new(HotCustomCardId.Mayhem, "MAYHEM", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Melancholy] = new(HotCustomCardId.Melancholy, "MELANCHOLY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.AfterDeath, true, false, false, 0);
        arr[(int)HotCustomCardId.MelodicShot] = new(HotCustomCardId.MelodicShot, "MELODIC_SHOT", HotCardDomain.Fool, HotCalcVarKind.MelodicShot, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MementoMori] = new(HotCustomCardId.MementoMori, "MEMENTO_MORI", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Metamorphosis] = new(HotCustomCardId.Metamorphosis, "METAMORPHOSIS", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.MeteorShower] = new(HotCustomCardId.MeteorShower, "METEOR_SHOWER", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MeteorStrike] = new(HotCustomCardId.MeteorStrike, "METEOR_STRIKE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Midnight] = new(HotCustomCardId.Midnight, "MIDNIGHT", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.AfterCardEnteredCombat | HotHookMask.AfterCardExhausted, true, false, false, 0);
        arr[(int)HotCustomCardId.MightyShield] = new(HotCustomCardId.MightyShield, "MIGHTY_SHIELD", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MightyWeapon] = new(HotCustomCardId.MightyWeapon, "MIGHTY_WEAPON", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Mimic] = new(HotCustomCardId.Mimic, "MIMIC", HotCardDomain.Colorless, HotCalcVarKind.Mimic, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MindBlast] = new(HotCustomCardId.MindBlast, "MIND_BLAST", HotCardDomain.Colorless, HotCalcVarKind.MindBlast, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MindRot] = new(HotCustomCardId.MindRot, "MIND_ROT", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MinionDiveBomb] = new(HotCustomCardId.MinionDiveBomb, "MINION_DIVE_BOMB", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.MinionSacrifice] = new(HotCustomCardId.MinionSacrifice, "MINION_SACRIFICE", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.MinionStrike] = new(HotCustomCardId.MinionStrike, "MINION_STRIKE", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Mirage] = new(HotCustomCardId.Mirage, "MIRAGE", HotCardDomain.Silent, HotCalcVarKind.Mirage, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Misery] = new(HotCustomCardId.Misery, "MISERY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Modded] = new(HotCustomCardId.Modded, "MODDED", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MoltenFist] = new(HotCustomCardId.MoltenFist, "MOLTEN_FIST", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MomentumStrike] = new(HotCustomCardId.MomentumStrike, "MOMENTUM_STRIKE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MonarchsGaze] = new(HotCustomCardId.MonarchsGaze, "MONARCHS_GAZE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Monologue] = new(HotCustomCardId.Monologue, "MONOLOGUE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Motif] = new(HotCustomCardId.Motif, "MOTIF", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.MourningBlow] = new(HotCustomCardId.MourningBlow, "MOURNING_BLOW", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MultiCast] = new(HotCustomCardId.MultiCast, "MULTI_CAST", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Murder] = new(HotCustomCardId.Murder, "MURDER", HotCardDomain.Silent, HotCalcVarKind.Murder, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MyShinies] = new(HotCustomCardId.MyShinies, "MY_SHINIES", HotCardDomain.Colorless, HotCalcVarKind.MyShinies, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.MythicForm] = new(HotCustomCardId.MythicForm, "MYTHIC_FORM", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, true, 14);
        arr[(int)HotCustomCardId.Nail] = new(HotCustomCardId.Nail, "NAIL", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.NecroMastery] = new(HotCustomCardId.NecroMastery, "NECRO_MASTERY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.NegativePulse] = new(HotCustomCardId.NegativePulse, "NEGATIVE_PULSE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.NeowsFury] = new(HotCustomCardId.NeowsFury, "NEOWS_FURY", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Neurosurge] = new(HotCustomCardId.Neurosurge, "NEUROSURGE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.Neutralize] = new(HotCustomCardId.Neutralize, "NEUTRALIZE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.NeutronAegis] = new(HotCustomCardId.NeutronAegis, "NEUTRON_AEGIS", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Nightmare] = new(HotCustomCardId.Nightmare, "NIGHTMARE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 26);
        arr[(int)HotCustomCardId.NoEscape] = new(HotCustomCardId.NoEscape, "NO_ESCAPE", HotCardDomain.Necrobinder, HotCalcVarKind.NoEscape, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Nock] = new(HotCustomCardId.Nock, "NOCK", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Normality] = new(HotCustomCardId.Normality, "NORMALITY", HotCardDomain.Curse, HotCalcVarKind.Normality, HotHookMask.BeforeCardPlayed, false, false, false, 0);
        arr[(int)HotCustomCardId.Nostalgia] = new(HotCustomCardId.Nostalgia, "NOSTALGIA", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.NotYet] = new(HotCustomCardId.NotYet, "NOT_YET", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, true, false, 0);
        arr[(int)HotCustomCardId.NoxiousFumes] = new(HotCustomCardId.NoxiousFumes, "NOXIOUS_FUMES", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 22);
        arr[(int)HotCustomCardId.Null] = new(HotCustomCardId.Null, "NULL", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Oblivion] = new(HotCustomCardId.Oblivion, "OBLIVION", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Offering] = new(HotCustomCardId.Offering, "OFFERING", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Omnislice] = new(HotCustomCardId.Omnislice, "OMNISLICE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.OneForAll] = new(HotCustomCardId.OneForAll, "ONE_FOR_ALL", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.OneTwoPunch] = new(HotCustomCardId.OneTwoPunch, "ONE_TWO_PUNCH", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Optimism] = new(HotCustomCardId.Optimism, "OPTIMISM", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Orbit] = new(HotCustomCardId.Orbit, "ORBIT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 26);
        arr[(int)HotCustomCardId.Outbreak] = new(HotCustomCardId.Outbreak, "OUTBREAK", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Outmaneuver] = new(HotCustomCardId.Outmaneuver, "OUTMANEUVER", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Outrage] = new(HotCustomCardId.Outrage, "OUTRAGE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Overclock] = new(HotCustomCardId.Overclock, "OVERCLOCK", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.PactsEnd] = new(HotCustomCardId.PactsEnd, "PACTS_END", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Pagestorm] = new(HotCustomCardId.Pagestorm, "PAGESTORM", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 24);
        arr[(int)HotCustomCardId.PaleBlueDot] = new(HotCustomCardId.PaleBlueDot, "PALE_BLUE_DOT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 24);
        arr[(int)HotCustomCardId.Panache] = new(HotCustomCardId.Panache, "PANACHE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.PanicButton] = new(HotCustomCardId.PanicButton, "PANIC_BUTTON", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Parry] = new(HotCustomCardId.Parry, "PARRY", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Parse] = new(HotCustomCardId.Parse, "PARSE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.ParticleWall] = new(HotCustomCardId.ParticleWall, "PARTICLE_WALL", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Patter] = new(HotCustomCardId.Patter, "PATTER", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Peck] = new(HotCustomCardId.Peck, "PECK", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.PerfectedStrike] = new(HotCustomCardId.PerfectedStrike, "PERFECTED_STRIKE", HotCardDomain.Ironclad, HotCalcVarKind.PerfectedStrike, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.PhantomBlades] = new(HotCustomCardId.PhantomBlades, "PHANTOM_BLADES", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.PhotonCut] = new(HotCustomCardId.PhotonCut, "PHOTON_CUT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.PiercingWail] = new(HotCustomCardId.PiercingWail, "PIERCING_WAIL", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Pillage] = new(HotCustomCardId.Pillage, "PILLAGE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.PillarOfCreation] = new(HotCustomCardId.PillarOfCreation, "PILLAR_OF_CREATION", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.Pinpoint] = new(HotCustomCardId.Pinpoint, "PINPOINT", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.AfterCardEnteredCombat | HotHookMask.AfterCardPlayed, true, false, false, 0);
        arr[(int)HotCustomCardId.Plot] = new(HotCustomCardId.Plot, "PLOT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.PoisonedStab] = new(HotCustomCardId.PoisonedStab, "POISONED_STAB", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Poke] = new(HotCustomCardId.Poke, "POKE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.PommelStrike] = new(HotCustomCardId.PommelStrike, "POMMEL_STRIKE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.PoorSleep] = new(HotCustomCardId.PoorSleep, "POOR_SLEEP", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Pounce] = new(HotCustomCardId.Pounce, "POUNCE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.PreciseCut] = new(HotCustomCardId.PreciseCut, "PRECISE_CUT", HotCardDomain.Silent, HotCalcVarKind.PreciseCut, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Predator] = new(HotCustomCardId.Predator, "PREDATOR", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.PrepTime] = new(HotCustomCardId.PrepTime, "PREP_TIME", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Prepared] = new(HotCustomCardId.Prepared, "PREPARED", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.PrimalForce] = new(HotCustomCardId.PrimalForce, "PRIMAL_FORCE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Production] = new(HotCustomCardId.Production, "PRODUCTION", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Prolong] = new(HotCustomCardId.Prolong, "PROLONG", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Prophesize] = new(HotCustomCardId.Prophesize, "PROPHESIZE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Protector] = new(HotCustomCardId.Protector, "PROTECTOR", HotCardDomain.Necrobinder, HotCalcVarKind.Protector, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Prowess] = new(HotCustomCardId.Prowess, "PROWESS", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.PullAggro] = new(HotCustomCardId.PullAggro, "PULL_AGGRO", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.PullFromBelow] = new(HotCustomCardId.PullFromBelow, "PULL_FROM_BELOW", HotCardDomain.Necrobinder, HotCalcVarKind.PullFromBelow, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.PunchOfJoy] = new(HotCustomCardId.PunchOfJoy, "PUNCH_OF_JOY", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Purity] = new(HotCustomCardId.Purity, "PURITY", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Putrefy] = new(HotCustomCardId.Putrefy, "PUTREFY", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Pyre] = new(HotCustomCardId.Pyre, "PYRE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Quadcast] = new(HotCustomCardId.Quadcast, "QUADCAST", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Quasar] = new(HotCustomCardId.Quasar, "QUASAR", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.QuickShot] = new(HotCustomCardId.QuickShot, "QUICK_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Quietus] = new(HotCustomCardId.Quietus, "QUIETUS", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Quiver] = new(HotCustomCardId.Quiver, "QUIVER", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.Radiate] = new(HotCustomCardId.Radiate, "RADIATE", HotCardDomain.Regent, HotCalcVarKind.Radiate, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Rage] = new(HotCustomCardId.Rage, "RAGE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.RainShot] = new(HotCustomCardId.RainShot, "RAIN_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Rainbow] = new(HotCustomCardId.Rainbow, "RAINBOW", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Rally] = new(HotCustomCardId.Rally, "RALLY", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Rampage] = new(HotCustomCardId.Rampage, "RAMPAGE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Rattle] = new(HotCustomCardId.Rattle, "RATTLE", HotCardDomain.Necrobinder, HotCalcVarKind.Rattle, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Reanimate] = new(HotCustomCardId.Reanimate, "REANIMATE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Reap] = new(HotCustomCardId.Reap, "REAP", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.ReaperForm] = new(HotCustomCardId.ReaperForm, "REAPER_FORM", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 36);
        arr[(int)HotCustomCardId.Reave] = new(HotCustomCardId.Reave, "REAVE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Reboot] = new(HotCustomCardId.Reboot, "REBOOT", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Rebound] = new(HotCustomCardId.Rebound, "REBOUND", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Recall] = new(HotCustomCardId.Recall, "RECALL", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.RefineBlade] = new(HotCustomCardId.RefineBlade, "REFINE_BLADE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Reflect] = new(HotCustomCardId.Reflect, "REFLECT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Reflex] = new(HotCustomCardId.Reflex, "REFLEX", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Refract] = new(HotCustomCardId.Refract, "REFRACT", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Regret] = new(HotCustomCardId.Regret, "REGRET", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.BeforeTurnEnd, true, false, false, 0);
        arr[(int)HotCustomCardId.Relax] = new(HotCustomCardId.Relax, "RELAX", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Rend] = new(HotCustomCardId.Rend, "REND", HotCardDomain.Colorless, HotCalcVarKind.Rend, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.RepeatShot] = new(HotCustomCardId.RepeatShot, "REPEAT_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Resonance] = new(HotCustomCardId.Resonance, "RESONANCE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Restlessness] = new(HotCustomCardId.Restlessness, "RESTLESSNESS", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.RhythmStrike] = new(HotCustomCardId.RhythmStrike, "RHYTHM_STRIKE", HotCardDomain.Fool, HotCalcVarKind.RhythmStrike, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Ricochet] = new(HotCustomCardId.Ricochet, "RICOCHET", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.RightHandHand] = new(HotCustomCardId.RightHandHand, "RIGHT_HAND_HAND", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.AfterCardPlayed, false, false, false, 0);
        arr[(int)HotCustomCardId.RipAndTear] = new(HotCustomCardId.RipAndTear, "RIP_AND_TEAR", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.RocketPunch] = new(HotCustomCardId.RocketPunch, "ROCKET_PUNCH", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.AfterCardGenerated, true, false, false, 0);
        arr[(int)HotCustomCardId.RollingBoulder] = new(HotCustomCardId.RollingBoulder, "ROLLING_BOULDER", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.RoyalGamble] = new(HotCustomCardId.RoyalGamble, "ROYAL_GAMBLE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Royalties] = new(HotCustomCardId.Royalties, "ROYALTIES", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Rummage] = new(HotCustomCardId.Rummage, "RUMMAGE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Rupture] = new(HotCustomCardId.Rupture, "RUPTURE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 18);
        arr[(int)HotCustomCardId.Sacrifice] = new(HotCustomCardId.Sacrifice, "SACRIFICE", HotCardDomain.Necrobinder, HotCalcVarKind.Sacrifice, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Sadness] = new(HotCustomCardId.Sadness, "SADNESS", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Salvo] = new(HotCustomCardId.Salvo, "SALVO", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Scavenge] = new(HotCustomCardId.Scavenge, "SCAVENGE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Scourge] = new(HotCustomCardId.Scourge, "SCOURGE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Scrape] = new(HotCustomCardId.Scrape, "SCRAPE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Scrawl] = new(HotCustomCardId.Scrawl, "SCRAWL", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SculptingStrike] = new(HotCustomCardId.SculptingStrike, "SCULPTING_STRIKE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Seance] = new(HotCustomCardId.Seance, "SEANCE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SecondWind] = new(HotCustomCardId.SecondWind, "SECOND_WIND", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SecretTechnique] = new(HotCustomCardId.SecretTechnique, "SECRET_TECHNIQUE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SecretWeapon] = new(HotCustomCardId.SecretWeapon, "SECRET_WEAPON", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SeekerStrike] = new(HotCustomCardId.SeekerStrike, "SEEKER_STRIKE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SeekingEdge] = new(HotCustomCardId.SeekingEdge, "SEEKING_EDGE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.SelfDeprecate] = new(HotCustomCardId.SelfDeprecate, "SELF_DEPRECATE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SentryMode] = new(HotCustomCardId.SentryMode, "SENTRY_MODE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.SerpentForm] = new(HotCustomCardId.SerpentForm, "SERPENT_FORM", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.SerratedArrows] = new(HotCustomCardId.SerratedArrows, "SERRATED_ARROWS", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.SetupStrike] = new(HotCustomCardId.SetupStrike, "SETUP_STRIKE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SevenStars] = new(HotCustomCardId.SevenStars, "SEVEN_STARS", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Severance] = new(HotCustomCardId.Severance, "SEVERANCE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ShadowShield] = new(HotCustomCardId.ShadowShield, "SHADOW_SHIELD", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.ShadowStep] = new(HotCustomCardId.ShadowStep, "SHADOW_STEP", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Shadowmeld] = new(HotCustomCardId.Shadowmeld, "SHADOWMELD", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Shame] = new(HotCustomCardId.Shame, "SHAME", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SharedFate] = new(HotCustomCardId.SharedFate, "SHARED_FATE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Shatter] = new(HotCustomCardId.Shatter, "SHATTER", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ShiningStrike] = new(HotCustomCardId.ShiningStrike, "SHINING_STRIKE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Shiv] = new(HotCustomCardId.Shiv, "SHIV", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Shockwave] = new(HotCustomCardId.Shockwave, "SHOCKWAVE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ShowOff] = new(HotCustomCardId.ShowOff, "SHOW_OFF", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 18);
        arr[(int)HotCustomCardId.Shroud] = new(HotCustomCardId.Shroud, "SHROUD", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 22);
        arr[(int)HotCustomCardId.ShrugItOff] = new(HotCustomCardId.ShrugItOff, "SHRUG_IT_OFF", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SicEm] = new(HotCustomCardId.SicEm, "SIC_EM", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Sidestep] = new(HotCustomCardId.Sidestep, "SIDESTEP", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SignalBoost] = new(HotCustomCardId.SignalBoost, "SIGNAL_BOOST", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Simpleton] = new(HotCustomCardId.Simpleton, "SIMPLETON", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Skewer] = new(HotCustomCardId.Skewer, "SKEWER", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Skim] = new(HotCustomCardId.Skim, "SKIM", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SkyShot] = new(HotCustomCardId.SkyShot, "SKY_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SleightOfFlesh] = new(HotCustomCardId.SleightOfFlesh, "SLEIGHT_OF_FLESH", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 20);
        arr[(int)HotCustomCardId.Slice] = new(HotCustomCardId.Slice, "SLICE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Slimed] = new(HotCustomCardId.Slimed, "SLIMED", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Sloth] = new(HotCustomCardId.Sloth, "SLOTH", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Smokestack] = new(HotCustomCardId.Smokestack, "SMOKESTACK", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Snakebite] = new(HotCustomCardId.Snakebite, "SNAKEBITE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Snap] = new(HotCustomCardId.Snap, "SNAP", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Sneaky] = new(HotCustomCardId.Sneaky, "SNEAKY", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.SolarStrike] = new(HotCustomCardId.SolarStrike, "SOLAR_STRIKE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SongPath] = new(HotCustomCardId.SongPath, "SONG_PATH", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Soot] = new(HotCustomCardId.Soot, "SOOT", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SorrowfulStrike] = new(HotCustomCardId.SorrowfulStrike, "SORROWFUL_STRIKE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Soul] = new(HotCustomCardId.Soul, "SOUL", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SoulStorm] = new(HotCustomCardId.SoulStorm, "SOUL_STORM", HotCardDomain.Necrobinder, HotCalcVarKind.SoulStorm, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Soulbound] = new(HotCustomCardId.Soulbound, "SOULBOUND", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.SovereignBlade] = new(HotCustomCardId.SovereignBlade, "SOVEREIGN_BLADE", HotCardDomain.Token, HotCalcVarKind.SovereignBlade, HotHookMask.AfterCardEnteredCombat, false, false, false, 0);
        arr[(int)HotCustomCardId.Sow] = new(HotCustomCardId.Sow, "SOW", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SpectrumShift] = new(HotCustomCardId.SpectrumShift, "SPECTRUM_SHIFT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, true, 22);
        arr[(int)HotCustomCardId.Speedster] = new(HotCustomCardId.Speedster, "SPEEDSTER", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Spinner] = new(HotCustomCardId.Spinner, "SPINNER", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.SpiritOfAsh] = new(HotCustomCardId.SpiritOfAsh, "SPIRIT_OF_ASH", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Spite] = new(HotCustomCardId.Spite, "SPITE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Splash] = new(HotCustomCardId.Splash, "SPLASH", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.SpoilsMap] = new(HotCustomCardId.SpoilsMap, "SPOILS_MAP", HotCardDomain.Quest, HotCalcVarKind.None, HotHookMask.AfterCardEnteredCombat, true, false, false, 0);
        arr[(int)HotCustomCardId.SpoilsOfBattle] = new(HotCustomCardId.SpoilsOfBattle, "SPOILS_OF_BATTLE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SporeMind] = new(HotCustomCardId.SporeMind, "SPORE_MIND", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Spur] = new(HotCustomCardId.Spur, "SPUR", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Squash] = new(HotCustomCardId.Squash, "SQUASH", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Squeeze] = new(HotCustomCardId.Squeeze, "SQUEEZE", HotCardDomain.Necrobinder, HotCalcVarKind.Squeeze, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Stack] = new(HotCustomCardId.Stack, "STACK", HotCardDomain.Event, HotCalcVarKind.Stack, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Stampede] = new(HotCustomCardId.Stampede, "STAMPEDE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Stardust] = new(HotCustomCardId.Stardust, "STARDUST", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SteadyCadence] = new(HotCustomCardId.SteadyCadence, "STEADY_CADENCE", HotCardDomain.Fool, HotCalcVarKind.SteadyCadence, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Stoke] = new(HotCustomCardId.Stoke, "STOKE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.Stomp] = new(HotCustomCardId.Stomp, "STOMP", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.AfterCardEnteredCombat | HotHookMask.BeforeCardPlayed, false, false, false, 0);
        arr[(int)HotCustomCardId.StoneArmor] = new(HotCustomCardId.StoneArmor, "STONE_ARMOR", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 18);
        arr[(int)HotCustomCardId.Storm] = new(HotCustomCardId.Storm, "STORM", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 22);
        arr[(int)HotCustomCardId.StormOfSteel] = new(HotCustomCardId.StormOfSteel, "STORM_OF_STEEL", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Strangle] = new(HotCustomCardId.Strangle, "STRANGLE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Stratagem] = new(HotCustomCardId.Stratagem, "STRATAGEM", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Strength] = new(HotCustomCardId.Strength, "STRENGTH", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.StrikeDefect] = new(HotCustomCardId.StrikeDefect, "STRIKE_DEFECT", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.StrikeFool] = new(HotCustomCardId.StrikeFool, "STRIKE_FOOL", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.StrikeIronclad] = new(HotCustomCardId.StrikeIronclad, "STRIKE_IRONCLAD", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.StrikeNecrobinder] = new(HotCustomCardId.StrikeNecrobinder, "STRIKE_NECROBINDER", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.StrikeRegent] = new(HotCustomCardId.StrikeRegent, "STRIKE_REGENT", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.StrikeSilent] = new(HotCustomCardId.StrikeSilent, "STRIKE_SILENT", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Subroutine] = new(HotCustomCardId.Subroutine, "SUBROUTINE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.SuckerPunch] = new(HotCustomCardId.SuckerPunch, "SUCKER_PUNCH", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SummonForth] = new(HotCustomCardId.SummonForth, "SUMMON_FORTH", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Sunder] = new(HotCustomCardId.Sunder, "SUNDER", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Supercritical] = new(HotCustomCardId.Supercritical, "SUPERCRITICAL", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Supermassive] = new(HotCustomCardId.Supermassive, "SUPERMASSIVE", HotCardDomain.Regent, HotCalcVarKind.Supermassive, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Suppress] = new(HotCustomCardId.Suppress, "SUPPRESS", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Survivor] = new(HotCustomCardId.Survivor, "SURVIVOR", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SweepingBeam] = new(HotCustomCardId.SweepingBeam, "SWEEPING_BEAM", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SweepingGaze] = new(HotCustomCardId.SweepingGaze, "SWEEPING_GAZE", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.SwordBoomerang] = new(HotCustomCardId.SwordBoomerang, "SWORD_BOOMERANG", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.SwordSage] = new(HotCustomCardId.SwordSage, "SWORD_SAGE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 24);
        arr[(int)HotCustomCardId.Synchronize] = new(HotCustomCardId.Synchronize, "SYNCHRONIZE", HotCardDomain.Defect, HotCalcVarKind.Synchronize, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Synthesis] = new(HotCustomCardId.Synthesis, "SYNTHESIS", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Tactician] = new(HotCustomCardId.Tactician, "TACTICIAN", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.TagTeam] = new(HotCustomCardId.TagTeam, "TAG_TEAM", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Tank] = new(HotCustomCardId.Tank, "TANK", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Taunt] = new(HotCustomCardId.Taunt, "TAUNT", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.TearAsunder] = new(HotCustomCardId.TearAsunder, "TEAR_ASUNDER", HotCardDomain.Ironclad, HotCalcVarKind.TearAsunder, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Temperance] = new(HotCustomCardId.Temperance, "TEMPERANCE", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Tempest] = new(HotCustomCardId.Tempest, "TEMPEST", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Terraforming] = new(HotCustomCardId.Terraforming, "TERRAFORMING", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.TeslaCoil] = new(HotCustomCardId.TeslaCoil, "TESLA_COIL", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheBall] = new(HotCustomCardId.TheBall, "THE_BALL", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheBomb] = new(HotCustomCardId.TheBomb, "THE_BOMB", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheChariot] = new(HotCustomCardId.TheChariot, "THE_CHARIOT", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.TheDevil] = new(HotCustomCardId.TheDevil, "THE_DEVIL", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheEmperor] = new(HotCustomCardId.TheEmperor, "THE_EMPEROR", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.TheEmpress] = new(HotCustomCardId.TheEmpress, "THE_EMPRESS", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheFool] = new(HotCustomCardId.TheFool, "THE_FOOL", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheGambit] = new(HotCustomCardId.TheGambit, "THE_GAMBIT", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.TheHangedMan] = new(HotCustomCardId.TheHangedMan, "THE_HANGED_MAN", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheHermit] = new(HotCustomCardId.TheHermit, "THE_HERMIT", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheHierophant] = new(HotCustomCardId.TheHierophant, "THE_HIEROPHANT", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.TheHighPriestess] = new(HotCustomCardId.TheHighPriestess, "THE_HIGH_PRIESTESS", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.TheHunt] = new(HotCustomCardId.TheHunt, "THE_HUNT", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheLovers] = new(HotCustomCardId.TheLovers, "THE_LOVERS", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, true, 0);
        arr[(int)HotCustomCardId.TheMagician] = new(HotCustomCardId.TheMagician, "THE_MAGICIAN", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheMoon] = new(HotCustomCardId.TheMoon, "THE_MOON", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheScythe] = new(HotCustomCardId.TheScythe, "THE_SCYTHE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheSealedThrone] = new(HotCustomCardId.TheSealedThrone, "THE_SEALED_THRONE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 30);
        arr[(int)HotCustomCardId.TheShot] = new(HotCustomCardId.TheShot, "THE_SHOT", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheSmith] = new(HotCustomCardId.TheSmith, "THE_SMITH", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.TheStar] = new(HotCustomCardId.TheStar, "THE_STAR", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheSun] = new(HotCustomCardId.TheSun, "THE_SUN", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheTower] = new(HotCustomCardId.TheTower, "THE_TOWER", HotCardDomain.Tarot, HotCalcVarKind.TheTower, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TheWorld] = new(HotCustomCardId.TheWorld, "THE_WORLD", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ThinkingAhead] = new(HotCustomCardId.ThinkingAhead, "THINKING_AHEAD", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Thrash] = new(HotCustomCardId.Thrash, "THRASH", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ThrummingHatchet] = new(HotCustomCardId.ThrummingHatchet, "THRUMMING_HATCHET", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.BeforeHandDraw, true, false, false, 0);
        arr[(int)HotCustomCardId.Thunder] = new(HotCustomCardId.Thunder, "THUNDER", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Thunderclap] = new(HotCustomCardId.Thunderclap, "THUNDERCLAP", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Timber] = new(HotCustomCardId.Timber, "TIMBER", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TimesUp] = new(HotCustomCardId.TimesUp, "TIMES_UP", HotCardDomain.Necrobinder, HotCalcVarKind.TimesUp, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.ToolsOfTheTrade] = new(HotCustomCardId.ToolsOfTheTrade, "TOOLS_OF_THE_TRADE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 22);
        arr[(int)HotCustomCardId.ToricToughness] = new(HotCustomCardId.ToricToughness, "TORIC_TOUGHNESS", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Toxic] = new(HotCustomCardId.Toxic, "TOXIC", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Tracking] = new(HotCustomCardId.Tracking, "TRACKING", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 14);
        arr[(int)HotCustomCardId.Transfigure] = new(HotCustomCardId.Transfigure, "TRANSFIGURE", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TrashToTreasure] = new(HotCustomCardId.TrashToTreasure, "TRASH_TO_TREASURE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Tremble] = new(HotCustomCardId.Tremble, "TREMBLE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.TrickShot] = new(HotCustomCardId.TrickShot, "TRICK_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TrueGrit] = new(HotCustomCardId.TrueGrit, "TRUE_GRIT", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Tune] = new(HotCustomCardId.Tune, "TUNE", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Turbo] = new(HotCustomCardId.Turbo, "TURBO", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Tutor] = new(HotCustomCardId.Tutor, "TUTOR", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TwinShot] = new(HotCustomCardId.TwinShot, "TWIN_SHOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.TwinStrike] = new(HotCustomCardId.TwinStrike, "TWIN_STRIKE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Tyranny] = new(HotCustomCardId.Tyranny, "TYRANNY", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 24);
        arr[(int)HotCustomCardId.UltimateDefend] = new(HotCustomCardId.UltimateDefend, "ULTIMATE_DEFEND", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.UltimateStrike] = new(HotCustomCardId.UltimateStrike, "ULTIMATE_STRIKE", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Undeath] = new(HotCustomCardId.Undeath, "UNDEATH", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Underworld] = new(HotCustomCardId.Underworld, "UNDERWORLD", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Unleash] = new(HotCustomCardId.Unleash, "UNLEASH", HotCardDomain.Necrobinder, HotCalcVarKind.Unleash, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Unmovable] = new(HotCustomCardId.Unmovable, "UNMOVABLE", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Unrelenting] = new(HotCustomCardId.Unrelenting, "UNRELENTING", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Untouchable] = new(HotCustomCardId.Untouchable, "UNTOUCHABLE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.UpMySleeve] = new(HotCustomCardId.UpMySleeve, "UP_MY_SLEEVE", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Uppercut] = new(HotCustomCardId.Uppercut, "UPPERCUT", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Uproar] = new(HotCustomCardId.Uproar, "UPROAR", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Uproot] = new(HotCustomCardId.Uproot, "UPROOT", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Veilpiercer] = new(HotCustomCardId.Veilpiercer, "VEILPIERCER", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Venerate] = new(HotCustomCardId.Venerate, "VENERATE", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Vicious] = new(HotCustomCardId.Vicious, "VICIOUS", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.ViolentChord] = new(HotCustomCardId.ViolentChord, "VIOLENT_CHORD", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 14);
        arr[(int)HotCustomCardId.Vitriol] = new(HotCustomCardId.Vitriol, "VITRIOL", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Void] = new(HotCustomCardId.Void, "VOID", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.AfterCardDrawn, true, false, false, 0);
        arr[(int)HotCustomCardId.VoidForm] = new(HotCustomCardId.VoidForm, "VOID_FORM", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 44);
        arr[(int)HotCustomCardId.Volley] = new(HotCustomCardId.Volley, "VOLLEY", HotCardDomain.Colorless, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Voltaic] = new(HotCustomCardId.Voltaic, "VOLTAIC", HotCardDomain.Defect, HotCalcVarKind.Voltaic, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.WahBlast] = new(HotCustomCardId.WahBlast, "WAH_BLAST", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.WahCho] = new(HotCustomCardId.WahCho, "WAH_CHO", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.WahChoKi] = new(HotCustomCardId.WahChoKi, "WAH_CHO_KI", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.WahHammer] = new(HotCustomCardId.WahHammer, "WAH_HAMMER", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.ModifyDamageMultiplicative, false, false, false, 0);
        arr[(int)HotCustomCardId.WahKi] = new(HotCustomCardId.WahKi, "WAH_KI", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.WahSweep] = new(HotCustomCardId.WahSweep, "WAH_SWEEP", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.WahSymphony] = new(HotCustomCardId.WahSymphony, "WAH_SYMPHONY", HotCardDomain.Fool, HotCalcVarKind.WahSymphony, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.WahTouch] = new(HotCustomCardId.WahTouch, "WAH_TOUCH", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.WasteAway] = new(HotCustomCardId.WasteAway, "WASTE_AWAY", HotCardDomain.Token, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.WellLaidPlans] = new(HotCustomCardId.WellLaidPlans, "WELL_LAID_PLANS", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 26);
        arr[(int)HotCustomCardId.WheelOfFortune] = new(HotCustomCardId.WheelOfFortune, "WHEEL_OF_FORTUNE", HotCardDomain.Tarot, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Whirlwind] = new(HotCustomCardId.Whirlwind, "WHIRLWIND", HotCardDomain.Ironclad, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Whistle] = new(HotCustomCardId.Whistle, "WHISTLE", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.WhistlingArrows] = new(HotCustomCardId.WhistlingArrows, "WHISTLING_ARROWS", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.BeforeAttack, true, false, false, 0);
        arr[(int)HotCustomCardId.WhiteNoise] = new(HotCustomCardId.WhiteNoise, "WHITE_NOISE", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, false, false, true, 14);
        arr[(int)HotCustomCardId.Wish] = new(HotCustomCardId.Wish, "WISH", HotCardDomain.Event, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Wisp] = new(HotCustomCardId.Wisp, "WISP", HotCardDomain.Necrobinder, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Wither] = new(HotCustomCardId.Wither, "WITHER", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Wound] = new(HotCustomCardId.Wound, "WOUND", HotCardDomain.Status, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.WraithForm] = new(HotCustomCardId.WraithForm, "WRAITH_FORM", HotCardDomain.Silent, HotCalcVarKind.None, HotHookMask.None, false, false, false, 46);
        arr[(int)HotCustomCardId.Wrest] = new(HotCustomCardId.Wrest, "WREST", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Writhe] = new(HotCustomCardId.Writhe, "WRITHE", HotCardDomain.Curse, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.WroughtInWar] = new(HotCustomCardId.WroughtInWar, "WROUGHT_IN_WAR", HotCardDomain.Regent, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        arr[(int)HotCustomCardId.Yah] = new(HotCustomCardId.Yah, "YAH", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, true, false, false, 20);
        arr[(int)HotCustomCardId.Yoga] = new(HotCustomCardId.Yoga, "YOGA", HotCardDomain.Fool, HotCalcVarKind.None, HotHookMask.None, false, false, false, 0);
        arr[(int)HotCustomCardId.Zap] = new(HotCustomCardId.Zap, "ZAP", HotCardDomain.Defect, HotCalcVarKind.None, HotHookMask.None, true, false, false, 0);
        return arr;
    }

    public static void CompileCardBytecode(
        HotCustomCardId cardId,
        ushort cardDefId,
        Func<HotPowerId, ushort> ensurePowerDefId,
        List<HotCardEffectOp> ops,
        out int opStart,
        out byte opCount,
        out HotCustomCardId customCardId)
    {
        opStart = ops.Count;
        customCardId = HotCustomCardId.None;
        switch (cardId)
        {
            case HotCustomCardId.Accelerant:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.AccelerantPower)));
                break;
            case HotCustomCardId.Accuracy:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.AccuracyPower)));
                break;
            case HotCustomCardId.Afterimage:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.AfterimagePower)));
                break;
            case HotCustomCardId.Afterlife:
                ops.Add(new HotCardEffectOp(HotCardOpKind.SummonOsty, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Aggression:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.AggressionPower)));
                break;
            case HotCustomCardId.Alignment:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainEnergy, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.AllIsWell:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.AllIsWellPower)));
                break;
            case HotCustomCardId.Anger:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.AddFixedCard, HotCardOpFlags.AddCloneToDiscard, (short)cardDefId));
                break;
            case HotCustomCardId.Anticipate:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.AnticipatePower)));
                break;
            case HotCustomCardId.Apparition:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.IntangiblePower)));
                break;
            case HotCustomCardId.Archery:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ArcheryPower)));
                break;
            case HotCustomCardId.ArrowStab:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.AimPower)));
                break;
            case HotCustomCardId.Arsenal:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ArsenalPower)));
                break;
            case HotCustomCardId.AscendersBane:
                break;
            case HotCustomCardId.Assassinate:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomCardId.AstralPulse:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage | HotCardOpFlags.UseCardHits, 0));
                break;
            case HotCustomCardId.Automation:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.AutomationPower)));
                break;
            case HotCustomCardId.Backflip:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Backstab:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.BadLuck:
                break;
            case HotCustomCardId.BallLightning:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Lightning));
                break;
            case HotCustomCardId.BansheesCry:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Barricade:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.BarricadePower)));
                break;
            case HotCustomCardId.Bash:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomCardId.BattleTrance:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.NoDrawPower)));
                break;
            case HotCustomCardId.BeaconOfHope:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.BeaconOfHopePower)));
                break;
            case HotCustomCardId.BeamCell:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomCardId.BelieveInYou:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainEnergy, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.BlackHole:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.BlackHolePower)));
                break;
            case HotCustomCardId.Blaze:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.StrengthPower)));
                break;
            case HotCustomCardId.BloodWall:
                ops.Add(new HotCardEffectOp(HotCardOpKind.LoseHp, HotCardOpFlags.UseCardMagic, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Bludgeon:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Blur:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.BlurPower)));
                break;
            case HotCustomCardId.Bodyguard:
                ops.Add(new HotCardEffectOp(HotCardOpKind.SummonOsty, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Bolas:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.BootSequence:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Bounce:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.StretchPower)));
                break;
            case HotCustomCardId.Break:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomCardId.Breakthrough:
                ops.Add(new HotCardEffectOp(HotCardOpKind.LoseHp, HotCardOpFlags.UseCardMagic, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Buffer:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.BufferPower)));
                break;
            case HotCustomCardId.Bulwark:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Forge, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Burn:
                break;
            case HotCustomCardId.Burst:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.BurstPower)));
                break;
            case HotCustomCardId.Bury:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.ByrdSwoop:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.ByrdonisEgg:
                break;
            case HotCustomCardId.Cacophony:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CacophonyPower)));
                break;
            case HotCustomCardId.Calamity:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.CalamityPower)));
                break;
            case HotCustomCardId.Calcify:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CalcifyPower)));
                break;
            case HotCustomCardId.CallOfTheVoid:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CallOfTheVoidPower)));
                break;
            case HotCustomCardId.Caltrops:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ThornsPower)));
                break;
            case HotCustomCardId.CelestialMight:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage | HotCardOpFlags.UseCardHits, 0));
                break;
            case HotCustomCardId.ChargeBattery:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EnergyNextTurnPower)));
                break;
            case HotCustomCardId.ChildOfTheStars:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ChildOfTheStarsPower)));
                break;
            case HotCustomCardId.Clash:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.CloakOfStars:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Clumsy:
                break;
            case HotCustomCardId.Coda:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.ColdSnap:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Frost));
                break;
            case HotCustomCardId.Colossus:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ColossusPower)));
                break;
            case HotCustomCardId.Compost:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CompostPower)));
                break;
            case HotCustomCardId.Concoct:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ConcoctPower)));
                break;
            case HotCustomCardId.Conqueror:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Forge, HotCardOpFlags.UseCardMagic, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.ConquerorPower)));
                break;
            case HotCustomCardId.Coolant:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CoolantPower)));
                break;
            case HotCustomCardId.Coolheaded:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Frost));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.CorrosiveWave:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CorrosiveWavePower)));
                break;
            case HotCustomCardId.Corruption:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CorruptionPower)));
                break;
            case HotCustomCardId.Countdown:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CountdownPower)));
                break;
            case HotCustomCardId.CoveringFire:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CoveringFirePower)));
                break;
            case HotCustomCardId.CreativeAi:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CreativeAiPower)));
                break;
            case HotCustomCardId.CrimsonMantle:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CrimsonMantlePower)));
                break;
            case HotCustomCardId.Cruelty:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.CrueltyPower)));
                break;
            case HotCustomCardId.CurseOfTheBell:
                break;
            case HotCustomCardId.DarkEmbrace:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.DarkEmbracePower)));
                break;
            case HotCustomCardId.DarkShackles:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DarkShacklesPower)));
                break;
            case HotCustomCardId.Dash:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Dazed:
                break;
            case HotCustomCardId.Death:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DoomPower)));
                break;
            case HotCustomCardId.Debilitate:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DebilitatePower)));
                break;
            case HotCustomCardId.Debt:
                break;
            case HotCustomCardId.Decay:
                break;
            case HotCustomCardId.DefendDefect:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.DefendFool:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.DefendIronclad:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.DefendNecrobinder:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.DefendRegent:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.DefendSilent:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Defile:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Deflect:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Defragment:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.FocusPower)));
                break;
            case HotCustomCardId.Defy:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.WeakPower)));
                break;
            case HotCustomCardId.Delay:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EnergyNextTurnPower)));
                break;
            case HotCustomCardId.DemonForm:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DemonFormPower)));
                break;
            case HotCustomCardId.Devastate:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.DevourLife:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DevourLifePower)));
                break;
            case HotCustomCardId.DingyDagger:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.DislikeYou:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DislikeYouPower)));
                break;
            case HotCustomCardId.Doubt:
                break;
            case HotCustomCardId.Dowsing:
                break;
            case HotCustomCardId.DramaticEntrance:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.EchoForm:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EchoFormPower)));
                break;
            case HotCustomCardId.Ecstasy:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EcstasyPower)));
                break;
            case HotCustomCardId.EnfeeblingTouch:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EnfeeblingTouchPower)));
                break;
            case HotCustomCardId.Ennui:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EnnuiPower)));
                break;
            case HotCustomCardId.Ensemble:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EnsemblePower)));
                break;
            case HotCustomCardId.Entropy:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EntropyPower)));
                break;
            case HotCustomCardId.Envenom:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EnvenomPower)));
                break;
            case HotCustomCardId.EpicRetrieval:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Equilibrium:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.RetainHandPower)));
                break;
            case HotCustomCardId.EternalArmor:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PlatingPower)));
                break;
            case HotCustomCardId.Expertise:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.RetainHandPower)));
                break;
            case HotCustomCardId.ExplosiveArrows:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ExplosiveArrowsPower)));
                break;
            case HotCustomCardId.Exterminate:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage | HotCardOpFlags.UseCardHits, 0));
                break;
            case HotCustomCardId.Fade:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.FadePower)));
                break;
            case HotCustomCardId.Fasten:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.FastenPower)));
                break;
            case HotCustomCardId.Fear:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomCardId.FeelNoPain:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.FeelNoPainPower)));
                break;
            case HotCustomCardId.Feral:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.FeralPower)));
                break;
            case HotCustomCardId.Finesse:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.FlickFlack:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.FocusedStrike:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.FocusedStrikePower)));
                break;
            case HotCustomCardId.Folly:
                break;
            case HotCustomCardId.Footwork:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DexterityPower)));
                break;
            case HotCustomCardId.ForbiddenGrimoire:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.ForbiddenGrimoirePower)));
                break;
            case HotCustomCardId.ForegoneConclusion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ForegoneConclusionPower)));
                break;
            case HotCustomCardId.Fuel:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainEnergy, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Furnace:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.FurnacePower)));
                break;
            case HotCustomCardId.Fusion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Plasma));
                break;
            case HotCustomCardId.GatherLight:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainStars, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Genesis:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.GenesisPower)));
                break;
            case HotCustomCardId.GiantRock:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Glasswork:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Glass));
                break;
            case HotCustomCardId.Greed:
                break;
            case HotCustomCardId.Guilty:
                break;
            case HotCustomCardId.Hailstorm:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.HailstormPower)));
                break;
            case HotCustomCardId.HammerTime:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.HammerTimePower)));
                break;
            case HotCustomCardId.Haunt:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.HauntPower)));
                break;
            case HotCustomCardId.Hegemony:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EnergyNextTurnPower)));
                break;
            case HotCustomCardId.HelloWorld:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.HelloWorldPower)));
                break;
            case HotCustomCardId.Hellraiser:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.HellraiserPower)));
                break;
            case HotCustomCardId.Hemokinesis:
                ops.Add(new HotCardEffectOp(HotCardOpKind.LoseHp, HotCardOpFlags.UseCardMagic, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.HiddenRoots:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.HiddenRootsPower)));
                break;
            case HotCustomCardId.Honing:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.HoningPower)));
                break;
            case HotCustomCardId.Hotfix:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.HotfixPower)));
                break;
            case HotCustomCardId.HowlFromBeyond:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.IAmInvincible:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Impervious:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Infection:
                break;
            case HotCustomCardId.Inferno:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.InfernoPower)));
                break;
            case HotCustomCardId.InfiniteBlades:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.InfiniteBladesPower)));
                break;
            case HotCustomCardId.Inflame:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.StrengthPower)));
                break;
            case HotCustomCardId.Injury:
                break;
            case HotCustomCardId.Intercept:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.CoveredPower)));
                break;
            case HotCustomCardId.IronWave:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Iteration:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.IterationPower)));
                break;
            case HotCustomCardId.Juggernaut:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.JuggernautPower)));
                break;
            case HotCustomCardId.Juggling:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.JugglingPower)));
                break;
            case HotCustomCardId.Justice:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.KinglyKick:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.KinglyPunch:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Knockdown:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.KnockdownPower)));
                break;
            case HotCustomCardId.LanternKey:
                break;
            case HotCustomCardId.Leap:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.LegSweep:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.WeakPower)));
                break;
            case HotCustomCardId.Lethality:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.LethalityPower)));
                break;
            case HotCustomCardId.LightningRod:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.LightningRodPower)));
                break;
            case HotCustomCardId.Loop:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.LoopPower)));
                break;
            case HotCustomCardId.LostLute:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.LostLutePower)));
                break;
            case HotCustomCardId.Lullaby:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.LullabyPower)));
                break;
            case HotCustomCardId.Luminesce:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainEnergy, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.MachineLearning:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.MachineLearningPower)));
                break;
            case HotCustomCardId.MakeItSo:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Mangle:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ManglePower)));
                break;
            case HotCustomCardId.MasterOfStrategy:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.MasterPlanner:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.MasterPlannerPower)));
                break;
            case HotCustomCardId.Mayhem:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.MayhemPower)));
                break;
            case HotCustomCardId.Melancholy:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Midnight:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.MinionDiveBomb:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.MinionSacrifice:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.MinionStrike:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.MonarchsGaze:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.MonarchsGazePower)));
                break;
            case HotCustomCardId.Motif:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.MotifPower)));
                break;
            case HotCustomCardId.MythicForm:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.MythicFormPower)));
                break;
            case HotCustomCardId.NecroMastery:
                ops.Add(new HotCardEffectOp(HotCardOpKind.SummonOsty, HotCardOpFlags.UseCardMagic, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.NecroMasteryPower)));
                break;
            case HotCustomCardId.NeutronAegis:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PlatingPower)));
                break;
            case HotCustomCardId.Nock:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.AimPower)));
                break;
            case HotCustomCardId.Nostalgia:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.NostalgiaPower)));
                break;
            case HotCustomCardId.NotYet:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Heal, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.NoxiousFumes:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.NoxiousFumesPower)));
                break;
            case HotCustomCardId.Null:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.WeakPower)));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Dark));
                break;
            case HotCustomCardId.Oblivion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.OblivionPower)));
                break;
            case HotCustomCardId.OneTwoPunch:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.OneTwoPunchPower)));
                break;
            case HotCustomCardId.Orbit:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.OrbitPower)));
                break;
            case HotCustomCardId.Outmaneuver:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EnergyNextTurnPower)));
                break;
            case HotCustomCardId.Pagestorm:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PagestormPower)));
                break;
            case HotCustomCardId.Panache:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PanachePower)));
                break;
            case HotCustomCardId.PanicButton:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.NoBlockPower)));
                break;
            case HotCustomCardId.Parry:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ParryPower)));
                break;
            case HotCustomCardId.Parse:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.ParticleWall:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Patter:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VigorPower)));
                break;
            case HotCustomCardId.Peck:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage | HotCardOpFlags.UseCardHits, 0));
                break;
            case HotCustomCardId.PhantomBlades:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PhantomBladesPower)));
                break;
            case HotCustomCardId.Pinpoint:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.PoisonedStab:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PoisonPower)));
                break;
            case HotCustomCardId.PommelStrike:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.PoorSleep:
                break;
            case HotCustomCardId.Pounce:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.FreeSkillPower)));
                break;
            case HotCustomCardId.PrepTime:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PrepTimePower)));
                break;
            case HotCustomCardId.Production:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainEnergy, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Prophesize:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.PullAggro:
                ops.Add(new HotCardEffectOp(HotCardOpKind.SummonOsty, HotCardOpFlags.UseCardMagic, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Pyre:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PyrePower)));
                break;
            case HotCustomCardId.Quietus:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.QuietusPower)));
                break;
            case HotCustomCardId.Rage:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.RagePower)));
                break;
            case HotCustomCardId.Rainbow:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Lightning));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Frost));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Dark));
                break;
            case HotCustomCardId.Reanimate:
                ops.Add(new HotCardEffectOp(HotCardOpKind.SummonOsty, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Reap:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.ReaperForm:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.ReaperFormPower)));
                break;
            case HotCustomCardId.Rebound:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.ReboundPower)));
                break;
            case HotCustomCardId.Reflect:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.ReflectPower)));
                break;
            case HotCustomCardId.Reflex:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Regret:
                break;
            case HotCustomCardId.Ricochet:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackRandom, HotCardOpFlags.UseCardDamage | HotCardOpFlags.UseCardHits, 0));
                break;
            case HotCustomCardId.RipAndTear:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackRandom, HotCardOpFlags.UseCardDamage | HotCardOpFlags.UseCardHits, 0));
                break;
            case HotCustomCardId.RocketPunch:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.RoyalGamble:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainStars, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Royalties:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.RoyaltiesPower)));
                break;
            case HotCustomCardId.Rupture:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.RupturePower)));
                break;
            case HotCustomCardId.Sadness:
                break;
            case HotCustomCardId.Salvo:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.RetainHandPower)));
                break;
            case HotCustomCardId.SeekingEdge:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.SeekingEdgePower)));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Forge, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.SentryMode:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SentryModePower)));
                break;
            case HotCustomCardId.SerpentForm:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SerpentFormPower)));
                break;
            case HotCustomCardId.SerratedArrows:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SerratedArrowsPower)));
                break;
            case HotCustomCardId.SetupStrike:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SetupStrikePower)));
                break;
            case HotCustomCardId.SevenStars:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage | HotCardOpFlags.UseCardHits, 0));
                break;
            case HotCustomCardId.ShadowShield:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Dark));
                break;
            case HotCustomCardId.Shadowmeld:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ShadowmeldPower)));
                break;
            case HotCustomCardId.Shame:
                break;
            case HotCustomCardId.ShiningStrike:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainStars, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.ShrugItOff:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Sidestep:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.EnergyNextTurnPower)));
                break;
            case HotCustomCardId.SignalBoost:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SignalBoostPower)));
                break;
            case HotCustomCardId.Simpleton:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SimpletonPower)));
                break;
            case HotCustomCardId.Skim:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.SleightOfFlesh:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SleightOfFleshPower)));
                break;
            case HotCustomCardId.Smokestack:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SmokestackPower)));
                break;
            case HotCustomCardId.Snakebite:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PoisonPower)));
                break;
            case HotCustomCardId.Sneaky:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SneakyPower)));
                break;
            case HotCustomCardId.SolarStrike:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainStars, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.SongPath:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SongPathPower)));
                break;
            case HotCustomCardId.Soot:
                break;
            case HotCustomCardId.Soul:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Soulbound:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.SoulboundPower)));
                break;
            case HotCustomCardId.Sow:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.SpectrumShift:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SpectrumShiftPower)));
                break;
            case HotCustomCardId.Speedster:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SpeedsterPower)));
                break;
            case HotCustomCardId.SpiritOfAsh:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SpiritOfAshPower)));
                break;
            case HotCustomCardId.SpoilsMap:
                break;
            case HotCustomCardId.Squash:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomCardId.Stampede:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.StampedePower)));
                break;
            case HotCustomCardId.StoneArmor:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PlatingPower)));
                break;
            case HotCustomCardId.Storm:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.StormPower)));
                break;
            case HotCustomCardId.Strangle:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.StranglePower)));
                break;
            case HotCustomCardId.Stratagem:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.StratagemPower)));
                break;
            case HotCustomCardId.Strength:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.StrengthPower)));
                break;
            case HotCustomCardId.StrikeDefect:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.StrikeFool:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.StrikeIronclad:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.StrikeNecrobinder:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.StrikeRegent:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.StrikeSilent:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Subroutine:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.SubroutinePower)));
                break;
            case HotCustomCardId.SuckerPunch:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.WeakPower)));
                break;
            case HotCustomCardId.Supercritical:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainEnergy, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.SwordBoomerang:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackRandom, HotCardOpFlags.UseCardDamage | HotCardOpFlags.UseCardHits, 0));
                break;
            case HotCustomCardId.SwordSage:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SwordSagePower)));
                break;
            case HotCustomCardId.Synthesis:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.FreePowerPower)));
                break;
            case HotCustomCardId.Tactician:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainEnergy, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.TagTeam:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.TagTeamPower)));
                break;
            case HotCustomCardId.Tank:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.TankPower)));
                break;
            case HotCustomCardId.Taunt:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomCardId.Temperance:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Terraforming:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VigorPower)));
                break;
            case HotCustomCardId.TheChariot:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.TheEmperor:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainEnergy, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.TheGambit:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.TheGambitPower)));
                break;
            case HotCustomCardId.TheHighPriestess:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.TheSealedThrone:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.TheSealedThronePower)));
                break;
            case HotCustomCardId.TheSmith:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Forge, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.ThrummingHatchet:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Thunder:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ThunderPower)));
                break;
            case HotCustomCardId.Thunderclap:
                ops.Add(new HotCardEffectOp(HotCardOpKind.AttackAll, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerAllEnemies, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomCardId.ToolsOfTheTrade:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.ToolsOfTheTradePower)));
                break;
            case HotCustomCardId.ToricToughness:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ToricToughnessPower)));
                break;
            case HotCustomCardId.TrashToTreasure:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.TrashToTreasurePower)));
                break;
            case HotCustomCardId.Tremble:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomCardId.Tune:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.TunePower)));
                break;
            case HotCustomCardId.TwinStrike:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage | HotCardOpFlags.UseCardHits, 0));
                break;
            case HotCustomCardId.Tyranny:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.TyrannyPower)));
                break;
            case HotCustomCardId.UltimateDefend:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.UltimateStrike:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomCardId.Underworld:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.UnderworldPower)));
                break;
            case HotCustomCardId.Unmovable:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.UnmovablePower)));
                break;
            case HotCustomCardId.Unrelenting:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.FreeAttackPower)));
                break;
            case HotCustomCardId.Untouchable:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Veilpiercer:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.VeilpiercerPower)));
                break;
            case HotCustomCardId.Venerate:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainStars, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Vicious:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ViciousPower)));
                break;
            case HotCustomCardId.ViolentChord:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ViolentChordPower)));
                break;
            case HotCustomCardId.Void:
                break;
            case HotCustomCardId.WahChoKi:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.WellLaidPlans:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.WellLaidPlansPower)));
                break;
            case HotCustomCardId.WhistlingArrows:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomCardId.Wisp:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainEnergy, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Wither:
                break;
            case HotCustomCardId.Wound:
                break;
            case HotCustomCardId.Writhe:
                break;
            case HotCustomCardId.WroughtInWar:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.UseCardDamage, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.Forge, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomCardId.Yah:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.YahPower)));
                break;
            case HotCustomCardId.Zap:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ChannelOrb, HotCardOpFlags.FixedOne, (short)HotOrbId.Lightning));
                break;
            default:
                customCardId = cardId;
                break;
        }
        opCount = (byte)(ops.Count - opStart);
    }
}
