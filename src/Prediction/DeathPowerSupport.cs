using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class DeathPowerSupport
{
    public static bool Trigger(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature dead)
    {
        foreach (PowerModel power in combat.EffectivePowers().ToArray())
        {
            if (power.Amount <= 0)
                continue;

            if (power is RavenousPower
                && !ReferenceEquals(power.Owner, dead)
                && power.Owner.Side == dead.Side
                && simulator.State.GetCreature(power.Owner).IsAlive)
            {
                combat.ForceStunnedMove(power.Owner);
                combat.Apply<StrengthPower>(power.Owner, power.Amount, power.Owner);
                continue;
            }

            if (power is CrabRagePower
                && !ReferenceEquals(power.Owner, dead)
                && power.Owner.Side == dead.Side)
            {
                combat.Apply<StrengthPower>(
                    power.Owner,
                    power.DynamicVars.Strength.IntValue,
                    power.Owner);
                simulator.GainBlock(
                    power.Owner,
                    power.DynamicVars.Block.BaseValue,
                    ValueProp.Unpowered);
                if (simulator.HasPendingChoice)
                    return false;
                combat.SetPowerAmount(power, 0);
                continue;
            }

            if (power is DampenPower)
            {
                combat.RemoveDampenCaster(dead);
                continue;
            }

            if (power is SurroundedPower
                && dead.Side != power.Owner.Side
                && power.Owner.Player is { } surroundedPlayer)
            {
                Creature[] remaining = combat.Enemies
                    .Where(simulator.State.IsHittable)
                    .ToArray();
                if (remaining.Length > 0
                    && (remaining.All(enemy => combat.GetAmount<BackAttackLeftPower>(enemy) > 0)
                        || remaining.All(enemy => combat.GetAmount<BackAttackRightPower>(enemy) > 0)))
                {
                    PowerLifecycleSupport.UpdateSurroundedForTarget(
                        simulator, combat, surroundedPlayer, remaining[0]);
                }
                continue;
            }

            if (!ReferenceEquals(power.Owner, dead))
                continue;

            switch (power)
            {
                case AdaptablePower:
                    combat.BeginAdaptableRevive(dead);
                    break;
                case IllusionPower:
                    combat.BeginIllusionRevive(dead);
                    break;
                case InfestedPower:
                    for (int index = 0; index < 4; index++)
                    {
                        int slotIndex = index + 1;
                        MonsterSpawnSupport.Spawn<Wriggler>(
                            simulator,
                            combat,
                            dead,
                            $"wriggler{slotIndex}",
                            configure: wriggler => wriggler.StartStunned = true);
                    }
                    break;
                case ReattachPower:
                    combat.BeginReattach(simulator, dead);
                    break;
                case StockPower stock when stock.Amount > 0:
                    MonsterSpawnSupport.Spawn<Axebot>(
                        simulator,
                        combat,
                        dead,
                        dead.SlotName,
                        configure: axebot =>
                        {
                            axebot.ShouldPlaySpawnAnimation = true;
                            axebot.StockAmount = stock.Amount - 1;
                        });
                    break;
                case SurprisePower:
                    Creature fat = MonsterSpawnSupport.Create<FatGremlin>(simulator, combat, "fat");
                    foreach (ThieveryPower thievery in combat.EffectivePowers()
                                 .OfType<ThieveryPower>()
                                 .Where(candidate => candidate.Owner == dead && candidate.Amount > 0)
                                 .ToArray())
                    {
                        HeistPower heist = combat.AddPowerInstance<HeistPower>(
                            fat,
                            thievery.DynamicVars.Gold.IntValue,
                            dead);
                        heist._target = thievery.Target;
                    }
                    MonsterSpawnSupport.Spawn<SneakyGremlin>(simulator, combat, dead, "sneaky");
                    MonsterSpawnSupport.AddCreated(simulator, combat, dead, fat);
                    break;
                case PossessSpeedPower or PossessStrengthPower:
                    combat.RefundPossessedStats(dead);
                    break;
            }
            if (simulator.HasPendingChoice)
                return false;
        }
        combat.RecoverStolenResources(simulator, dead);
        if (simulator.HasPendingChoice)
            return false;
        combat.RemovePowersAfterDeath(dead);
        combat.CompleteDeathPhase(dead);
        return true;
    }

}
