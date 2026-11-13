using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    public int AvailableArrows(Player player) => State.GetPlayerCombatState(player).Arrows.Count(arrow => arrow.IsAvailable);

    public ImpaledPower? ImpaledBy(Creature target, Player player)
        => ((SimulatedCombatState)State.CombatState).EffectivePowers().OfType<ImpaledPower>()
            .FirstOrDefault(power => power.Amount > 0 && power.Owner == target && power.Applier == player.Creature);

    public ArrowSlot? ShootArrow(Player player, PredictedCard? source)
    {
        ArrowSlot? arrow = State.GetPlayerCombatState(player).Arrows.LastOrDefault(arrow => arrow.IsAvailable);
        if (arrow is not null)
        {
            arrow.Status = ArrowStatus.Away;
            History.RecordArrowShot(player, State.GetPlayerCombatState(player).Arrows.ToList().IndexOf(arrow));
        }
        ArrowMirrors.AfterShot(new() { Simulator = this, Player = player, Arrow = arrow, Source = source });
        return arrow;
    }

    public bool ShootArrowAt(Creature target, Player player, PredictedCard source, AttackCommand attack)
    {
        if (State.GetCreature(target).IsDead) return false;
        ArrowSlot? arrow = ShootArrow(player, source);
        if (HasPendingChoice || arrow is null) return false;
        if (State.GetCreature(target).IsDead) return true;
        attack.Simulate(this);
        if (HasPendingChoice) return false;
        ImpaleArrow(target, arrow, source, hookSource: null);
        return true;
    }

    public void ImpaleArrow(Creature target, ArrowSlot arrow, PredictedCard? source, PredictedCard? hookSource)
    {
        var combat = (SimulatedCombatState)State.CombatState;
        ImpaledPower? power = ImpaledBy(target, arrow.owner);
        if (power is null)
        {
            if (!IsEnding && State.GetCreature(target).IsAlive)
            {
                combat.ApplyPowerFromSource(typeof(ImpaledPower), target, 1, arrow.owner.Creature, source?.Preview);
                power = ImpaledBy(target, arrow.owner)
                    ?? throw new InvalidOperationException("Arrow power application did not create its target state.");
            }
            else
            {
                power = (ImpaledPower)ModelDb.Power<ImpaledPower>().ToMutable();
                power._owner = target;
                power._applier = arrow.owner.Creature;
            }
        }
        power.AddArrow(arrow);
        ArrowMirrors.AfterImpaled(new() { Simulator = this, Player = arrow.owner, Arrow = arrow,
            Impaled = power, Source = hookSource });
        if (HasPendingChoice) return;
        if (arrow.DestroyAfterImpaled)
        {
            if (arrow.Status != ArrowStatus.Destroyed && State.GetCreature(target).IsAlive)
            {
                power.GetInternalData<ImpaledPower.Data>().arrowSources.Remove(arrow);
                RemoveEmptyImpaled(power);
            }
            arrow.Status = ArrowStatus.Destroyed;
        }
        else if (State.GetCreature(target).IsDead && power.Arrows.Contains(arrow))
            DislodgeArrow(power, source);
    }

    public void DislodgeArrow(ImpaledPower power, PredictedCard? source)
    {
        ArrowSlot arrow = power.RemoveFirstArrow();
        if (arrow.DestroyAfterImpaled)
        {
            arrow.Status = ArrowStatus.Destroyed;
            return;
        }
        ArrowMirrors.AfterDislodged(new() { Simulator = this, Player = arrow.owner, Arrow = arrow,
            Impaled = power, Source = source });
        if (HasPendingChoice) return;
        if (arrow.Status is ArrowStatus.Retrieved or ArrowStatus.Destroyed)
            throw new InvalidOperationException($"Cannot retrieve an arrow in state {arrow.Status}.");
        arrow.Status = ArrowStatus.Retrieved;
        arrow.DestroyAfterImpaled = false;
    }

    public void DislodgeArrow(Creature target, Player player, PredictedCard? source)
    {
        if (ImpaledBy(target, player) is not { } power) return;
        DislodgeArrow(power, source);
        if (!HasPendingChoice) RemoveEmptyImpaled(power);
    }

    public void RemoveEmptyImpaled(ImpaledPower power)
    {
        if (power.Arrows.Count == 0 && ((SimulatedCombatState)State.CombatState).EffectivePowers().Contains(power))
            ((SimulatedCombatState)State.CombatState).SetPowerAmount(power, 0);
    }
}
