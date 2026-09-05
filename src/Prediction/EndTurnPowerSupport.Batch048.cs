using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class EndTurnPowerSupport
{
    private static bool TriggerBatch048(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CombatSide side,
        IReadOnlySet<Creature> participants)
    {
        foreach (PowerModel power in combat.EffectivePowers().ToArray())
        {
            if (power.Amount <= 0)
                continue;

            Creature owner = power.Owner;
            bool ownerParticipates = participants.Contains(owner);
            switch (power)
            {
                case DebilitatePower when ownerParticipates:
                    combat.SetPowerAmount(power, power.Amount - 1);
                    break;
                case HellraiserPower hellraiser when ownerParticipates:
                    combat.ResetHellraiserTurn(simulator, hellraiser);
                    break;
                case MagicBombPower when ownerParticipates
                    && power.Applier is { } bombApplier
                    && simulator.State.GetCreature(bombApplier).IsAlive:
                    using (simulator.PushDamageSource(
                        CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(MagicBombPower))))
                    {
                        simulator.Damage(owner, power.Amount, ValueProp.Unpowered, owner);
                    }
                    if (simulator.HasPendingChoice)
                        return false;
                    combat.SetPowerAmount(power, 0);
                    break;
                case MonologuePower monologue when ownerParticipates:
                    int strengthApplied = monologue.DynamicVars[MonologuePower.strengthAppliedKey].IntValue;
                    if (strengthApplied != 0)
                        combat.Apply<StrengthPower>(owner, -strengthApplied, owner);
                    combat.SetPowerAmount(power, 0);
                    break;
                case OblivionPower when side == CombatSide.Player:
                    combat.SetPowerAmount(power, 0);
                    break;
                case PanachePower panache when ownerParticipates:
                    combat.ResetPanacheTurn(simulator, panache);
                    break;
                case SicEmPower when ownerParticipates:
                    combat.SetPowerAmount(power, 0);
                    break;
                case SkittishPower skittish when side != owner.Side:
                    combat.ResetSkittishTurn(simulator, skittish);
                    break;
                case StranglePower when ownerParticipates:
                    combat.SetPowerAmount(power, 0);
                    break;
                case UnderworldPower when side == CombatSide.Enemy:
                    combat.SetPowerAmount(power, 0);
                    break;
            }
            if (simulator.HasPendingChoice)
                return false;
        }
        return true;
    }
}
