using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;

internal static class QuietusPowerMirrors
{
    internal static void BeforeAttack(QuietusPower power, BeforeAttackMirrorContext context)
    {
        AttackCommand command = context.Command;
        if (command.Attacker == power.Owner && command.DamageProps.IsPoweredAttack()
            && command.DamageProps.HasFlag(ValueProp.FromArrow) && command.ModelSource is null or CardModel)
            context.StateStore.Get<State>(power).Command ??= command;
    }

    internal static decimal ModifyDamageMultiplicative(QuietusPower power, ModifyDamageMirrorContext context)
    {
        if (context.Dealer != power.Owner || !context.Props.IsPoweredAttack()
            || !context.Props.HasFlag(ValueProp.FromArrow)) return 1;
        AttackCommand? command = context.StateStore.Get<State>(power).Command;
        return command is not null && context.CardSource is not null
            && !context.CardSource.References(command.ModelSource) ? 1 : power.Amount;
    }

    internal static void AfterAttack(QuietusPower power, AfterAttackMirrorContext context)
        => CompleteOrAbort(power, context, completed: true);

    internal static void CompleteOrAbort(QuietusPower power, AfterAttackMirrorContext context, bool completed)
    {
        State state = context.StateStore.Get<State>(power);
        if (state.Command != context.Command) return;
        state.Command = null;
        if (completed) ((SimulatedCombatState)context.CombatState).SetPowerAmount(power, 0);
    }

    private sealed class State : IPredictionStateForkable, IPredictionForkBoundary
    {
        internal AttackCommand? Command;
        public void AssertForkable()
        {
            if (Command is not null) throw new InvalidOperationException("Cannot fork during Quietus attack resolution.");
        }
        public object Fork(PredictionForkContext context)
        {
            AssertForkable();
            return new State();
        }
    }
}
