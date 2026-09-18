using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 故障机器人开局能力投影（Draft）。球位、集中与充能来源都读取真实模拟状态；
/// 复杂球机制的远期值使用有界保守代理，仅用于路线保活。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private int DefectPowerOpeningProjectionPotential(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int turns = Math.Max(0, PowerRemainingTurns(child) - 1);
        int incoming = PowerIncomingDamage(child);
        int orbs = PowerOrbCount(child);
        int enemyCount = Math.Max(1, child.Snapshot.AliveEnemyCount);
        int statusSources = PowerCountWithStatusVar(child);
        int powerSources = PowerCountType(child, CardType.Power);
        return cardId switch
        {
            "BIASED_COGNITION" => PowerPerTurnDamagePotential(
                child,
                PowerAmountGain<FocusPower>(parent, child),
                orbs,
                1),
            "BUFFER" => BufferPotential(parent, child, incoming),
            "BULK_UP" => PowerGrowthFrontierPotential(
                child,
                blockPerSkillBonus: PowerAmountGain<DexterityPower>(parent, child),
                damagePerAttack: PowerAmountGain<StrengthPower>(parent, child)),
            "CAPACITOR" => PowerPerTurnResourcePotential(
                Math.Max(0, PowerOrbCapacity(child) - PowerOrbCapacity(parent)),
                turns),
            "CONSUMING_SHADOW" => PowerPerTurnResourcePotential(
                Math.Max(1, orbs),
                turns),
            "COOLANT" => PowerPerTurnBlockPotential(
                child,
                SaturatingProduct(
                    PowerAmountGain<CoolantPower>(parent, child),
                    PowerDistinctOrbTypes(child)),
                turns,
                incoming),
            "CREATIVE_AI" => PowerPerTurnResourcePotential(
                PowerEnergyUnit(child),
                turns),
            "DEFRAGMENT" => PowerPerTurnDamagePotential(
                child,
                PowerAmountGain<FocusPower>(parent, child),
                orbs,
                1),
            "ECHO_FORM" => PowerPerTurnDamagePotential(
                child,
                Math.Max(1, PowerMaxAttackDamage(child)),
                turns,
                1),
            "FERAL" => PowerPerTurnResourcePotential(
                PowerEnergyUnit(child),
                turns),
            "HAILSTORM" => PowerPerTurnDamagePotential(
                child,
                PowerAmountGain<HailstormPower>(parent, child),
                turns,
                enemyCount),
            "ITERATION" => PowerPerTriggerResourcePotential(
                PowerAmountGain<IterationPower>(parent, child) * PowerEnergyUnit(child),
                statusSources),
            "LOOP" => PowerPerTurnResourcePotential(
                Math.Max(1, orbs),
                turns),
            "MACHINE_LEARNING" => PowerPerTurnResourcePotential(
                PowerEnergyUnit(child),
                turns),
            "SMOKESTACK" => PowerPerTriggerDamagePotential(
                child,
                PowerAmountGain<SmokestackPower>(parent, child),
                statusSources),
            "SPINNER" => PowerPerTurnResourcePotential(1, turns),
            "STORM" => PowerPerTriggerDamagePotential(
                child,
                PowerEnergyUnit(child),
                powerSources),
            "SUBROUTINE" => PowerPerTriggerResourcePotential(
                PowerAmountGain<SubroutinePower>(parent, child) * PowerEnergyUnit(child),
                powerSources),
            "THUNDER" => PowerPerTriggerDamagePotential(
                child,
                PowerAmountGain<ThunderPower>(parent, child),
                PowerOrbCount<LightningOrb>(child)),
            "TRASH_TO_TREASURE" => PowerPerTriggerResourcePotential(
                PowerEnergyUnit(child),
                statusSources),
            _ => 0,
        };
    }

    /// <summary>缓冲只阻止若干次生命损失，按平均单次伤害计；多段小伤害不按总伤害整体抵消。</summary>
    private int BufferPotential(SearchNode parent, SearchNode child, int incoming)
    {
        int charges = PowerAmountGain<BufferPower>(parent, child);
        int raw = PowerCardProjectionMath.BufferPrevention(
            charges,
            incoming,
            PowerForecastIncomingHits(child, 1));
        return BoundedPrevention(raw, incoming);
    }
}

