using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;

internal static class AimPowerMirrors
{
    internal static void CaptureRoot(CombatPredictionSimulator simulator, AimPower target, AimPower source)
    {
        var command = source.GetInternalData<AimPower.Data>().commandToModify;
        _ = simulator.StateStore.GetReadOnly(target, () => new State
        {
            HasLatchedCommand = command is not null,
            SourceIdentity = command?.ModelSource as CardModel
        });
    }

    internal static void BeforeAttack(AimPower power, BeforeAttackMirrorContext c)
    {
        var command = c.Command;
        if (command.Attacker != power.Owner || !command.DamageProps.IsPoweredAttack()
            || !command.DamageProps.HasFlag(ValueProp.FromArrow) || command.ModelSource is not (null or CardModel)) return;
        var state = c.StateStore.Get<State>(power);
        if (state.HasLatchedCommand) return;
        state.HasLatchedCommand = true;
        state.SourceIdentity = command.ModelSource as CardModel;
        state.ActiveCommand = command;
        state.StartingAmount = power.Amount;
    }

    internal static decimal ModifyDamageAdditive(AimPower power, ModifyDamageMirrorContext c)
    {
        if (c.Dealer != power.Owner || !c.Props.IsPoweredAttack() || !c.Props.HasFlag(ValueProp.FromArrow)) return 0;
        var state = c.StateStore.Get<State>(power);
        if (state.HasLatchedCommand && c.CardSource is not null && !c.CardSource.References(state.SourceIdentity)) return 0;
        return c.CardSource?.Preview is HeavyShot heavy ? heavy.ModifyAimAmount(power.Amount) : power.Amount;
    }

    internal static void AfterAttack(AimPower power, AfterAttackMirrorContext c) => CompleteOrAbort(power, c, true);

    internal static void CompleteOrAbort(AimPower power, AfterAttackMirrorContext c, bool completed)
    {
        var state = c.StateStore.Get<State>(power);
        if (state.ActiveCommand != c.Command) return;
        state.ActiveCommand = null;
        if (completed) ((SimulatedCombatState)c.CombatState).SetPowerAmount(power, power.Amount - state.StartingAmount);
        else { state.HasLatchedCommand = false; state.SourceIdentity = null; }
        state.StartingAmount = 0;
    }

    internal static string Describe(AimPower power, CombatPredictionSimulator? simulator)
    {
        bool latched;
        CardModel? source;
        if (simulator is null)
        {
            var command = power.GetInternalData<AimPower.Data>().commandToModify;
            latched = command is not null;
            source = command?.ModelSource as CardModel;
        }
        else
        {
            var state = simulator.StateStore.Peek(power, static () => new State());
            state.AssertForkable();
            latched = state.HasLatchedCommand;
            source = state.SourceIdentity;
        }
        if (!latched) return "ready";
        if (source is null) return "latched:null";
        int index = simulator is null
            ? Array.IndexOf(power.Owner.Player!.PlayerCombatState!.AllCards.ToArray(), source)
            : Array.FindIndex(simulator.State.GetPlayerCombatState(power.Owner.Player!).AllCards.ToArray(), card => card.References(source));
        return $"latched:{index}";
    }

    private sealed class State : IPredictionStateForkable, IPredictionForkBoundary
    {
        public bool HasLatchedCommand;
        public CardModel? SourceIdentity;
        public AttackCommand? ActiveCommand;
        public int StartingAmount;
        public void AssertForkable()
        {
            if (ActiveCommand is not null) throw new InvalidOperationException("Cannot fork during Aim attack resolution.");
        }
        public object Fork(PredictionForkContext context) { AssertForkable(); return MemberwiseClone(); }
    }
}
