using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

namespace CombatSolver;

internal static class PotionUsePolicy
{
    public const decimal AmbergrisMinimumHpSavedFraction = 0.40m;

    public static int RequiredHpSaved(int potionCount)
        => potionCount * SolverWeights.PotionMinimumHpSaved;

    public static int ExplicitUseCount(int potionCount, int automaticPotionCount)
        => potionCount - automaticPotionCount;

    public static int AdditionalRequiredUseStrategicHpCost(int strategicHpCost)
        => Math.Max(0, strategicHpCost - SolverWeights.PotionMinimumHpSaved);

    /// <summary>
    /// Potions the solver must be clearly rewarded for spending, because their effect is hard to replace.
    /// </summary>
    /// <remarks>
    /// Ambergris is deliberately absent: <see cref="MeetsAmbergrisRestriction"/> already holds it to a far higher
    /// bar (a fraction of maximum HP), and that calculation is written against the baseline cost.
    /// </remarks>
    private static readonly HashSet<string> HighValuePotionIds =
    [
        "GLOWWATER_POTION",
        "SWIFT_POTION",
        "GAMBLERS_BREW",
        "DUPLICATOR",
        "OROBIC_ACID",
        "POT_OF_GHOULS",
    ];

    private static readonly HashSet<string> ElevatedValuePotionIds =
    [
        "DISTILLED_CHAOS",
        "CLARITY",
        "RADIANT_TINCTURE",
        "CURE_ALL",
        "LIQUID_MEMORIES",
        "BOTTLED_POTENTIAL",
        "TOUCH_OF_INSANITY",
    ];

    /// <summary>
    /// The HP a route must save to justify spending this potion. Token potions are free to spend; everything else
    /// costs at least the baseline, and the two value tiers cost more so a cheap potion is spent before a scarce one.
    /// </summary>
    public static int StrategicHpCost(PotionModel potion, bool renewablePotionShapedRock = false)
    {
        if (potion.Rarity == PotionRarity.Token || renewablePotionShapedRock && potion is PotionShapedRock)
            return 0;
        string id = potion.Id.Entry;
        if (HighValuePotionIds.Contains(id))
            return SolverWeights.PotionHighValueHpSaved;
        if (ElevatedValuePotionIds.Contains(id))
            return SolverWeights.PotionElevatedValueHpSaved;
        return SolverWeights.PotionMinimumHpSaved;
    }

    public static bool RequiresOpeningUse(PotionModel potion)
        => potion is DexterityPotion
            or FocusPotion
            or FyshOil
            or LiquidBronze
            or MazalethsGift
            or PotionOfCapacity
            or SoldiersStew
            or StrengthPotion;

    public static bool RequiresOpeningUse(string potionId)
        => RequiresOpeningUse(ModelDb.AllPotions.Single(candidate =>
            candidate.Id.Entry.Equals(potionId, StringComparison.Ordinal)));

    public static int StrategicHpCost(string potionId, bool renewablePotionShapedRock = false)
    {
        PotionModel potion = ModelDb.AllPotions.Single(candidate =>
            candidate.Id.Entry.Equals(potionId, StringComparison.Ordinal));
        return StrategicHpCost(potion, renewablePotionShapedRock);
    }

    public static int HpSaved(int potionFreeHpDeficit, int potionRouteHpDeficit)
        => Math.Max(0, potionFreeHpDeficit - potionRouteHpDeficit);

    public static int SmartRequiredHpSaved(
        int strategicHpCost,
        BossHpRelief bossHpRelief = BossHpRelief.None)
        => ActEndingBossPolicy.RawHpRequiredForPersistentValue(
            strategicHpCost,
            bossHpRelief);

    public static int AmbergrisRequiredHpSaved(int maximumHp)
        => (int)Math.Ceiling(maximumHp * AmbergrisMinimumHpSavedFraction);

    public static int EffectiveStrategicHpCost(
        int strategicHpCost,
        int ambergrisCount,
        int maximumHp)
        => strategicHpCost + ambergrisCount
            * (AmbergrisRequiredHpSaved(maximumHp) - SolverWeights.PotionMinimumHpSaved);

    public static bool MeetsAmbergrisRestriction(
        bool hasPotionFreeBaseline,
        int ambergrisCount,
        int strategicHpCost,
        int maximumHp,
        int potionFreePlayerHp,
        int potionRoutePlayerHp)
    {
        if (ambergrisCount == 0)
            return true;
        if (!hasPotionFreeBaseline)
            return false;
        int required = EffectiveStrategicHpCost(strategicHpCost, ambergrisCount, maximumHp);
        return Math.Max(0, potionRoutePlayerHp - potionFreePlayerHp) >= required;
    }

    public static bool IsEligible(
        SolverPotionPolicy policy,
        int explicitPotionCount,
        int strategicHpCost,
        bool potionFreeWon,
        int potionFreeHpDeficit,
        bool anyRouteWon,
        bool potionRouteWon,
        int potionRouteHpDeficit)
        => policy switch
        {
            SolverPotionPolicy.Disabled => explicitPotionCount == 0,
            SolverPotionPolicy.RequireAtLeastOne => explicitPotionCount > 0
                && (!anyRouteWon || potionRouteWon)
                && (explicitPotionCount == 1
                    || !potionFreeWon
                    || HpSaved(potionFreeHpDeficit, potionRouteHpDeficit)
                        >= AdditionalRequiredUseStrategicHpCost(strategicHpCost)),
            SolverPotionPolicy.Smart => explicitPotionCount == 0
                || potionRouteWon && !potionFreeWon
                || HpSaved(potionFreeHpDeficit, potionRouteHpDeficit)
                    >= SmartRequiredHpSaved(strategicHpCost),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
}

internal readonly record struct PotionFreePolicyBaseline(
    bool Won,
    int HpDeficit,
    int PlayerHp,
    int? CombatEndedTurn);

internal sealed class PotionPolicyUnsatisfiedException(string message) : InvalidOperationException(message);
