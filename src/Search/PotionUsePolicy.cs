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

    public static int StrategicHpCost(PotionModel potion, bool renewablePotionShapedRock = false)
        => potion.Rarity == PotionRarity.Token
            || renewablePotionShapedRock && potion is PotionShapedRock
                ? 0
                : SolverWeights.PotionMinimumHpSaved;

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
