using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

namespace CombatSolver;

internal enum PotionValueTier
{
    Standard,
    Elevated,
    High,
}

internal readonly record struct PotionValuationRule(string PotionId, PotionValueTier Tier);

internal sealed class PotionValuationRegistry
{
    // Ambergris stays at the baseline tier: its maximum-HP restriction is applied separately.
    internal static PotionValuationRegistry Default { get; } = new(
    [
        new("GLOWWATER_POTION", PotionValueTier.High),
        new("SWIFT_POTION", PotionValueTier.High),
        new("GAMBLERS_BREW", PotionValueTier.High),
        new("DUPLICATOR", PotionValueTier.High),
        new("OROBIC_ACID", PotionValueTier.High),
        new("POT_OF_GHOULS", PotionValueTier.High),
        new("DISTILLED_CHAOS", PotionValueTier.Elevated),
        new("CLARITY", PotionValueTier.Elevated),
        new("RADIANT_TINCTURE", PotionValueTier.Elevated),
        new("CURE_ALL", PotionValueTier.Elevated),
        new("LIQUID_MEMORIES", PotionValueTier.Elevated),
        new("BOTTLED_POTENTIAL", PotionValueTier.Elevated),
        new("TOUCH_OF_INSANITY", PotionValueTier.Elevated),
    ],
    [
        typeof(DexterityPotion),
        typeof(FocusPotion),
        typeof(FyshOil),
        typeof(HeartOfIron),
        typeof(LiquidBronze),
        typeof(MazalethsGift),
        typeof(PotionOfCapacity),
        typeof(SoldiersStew),
        typeof(StrengthPotion),
    ]);

    private readonly IReadOnlyDictionary<string, PotionValueTier> _tiers;
    private readonly Type[] _openingTypes;

    internal PotionValuationRegistry(
        IEnumerable<PotionValuationRule> tiers,
        IEnumerable<Type> openingTypes)
    {
        _tiers = tiers.ToDictionary(rule => rule.PotionId, rule => rule.Tier,
            StringComparer.Ordinal);
        _openingTypes = openingTypes.ToArray();
    }

    internal int StrategicHpCost(PotionModel potion, bool renewablePotionShapedRock)
    {
        if (potion.Rarity == PotionRarity.Token
            || renewablePotionShapedRock && potion is PotionShapedRock)
            return 0;
        PotionValueTier tier = _tiers.GetValueOrDefault(potion.Id.Entry);
        return tier switch
        {
            PotionValueTier.High => SolverWeights.PotionHighValueHpSaved,
            PotionValueTier.Elevated => SolverWeights.PotionElevatedValueHpSaved,
            _ => SolverWeights.PotionMinimumHpSaved,
        };
    }

    internal bool RequiresOpeningUse(PotionModel potion)
        => _openingTypes.Any(type => type.IsInstanceOfType(potion));

    internal bool GeneratesPotionChain(string potionId)
        => string.Equals(potionId, "ENTROPIC_BREW", StringComparison.Ordinal);
}
