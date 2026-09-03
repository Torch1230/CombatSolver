using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Resources;

using Registry = MethodMirrorRegistry<AbstractModel, AfterStarsGainedMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterStarsGained.
internal static class AfterStarsGainedMirrors
{
    private static readonly MirrorMethodSpec AfterStarsGained = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterStarsGained),
        [typeof(int), typeof(Player)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterStarsGainedMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterStarsGained);

        registry.Register<BlackHolePower>(HandleBlackHolePower);

        return registry;
    }

    private static void HandleBlackHolePower(
        BlackHolePower power,
        AfterStarsGainedMirrorContext context)
    {
        if (context.Amount > 0 && context.Gainer == power.Owner.Player)
        {
            context.Simulator.Damage(
                context.State.HittableEnemies,
                power.Amount,
                ValueProp.Unpowered,
                power.Owner);
        }
    }
}

internal sealed class AfterStarsGainedMirrorContext : CombatMirrorContext
{
    public required int Amount { get; init; }

    public required Player Gainer { get; init; }
}
