using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;

internal static class GigantificationPowerMirrors
{
    public static void BeforeAttack(GigantificationPower power, BeforeAttackMirrorContext context)
    {
        if (context.StateStore.GetPowerAmount(power).IsActive && ShouldTrigger(power, context.Command))
        {
            GetState(power, context).CommandToModify ??= context.Command;
        }
    }

    public static void AfterAttack(GigantificationPower power, AfterAttackMirrorContext context)
        => CompleteOrAbort(power, context, completed: true);

    public static void CompleteOrAbort(
        GigantificationPower power,
        AfterAttackMirrorContext context,
        bool completed)
    {
        var state = GetState(power, context);
        if (context.Command == state.CommandToModify)
        {
            state.CommandToModify = null;
            if (completed)
                context.StateStore.GetPowerAmount(power).Decrement();
        }
    }

    public static decimal ModifyDamageMultiplicative(
        GigantificationPower power,
        ModifyDamageMirrorContext context)
    {
        if (!context.StateStore.GetPowerAmount(power).IsActive ||
            context.CardSource is null ||
            context.CardSource.Preview.Owner.Creature != power.Owner ||
            !context.Props.IsPoweredAttack())
        {
            return 1;
        }

        var commandToModify = GetState(power, context).CommandToModify;
        return commandToModify is null || context.CardSource.References(commandToModify.ModelSource) ? 3 : 1;
    }

    private static State GetState(GigantificationPower power, CombatMirrorContext context)
    {
        return context.StateStore.Get<State>(power);
    }

    private static bool ShouldTrigger(GigantificationPower power, AttackCommand command)
    {
        return command.ModelSource is CardModel card &&
            card.Owner.Creature == power.Owner &&
            card.Type == CardType.Attack &&
            command.DamageProps.IsPoweredAttack();
    }

    private sealed class State : IPredictionStateForkable, IPredictionForkBoundary
    {
        public AttackCommand? CommandToModify { get; set; }

        public object Fork(PredictionForkContext context)
        {
            AssertForkable();
            return MemberwiseClone();
        }

        public void AssertForkable()
        {
            if (CommandToModify is not null)
                throw new InvalidOperationException("Cannot fork during Gigantification attack resolution.");
        }
    }
}
