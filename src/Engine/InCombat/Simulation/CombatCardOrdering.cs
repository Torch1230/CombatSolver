using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace CombatSolver.Engine.InCombat.Simulation;

internal static class CombatCardOrdering
{
    internal static List<PredictedCard> StableShuffle(this List<PredictedCard> cards, Rng rng,
        CombatPredictionSimulator simulator)
    {
        Dictionary<DynamicVarSet, string[]>? orderedNames = null;
        cards.Sort((left, right) => Compare(simulator, left, right, ref orderedNames));
        return cards.UnstableShuffle(rng);
    }

    internal static int Compare(CombatPredictionSimulator simulator, PredictedCard left, PredictedCard right)
    {
        Dictionary<DynamicVarSet, string[]>? orderedNames = null;
        return Compare(simulator, left, right, ref orderedNames);
    }

    // CardModel.CompareTo uses full combat cost after ID, upgrade, enchantment and affliction.
    // Resolve that cost through the same branch hooks used by legal card plays.
    private static int Compare(CombatPredictionSimulator simulator, PredictedCard left, PredictedCard right,
        ref Dictionary<DynamicVarSet, string[]>? orderedNames)
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
        DynamicVarSet variables = a.DynamicVars;
        if (variables.Count == 0) return 0;
        if (variables.Count == 1)
        {
            foreach (var variable in variables)
                if (b.DynamicVars.TryGetValue(variable.Key, out var value))
                    return variable.Value.BaseValue.CompareTo(value.BaseValue);
            return 0;
        }

        // Only names are frozen within this one sort. Values and branch costs remain
        // live to each comparison, and no model or variable set survives the call.
        orderedNames ??= new(ReferenceEqualityComparer.Instance);
        if (!orderedNames.TryGetValue(variables, out string[]? names))
        {
            names = variables.Keys.Order(StringComparer.Ordinal).ToArray();
            orderedNames.Add(variables, names);
        }
        foreach (string name in names)
        {
            if (!b.DynamicVars.TryGetValue(name, out var value)) continue;
            result = a.DynamicVars[name].BaseValue.CompareTo(value.BaseValue);
            if (result != 0) return result;
        }
        return 0;
    }
}
