using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>无色能力开局投影（Draft）。无色能力可在任意角色持有，投影只读真实牌区与 Power。</summary>
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
            "AUTOMATION" => AutomationPotential(parent, child, turns),
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

    /// <summary>按预计剩余回合的实际抽牌量折算“每10抽返1能量”，而不是把剩余回合当作触发次数。</summary>
    private int AutomationPotential(SearchNode parent, SearchNode child, int turns)
    {
        int amount = PowerAmountGain<AutomationPower>(parent, child);
        if (amount == 0)
            return 0;
        int payouts = PowerCardProjectionMath.AutomationPayouts(
            PowerDrawPerTurn(child),
            Math.Max(0, turns));
        return payouts <= 0
            ? 0
            : PowerPerTriggerResourcePotential(
                amount * PowerEnergyUnit(child),
                payouts);
    }
}

