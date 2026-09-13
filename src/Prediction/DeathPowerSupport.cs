using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class DeathPowerSupport
{
    /// <summary>
    /// 这个死亡效果会不会往场上放一个新的<b>主要</b>敌人。
    /// </summary>
    /// <remarks>
    /// 补货、寄生和惊吓都会生成主要敌人，结算完成前应保留战斗。
    /// 补货在 AfterDeath 镜像内、旧个体移出阵容前生成替补；寄生与惊吓仍由
    /// <see cref="CorePowerSupport.ApplyEnemyDeathPowers" /> 的领域清理结算。
    ///
    /// 幻象和重接不在这里：它们复活的是同一个个体，走的是
    /// <c>SimulatedCombatState.RevivingEnemyHp</c> 那条既有的有效生命路径。
    ///
    /// 新增会生成主要敌人的死亡效果时，应同时登记这里的终局约束和对应权威结算入口。
    /// </remarks>
    public static bool SpawnsPrimaryEnemyOnDeath(PowerModel power)
        => power.Amount > 0 && power is StockPower or InfestedPower or SurprisePower;

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
