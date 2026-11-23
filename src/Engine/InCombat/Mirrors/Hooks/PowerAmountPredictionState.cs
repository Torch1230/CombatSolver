using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks;

// Shadow amount shared by hook mirrors that consume an existing live power without mutating it.
internal sealed class PowerAmountPredictionState(int amount) : IPredictionStateForkable
{
    public int Amount { get; set; } = amount;

    public bool IsActive => Amount > 0;

    public void Decrement()
    {
        Decrease(1);
    }

    public void Decrease(int amount)
    {
        Amount = Math.Max(0, Amount - amount);
    }

    public void Consume()
    {
        Amount = 0;
    }

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal static class PredictionStateStorePowerAmountExtensions
{
    public static PowerAmountPredictionState GetPowerAmount(
        this PredictionStateStore store,
        PowerModel power)
    {
        return store.Get(power, static value => new PowerAmountPredictionState(value.Amount));
    }

    public static PowerAmountPredictionState PeekPowerAmount(
        this PredictionStateStore store,
        PowerModel power)
    {
        return store.Peek(power, static value => new PowerAmountPredictionState(value.Amount));
    }
}
