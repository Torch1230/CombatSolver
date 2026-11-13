using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal static partial class MonsterMoveEffects
{
    private static readonly IReadOnlyDictionary<string, string[]> StaticIntMembers =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["LivingFog"] = ["BloatAmount"],
            ["ToughEgg"] = ["HatchlingMinHp", "HatchlingMaxHp"],
            ["TestSubject"] = ["BurningGrowlBurnCount", "BurningGrowlStrengthGain"],
            ["BowlbugEgg"] = ["ProtectBlock"],
            ["InfestedPrism"] = ["VitalSparkAmount"],
            ["KinFollower"] = ["DanceStrength"],
            ["KinPriest"] = ["RitualStrength"],
            ["KnowledgeDemon"] = ["PonderStrength"],
            ["LagavulinMatriarch"] = ["Slash2Block", "DebuffAmount"],
            ["Myte"] = ["SuckStrength"],
            ["Nibbit"] = ["SliceBlock", "HissStrengthGain"],
            ["MagiKnight"] = ["PowerShieldBlock"],
            ["Seapunk"] = ["BubbleBlock", "BubbleStr"],
            ["BowlbugNectar"] = ["BuffStrengthGain"],
            ["CorpseSlug"] = ["GoopFrailAmt"],
            ["SoulFysh"] = ["ScreamMoveAmount", "GazeMoveAmount"],
            ["TheLost"] = ["DebilitatingSmogStrengthStealAmount"],
            ["CalcifiedCultist"] = ["IncantationAmount"],
            ["DampCultist"] = ["IncantationAmount"],
            ["CeremonialBeast"] = ["PlowStrength", "CrushStrength", "PlowAmount", "RitualAmount"],
            ["Ovicopter"] = ["NutritionalPasteStrengthAmount"],
            ["SkulkingColony"] = ["InertiaStrengthGain"],
            ["TheInsatiable"] = ["SalivateStrength", "SandpitAmount"],
            ["Vantom"] = ["PrepareStrength"],
            ["TheObscura"] = ["HardeningStrikeBlock"],
            ["TheForgotten"] = ["DebilitatingSmogDexStealAmount"],
            ["LouseProgenitor"] = ["CurlBlock", "GrowStrength"],
            ["Tunneler"] = ["BlockGain"],
            ["Crusher"] = ["AdaptStrengthGain"],
            ["Rocket"] = ["ChargeUpStrengthGain"],
            ["PhantasmalGardener"] = ["EnlargeStr"],
            ["Axebot"] = ["BootUpBlock", "BootUpStrGain", "RespawnCount"],
            ["Aeonglass"] = ["EbbBlock", "WitherAmount", "IncreasingIntensityBaseStrength"],
            ["AxeRubyRaider"] = ["SwingBlock"],
            ["WaterfallGiant"] = ["PressurizeAmount", "SiphonHeal", "PressureGunIncrease", "SteamBoostAmount"],
        };

    internal static IReadOnlyDictionary<string, int> CaptureStaticIntValues(MonsterModel monster)
    {
        if (!StaticIntMembers.TryGetValue(monster.GetType().Name, out string[]? members))
            return new Dictionary<string, int>(0, StringComparer.Ordinal);
        Dictionary<string, int> values = new(members.Length, StringComparer.Ordinal);
        foreach (string member in members)
            values.Add(member, MonsterValueReader.ReadInt(monster, member));
        return values;
    }
}
