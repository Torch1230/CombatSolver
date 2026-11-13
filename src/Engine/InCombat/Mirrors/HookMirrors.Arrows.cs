using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors;

internal static partial class HookMirrors
{
    internal static void InvokeArrowHooks(MethodMirrorRegistry<AbstractModel, ArrowMirrorContext> registry,
        ArrowMirrorContext context)
    {
        foreach (AbstractModel listener in IterateCombatHookListeners(context.Simulator))
        {
            if (registry.Invoke(listener, context).Kind == MirrorDispatchKind.Unsupported)
                throw PredictionUnsupportedException.ForContent(
                    $"Arrow hook requires a mirror for {listener.GetType().FullName}.", listener.GetType());
            if (context.Simulator.HasPendingChoice) return;
        }
    }
}
