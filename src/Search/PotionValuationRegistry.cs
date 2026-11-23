using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// Single-source-of-truth potion valuation adapter delegating directly to <see cref="HotPotionCatalog"/>
/// and per-potion-ID <see cref="PotionHpThresholds"/> overrides (falling back to built-in catalog defaults
/// 0 / 9 / 14 / 18 and Ambergris max(18, ceil(40% MaxHp))).
/// </summary>
internal static class PotionValuationRegistry
{
    internal static bool IsCombatUsable(PotionModel potion)
        => IsCombatUsable(potion.Id.Entry);

    internal static bool IsCombatUsable(string? potionEntry)
    {
        if (string.IsNullOrEmpty(potionEntry))
            return false;
        HotCustomPotionId id = HotPotionCatalog.FindByEntry(potionEntry);
        return id != HotCustomPotionId.None && HotPotionCatalog.Get(id).IsCombatUsable;
    }

    internal static int DefaultStrategicHpCost(
        PotionModel potion,
        bool renewablePotionShapedRock = false,
        int playerMaxHp = 0)
    {
        if (potion.Rarity == PotionRarity.Token)
            return 0;

        HotCustomPotionId id = HotPotionCatalog.FindByEntry(potion.Id.Entry);
        if (renewablePotionShapedRock && id == HotCustomPotionId.PotionShapedRock)
            return 0;
        if (id == HotCustomPotionId.Ambergris)
            return PotionHpThresholds.DefaultAmbergrisCost(playerMaxHp);
        if (id != HotCustomPotionId.None)
            return HotPotionCatalog.Get(id).BaseStrategicHpCost;
        return PotionHpThresholds.DefaultStandard;
    }

    internal static int StrategicHpCost(
        PotionModel potion,
        bool renewablePotionShapedRock = false,
        PotionHpThresholds? thresholds = null,
        int playerMaxHp = 0)
    {
        PotionHpThresholds effective = thresholds ?? PotionHpThresholds.Default;
        if (effective.TryGetOverride(potion.Id.Entry, out int overrideHp))
            return overrideHp;
        return DefaultStrategicHpCost(potion, renewablePotionShapedRock, playerMaxHp);
    }
}
