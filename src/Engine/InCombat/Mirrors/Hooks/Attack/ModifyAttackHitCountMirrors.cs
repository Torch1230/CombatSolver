using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;

using Registry = MethodMirrorRegistry<AbstractModel, ModifyAttackHitCountMirrorContext, int>;

// Mirrors Hook.ModifyAttackHitCount while preserving listener-to-listener result chaining.
internal static class ModifyAttackHitCountMirrors
{
    private static readonly MirrorMethodSpec ModifyAttackHitCount = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ModifyAttackHitCount),
        [typeof(AttackCommand), typeof(int)]);

    private static readonly Registry Registry = new(ModifyAttackHitCount);

    static ModifyAttackHitCountMirrors()
        => Registry.Register<Thumbscrew>((relic, c) => relic.ModifyAttackHitCount(c.Command, c.HitCount));

    public static int Invoke(AbstractModel listener, ModifyAttackHitCountMirrorContext context)
    {
        return Registry.Invoke(listener, context, context.HitCount).Value;
    }
}

internal sealed class ModifyAttackHitCountMirrorContext : CombatMirrorContext
{
    public required AttackCommand Command { get; init; }

    public required int HitCount { get; set; }
}
