using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Modding;

namespace CombatSolver;

internal static class PowerDynamicVarWarmup
{
    private static bool _canonicalPowersMaterialized;

    public static void EnsureMaterialized(CombatState state)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("Power dynamic variables must be materialized on the main thread.");

        if (!_canonicalPowersMaterialized)
        {
            EnsureCanonicalMaterialized(ModelDb.AllPowers);
            _canonicalPowersMaterialized = true;
        }

        foreach (PowerModel power in state.Creatures.SelectMany(creature => creature.Powers))
            _ = power.DynamicVars;
    }

    internal static void EnsureCanonicalMaterialized(IEnumerable<PowerModel> powers)
    {
        foreach (PowerModel power in powers)
        {
            _ = AssemblyInfo.ModForType(power.GetType(), out bool isBaseGame);
            if (isBaseGame) _ = power.DynamicVars;
        }
    }
}
