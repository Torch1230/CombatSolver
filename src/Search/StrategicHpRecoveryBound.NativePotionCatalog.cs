using System.Collections.Frozen;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
namespace CombatSolver;
internal static partial class StrategicHpRecoveryBound
{
    // Positive, pinned metadata. This certifies each potion's own effects only
    // within the existing complete component root checks: all initial/permanent/
    // returning cards, attachments, global callbacks, enemies, native generation
    // filters, player count and branch state must still be certified independently.
    // Unknown, deprecated and test models never default to zero. Blood/Ambergris/
    // Fruit/Fairy/Entropic retain the conservative runtime fallback; Regen retains
    // the existing dose/quota/active-effect calculation.
    private static readonly FrozenSet<Type> NativeZeroRecoveryPotions = new Type[]
    {
        typeof(Ashwater),
        typeof(AttackPotion),
        typeof(BeetleJuice),
        typeof(BlessingOfTheForge),
        typeof(BlockPotion),
        typeof(BoneBrew),
        typeof(BottledPotential),
        typeof(BottledSong), typeof(CrystallineEuphoria), typeof(PrecisionPotion),
        typeof(Clarity),
        typeof(ColorlessPotion),
        typeof(CosmicConcoction),
        typeof(CunningPotion),
        typeof(CureAll),
        typeof(DexterityPotion),
        typeof(DistilledChaos),
        typeof(DropletOfPrecognition),
        typeof(Duplicator),
        typeof(EnergyPotion),
        typeof(EssenceOfDarkness),
        typeof(ExplosiveAmpoule),
        typeof(FirePotion),
        typeof(FlexPotion),
        typeof(FocusPotion),
        typeof(Fortifier),
        typeof(FoulPotion),
        typeof(FyshOil),
        typeof(GamblersBrew),
        typeof(GhostInAJar),
        typeof(GigantificationPotion),
        typeof(GlowwaterPotion),
        typeof(HeartOfIron),
        typeof(KingsCourage),
        typeof(LiquidBronze),
        typeof(LiquidMemories),
        typeof(LuckyTonic),
        typeof(MazalethsGift),
        typeof(OrobicAcid),
        typeof(PoisonPotion),
        typeof(PotionOfBinding),
        typeof(PotionOfCapacity),
        typeof(PotionOfDoom),
        typeof(PotionShapedRock),
        typeof(PotOfGhouls),
        typeof(PowderedDemise),
        typeof(PowerPotion),
        typeof(RadiantTincture),
        typeof(ShacklingPotion),
        typeof(ShipInABottle),
        typeof(SkillPotion),
        typeof(SneckoOil),
        typeof(SoldiersStew),
        typeof(SpeedPotion),
        typeof(StableSerum),
        typeof(StarPotion),
        typeof(StrengthPotion),
        typeof(SwiftPotion),
        typeof(TouchOfInsanity),
        typeof(VulnerablePotion),
        typeof(WeakPotion),
    }.ToFrozenSet();
    private static readonly FrozenSet<Type> NativePotionZeroRecoveryPowers = new Type[]
    {
        typeof(BlockNextTurnPower),
        typeof(BufferPower),
        typeof(ClarityPower),
        typeof(DemisePower),
        typeof(DexterityPower),
        typeof(DoomPower),
        typeof(DuplicationPower),
        typeof(FlexPotionPower),
        typeof(FocusPower),
        typeof(GigantificationPower),
        typeof(IntangiblePower),
        typeof(PlatingPower),
        typeof(PoisonPower),
        typeof(RadiancePower),
        typeof(RetainHandPower),
        typeof(RitualPower),
        typeof(ShacklingPotionPower),
        typeof(ShrinkPower),
        typeof(SpeedPotionPower),
        typeof(StrengthPower),
        typeof(ThornsPower),
        typeof(VulnerablePower),
        typeof(WeakPower),
    }.ToFrozenSet();
    internal static bool HasNativeZeroRecoveryPotionCertificate(Type type)
        => typeof(PotionModel).Module.ModuleVersionId == ComponentAuditMvid
            && NativeZeroRecoveryPotions.Contains(type);
}
