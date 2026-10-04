using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Resources;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal static class GoldGainSupport
{
    internal static decimal ModifyGoldGained(CombatPredictionSimulator simulator,
        SimulatedCombatState combat, Player player, decimal amount)
    {
        IReadOnlyList<AbstractModel> listeners = ((ICombatPredictionHookListenerSource)combat).RunHookListeners;
        List<AbstractModel>? modifiers = null;
        var context = new GoldGainMirrorContext { Simulator = simulator, Player = player, Amount = amount };
        foreach (AbstractModel listener in listeners)
        {
            decimal before = context.Amount;
            context.Amount = GoldGainedMirrors.Modify(listener, context);
            if ((int)before != (int)context.Amount)
                (modifiers ??= []).Add(listener);
        }
        if (modifiers is not null)
        {
            // Native code re-enumerates the listeners and dispatches only models
            // whose integer amount changed, preserving original listener order.
            foreach (AbstractModel listener in ((ICombatPredictionHookListenerSource)combat).RunHookListeners)
                if (modifiers.Contains(listener))
                    GoldGainedMirrors.AfterModify(listener, context);
        }
        return context.Amount;
    }

    internal static void AfterGoldGained(CombatPredictionSimulator simulator,
        SimulatedCombatState combat, Player player)
    {
        var context = new GoldGainMirrorContext { Simulator = simulator, Player = player, Amount = 0m };
        foreach (AbstractModel listener in combat.GoldAfterGainHookListeners(simulator))
            GoldGainedMirrors.AfterGain(listener, context);
    }
}
