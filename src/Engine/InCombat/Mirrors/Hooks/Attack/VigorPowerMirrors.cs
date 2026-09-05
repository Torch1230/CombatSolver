using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;

internal static class VigorPowerMirrors
{
    public static void BeforeAttack(VigorPower power, BeforeAttackMirrorContext context)
    {
        if (power.Amount > 0 && ShouldTrigger(power, context.Command))
        {
            var state = GetState(power, context);
            if (state.CommandToModify is null)
            {
                state.CommandToModify = context.Command;
                state.AmountWhenAttackStarted = power.Amount;
            }
        }
    }

    public static void AfterAttack(VigorPower power, AfterAttackMirrorContext context)
        => CompleteOrAbort(power, context, completed: true);

    public static void CompleteOrAbort(
        VigorPower power,
        AfterAttackMirrorContext context,
        bool completed)
    {
        var state = GetState(power, context);
        if (context.Command == state.CommandToModify)
        {
            if (completed)
            {
                if (context.CombatState is not ICombatPredictionEffectSink effects)
                    throw new InvalidOperationException("活力攻击结算缺少可写预测状态。");
                effects.SetPowerAmount(power, Math.Max(0, power.Amount - state.AmountWhenAttackStarted));
            }
            state.CommandToModify = null;
            state.AmountWhenAttackStarted = 0;
        }
    }

    public static decimal ModifyDamageAdditive(VigorPower power, ModifyDamageMirrorContext context)
    {
        if (power.Amount <= 0 || context.Dealer != power.Owner || !context.Props.IsPoweredAttack())
        {
            return 0;
        }

        var commandToModify = GetState(power, context).CommandToModify;
        if (commandToModify is not null &&
            context.CardSource is not null &&
            !context.CardSource.References(commandToModify.ModelSource))
        {
            return 0;
        }

        if (commandToModify is not null && commandToModify.Attacker != context.Dealer)
        {
            return 0;
        }

        return power.Amount;
    }

    private static State GetState(VigorPower power, CombatMirrorContext context)
    {
        return context.StateStore.Get<State>(power);
    }

    private static bool ShouldTrigger(VigorPower power, AttackCommand command)
    {
        return command.Attacker == power.Owner &&
            command.DamageProps.IsPoweredAttack() &&
            command.ModelSource is null or CardModel;
    }

    private sealed class State : IPredictionStateForkable, IPredictionForkBoundary
    {
        public AttackCommand? CommandToModify { get; set; }

        public int AmountWhenAttackStarted { get; set; }

        public object Fork(PredictionForkContext context)
        {
            AssertForkable();
            return MemberwiseClone();
        }

        public void AssertForkable()
        {
            if (CommandToModify is not null || AmountWhenAttackStarted != 0)
                throw new InvalidOperationException("Cannot fork during Vigor attack resolution.");
        }
    }
}
