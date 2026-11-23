using System.Collections.Frozen;

namespace CombatSolver.HotKernel;

internal readonly record struct HotPotionCatalogEntry(
    HotCustomPotionId Id,
    string Entry,
    bool UsesBytecode,
    bool CanHealPlayer,
    bool CanGenerateUnknown,
    short BaseStrategicHpCost,
    bool RequiresOpeningUse,
    bool GeneratesPotionChain,
    bool IsCombatUsable);

/// <summary>
/// Static STS2 0.113.1 catalog covering all 67 vanilla potions with bytecode compilation and valuation weights.
/// </summary>
internal static class HotPotionCatalog
{
    public static readonly HotPotionCatalogEntry[] ById = BuildById();
    public static readonly FrozenDictionary<string, HotCustomPotionId> IdByEntry = BuildIdByEntry();

    public static HotCustomPotionId FindByEntry(string entry)
        => IdByEntry.TryGetValue(entry, out HotCustomPotionId id) ? id : HotCustomPotionId.None;

    public static ref readonly HotPotionCatalogEntry Get(HotCustomPotionId id)
    {
        int idx = (int)id;
        return ref ById[(uint)idx < (uint)ById.Length ? idx : 0];
    }

    private static FrozenDictionary<string, HotCustomPotionId> BuildIdByEntry()
    {
        Dictionary<string, HotCustomPotionId> map = new(ById.Length, StringComparer.Ordinal);
        for (int i = 1; i < ById.Length; i++)
            map[ById[i].Entry] = ById[i].Id;
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static HotPotionCatalogEntry[] BuildById()
    {
        HotPotionCatalogEntry[] arr = new HotPotionCatalogEntry[(int)HotCustomPotionId.Count];
        arr[(int)HotCustomPotionId.Ambergris] = new(HotCustomPotionId.Ambergris, "AMBERGRIS", false, true, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.Ashwater] = new(HotCustomPotionId.Ashwater, "ASHWATER", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.AttackPotion] = new(HotCustomPotionId.AttackPotion, "ATTACK_POTION", false, false, true, 9, false, false, true);
        arr[(int)HotCustomPotionId.BeetleJuice] = new(HotCustomPotionId.BeetleJuice, "BEETLE_JUICE", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.BlessingOfTheForge] = new(HotCustomPotionId.BlessingOfTheForge, "BLESSING_OF_THE_FORGE", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.BlockPotion] = new(HotCustomPotionId.BlockPotion, "BLOCK_POTION", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.BloodPotion] = new(HotCustomPotionId.BloodPotion, "BLOOD_POTION", false, true, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.BoneBrew] = new(HotCustomPotionId.BoneBrew, "BONE_BREW", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.BottledPotential] = new(HotCustomPotionId.BottledPotential, "BOTTLED_POTENTIAL", false, false, false, 14, false, false, true);
        arr[(int)HotCustomPotionId.BottledSong] = new(HotCustomPotionId.BottledSong, "BOTTLED_SONG", false, false, true, 9, false, false, true);
        arr[(int)HotCustomPotionId.Clarity] = new(HotCustomPotionId.Clarity, "CLARITY", false, false, false, 14, false, false, true);
        arr[(int)HotCustomPotionId.ColorlessPotion] = new(HotCustomPotionId.ColorlessPotion, "COLORLESS_POTION", false, false, true, 9, false, false, true);
        arr[(int)HotCustomPotionId.CosmicConcoction] = new(HotCustomPotionId.CosmicConcoction, "COSMIC_CONCOCTION", false, false, true, 9, false, false, true);
        arr[(int)HotCustomPotionId.CrystallineEuphoria] = new(HotCustomPotionId.CrystallineEuphoria, "CRYSTALLINE_EUPHORIA", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.CunningPotion] = new(HotCustomPotionId.CunningPotion, "CUNNING_POTION", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.CureAll] = new(HotCustomPotionId.CureAll, "CURE_ALL", false, false, false, 14, false, false, true);
        arr[(int)HotCustomPotionId.DexterityPotion] = new(HotCustomPotionId.DexterityPotion, "DEXTERITY_POTION", true, false, false, 9, true, false, true);
        arr[(int)HotCustomPotionId.DistilledChaos] = new(HotCustomPotionId.DistilledChaos, "DISTILLED_CHAOS", false, false, true, 14, false, false, true);
        arr[(int)HotCustomPotionId.DropletOfPrecognition] = new(HotCustomPotionId.DropletOfPrecognition, "DROPLET_OF_PRECOGNITION", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.Duplicator] = new(HotCustomPotionId.Duplicator, "DUPLICATOR", true, false, false, 18, false, false, true);
        arr[(int)HotCustomPotionId.EnergyPotion] = new(HotCustomPotionId.EnergyPotion, "ENERGY_POTION", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.EntropicBrew] = new(HotCustomPotionId.EntropicBrew, "ENTROPIC_BREW", false, true, true, 9, false, true, true);
        arr[(int)HotCustomPotionId.EssenceOfDarkness] = new(HotCustomPotionId.EssenceOfDarkness, "ESSENCE_OF_DARKNESS", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.ExplosiveAmpoule] = new(HotCustomPotionId.ExplosiveAmpoule, "EXPLOSIVE_AMPOULE", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.FairyInABottle] = new(HotCustomPotionId.FairyInABottle, "FAIRY_IN_A_BOTTLE", false, true, false, 9, false, false, false);
        arr[(int)HotCustomPotionId.FirePotion] = new(HotCustomPotionId.FirePotion, "FIRE_POTION", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.FlexPotion] = new(HotCustomPotionId.FlexPotion, "FLEX_POTION", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.FocusPotion] = new(HotCustomPotionId.FocusPotion, "FOCUS_POTION", true, false, false, 9, true, false, true);
        arr[(int)HotCustomPotionId.Fortifier] = new(HotCustomPotionId.Fortifier, "FORTIFIER", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.FoulPotion] = new(HotCustomPotionId.FoulPotion, "FOUL_POTION", false, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.FruitJuice] = new(HotCustomPotionId.FruitJuice, "FRUIT_JUICE", false, true, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.FyshOil] = new(HotCustomPotionId.FyshOil, "FYSH_OIL", true, false, false, 9, true, false, true);
        arr[(int)HotCustomPotionId.GamblersBrew] = new(HotCustomPotionId.GamblersBrew, "GAMBLERS_BREW", false, false, false, 18, false, false, true);
        arr[(int)HotCustomPotionId.GhostInAJar] = new(HotCustomPotionId.GhostInAJar, "GHOST_IN_A_JAR", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.GigantificationPotion] = new(HotCustomPotionId.GigantificationPotion, "GIGANTIFICATION_POTION", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.GlowwaterPotion] = new(HotCustomPotionId.GlowwaterPotion, "GLOWWATER_POTION", false, false, false, 18, false, false, true);
        arr[(int)HotCustomPotionId.HeartOfIron] = new(HotCustomPotionId.HeartOfIron, "HEART_OF_IRON", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.KingsCourage] = new(HotCustomPotionId.KingsCourage, "KINGS_COURAGE", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.LiquidBronze] = new(HotCustomPotionId.LiquidBronze, "LIQUID_BRONZE", true, false, false, 9, true, false, true);
        arr[(int)HotCustomPotionId.LiquidMemories] = new(HotCustomPotionId.LiquidMemories, "LIQUID_MEMORIES", false, false, false, 14, false, false, true);
        arr[(int)HotCustomPotionId.LuckyTonic] = new(HotCustomPotionId.LuckyTonic, "LUCKY_TONIC", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.MazalethsGift] = new(HotCustomPotionId.MazalethsGift, "MAZALETHS_GIFT", true, false, false, 9, true, false, true);
        arr[(int)HotCustomPotionId.OrobicAcid] = new(HotCustomPotionId.OrobicAcid, "OROBIC_ACID", false, false, true, 18, false, false, true);
        arr[(int)HotCustomPotionId.PoisonPotion] = new(HotCustomPotionId.PoisonPotion, "POISON_POTION", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.PotOfGhouls] = new(HotCustomPotionId.PotOfGhouls, "POT_OF_GHOULS", false, false, false, 18, false, false, true);
        arr[(int)HotCustomPotionId.PotionOfBinding] = new(HotCustomPotionId.PotionOfBinding, "POTION_OF_BINDING", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.PotionOfCapacity] = new(HotCustomPotionId.PotionOfCapacity, "POTION_OF_CAPACITY", false, false, false, 9, true, false, true);
        arr[(int)HotCustomPotionId.PotionOfDoom] = new(HotCustomPotionId.PotionOfDoom, "POTION_OF_DOOM", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.PotionShapedRock] = new(HotCustomPotionId.PotionShapedRock, "POTION_SHAPED_ROCK", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.PowderedDemise] = new(HotCustomPotionId.PowderedDemise, "POWDERED_DEMISE", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.PowerPotion] = new(HotCustomPotionId.PowerPotion, "POWER_POTION", false, false, true, 9, false, false, true);
        arr[(int)HotCustomPotionId.PrecisionPotion] = new(HotCustomPotionId.PrecisionPotion, "PRECISION_POTION", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.RadiantTincture] = new(HotCustomPotionId.RadiantTincture, "RADIANT_TINCTURE", false, false, false, 14, false, false, true);
        arr[(int)HotCustomPotionId.RegenPotion] = new(HotCustomPotionId.RegenPotion, "REGEN_POTION", true, true, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.ShacklingPotion] = new(HotCustomPotionId.ShacklingPotion, "SHACKLING_POTION", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.ShipInABottle] = new(HotCustomPotionId.ShipInABottle, "SHIP_IN_A_BOTTLE", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.SkillPotion] = new(HotCustomPotionId.SkillPotion, "SKILL_POTION", false, false, true, 9, false, false, true);
        arr[(int)HotCustomPotionId.SneckoOil] = new(HotCustomPotionId.SneckoOil, "SNECKO_OIL", false, false, true, 9, false, false, true);
        arr[(int)HotCustomPotionId.SoldiersStew] = new(HotCustomPotionId.SoldiersStew, "SOLDIERS_STEW", false, false, false, 9, true, false, true);
        arr[(int)HotCustomPotionId.SpeedPotion] = new(HotCustomPotionId.SpeedPotion, "SPEED_POTION", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.StableSerum] = new(HotCustomPotionId.StableSerum, "STABLE_SERUM", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.StarPotion] = new(HotCustomPotionId.StarPotion, "STAR_POTION", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.StrengthPotion] = new(HotCustomPotionId.StrengthPotion, "STRENGTH_POTION", true, false, false, 9, true, false, true);
        arr[(int)HotCustomPotionId.SwiftPotion] = new(HotCustomPotionId.SwiftPotion, "SWIFT_POTION", true, false, false, 18, false, false, true);
        arr[(int)HotCustomPotionId.TouchOfInsanity] = new(HotCustomPotionId.TouchOfInsanity, "TOUCH_OF_INSANITY", false, false, false, 14, false, false, true);
        arr[(int)HotCustomPotionId.VulnerablePotion] = new(HotCustomPotionId.VulnerablePotion, "VULNERABLE_POTION", true, false, false, 9, false, false, true);
        arr[(int)HotCustomPotionId.WeakPotion] = new(HotCustomPotionId.WeakPotion, "WEAK_POTION", true, false, false, 9, false, false, true);
        return arr;
    }

    public static void CompilePotionBytecode(
        HotCustomPotionId potionId,
        Func<HotPowerId, ushort> ensurePowerDefId,
        List<HotCardEffectOp> ops,
        out int opStart,
        out byte opCount,
        out HotCustomPotionId customPotionId)
    {
        opStart = ops.Count;
        customPotionId = HotCustomPotionId.None;
        switch (potionId)
        {
            case HotCustomPotionId.BlockPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.Unpowered | HotCardOpFlags.UseCardBlock, 0));
                break;
            case HotCustomPotionId.BoneBrew:
                ops.Add(new HotCardEffectOp(HotCardOpKind.SummonOsty, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomPotionId.DexterityPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DexterityPower)));
                break;
            case HotCustomPotionId.Duplicator:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.FixedOne, (short)ensurePowerDefId(HotPowerId.DuplicationPower)));
                break;
            case HotCustomPotionId.EnergyPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainEnergy, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomPotionId.FlexPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.FlexPotionPower)));
                break;
            case HotCustomPotionId.FocusPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.FocusPower)));
                break;
            case HotCustomPotionId.FyshOil:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.StrengthPower)));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DexterityPower)));
                break;
            case HotCustomPotionId.GhostInAJar:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.IntangiblePower)));
                break;
            case HotCustomPotionId.GigantificationPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.GigantificationPower)));
                break;
            case HotCustomPotionId.HeartOfIron:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PlatingPower)));
                break;
            case HotCustomPotionId.KingsCourage:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Forge, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomPotionId.LiquidBronze:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ThornsPower)));
                break;
            case HotCustomPotionId.LuckyTonic:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.BufferPower)));
                break;
            case HotCustomPotionId.MazalethsGift:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.RitualPower)));
                break;
            case HotCustomPotionId.PoisonPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.PoisonPower)));
                break;
            case HotCustomPotionId.PotionOfBinding:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerAllEnemies, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.WeakPower)));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerAllEnemies, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomPotionId.PotionOfDoom:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DoomPower)));
                break;
            case HotCustomPotionId.PotionShapedRock:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Attack, HotCardOpFlags.Unpowered | HotCardOpFlags.UseCardDamage, 0));
                break;
            case HotCustomPotionId.PowderedDemise:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.DemisePower)));
                break;
            case HotCustomPotionId.PrecisionPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.AimPower)));
                break;
            case HotCustomPotionId.RegenPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.RegenPower)));
                break;
            case HotCustomPotionId.ShacklingPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerAllEnemies, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.ShacklingPotionPower)));
                break;
            case HotCustomPotionId.ShipInABottle:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainBlock, HotCardOpFlags.Unpowered | HotCardOpFlags.UseCardBlock, 0));
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.BlockNextTurnPower)));
                break;
            case HotCustomPotionId.SpeedPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.SpeedPotionPower)));
                break;
            case HotCustomPotionId.StableSerum:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.RetainHandPower)));
                break;
            case HotCustomPotionId.StarPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.GainStars, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomPotionId.StrengthPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerSelf, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.StrengthPower)));
                break;
            case HotCustomPotionId.SwiftPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.Draw, HotCardOpFlags.UseCardMagic, 0));
                break;
            case HotCustomPotionId.VulnerablePotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.VulnerablePower)));
                break;
            case HotCustomPotionId.WeakPotion:
                ops.Add(new HotCardEffectOp(HotCardOpKind.ApplyPowerTarget, HotCardOpFlags.UseCardMagic, (short)ensurePowerDefId(HotPowerId.WeakPower)));
                break;
            default:
                customPotionId = potionId;
                break;
        }
        opCount = (byte)(ops.Count - opStart);
    }
}
