using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 鏁呴殰鏈哄櫒浜哄紑灞€鑳藉姏鎶曞奖锛圖raft锛夈€傜悆浣嶃€侀泦涓笌鍏呰兘鏉ユ簮閮借鍙栫湡瀹炴ā鎷熺姸鎬侊紱
/// 澶嶆潅鐞冩満鍒剁殑杩滄湡鍊间娇鐢ㄦ湁鐣屼繚瀹堜唬鐞嗭紝浠呯敤浜庤矾绾夸繚娲汇€?/// </summary>
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
            "BUFFER" => PowerPerTriggerBlockPotential(
                child,
                1,
                SaturatingProduct(
                    PowerAmountGain<BufferPower>(parent, child),
                    Math.Max(1, incoming)),
                incoming),
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
}

