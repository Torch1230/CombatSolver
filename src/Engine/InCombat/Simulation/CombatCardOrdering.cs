using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace CombatSolver.Engine.InCombat.Simulation;

internal static class CombatCardOrdering
{
    internal static List<PredictedCard> StableShuffle(this List<PredictedCard> cards, Rng rng,
        CombatPredictionSimulator simulator)
    {
        cards.Sort((left, right) => Compare(simulator, left, right));
        return cards.UnstableShuffle(rng);
    }

    // CardModel.CompareTo uses full combat cost after ID, upgrade, enchantment and affliction.
    // Resolve that cost through the same branch hooks used by legal card plays.
    internal static int Compare(CombatPredictionSimulator simulator, PredictedCard left, PredictedCard right)
    {
        if (ReferenceEquals(left, right)) return 0;
        CardModel a = left.Preview, b = right.Preview;
        int result = a.Id.CompareTo(b.Id);
        if (result != 0) return result;
        result = a.CurrentUpgradeLevel.CompareTo(b.CurrentUpgradeLevel);
        if (result != 0) return result;
        result = ModelId.Compare(a.Enchantment?.Id, b.Enchantment?.Id);
        if (result != 0) return result;
        result = ModelId.Compare(a.Affliction?.Id, b.Affliction?.Id);
        if (result != 0) return result;
        int leftCost = a.EnergyCost.CostsX ? a.EnergyCost.CapturedXValue : Math.Max(0, left.GetEnergyCostValueWithModifiers(simulator));
        int rightCost = b.EnergyCost.CostsX ? b.EnergyCost.CapturedXValue : Math.Max(0, right.GetEnergyCostValueWithModifiers(simulator));
        result = leftCost.CompareTo(rightCost);
        if (result != 0) return result;
        result = a.BaseReplayCount.CompareTo(b.BaseReplayCount);
        if (result != 0) return result;
        foreach (string name in a.DynamicVars.Keys.Order(StringComparer.Ordinal))
        {
            if (!b.DynamicVars.TryGetValue(name, out var value)) continue;
            result = a.DynamicVars[name].BaseValue.CompareTo(value.BaseValue);
            if (result != 0) return result;
        }
        return 0;
    }
}
