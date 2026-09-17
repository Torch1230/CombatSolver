using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>鏃犺壊鑳藉姏寮€灞€鎶曞奖锛圖raft锛夈€傛棤鑹茶兘鍔涘彲鍦ㄤ换鎰忚鑹叉寔鏈夛紝鎶曞奖鍙鐪熷疄鐗屽尯涓?Power銆?/summary>
internal sealed partial class CombatBeamSolver
{
    private int ColorlessPowerOpeningProjectionPotential(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int turns = Math.Max(0, PowerRemainingTurns(child) - 1);
        int incoming = PowerIncomingDamage(child);
        int enemyCount = Math.Max(1, child.Snapshot.AliveEnemyCount);
        int attacks = Math.Max(1, PowerCountType(child, CardType.Attack));
        return cardId switch
        {
            "AUTOMATION" => PowerPerTriggerResourcePotential(
                PowerAmountGain<AutomationPower>(parent, child) * PowerEnergyUnit(child),
                Math.Max(1, turns)),
            "CALAMITY" => PowerPerTriggerDamagePotential(
                child,
                Math.Max(1, PowerMaxAttackDamage(child)),
                attacks),
            "ENTROPY" => PowerPerTurnResourcePotential(
                PowerEnergyUnit(child),
                turns),
            "ETERNAL_ARMOR" => PowerPerTurnBlockPotential(
                child,
                PowerAmountGain<PlatingPower>(parent, child),
                turns,
                incoming),
            "FASTEN" => PowerGrowthFrontierPotential(
                child,
                blockPerSkillBonus: PowerAmountGain<FastenPower>(parent, child)),
            "MAYHEM" => PowerPerTurnResourcePotential(
                PowerEnergyUnit(child),
                turns),
            "NOSTALGIA" => PowerPerTurnResourcePotential(
                PowerEnergyUnit(child),
                turns),
            "PANACHE" => PowerPerTriggerDamagePotential(
                child,
                PowerAmountGain<PanachePower>(parent, child) * enemyCount,
                Math.Max(1, attacks / 5)),
            "PREP_TIME" => PowerGrowthFrontierPotential(
                child,
                damagePerAttack: PowerAmountGain<VigorPower>(parent, child)),
            "PROWESS" => PowerGrowthFrontierPotential(
                child,
                blockPerSkillBonus: PowerAmountGain<DexterityPower>(parent, child),
                damagePerAttack: PowerAmountGain<StrengthPower>(parent, child)),
            "ROLLING_BOULDER" => PowerPerTurnDamagePotential(
                child,
                PowerAmountGain<RollingBoulderPower>(parent, child),
                turns,
                enemyCount),
            "STRATAGEM" => PowerPerTurnResourcePotential(
                PowerEnergyUnit(child),
                turns),
            _ => 0,
        };
    }
}

