using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    internal void SynchronizePowerAmountPredictionStates()
    {
        if (State.CombatState is not ICombatPredictionEffectSink effects
            || !StateStore.HasEntries<PowerAmountPredictionState>())
            return;
        (AbstractModel Model, PowerAmountPredictionState State)[] pending =
            StateStore.ReadEntries<PowerAmountPredictionState>().ToArray();
        foreach ((AbstractModel model, PowerAmountPredictionState amount) in pending)
        {
            if (model is PowerModel power && power.Amount != amount.Amount)
                effects.SetPowerAmount(power, amount.Amount);
            StateStore.Remove<PowerAmountPredictionState>(model);
        }
    }
}
