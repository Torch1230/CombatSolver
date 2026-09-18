using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 铁甲战士开局能力投影（Draft）。只读取真实牌区、敌人、Power 与冻结意图；
/// 数值是有界潜力，只用于承诺保路，不进入终局比较。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private int IroncladPowerOpeningProjectionPotential(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int turns = Math.Max(0, PowerRemainingTurns(child) - 1);
        int incoming = PowerIncomingDamage(child);
        int attacks = PowerCountType(child, CardType.Attack);
        int enemyCount = Math.Max(1, child.Snapshot.AliveEnemyCount);
        return cardId switch
        {
            "AGGRESSION" => PowerPerTurnResourcePotential(
                Math.Max(1, PowerMaxAttackDamage(child)),
                turns),
            "BARRICADE" => PowerPerTurnBlockPotential(
                child,
                Math.Max(1, PowerMaxBlockValue(child)),
                turns,
                incoming),
            "CORRUPTION" => PowerPerTriggerResourcePotential(
                Math.Max(0, PowerCountType(child, CardType.Skill)),
                Math.Max(1, PowerEnergyUnit(child))),
            "CRIMSON_MANTLE" => PowerPerTurnBlockPotential(
                child,
                PowerAmountGain<CrimsonMantlePower>(parent, child),
                turns,
                incoming),
            "CRUELTY" => PowerPerTriggerDamagePotential(
                child,
                PowerAmountGain<CrueltyPower>(parent, child),
                PowerTotalAttackDamage(child) / 100),
            "DARK_EMBRACE" => PowerPerTriggerResourcePotential(
                PowerAmountGain<DarkEmbracePower>(parent, child) * Math.Max(1, attacks),
                PowerCountWithExhaust(child)),
            "DEMON_FORM" => PowerGrowthFrontierPotential(
                child,
                damagePerAttack: PowerAmountGain<DemonFormPower>(parent, child)),
            "FEEL_NO_PAIN" => PowerPerTriggerBlockPotential(
                child,
                PowerAmountGain<FeelNoPainPower>(parent, child),
                PowerCountWithExhaust(child),
                incoming),
            "HELLRAISER" => PowerPerTurnDamagePotential(
                child,
                Math.Max(1, PowerMaxAttackDamage(child)),
                turns,
                1),
            "INFERNO" => PowerPerTurnDamagePotential(
                child,
                PowerAmountGain<InfernoPower>(parent, child),
                turns,
                enemyCount),
            "INFLAME" => PowerGrowthFrontierPotential(
                child,
                damagePerAttack: PowerAmountGain<StrengthPower>(parent, child)),
            "JUGGERNAUT" => PowerPerTriggerDamagePotential(
                child,
                PowerAmountGain<JuggernautPower>(parent, child),
                Math.Max(1, PowerCountWithBlockVar(child))),
            "JUGGLING" => PowerPerTurnResourcePotential(
                Math.Max(1, PowerMaxAttackDamage(child)),
                turns),
            "PYRE" => PowerPerTurnResourcePotential(
                PowerAmountGain<PyrePower>(parent, child),
                turns),
            "RUPTURE" => PowerGrowthFrontierPotential(
                child,
                damagePerAttack: SaturatingProduct(
                    PowerAmountGain<RupturePower>(parent, child),
                    Math.Max(1, PowerCountWithSelfDamage(child)))),
            "STAMPEDE" => PowerPerTurnDamagePotential(
                child,
                Math.Max(1, PowerMaxAttackDamage(child)),
                turns,
                1),
            "STONE_ARMOR" => PowerPerTurnBlockPotential(
                child,
                PowerAmountGain<PlatingPower>(parent, child),
                turns,
                incoming),
            "UNMOVABLE" => PowerPerTurnBlockPotential(
                child,
                Math.Max(1, PowerMaxBlockValue(child)),
                1,
                incoming),
            "VICIOUS" => PowerPerTriggerResourcePotential(
                PowerAmountGain<ViciousPower>(parent, child)
                    * Math.Max(1, PowerEnergyUnit(child)),
                PowerCardProjectionMath.ViciousDrawTriggers(PowerCountWithVulnerable(child))),
            _ => 0,
        };
    }
}


