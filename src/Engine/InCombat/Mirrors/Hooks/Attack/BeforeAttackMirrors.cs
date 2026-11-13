using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;

using Registry = MethodMirrorRegistry<AbstractModel, BeforeAttackMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.BeforeAttack.
internal static class BeforeAttackMirrors
{
    private static readonly MirrorMethodSpec BeforeAttack = MirrorMethodSpec.Hook(
        nameof(AbstractModel.BeforeAttack),
        [typeof(AttackCommand)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, BeforeAttackMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(BeforeAttack);
        registry.Register<AimPower>(AimPowerMirrors.BeforeAttack);
        registry.Register<QuietusPower>(QuietusPowerMirrors.BeforeAttack);

        registry.Register<GigantificationPower>(GigantificationPowerMirrors.BeforeAttack);
        registry.RegisterIgnored<HellraiserPower>();
        registry.Register<VigorPower>(VigorPowerMirrors.BeforeAttack);

        return registry;
    }
}

internal sealed class BeforeAttackMirrorContext : CombatMirrorContext
{
    public required AttackCommand Command { get; init; }
}
