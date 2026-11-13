using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors;

internal static partial class HookMirrors
{
    internal static void AfterCreatureAddedToCombat(CombatPredictionSimulator simulator, Creature creature)
    {
        AfterCreatureAddedMirrors.Seal();
        List<AbstractModel> listeners = [];
        foreach (AbstractModel listener in IterateCombatHookListeners(simulator, MirroredHookMask.AdditionalCallbacks))
            listeners.Add(listener);
        var context = new AfterCreatureAddedMirrorContext { Simulator = simulator, Creature = creature };
        foreach (AbstractModel listener in listeners)
        {
            AfterCreatureAddedMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
            {
                simulator.RejectExecutionContinuation();
                return;
            }
        }
    }
}
